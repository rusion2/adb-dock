#requires -Version 5.1
<#
.SYNOPSIS
Compile and exercise an isolated installer without touching real startup items.
.EXAMPLE
./scripts/TestInstaller.ps1 -PayloadDirectory .build/installer-payload -NsisCompilerPath .build/tools/nsis/makensis.exe
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$PayloadDirectory,
    [Parameter(Mandatory = $true)][string]$NsisCompilerPath,
    [string]$FileManifest,
    [string]$AppVersion,
    [ValidateRange(15, 600)][int]$ProcessTimeoutSeconds = 180
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if ($env:OS -ne 'Windows_NT') { throw 'Installer checks require Windows.' }

$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$payloadPath = [IO.Path]::GetFullPath($PayloadDirectory).TrimEnd('\')
$compilerPath = [IO.Path]::GetFullPath($NsisCompilerPath)
if (-not (Test-Path -LiteralPath (Join-Path $payloadPath 'AdbDock.exe') -PathType Leaf)) { throw 'The payload does not contain AdbDock.exe.' }
if (-not (Test-Path -LiteralPath $compilerPath -PathType Leaf)) { throw 'The NSIS compiler was not found.' }
if (-not $AppVersion) {
    [xml]$applicationProject = Get-Content -LiteralPath (Join-Path $projectRoot 'src\AdbDock.App\AdbDock.App.csproj') -Raw
    $AppVersion = [string]($applicationProject.Project.PropertyGroup | Where-Object { $_.Version } | Select-Object -First 1).Version
}
if ($AppVersion -notmatch '^\d+\.\d+\.\d+$') { throw 'AppVersion must have three numeric components.' }

$checkId = [Guid]::NewGuid().ToString('N')
$runDirectory = Join-Path $projectRoot ('.build\installer-tests\' + $checkId)
New-Item -ItemType Directory -Path $runDirectory -Force | Out-Null
# Construct the Unicode suffix in code so this script also parses in Windows
# PowerShell 5.1 when checked out as UTF-8 without a BOM.
$unicodeSuffix = [string][char]0x6D4B + [char]0x8BD5
$installDirectory = Join-Path $runDirectory ('Installed App ' + $unicodeSuffix)
$movedDirectory = Join-Path $runDirectory ('Moved App ' + $unicodeSuffix)
$menuDirectory = Join-Path $runDirectory ('Start Menu ' + $unicodeSuffix)
$desktopDirectory = Join-Path $runDirectory ('Desktop ' + $unicodeSuffix)
$testRegistryRoot = 'Software\AdbDock.InstallerTests\' + $checkId
$appKey = $testRegistryRoot + '\App'
$uninstallKey = $testRegistryRoot + '\Uninstall'
$runKey = $testRegistryRoot + '\Run'
$approvalKey = $testRegistryRoot + '\Approval'
$mutexPrefix = 'Local\AdbDock.InstallerTests.' + $checkId + '.'
$setupPath = Join-Path $runDirectory 'AdbDock-Isolated-Setup.exe'
$script:installerNativeCounter = 0
$installerTestResults = New-Object 'System.Collections.Generic.List[object]'
$registryHive = $null
$failure = $null

function ConvertTo-NsisLiteral {
    param([string]$Value)
    if ($Value -match '[\r\n]') { throw 'A path contains a line break.' }
    return $Value.Replace('$', '$$').Replace('"', '$\"')
}

function Get-PayloadRelativePath {
    param([string]$Path)
    $resolved = [IO.Path]::GetFullPath($Path)
    $prefix = $payloadPath + '\'
    if (-not $resolved.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) { throw 'A payload path escapes its root.' }
    $relative = $resolved.Substring($prefix.Length)
    if ($relative -match '[*?\r\n]') { throw 'The payload contains an unsupported file name.' }
    return $relative
}

function ConvertTo-NativeArgument {
    param([AllowEmptyString()][string]$Value)
    if ($Value.Length -gt 0 -and $Value -notmatch '[\s"]') { return $Value }
    $escaped = [regex]::Replace($Value, '(\\*)"', '$1$1\"')
    $escaped = [regex]::Replace($escaped, '(\\+)$', '$1$1')
    return '"' + $escaped + '"'
}

function Invoke-OwnedProcess {
    param([string]$Executable, [string[]]$Arguments, [int]$ExpectedExit = 0, [string]$InstallDirectory)
    $script:installerNativeCounter++
    $logPath = Join-Path $runDirectory ('process-{0:D2}.log' -f $script:installerNativeCounter)
    $start = New-Object Diagnostics.ProcessStartInfo
    $start.FileName = $Executable
    $start.Arguments = (($Arguments | ForEach-Object { ConvertTo-NativeArgument $_ }) -join ' ')
    if ($InstallDirectory) {
        if ($InstallDirectory -match '["\r\n]' -or -not [IO.Path]::IsPathRooted($InstallDirectory)) { throw 'Unsafe NSIS installation directory.' }
        # NSIS parses /D from the raw command-line tail, which MUST be unquoted
        # even when the absolute directory contains spaces.
        $start.Arguments += ' /D=' + $InstallDirectory
    }
    $start.WorkingDirectory = $runDirectory
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.StandardOutputEncoding = New-Object Text.UTF8Encoding($false)
    $start.StandardErrorEncoding = New-Object Text.UTF8Encoding($false)
    $process = New-Object Diagnostics.Process
    $process.StartInfo = $start
    try {
        if (-not $process.Start()) { throw 'Could not start the test-owned process.' }
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit($ProcessTimeoutSeconds * 1000)) {
            # This is the exact process created above, never a name or process-tree match.
            $process.Kill()
            [void]$process.WaitForExit(3000)
            throw ('The test-owned process timed out: ' + $Executable)
        }
        $output = $stdout.GetAwaiter().GetResult()
        $errorOutput = $stderr.GetAwaiter().GetResult()
        [IO.File]::WriteAllText($logPath, $output + [Environment]::NewLine + $errorOutput, (New-Object Text.UTF8Encoding($false)))
        if ($process.ExitCode -ne $ExpectedExit) {
            throw ('Process exit {0}, expected {1}: {2}. See {3}' -f $process.ExitCode, $ExpectedExit, $Executable, $logPath)
        }
    }
    finally { $process.Dispose() }
}

function Assert-InstallerCondition {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) { throw $Message }
}

function Get-TestRegistryValue {
    param([string]$Key, [string]$Name)
    $opened = $registryHive.OpenSubKey($Key)
    try { if ($null -ne $opened) { return $opened.GetValue($Name, $null) } }
    finally { if ($null -ne $opened) { $opened.Dispose() } }
    return $null
}

function Set-TestRegistryValue {
    param([string]$Key, [string]$Name, $Value, [Microsoft.Win32.RegistryValueKind]$Kind = [Microsoft.Win32.RegistryValueKind]::String)
    if (-not $Key.StartsWith($testRegistryRoot + '\', [StringComparison]::Ordinal)) { throw 'A test attempted to write outside its registry subtree.' }
    $opened = $registryHive.CreateSubKey($Key, $true)
    try { $opened.SetValue($Name, $Value, $Kind) }
    finally { $opened.Dispose() }
}

function Remove-TestRegistryValue {
    param([string]$Key, [string]$Name)
    if (-not $Key.StartsWith($testRegistryRoot + '\', [StringComparison]::Ordinal)) { throw 'A test attempted to write outside its registry subtree.' }
    $opened = $registryHive.OpenSubKey($Key, $true)
    try { if ($null -ne $opened) { $opened.DeleteValue($Name, $false) } }
    finally { if ($null -ne $opened) { $opened.Dispose() } }
}

function Invoke-InstallerCheck {
    param([string]$Name, [scriptblock]$Action)
    $watch = [Diagnostics.Stopwatch]::StartNew()
    try {
        & $Action
        $installerTestResults.Add([ordered]@{ name = $Name; passed = $true; seconds = $watch.Elapsed.TotalSeconds })
        Write-Host ('PASS ' + $Name)
    }
    catch {
        $installerTestResults.Add([ordered]@{ name = $Name; passed = $false; seconds = $watch.Elapsed.TotalSeconds; error = $_.Exception.ToString() })
        throw
    }
}

function Assert-PayloadInstalled {
    param([string]$Directory)
    foreach ($file in $payloadFiles) {
        $destination = Join-Path $Directory (Get-PayloadRelativePath $file.FullName)
        Assert-InstallerCondition (Test-Path -LiteralPath $destination -PathType Leaf) ('Missing installed file: ' + $destination)
        Assert-InstallerCondition ((Get-Item -LiteralPath $destination).Length -eq $file.Length) ('Installed file length mismatch: ' + $destination)
    }
    $sourceHash = (Get-FileHash -LiteralPath (Join-Path $payloadPath 'AdbDock.exe') -Algorithm SHA256).Hash
    $installedHash = (Get-FileHash -LiteralPath (Join-Path $Directory 'AdbDock.exe') -Algorithm SHA256).Hash
    Assert-InstallerCondition ($sourceHash -eq $installedHash) 'Installed executable SHA256 differs from the payload.'
}

function Install-TestCopy {
    param([string]$Directory)
    Invoke-OwnedProcess $setupPath @('/S') -InstallDirectory $Directory
    Assert-PayloadInstalled $Directory
    Assert-InstallerCondition ((Get-TestRegistryValue $appKey 'InstallDir') -eq $Directory) 'Installer ignored its selected directory.'
    Assert-InstallerCondition (Test-Path -LiteralPath (Join-Path $Directory 'Uninstall.exe') -PathType Leaf) 'Uninstaller was not created.'
}

function Uninstall-TestCopy {
    param([string]$Directory)
    Invoke-OwnedProcess (Join-Path $Directory 'Uninstall.exe') @('/S')
    # NSIS may copy its uninstaller to a temporary process. Observe the actual
    # completion condition instead of assuming the original stub's exit suffices.
    $watch = [Diagnostics.Stopwatch]::StartNew()
    while ((Test-Path -LiteralPath (Join-Path $Directory 'AdbDock.exe')) -or (Test-Path -LiteralPath (Join-Path $Directory 'Uninstall.exe')) -or ((Get-TestRegistryValue $appKey 'InstallDir') -eq $Directory)) {
        if ($watch.Elapsed.TotalSeconds -gt $ProcessTimeoutSeconds) { throw 'The uninstaller did not remove its owned executable files.' }
        Start-Sleep -Milliseconds 100
    }
    foreach ($file in $payloadFiles) {
        Assert-InstallerCondition (-not (Test-Path -LiteralPath (Join-Path $Directory (Get-PayloadRelativePath $file.FullName)))) ('Owned payload file remained after uninstall: ' + $file.Name)
    }
}

try {
    $entries = @(Get-ChildItem -LiteralPath $payloadPath -Recurse -Force)
    foreach ($entry in $entries) {
        if (($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'Payload reparse points are not supported.' }
        [void](Get-PayloadRelativePath $entry.FullName)
    }
    $payloadFiles = @($entries | Where-Object { -not $_.PSIsContainer })
    if ($payloadFiles.Count -eq 0) { throw 'The payload is empty.' }
    if ($FileManifest) {
        $manifestPath = [IO.Path]::GetFullPath($FileManifest)
        if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) { throw 'The supplied manifest does not exist.' }
    }
    else {
        $manifestPath = Join-Path $runDirectory 'PayloadManifest.nsh'
        $lines = New-Object 'System.Collections.Generic.List[string]'
        $lines.Add('!macro RemovePayload')
        foreach ($file in $payloadFiles) {
            $relative = ConvertTo-NsisLiteral (Get-PayloadRelativePath $file.FullName)
            $lines.Add('  Delete "$INSTDIR\' + $relative + '"')
            $lines.Add('  IfFileExists "$INSTDIR\' + $relative + '" 0 +2')
            $lines.Add('  StrCpy $PayloadDeleteFailed 1')
        }
        foreach ($directory in @($entries | Where-Object { $_.PSIsContainer } | Sort-Object { $_.FullName.Length } -Descending)) {
            $lines.Add('  RMDir "$INSTDIR\' + (ConvertTo-NsisLiteral (Get-PayloadRelativePath $directory.FullName)) + '"')
        }
        $lines.Add('!macroend')
        [IO.File]::WriteAllLines($manifestPath, $lines, (New-Object Text.UTF8Encoding($false)))
    }

    $defines = [ordered]@{
        APP_VERSION = $AppVersion
        PAYLOAD_DIR = $payloadPath
        OUTPUT_FILE = $setupPath
        FILE_MANIFEST = $manifestPath
        APP_REG_KEY = $appKey
        UNINSTALL_REG_KEY = $uninstallKey
        RUN_REG_KEY = $runKey
        APPROVAL_REG_KEY = $approvalKey
        RUN_VALUE = 'AdbDock'
        LINKS_ROOT = $menuDirectory
        DESKTOP_ROOT = $desktopDirectory
        INSTANCE_MUTEX_PREFIX = $mutexPrefix
        FAST_TEST_COMPRESSION = '1'
    }
    $compilerArguments = @('/INPUTCHARSET', 'UTF8', '/OUTPUTCHARSET', 'UTF8', '/WX', '/V2')
    foreach ($item in $defines.GetEnumerator()) { $compilerArguments += '/D' + $item.Key + '=' + (ConvertTo-NsisLiteral $item.Value) }
    $compilerArguments += Join-Path $projectRoot 'installer\AdbDock.nsi'
    Invoke-InstallerCheck 'Strict NSIS compilation of the isolated installer' { Invoke-OwnedProcess $compilerPath $compilerArguments }
    $registryHive = [Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::CurrentUser, [Microsoft.Win32.RegistryView]::Registry64)
    $unrelatedCommand = '"C:\Other Application\unchanged.exe"'
    Set-TestRegistryValue $runKey 'OtherApplication' $unrelatedCommand
    $expectedCommand = '"' + (Join-Path $installDirectory 'AdbDock.exe') + '"'

    Invoke-InstallerCheck 'Running-instance guard uses the isolated current-user SID mutex' {
        $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
        try { $sid = $identity.User.Value } finally { $identity.Dispose() }
        $mutex = New-Object Threading.Mutex($false, ($mutexPrefix + $sid))
        try {
            Invoke-OwnedProcess $setupPath @('/S') 2 -InstallDirectory $installDirectory
            Assert-InstallerCondition (-not (Test-Path -LiteralPath (Join-Path $installDirectory 'AdbDock.exe'))) 'A running-instance guard allowed file installation.'
        }
        finally { $mutex.Dispose() }
    }
    Invoke-InstallerCheck 'Fresh Unicode-path install defaults to quoted login startup and Start Menu links' {
        Install-TestCopy $installDirectory
        Assert-InstallerCondition ((Get-TestRegistryValue $runKey 'AdbDock') -eq $expectedCommand) 'Fresh login startup is not the quoted installed executable.'
        Assert-InstallerCondition ((Get-TestRegistryValue $uninstallKey 'DisplayVersion') -eq $AppVersion) 'Uninstall version is missing.'
        Assert-InstallerCondition (Test-Path -LiteralPath (Join-Path $menuDirectory 'ADB Dock.lnk')) 'Application Start Menu shortcut is missing.'
        Assert-InstallerCondition (Test-Path -LiteralPath (Join-Path $menuDirectory 'Uninstall ADB Dock.lnk')) 'Uninstall Start Menu shortcut is missing.'
        Assert-InstallerCondition (-not (Test-Path -LiteralPath (Join-Path $desktopDirectory 'ADB Dock.lnk'))) 'Optional desktop shortcut was selected by default.'
        [IO.File]::WriteAllText((Join-Path $installDirectory 'keep-user-file.txt'), 'User file must survive uninstall.')
    }
    Invoke-InstallerCheck 'Upgrade preserves an explicit login-startup opt-out' {
        Remove-TestRegistryValue $runKey 'AdbDock'
        Install-TestCopy $installDirectory
        Assert-InstallerCondition ($null -eq (Get-TestRegistryValue $runKey 'AdbDock')) 'Upgrade re-enabled an opted-out startup item.'
        Assert-InstallerCondition ((Get-TestRegistryValue $runKey 'OtherApplication') -eq $unrelatedCommand) 'Upgrade changed another application startup item.'
    }
    Invoke-InstallerCheck 'Upgrade preserves Explorer disabled startup approval' {
        Set-TestRegistryValue $runKey 'AdbDock' $expectedCommand
        [byte[]]$disabled = @(3, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0)
        Set-TestRegistryValue $approvalKey 'AdbDock' $disabled ([Microsoft.Win32.RegistryValueKind]::Binary)
        Install-TestCopy $installDirectory
        $approval = Get-TestRegistryValue $approvalKey 'AdbDock'
        Assert-InstallerCondition ($null -ne $approval -and $approval[0] -eq 3) 'Upgrade cleared the disabled Explorer startup approval.'
        Assert-InstallerCondition ($null -eq (Get-TestRegistryValue $runKey 'AdbDock')) 'Disabled startup was re-enabled by upgrade.'
    }
    Invoke-InstallerCheck 'Uninstall removes owned startup and files but retains unrelated data' {
        Set-TestRegistryValue $runKey 'AdbDock' $expectedCommand
        Uninstall-TestCopy $installDirectory
        Assert-InstallerCondition ($null -eq (Get-TestRegistryValue $runKey 'AdbDock')) 'Owned startup registration survived uninstall.'
        Assert-InstallerCondition ($null -eq (Get-TestRegistryValue $approvalKey 'AdbDock')) 'Owned approval record survived uninstall.'
        Assert-InstallerCondition ($null -eq (Get-TestRegistryValue $uninstallKey 'UninstallString')) 'Owned uninstall entry survived uninstall.'
        Assert-InstallerCondition ($null -eq (Get-TestRegistryValue $appKey 'InstallDir')) 'Owned installation metadata survived uninstall.'
        Assert-InstallerCondition ((Get-TestRegistryValue $runKey 'OtherApplication') -eq $unrelatedCommand) 'Uninstall changed another application startup item.'
        Assert-InstallerCondition ([IO.File]::ReadAllText((Join-Path $installDirectory 'keep-user-file.txt')) -eq 'User file must survive uninstall.') 'Uninstall removed or changed unrelated user files.'
        Assert-InstallerCondition (-not (Test-Path -LiteralPath (Join-Path $menuDirectory 'ADB Dock.lnk'))) 'Owned Start Menu link survived uninstall.'
    }
    Invoke-InstallerCheck 'Uninstall preserves a startup command owned by another installed copy' {
        Install-TestCopy $installDirectory
        $foreignCommand = '"' + (Join-Path $runDirectory 'Other Copy\AdbDock.exe') + '"'
        Set-TestRegistryValue $runKey 'AdbDock' $foreignCommand
        Uninstall-TestCopy $installDirectory
        Assert-InstallerCondition ((Get-TestRegistryValue $runKey 'AdbDock') -eq $foreignCommand) 'Uninstall removed another copy startup registration.'
        Assert-InstallerCondition ((Get-TestRegistryValue $runKey 'OtherApplication') -eq $unrelatedCommand) 'Another application startup registration changed.'
    }
    Invoke-InstallerCheck 'An older copy uninstaller preserves a moved installation and its links' {
        Remove-TestRegistryValue $runKey 'AdbDock'
        Install-TestCopy $installDirectory
        Install-TestCopy $movedDirectory
        $movedCommand = '"' + (Join-Path $movedDirectory 'AdbDock.exe') + '"'
        $linkPath = Join-Path $menuDirectory 'ADB Dock.lnk'
        $linkHash = (Get-FileHash -LiteralPath $linkPath -Algorithm SHA256).Hash
        Uninstall-TestCopy $installDirectory
        Assert-InstallerCondition ((Get-TestRegistryValue $appKey 'InstallDir') -eq $movedDirectory) 'Old uninstaller removed current-copy metadata.'
        Assert-InstallerCondition ((Get-TestRegistryValue $uninstallKey 'InstallLocation') -eq $movedDirectory) 'Old uninstaller removed the current uninstall entry.'
        Assert-InstallerCondition ((Get-FileHash -LiteralPath $linkPath -Algorithm SHA256).Hash -eq $linkHash) 'Old uninstaller changed the current Start Menu link.'
        Assert-InstallerCondition ((Get-TestRegistryValue $runKey 'AdbDock') -eq $movedCommand) 'Old uninstaller changed the current-copy startup command.'
        Assert-PayloadInstalled $movedDirectory
        Uninstall-TestCopy $movedDirectory
        Assert-InstallerCondition ($null -eq (Get-TestRegistryValue $runKey 'AdbDock')) 'Current-copy uninstall left its startup command.'
    }
}
catch { $failure = $_.Exception }
finally {
    if ($null -ne $registryHive) {
        try {
            # The only recursive operation is registry cleanup of this verified,
            # unique inert test subtree, never a real startup/uninstall key.
            if ($testRegistryRoot -cne ('Software\AdbDock.InstallerTests\' + $checkId) -or $checkId -notmatch '^[a-f0-9]{32}$') { throw 'Refusing unsafe test-registry cleanup.' }
            $registryHive.DeleteSubKeyTree($testRegistryRoot, $false)
        }
        catch { if ($null -eq $failure) { $failure = $_.Exception } }
        finally { $registryHive.Dispose() }
    }
    $report = [ordered]@{
        passed = ($null -eq $failure)
        appVersion = $AppVersion
        payloadDirectory = $payloadPath
        compiler = $compilerPath
        testDirectory = $runDirectory
        registrySubtree = $testRegistryRoot
        realStartupModified = $false
        realShortcutsModified = $false
        checks = $installerTestResults.ToArray()
        failure = $(if ($null -eq $failure) { $null } else { $failure.ToString() })
        checkedAt = [DateTimeOffset]::Now.ToString('O')
    }
    $report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $runDirectory 'installer-test-results.json') -Encoding UTF8
    Write-Host ('Installer check artifacts: ' + $runDirectory)
}
if ($null -ne $failure) { throw $failure }
Write-Host ('Installer checks passed: ' + $installerTestResults.Count)
