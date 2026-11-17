#requires -Version 5.1
<#
.SYNOPSIS
Build, check and package a Windows x64 portable release of ADB Dock.
.EXAMPLE
./scripts/Publish.ps1
.EXAMPLE
./scripts/Publish.ps1 -SelfContained
.EXAMPLE
./scripts/Publish.ps1 -IncludeUiChecks
#>
[CmdletBinding()]
param(
    [switch]$SelfContained,
    [switch]$SkipChecks,
    [switch]$IncludeUiChecks
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if ($env:OS -ne 'Windows_NT') { throw 'ADB Dock must be published on Windows.' }
if ($IncludeUiChecks -and $SkipChecks) { throw '-IncludeUiChecks cannot be combined with -SkipChecks.' }
if ($IncludeUiChecks -and -not [Environment]::UserInteractive) {
    throw '-IncludeUiChecks requires an interactive Windows desktop.'
}

$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$appProject = Join-Path $projectRoot 'src\AdbDock.App\AdbDock.App.csproj'
$solution = Join-Path $projectRoot 'AdbDock.sln'
$dotnet = (Get-Command dotnet -CommandType Application -ErrorAction Stop).Source
[xml]$project = Get-Content -LiteralPath $appProject -Raw
$version = [string]($project.Project.PropertyGroup | Where-Object { $_.Version } | Select-Object -First 1).Version
if ($version -notmatch '^[0-9A-Za-z.+-]+$') { throw 'The project version cannot be used as a release name.' }

$flavor = if ($SelfContained) { 'self-contained' } else { 'framework-dependent' }
$releaseName = 'AdbDock-{0}-win-x64-{1}-{2}-{3}' -f $version, $flavor, (Get-Date -Format 'yyyyMMdd-HHmmss'), ([Guid]::NewGuid().ToString('N').Substring(0, 6))
$artifactRoot = Join-Path $projectRoot 'artifacts'
$publishDirectory = Join-Path $artifactRoot $releaseName
$archive = Join-Path $artifactRoot ($releaseName + '.zip')
$checksum = $archive + '.sha256'
$checkHost = Join-Path $projectRoot 'tests\AdbDock.Checks\bin\Release\net10.0\AdbDock.Checks.exe'
$uiCheckHost = Join-Path $projectRoot 'tests\AdbDock.UiChecks\bin\Release\net10.0-windows\AdbDock.UiChecks.exe'
$nativeUiChecksPassed = $null
$publishedSmokePassed = $null

# Use a new directory per invocation. Never delete or overwrite an earlier release.
if ((Test-Path -LiteralPath $publishDirectory) -or (Test-Path -LiteralPath $archive)) {
    throw 'The generated release name already exists. Run the script again.'
}

$environmentOverrides = @{
    DOTNET_CLI_HOME = (Join-Path $projectRoot '.build\dotnet-home')
    DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
    DOTNET_ADD_GLOBAL_TOOLS_TO_PATH = 'false'
    DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE = 'true'
    MSBuildEnableWorkloadResolver = 'false'
    # Test fixtures inherited from another session must not bypass the check runner.
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
    if ($LASTEXITCODE -ne 0) {
        throw ('dotnet {0} failed with exit code {1}.' -f $Arguments[0], $LASTEXITCODE)
    }
}

Push-Location -LiteralPath $projectRoot
try {
    foreach ($name in $environmentOverrides.Keys) {
        [Environment]::SetEnvironmentVariable($name, $environmentOverrides[$name], 'Process')
    }
    $sdk = & $dotnet --version
    if ($LASTEXITCODE -ne 0 -or $sdk -notmatch '^10\.') { throw '.NET 10 SDK is required.' }

    # A single MSBuild node also works in restricted shells without build-server IPC.
    Invoke-DotNet -Arguments @('build', $solution, '-c', 'Release', '--nologo', '--disable-build-servers', '-m:1', '-nr:false')
    if (-not $SkipChecks) {
        & $checkHost
        if ($LASTEXITCODE -ne 0) { throw "Checks failed with exit code $LASTEXITCODE. No release was packaged." }
    }
    if ($IncludeUiChecks) {
        $uiArtifacts = Join-Path $artifactRoot ('ui-checks-' + $releaseName)
        & $uiCheckHost $checkHost $uiArtifacts
        if ($LASTEXITCODE -ne 0) { throw "Native WPF checks failed with exit code $LASTEXITCODE. No release was packaged." }
        $nativeUiChecksPassed = $true
    }

    New-Item -ItemType Directory -Path $publishDirectory -Force | Out-Null
    $selfContainedValue = if ($SelfContained) { 'true' } else { 'false' }
    Invoke-DotNet -Arguments @(
        'publish', $appProject, '-c', 'Release', '-r', 'win-x64',
        '--self-contained', $selfContainedValue, '--nologo', '--disable-build-servers',
        '-m:1', '-nr:false', '-p:PublishSingleFile=false', '-p:PublishTrimmed=false',
        '-p:DebugType=None', '-p:DebugSymbols=false', '-o', $publishDirectory
    )
    foreach ($required in @('AdbDock.exe', 'AdbDock.dll', 'AdbDock.Core.dll', 'AdbDock.deps.json', 'AdbDock.runtimeconfig.json')) {
        if (-not (Test-Path -LiteralPath (Join-Path $publishDirectory $required) -PathType Leaf)) {
            throw "Publish output is incomplete: $required. The directory has been retained for inspection."
        }
    }
    if ($IncludeUiChecks) {
        $smokeArtifacts = Join-Path $artifactRoot ('release-smoke-' + $releaseName)
        & $uiCheckHost '--smoke' (Join-Path $publishDirectory 'AdbDock.exe') $checkHost $smokeArtifacts
        if ($LASTEXITCODE -ne 0) { throw "Published startup smoke failed with exit code $LASTEXITCODE. No ZIP was created; output was retained for inspection." }
        $publishedSmokePassed = $true
    }

    foreach ($document in (Get-ChildItem -LiteralPath $projectRoot -Filter '*.md' -File)) {
        Copy-Item -LiteralPath $document.FullName -Destination (Join-Path $publishDirectory $document.Name)
    }
    $runtimeNote = if ($SelfContained) { 'Includes the .NET runtime; no separate .NET installation is needed.' } else { 'Requires the Windows x64 .NET 10 Desktop Runtime (not just the base .NET Runtime).' }
    $releaseInfo = [ordered]@{
        application = 'ADB Dock'
        version = $version
        runtime = 'win-x64'
        selfContained = [bool]$SelfContained
        builtAt = [DateTimeOffset]::Now.ToString('O')
        sdk = ([string]$sdk).Trim()
        checksPassed = -not [bool]$SkipChecks
        nativeUiChecksPassed = $nativeUiChecksPassed
        publishedSmokePassed = $publishedSmokePassed
        toolsBundled = $false
        runtimeRequirement = $runtimeNote
    }
    $releaseInfo | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $publishDirectory 'release-info.json') -Encoding UTF8
    @"
ADB Dock $version - Windows x64

Extract the entire ZIP, then launch AdbDock.exe inside the extracted folder.
$runtimeNote

ADB and scrcpy are not included. Configure their paths in Settings, put their
directories on PATH, or add complete official distributions under:
  tools\platform-tools\adb.exe
  tools\scrcpy\scrcpy.exe
Keep their DLLs, scrcpy server, dependencies and license/notice files together.

Settings and errors.log are stored in %LOCALAPPDATA%\AdbDock by default.
Closing the panel hides it; use the tray menu or context menu to exit.
See README.md and release-info.json for prerequisites and validation limits.
"@ | Set-Content -LiteralPath (Join-Path $publishDirectory 'START-HERE.txt') -Encoding UTF8

    Compress-Archive -LiteralPath $publishDirectory -DestinationPath $archive -CompressionLevel Optimal
    $hash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
    ('{0}  {1}' -f $hash, [IO.Path]::GetFileName($archive)) | Set-Content -LiteralPath $checksum -Encoding ASCII
    Write-Host "Portable folder: $publishDirectory"
    Write-Host "ZIP: $archive"
    Write-Host "SHA256: $checksum"
    Write-Host 'Packaging complete. Real-device and manual DPI acceptance remain separate checks.'
}
finally {
    foreach ($name in $previousEnvironment.Keys) {
        [Environment]::SetEnvironmentVariable($name, $previousEnvironment[$name], 'Process')
    }
    Pop-Location
}
