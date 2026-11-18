#requires -Version 5.1
<#
.SYNOPSIS
Build a current-user Windows x64 installer with a self-contained .NET runtime.
.EXAMPLE
./scripts/BuildInstaller.ps1 -IncludeUiChecks
.EXAMPLE
./scripts/BuildInstaller.ps1 -PayloadDirectory 'G:\releases\self-contained-payload' -SkipChecks
#>
[CmdletBinding()]
param(
    [string]$PayloadDirectory,
    [string]$NsisCompilerPath,
    [switch]$SkipChecks,
    [switch]$IncludeUiChecks
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if ($env:OS -ne 'Windows_NT') { throw 'The installer must be built on Windows.' }
if ($IncludeUiChecks -and $SkipChecks) { throw '-IncludeUiChecks cannot be combined with -SkipChecks.' }
if ($IncludeUiChecks -and -not [Environment]::UserInteractive) {
    throw '-IncludeUiChecks requires an interactive Windows desktop.'
}

$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
. (Join-Path $PSScriptRoot 'ReleaseVersion.ps1')
. (Join-Path $PSScriptRoot 'ReleaseDocumentation.ps1')
$appProject = Join-Path $projectRoot 'src\AdbDock.App\AdbDock.App.csproj'
$solution = Join-Path $projectRoot 'AdbDock.sln'
$installerSource = Join-Path $projectRoot 'installer\AdbDock.nsi'
$releaseVersion = Get-ProjectReleaseVersion -ProjectPath $appProject
$version = $releaseVersion.Version
$sourceState = Assert-StableReleaseSource -ProjectRoot $projectRoot -ReleaseVersion $releaseVersion -ExternalPayload:([bool]$PayloadDirectory)
$dotnet = (Get-Command dotnet -CommandType Application -ErrorAction Stop).Source

$buildId = (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 6)
$releaseName = 'AdbDock-{0}-win-x64-Setup-{1}' -f $version, $buildId
$artifactRoot = Join-Path $projectRoot 'artifacts'
$stagingDirectory = Join-Path $projectRoot ('.build\installer\' + $releaseName)
$payload = Join-Path $stagingDirectory 'payload'
$manifest = Join-Path $stagingDirectory 'payload-files.nsh'
$inventory = Join-Path $stagingDirectory 'payload-inventory.txt'
$outputFile = Join-Path $artifactRoot ($releaseName + '.exe')
$infoFile = Join-Path $artifactRoot ($releaseName + '.installer-info.json')
$checkHost = Join-Path $projectRoot 'tests\AdbDock.Checks\bin\Release\net10.0\AdbDock.Checks.exe'
$uiCheckHost = Join-Path $projectRoot 'tests\AdbDock.UiChecks\bin\Release\net10.0-windows\AdbDock.UiChecks.exe'
$nativeUiChecksPassed = $null
$publishedSmokePassed = $null

# This archive is the portable compiler, not a machine-wide NSIS installation.
$nsisVersion = '3.13'
$nsisUrl = 'https://sourceforge.net/projects/nsis/files/NSIS%203/3.13/nsis-3.13.zip/download'
$nsisSha256 = 'BA63DFFC4410EE89193E1CB5A41989991BD77C61068DA17E3156D136B7B0B3D8'

$environmentOverrides = @{
    DOTNET_CLI_HOME = (Join-Path $projectRoot '.build\dotnet-home')
    NUGET_PACKAGES = (Join-Path $projectRoot '.build\nuget-packages')
    DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
    DOTNET_ADD_GLOBAL_TOOLS_TO_PATH = 'false'
    DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE = 'true'
    MSBuildEnableWorkloadResolver = 'false'
    ADBDOCK_UI_SNAPSHOT = $null
    ADB_DOCK_CHECKS_STATE = $null
    ADB_DOCK_OPERATIONS_CHECK_PIPE = $null
}
$previousEnvironment = @{}
foreach ($name in $environmentOverrides.Keys) {
    $previousEnvironment[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
}

function Invoke-DotNet {
    param([Parameter(Mandatory = $true)][string[]]$Arguments)
    & $dotnet @Arguments
    if ($LASTEXITCODE -ne 0) { throw ('dotnet {0} failed with exit code {1}.' -f $Arguments[0], $LASTEXITCODE) }
}

function ConvertTo-NsisString {
    param([Parameter(Mandatory = $true)][string]$Value)
    if ($Value.IndexOfAny([char[]]@([char]0, [char]10, [char]13)) -ge 0) {
        throw 'NSIS values cannot contain NUL or line breaks.'
    }
    return $Value.Replace('$', '$$').Replace('"', '$\"')
}

function Get-SafePayloadEntries {
    param([Parameter(Mandatory = $true)][string]$Directory)
    $absoluteRoot = [IO.Path]::GetFullPath($Directory).TrimEnd('\')
    $rootPrefix = $absoluteRoot + '\'
    $rootItem = Get-Item -LiteralPath $absoluteRoot -Force
    if (-not $rootItem.PSIsContainer -or ($rootItem.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw 'The payload must be a regular directory, not a reparse point.'
    }
    # Reject links before descending into any directory.
    $pending = New-Object 'System.Collections.Generic.Queue[string]'
    $pending.Enqueue($absoluteRoot)
    while ($pending.Count -gt 0) {
        foreach ($entry in (Get-ChildItem -LiteralPath $pending.Dequeue() -Force)) {
            if ($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw ('Payload links are not supported: ' + $entry.FullName) }
            $full = [IO.Path]::GetFullPath($entry.FullName)
            if (-not $full.StartsWith($rootPrefix, [StringComparison]::OrdinalIgnoreCase)) { throw 'A payload entry escaped its root.' }
            $relative = $full.Substring($rootPrefix.Length)
            if ($relative.IndexOfAny([char[]]'*?":') -ge 0 -or $relative -match '[\x00\r\n]' -or
                ($relative.Split('\') | Where-Object { $_ -eq '.' -or $_ -eq '..' -or $_.Length -eq 0 })) {
                throw ('Unsafe payload path: ' + $relative)
            }
            [pscustomobject]@{ FullName = $full; Relative = $relative; IsDirectory = $entry.PSIsContainer }
            if ($entry.PSIsContainer) { $pending.Enqueue($full) }
        }
    }
}

function Assert-NsisArchive {
    param([Parameter(Mandatory = $true)][string]$ArchivePath)
    if ((Get-FileHash -LiteralPath $ArchivePath -Algorithm SHA256).Hash -ne $nsisSha256) {
        throw 'The NSIS ZIP SHA256 does not match the pinned compiler. The file was retained for inspection.'
    }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [IO.Compression.ZipFile]::OpenRead($ArchivePath)
    try {
        $compilerEntry = 'nsis-' + $nsisVersion + '/makensis.exe'
        if ($null -eq $zip.GetEntry($compilerEntry)) { throw 'The NSIS archive has no expected compiler.' }
        foreach ($entry in $zip.Entries) {
            $entryPath = $entry.FullName.Replace('\', '/')
            if ($entryPath.StartsWith('/') -or $entryPath.Contains(':') -or
                ($entryPath.Split('/') | Where-Object { $_ -eq '..' -or $_ -eq '.' })) {
                throw 'The NSIS archive contains an unsafe entry path.'
            }
        }
    }
    finally { $zip.Dispose() }
}

function Receive-NsisArchive {
    param(
        [Parameter(Mandatory = $true)][string]$Destination,
        [Parameter(Mandatory = $true)][string]$DependenciesDirectory
    )
    $downloadUri = [Uri]$nsisUrl
    for ($hop = 0; $hop -le 2; $hop++) {
        $allowedHost = $downloadUri.Host -eq 'sourceforge.net' -or
            $downloadUri.Host -eq 'downloads.sourceforge.net' -or
            $downloadUri.Host.EndsWith('.dl.sourceforge.net', [StringComparison]::OrdinalIgnoreCase)
        if ($downloadUri.Scheme -ne 'https' -or -not $allowedHost -or $downloadUri.UserInfo.Length -gt 0) {
            throw 'The NSIS download redirect is outside the allowed official HTTPS hosts.'
        }
        $download = Join-Path $DependenciesDirectory ('nsis-' + $nsisVersion + '-' + $buildId + '-' + $hop + '.download')
        Invoke-WebRequest -Uri $downloadUri.AbsoluteUri -OutFile $download -UseBasicParsing -UserAgent 'Wget/1.21.4'
        if ((Get-FileHash -LiteralPath $download -Algorithm SHA256).Hash -eq $nsisSha256) {
            Assert-NsisArchive -ArchivePath $download
            Move-Item -LiteralPath $download -Destination $Destination
            return
        }
        if ((Get-Item -LiteralPath $download).Length -gt 2000000) {
            throw 'The NSIS download did not match its pinned SHA256. The download was retained.'
        }
        # SourceForge sometimes returns an HTML meta refresh instead of an HTTP redirect.
        $html = [IO.File]::ReadAllText($download)
        $refresh = [regex]::Match($html, '(?is)<meta\b(?=[^>]*http-equiv\s*=\s*["'']?refresh["'']?)[^>]*\bcontent\s*=\s*["''][^"'']*?url=([^"'']+)')
        if (-not $refresh.Success -or $hop -eq 2) {
            throw 'No verified NSIS ZIP was received. Supply -NsisCompilerPath or inspect the retained download.'
        }
        $redirect = [Net.WebUtility]::HtmlDecode($refresh.Groups[1].Value).Trim()
        $downloadUri = [Uri]::new($downloadUri, $redirect)
    }
}

Push-Location -LiteralPath $projectRoot
try {
    foreach ($name in $environmentOverrides.Keys) {
        [Environment]::SetEnvironmentVariable($name, $environmentOverrides[$name], 'Process')
    }
    if ((Test-Path -LiteralPath $stagingDirectory) -or (Test-Path -LiteralPath $outputFile)) {
        throw 'The generated installer name already exists. Run the script again.'
    }
    $sdk = & $dotnet --version
    if ($LASTEXITCODE -ne 0 -or $sdk -notmatch '^10\.') { throw '.NET 10 SDK is required.' }
    if (-not $SkipChecks -or -not $PayloadDirectory) {
        Invoke-DotNet -Arguments @('build', $solution, '-c', 'Release', '--nologo', '--disable-build-servers', '-m:1', '-nr:false')
    }
    if (-not $SkipChecks) {
        & $checkHost
        if ($LASTEXITCODE -ne 0) { throw "Core checks failed with exit code $LASTEXITCODE. No installer was created." }
    }
    if ($IncludeUiChecks) {
        & $uiCheckHost $checkHost (Join-Path $artifactRoot ('installer-ui-checks-' + $buildId))
        if ($LASTEXITCODE -ne 0) { throw "Native WPF checks failed with exit code $LASTEXITCODE. No installer was created." }
        $nativeUiChecksPassed = $true
    }

    if ($PayloadDirectory) {
        $inputPayload = (Resolve-Path -LiteralPath $PayloadDirectory).ProviderPath
        $inputPrefix = [IO.Path]::GetFullPath($inputPayload).TrimEnd('\') + '\'
        if ($stagingDirectory.StartsWith($inputPrefix, [StringComparison]::OrdinalIgnoreCase)) {
            throw 'The staging directory cannot be inside the supplied payload directory.'
        }
        $inputEntries = @(Get-SafePayloadEntries -Directory $inputPayload)
        $legacyDocumentation = @(Get-LegacyReleaseDocumentationPaths -PayloadDirectory $inputPayload)
    }
    New-Item -ItemType Directory -Path $payload -Force | Out-Null
    if ($PayloadDirectory) {
        foreach ($entry in $inputEntries) {
            if ($entry.Relative -in $legacyDocumentation) { continue }
            $destination = Join-Path $payload $entry.Relative
            if ($entry.IsDirectory) { New-Item -ItemType Directory -Path $destination -Force | Out-Null }
            else {
                New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($destination)) -Force | Out-Null
                Copy-Item -LiteralPath $entry.FullName -Destination $destination
            }
        }
    }
    else {
        Invoke-DotNet -Arguments @(
            'publish', $appProject, '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true',
            '--nologo', '--disable-build-servers', '-m:1', '-nr:false',
            '-p:PublishSingleFile=false', '-p:PublishTrimmed=false', '-p:DebugType=None', '-p:DebugSymbols=false', '-o', $payload
        )
    }
    foreach ($required in @('AdbDock.exe', 'AdbDock.dll', 'AdbDock.Core.dll', 'AdbDock.deps.json', 'AdbDock.runtimeconfig.json', 'coreclr.dll', 'hostfxr.dll', 'PresentationFramework.dll')) {
        if (-not (Test-Path -LiteralPath (Join-Path $payload $required) -PathType Leaf)) {
            throw ('A complete self-contained Windows x64 payload is required; missing ' + $required)
        }
    }
    $runtimeConfiguration = Get-Content -LiteralPath (Join-Path $payload 'AdbDock.runtimeconfig.json') -Raw | ConvertFrom-Json
    if ($null -eq $runtimeConfiguration.runtimeOptions.PSObject.Properties['includedFrameworks']) {
        throw 'The payload runtime configuration is not self-contained.'
    }
    $dependencies = Get-Content -LiteralPath (Join-Path $payload 'AdbDock.deps.json') -Raw | ConvertFrom-Json
    if (-not ([string]$dependencies.runtimeTarget.name).EndsWith('/win-x64', [StringComparison]::OrdinalIgnoreCase)) {
        throw 'The payload is not published for win-x64.'
    }
    Assert-PayloadReleaseVersion -PayloadDirectory $payload -ReleaseVersion $releaseVersion
    Copy-ReleaseDocumentation -ProjectRoot $projectRoot -DestinationDirectory $payload
    foreach ($license in @('LICENSE.txt', 'ThirdPartyNotices.txt')) {
        $licenseDestination = Join-Path $payload $license
        if (-not (Test-Path -LiteralPath $licenseDestination -PathType Leaf)) {
            $licenseSource = Join-Path ([IO.Path]::GetDirectoryName($dotnet)) $license
            if (-not (Test-Path -LiteralPath $licenseSource -PathType Leaf)) { throw ('Missing .NET notice: ' + $licenseSource) }
            Copy-Item -LiteralPath $licenseSource -Destination $licenseDestination
        }
    }
    if ($IncludeUiChecks) {
        & $uiCheckHost '--smoke' (Join-Path $payload 'AdbDock.exe') $checkHost (Join-Path $artifactRoot ('installer-payload-smoke-' + $buildId))
        if ($LASTEXITCODE -ne 0) { throw "Self-contained payload startup failed with exit code $LASTEXITCODE. No installer was created." }
        $publishedSmokePassed = $true
    }

    $entries = @(Get-SafePayloadEntries -Directory $payload)
    if ($entries | Where-Object { -not $_.IsDirectory -and [IO.Path]::GetFileName($_.Relative) -match '^(?:adb|scrcpy|uninstall)\.exe$' }) {
        throw 'The application payload must not contain external adb/scrcpy tools or a pre-existing uninstaller.'
    }
    $manifestLines = New-Object 'System.Collections.Generic.List[string]'
    $manifestLines.Add('; Generated exact payload removal list. Unknown files and directories are preserved.')
    $manifestLines.Add('!macro RemovePayload')
    foreach ($entry in ($entries | Where-Object { -not $_.IsDirectory } | Sort-Object Relative)) {
        $relative = ConvertTo-NsisString -Value $entry.Relative
        $manifestLines.Add('  Delete "$INSTDIR\' + $relative + '"')
        $manifestLines.Add('  IfFileExists "$INSTDIR\' + $relative + '" 0 +2')
        $manifestLines.Add('  StrCpy $PayloadDeleteFailed 1')
    }
    foreach ($entry in ($entries | Where-Object IsDirectory | Sort-Object { $_.Relative.Length } -Descending)) {
        $manifestLines.Add('  RMDir "$INSTDIR\' + (ConvertTo-NsisString -Value $entry.Relative) + '"')
    }
    $manifestLines.Add('!macroend')
    [IO.File]::WriteAllLines($manifest, $manifestLines, (New-Object Text.UTF8Encoding($true)))
    $inventoryEntries = foreach ($entry in $entries) {
        if ($entry.IsDirectory) { $entry.Relative + '\' } else { $entry.Relative }
    }
    [IO.File]::WriteAllLines($inventory, [string[]]$inventoryEntries, [Text.Encoding]::UTF8)

    if ($NsisCompilerPath) {
        $compiler = (Resolve-Path -LiteralPath $NsisCompilerPath).ProviderPath
        $compilerSource = 'explicit-compiler-path'
    }
    else {
        if ($nsisSha256 -notmatch '^[A-Fa-f0-9]{64}$' -or $nsisUrl -notmatch '^https://') {
            throw 'The official NSIS archive URL and SHA256 must be configured before downloading the compiler.'
        }
        $dependencies = Join-Path $projectRoot '.build\dependencies'
        New-Item -ItemType Directory -Path $dependencies -Force | Out-Null
        $archive = Join-Path $dependencies ('nsis-' + $nsisVersion + '-verified.zip')
        if (-not (Test-Path -LiteralPath $archive -PathType Leaf)) {
            Receive-NsisArchive -Destination $archive -DependenciesDirectory $dependencies
        }
        Assert-NsisArchive -ArchivePath $archive
        $extracted = Join-Path $dependencies ('nsis-' + $nsisVersion + '-' + $buildId)
        Expand-Archive -LiteralPath $archive -DestinationPath $extracted
        $compiler = Join-Path $extracted ('nsis-' + $nsisVersion + '\makensis.exe')
        $compilerSource = $nsisUrl
    }
    $compilerVersion = & $compiler '/VERSION'
    if ($LASTEXITCODE -ne 0 -or ([string]$compilerVersion).Trim() -notmatch '^v?3\.13$') { throw 'NSIS 3.13 is required.' }
    New-Item -ItemType Directory -Path $artifactRoot -Force | Out-Null
    $compilerArguments = @(
        '/INPUTCHARSET', 'UTF8', '/WX', '/V3',
        ('/DAPP_VERSION=' + (ConvertTo-NsisString -Value $version)),
        ('/DAPP_FILE_VERSION=' + (ConvertTo-NsisString -Value $releaseVersion.FileVersion)),
        ('/DPAYLOAD_DIR=' + (ConvertTo-NsisString -Value $payload)),
        ('/DOUTPUT_FILE=' + (ConvertTo-NsisString -Value $outputFile)),
        ('/DFILE_MANIFEST=' + (ConvertTo-NsisString -Value $manifest)),
        ('/DPAYLOAD_INVENTORY=' + (ConvertTo-NsisString -Value $inventory)),
        $installerSource
    )
    if ($releaseVersion.IsStable) {
        [void](Assert-StableReleaseSource -ProjectRoot $projectRoot -ReleaseVersion $releaseVersion -ExpectedCommit $sourceState.Commit)
    }
    & $compiler @compilerArguments
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $outputFile -PathType Leaf)) { throw 'NSIS compilation failed. Staging files have been retained.' }
    $hash = (Get-FileHash -LiteralPath $outputFile -Algorithm SHA256).Hash.ToLowerInvariant()
    ('{0}  {1}' -f $hash, [IO.Path]::GetFileName($outputFile)) | Set-Content -LiteralPath ($outputFile + '.sha256') -Encoding ASCII
    $info = [ordered]@{
        application = 'ADB Dock'
        version = $version
        fileVersion = $releaseVersion.FileVersion
        releaseChannel = $releaseVersion.Channel
        payloadProductVersion = (Get-Item -LiteralPath (Join-Path $payload 'AdbDock.dll')).VersionInfo.ProductVersion
        retainedReleaseBranch = $releaseVersion.ReleaseBranch
        installer = [IO.Path]::GetFileName($outputFile)
        installerSha256 = $hash
        runtime = 'win-x64'
        selfContained = $true
        installScope = 'current-user'
        defaultInstallDirectory = '%LOCALAPPDATA%\Programs\AdbDock'
        loginAutoStartDefaultEnabled = $true
        autoStartRegistryValue = 'HKCU\Software\Microsoft\Windows\CurrentVersion\Run\AdbDock'
        crashRestartEnabled = $false
        publishedSmokePassed = $publishedSmokePassed
        installerLifecycleTested = $false
        toolsBundled = $false
        sdk = ([string]$sdk).Trim()
        compilerVersion = ([string]$compilerVersion).Trim()
        compilerSource = $compilerSource
        compilerSha256 = (Get-FileHash -LiteralPath $compiler -Algorithm SHA256).Hash.ToLowerInvariant()
        officialCompilerArchiveSha256 = $nsisSha256
        payloadFileCount = @($entries | Where-Object { -not $_.IsDirectory }).Count
        builtAt = [DateTimeOffset]::Now.ToString('O')
    }
    $info += Get-InstallerSourceInfo -SourceState $sourceState -ChecksPassed (!$SkipChecks) -NativeUiChecksPassed $nativeUiChecksPassed -ExternalPayload:([bool]$PayloadDirectory)
    $info | ConvertTo-Json | Set-Content -LiteralPath $infoFile -Encoding UTF8
    Write-Host "Installer: $outputFile"
    Write-Host "SHA256: $outputFile.sha256"
    Write-Host "Installer information: $infoFile"
    Write-Host 'Installer build complete. Installation/uninstallation acceptance is a separate check.'
}
finally {
    foreach ($name in $previousEnvironment.Keys) {
        [Environment]::SetEnvironmentVariable($name, $previousEnvironment[$name], 'Process')
    }
    Pop-Location
}
