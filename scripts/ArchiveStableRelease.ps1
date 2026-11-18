#requires -Version 5.1
# Run after validating a stable build and before publishing its installers.
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'ReleaseVersion.ps1')
$release = Get-ProjectReleaseVersion (Join-Path $projectRoot 'src\AdbDock.App\AdbDock.App.csproj')
if (-not $release.IsStable) { throw 'Only a stable version can have a stable release archive.' }
$source = Assert-StableReleaseSource -ProjectRoot $projectRoot -ReleaseVersion $release
$commit = $source.Commit
$branchRef = 'refs/heads/' + $release.ReleaseBranch
$tagRef = 'refs/tags/' + $release.ReleaseTag
$pushUrls = @(git -C $projectRoot remote get-url --push --all origin)
if ($LASTEXITCODE -ne 0 -or $pushUrls.Count -ne 1) { throw 'Configure one push destination for origin.' }
$destination = $pushUrls[0]

function Read-ArchiveRefs {
    $refs = @{}
    $lines = @(git -C $projectRoot ls-remote -- $destination $branchRef $tagRef ($tagRef + '^{}'))
    if ($LASTEXITCODE -ne 0) { throw 'Cannot read the archive destination.' }
    foreach ($line in $lines) {
        $hash, $name = $line -split "\s+", 2
        $refs[$name] = $hash
    }
    if ($refs.ContainsKey($tagRef + '^{}')) { $refs[$tagRef] = $refs[$tagRef + '^{}'] }
    return $refs
}

$refs = Read-ArchiveRefs
foreach ($ref in @($branchRef, $tagRef)) {
    if ($refs.ContainsKey($ref) -and $refs[$ref] -cne $commit) {
        throw ("{0} already exists at a different commit. Use a new version." -f $ref)
    }
}
$localTag = git -C $projectRoot rev-parse --verify --quiet ($tagRef + '^{commit}')
if ($LASTEXITCODE -eq 0 -and $localTag -cne $commit) { throw 'A different commit already has this local release tag.' }

$missing = @($branchRef, $tagRef | Where-Object { -not $refs.ContainsKey($_) })
if ($missing.Count -gt 0) {
    # Each empty expected value means the ref MUST still be absent, even if
    # another publisher creates a fast-forwardable ref between the read and push.
    $leases = @($missing | ForEach-Object { '--force-with-lease=' + $_ + ':' })
    $refspecs = @($missing | ForEach-Object { $commit + ':' + $_ })
    git -C $projectRoot push --atomic --no-follow-tags @leases -- $destination @refspecs
    if ($LASTEXITCODE -ne 0) { throw 'The atomic archive push failed. Do not publish the release.' }
}
$verified = Read-ArchiveRefs
if ($verified[$branchRef] -cne $commit -or $verified[$tagRef] -cne $commit) {
    throw 'Remote release branch and tag do not both match the validated commit.'
}
Write-Host ("Verified {0} and {1} on origin at {2}." -f $release.ReleaseBranch, $release.ReleaseTag, $commit)
