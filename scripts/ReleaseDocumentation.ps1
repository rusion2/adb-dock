#requires -Version 5.1

function Get-LegacyReleaseDocumentationPaths {
    param([Parameter(Mandatory = $true)][string]$PayloadDirectory)

    # Root documents shipped with v0.1.3-beta.3, with LF or CRLF line endings.
    $legacyHashes = @{
        'VERSIONING.md' = @(
            '1BFCB6BD4D2121C22FBE8AF6CCD1486CB8DE7B1E64CBDAD7222912CE587458F1'
            '993F0268E4E862883269EB535F92373BCAC39469694FB2E4BA2CB81610C6DE5A'
        )
        '需求说明.md' = @(
            '5896181F6ED3DCA5DDA9209A147419AC6880B26411122C911C7014BC1EC11A37'
            'D0BABA24DAAC7647E34D127DBFAFBF78A099B2F82B8408B725EECE4EB5AC3400'
        )
        '开发进度.md' = @(
            '1864D9DF64FED7E0138BB78E710373B38BA204F109319A681A5CD29F1F1AFDA2'
            'B2BD62F93416DCDCA54B28FA3CCB185B7A824486E63863DB7C7C4FDE069CD736'
        )
    }
    foreach ($relativePath in $legacyHashes.Keys) {
        $path = Join-Path $PayloadDirectory $relativePath
        if ((Test-Path -LiteralPath $path -PathType Leaf) -and
            (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -in $legacyHashes[$relativePath]) {
            $relativePath
        }
    }
}

function Copy-ReleaseDocumentation {
    param(
        [Parameter(Mandatory = $true)][string]$ProjectRoot,
        [Parameter(Mandatory = $true)][string]$DestinationDirectory
    )

    $relativePaths = @(
        'README.md'
        'README.en.md'
        'docs\requirements.md'
        'docs\development.md'
        'docs\versioning.md'
        'docs\archive\development-history.md'
        'src\AdbDock.App\Assets\AppIcon.svg'
    )
    $imageDirectory = 'docs\images'
    $relativePaths += Get-ChildItem -LiteralPath (Join-Path $ProjectRoot $imageDirectory) -File | ForEach-Object {
        Join-Path $imageDirectory $_.Name
    }

    foreach ($relativePath in $relativePaths) {
        $source = Join-Path $ProjectRoot $relativePath
        $destination = Join-Path $DestinationDirectory $relativePath
        if (-not (Test-Path -LiteralPath $source -PathType Leaf)) {
            throw "Missing release documentation file: $source"
        }
        if (Test-Path -LiteralPath $destination -PathType Container) {
            throw "A payload directory conflicts with a release documentation file: $destination"
        }
        New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($destination)) -Force | Out-Null
        Copy-Item -LiteralPath $source -Destination $destination -Force
    }
}
