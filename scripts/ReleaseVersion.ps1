#requires -Version 5.1
# Shared release-label, Windows-resource and source-retention checks.
# Dot-sourcing this file defines functions; it does not build or change Git state.

function ConvertTo-WindowsFileVersion {
    param([Parameter(Mandatory = $true)][string]$FileVersion)
    if ($FileVersion -notmatch '\A(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\z') {
        throw 'FileVersion must contain four numeric components without leading zeros.'
    }
    foreach ($component in $FileVersion.Split('.')) {
        $number = [uint32]0
        if (-not [uint32]::TryParse($component, [ref]$number) -or $number -gt 65535) {
            throw 'Each Windows FileVersion component must be between 0 and 65535.'
        }
    }
    return $FileVersion
}

function Get-ReleaseVersion {
    param(
        [Parameter(Mandatory = $true)][string]$Version,
        [string]$FileVersion
    )
    $parsed = [regex]::Match($Version, '\A(?<major>0|[1-9][0-9]*)\.(?<minor>0|[1-9][0-9]*)\.(?<patch>0|[1-9][0-9]*)(?:-beta\.(?<beta>[1-9][0-9]*))?\z')
    if (-not $parsed.Success) {
        throw 'Version must be X.Y.Z or X.Y.Z-beta.N, with no leading zeros and beta N >= 1.'
    }
    $baseVersion = '{0}.{1}.{2}' -f $parsed.Groups['major'].Value, $parsed.Groups['minor'].Value, $parsed.Groups['patch'].Value
    # The display label and the Win32 numeric resource version are independent.
    [void](ConvertTo-WindowsFileVersion -FileVersion ($baseVersion + '.0'))
    if (-not $FileVersion) { $FileVersion = $baseVersion + '.0' }
    $numericVersion = ConvertTo-WindowsFileVersion -FileVersion $FileVersion
    $isStable = -not $parsed.Groups['beta'].Success
    [pscustomobject]@{
        Version = $Version
        BaseVersion = $baseVersion
        FileVersion = $numericVersion
        WindowsFileVersion = $numericVersion
        Channel = $(if ($isStable) { 'stable' } else { 'beta' })
        IsStable = $isStable
        BetaNumber = $(if ($isStable) { $null } else { $parsed.Groups['beta'].Value })
        ReleaseBranch = $(if ($isStable) { 'release/v' + $Version } else { $null })
        ReleaseTag = $(if ($isStable) { 'v' + $Version } else { $null })
    }
}

function Get-ProjectReleaseVersion {
    param([Parameter(Mandatory = $true)][string]$ProjectPath)
    [xml]$releaseProject = Get-Content -LiteralPath $ProjectPath -Raw -Encoding UTF8
    $versionNodes = $releaseProject.SelectNodes('/Project/PropertyGroup/Version')
    if ($versionNodes.Count -ne 1) { throw 'The project must declare one unambiguous Version.' }
    $fileVersionNodes = $releaseProject.SelectNodes('/Project/PropertyGroup/FileVersion')
    if ($fileVersionNodes.Count -gt 1) { throw 'The project must not declare multiple FileVersion values.' }
    $numericVersion = $null
    if ($fileVersionNodes.Count -eq 1) { $numericVersion = [string]$fileVersionNodes[0].InnerText }
    return Get-ReleaseVersion -Version ([string]$versionNodes[0].InnerText) -FileVersion $numericVersion
}

function Assert-PayloadReleaseVersion {
    param(
        [Parameter(Mandatory = $true)][string]$PayloadDirectory,
        [Parameter(Mandatory = $true)]$ReleaseVersion
    )
    foreach ($fileName in @('AdbDock.exe', 'AdbDock.dll')) {
        $file = Join-Path $PayloadDirectory $fileName
        if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { throw ('Missing release binary: ' + $file) }
        $binaryInfo = (Get-Item -LiteralPath $file).VersionInfo
        $productVersion = ([string]$binaryInfo.ProductVersion).Split('+')[0]
        # Exact label comparison prevents stable/beta and beta.1/beta.10 mixups.
        if ($productVersion -cne $ReleaseVersion.Version) {
            throw ('Payload ProductVersion mismatch in {0}: expected {1}, found {2}.' -f $fileName, $ReleaseVersion.Version, $binaryInfo.ProductVersion)
        }
        $actualFileVersion = ConvertTo-WindowsFileVersion -FileVersion ([string]$binaryInfo.FileVersion)
        if ($actualFileVersion -cne $ReleaseVersion.FileVersion) {
            throw ('Payload FileVersion mismatch in {0}: expected {1}, found {2}.' -f $fileName, $ReleaseVersion.FileVersion, $actualFileVersion)
        }
    }
}

function Assert-StableReleaseSource {
    param(
        [Parameter(Mandatory = $true)][string]$ProjectRoot,
        [Parameter(Mandatory = $true)]$ReleaseVersion,
        [switch]$ExternalPayload,
        [string]$ExpectedCommit
    )
    if ($ReleaseVersion.IsStable -and $ExternalPayload) {
        throw 'A stable installer must build its payload from the retained release branch; -PayloadDirectory is allowed only for beta builds.'
    }
    $git = Get-Command git -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    $branch = $null
    $commit = $null
    $clean = $null
    if ($null -ne $git) {
      try {
        $topLevel = & $git.Source '-C' $ProjectRoot 'rev-parse' '--show-toplevel' 2>$null
        if ($LASTEXITCODE -eq 0) {
            $sourceRoot = [IO.Path]::GetFullPath(([string]$topLevel).Trim()).TrimEnd('\', '/')
            $expectedRoot = [IO.Path]::GetFullPath($ProjectRoot).TrimEnd('\', '/')
            if ($sourceRoot -ine $expectedRoot -and $ReleaseVersion.IsStable) {
                throw 'The stable project directory must be the root of its retained Git checkout.'
            }
            $headOutput = & $git.Source '-C' $ProjectRoot 'rev-parse' '--verify' 'HEAD' 2>$null
            if ($LASTEXITCODE -eq 0) { $commit = ([string]$headOutput).Trim() }
            $branchOutput = & $git.Source '-C' $ProjectRoot 'symbolic-ref' '--quiet' '--short' 'HEAD' 2>$null
            if ($LASTEXITCODE -eq 0) { $branch = ([string]$branchOutput).Trim() }
            # Ignored build artifacts are excluded; new, nonignored source is not.
            $statusOutput = & $git.Source '-C' $ProjectRoot 'status' '--porcelain' '--untracked-files=all' 2>$null
            if ($LASTEXITCODE -eq 0) { $clean = [string]::IsNullOrWhiteSpace(($statusOutput -join "`n")) }
        }
      }
      catch {
        if ($ReleaseVersion.IsStable) { throw }
        # Missing or inaccessible Git metadata never blocks a beta package.
        $branch = $null
        $commit = $null
        $clean = $null
      }
    }
    if ($ReleaseVersion.IsStable) {
        if (-not $commit -or $commit -notmatch '^(?:[a-f0-9]{40}|[a-f0-9]{64})$') {
            throw 'Stable packaging requires a committed Git checkout.'
        }
        if ($branch -cne $ReleaseVersion.ReleaseBranch) {
            throw ('Stable {0} must be packaged from {1} at its current HEAD.' -f $ReleaseVersion.Version, $ReleaseVersion.ReleaseBranch)
        }
        if ($clean -ne $true) { throw 'Stable packaging requires a clean Git checkout, including committed release scripts and source.' }
        if ($ExpectedCommit -and $commit -cne $ExpectedCommit) { throw 'The stable release HEAD changed while packaging.' }
        $branchHead = & $git.Source '-C' $ProjectRoot 'rev-parse' '--verify' ('refs/heads/' + $ReleaseVersion.ReleaseBranch) 2>$null
        if ($LASTEXITCODE -ne 0 -or ([string]$branchHead).Trim() -cne $commit) {
            throw 'The retained stable branch does not point to the source HEAD.'
        }
    }
    [pscustomobject]@{ Branch = $branch; Commit = $commit; Clean = $clean }
}
