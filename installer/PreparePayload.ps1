#requires -Version 5.1
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('Install','Record','Uninstall')][string]$Mode,
    [Parameter(Mandatory)][string]$InstallDirectory,
    [Parameter(Mandatory)][string]$Inventory,
    [Parameter(Mandatory)][string]$RegistryKey
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath($InstallDirectory).TrimEnd('\')
if (!$root -or $root -eq [IO.Path]::GetPathRoot($root).TrimEnd('\')) { throw 'Expected an installation directory below a filesystem root.' }
$visited = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)

function Assert-RegularPath([string]$Path) {
    while ($Path -and $visited.Add($Path)) {
        try {
            $attributes = [IO.File]::GetAttributes($Path)
            if ($attributes -band [IO.FileAttributes]::ReparsePoint) { throw "A payload path is a reparse point: $Path" }
        }
        catch [IO.FileNotFoundException] { }
        catch [IO.DirectoryNotFoundException] { }
        $Path = [IO.Path]::GetDirectoryName($Path)
    }
}

# A trailing backslash marks a directory. Inspect all paths before changing any
# files, including parents of a missing file and the installer-owned uninstaller.
function Get-CheckedTarget([string]$Entry) {
    $relative = $Entry
    if ($Entry.EndsWith('\')) { $relative = $Entry.Substring(0, $Entry.Length - 1) }
    if (!$relative -or $Entry -match '[/:*?"<>|\x00-\x1f]' -or
        ($relative.Split('\') | Where-Object { !$_ -or $_ -match '[ .]$|^(?:CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\.|$)' })) {
        throw "Invalid relative payload path: $Entry"
    }
    $target = [IO.Path]::GetFullPath([IO.Path]::Combine($root, $relative))
    if (!$target.StartsWith($root + '\', [StringComparison]::OrdinalIgnoreCase)) { throw "Path outside the installation: $Entry" }
    Assert-RegularPath $target
    return $target
}

$files = [IO.File]::ReadAllLines($Inventory, [Text.Encoding]::UTF8)
if ($Mode -ne 'Record') {
    foreach ($entry in $files) { [void](Get-CheckedTarget $entry) }
    [void](Get-CheckedTarget 'Uninstall.exe')
}
if ($Mode -eq 'Uninstall') { return }

$hive = [Microsoft.Win32.RegistryKey]::OpenBaseKey('CurrentUser', 'Registry64')
try {
    $ownership = $hive.CreateSubKey($RegistryKey)
    try {
        if ($Mode -eq 'Record') {
            # Publish atomically after extraction. An interrupted extraction never
            # claims an unwritten file, and a retry can still use the old list.
            $ownership.SetValue($root, $files, [Microsoft.Win32.RegistryValueKind]::MultiString)
            return
        }
        # Each directory has a separate ownership record, including after a
        # move. First upgrades from a build without this record preserve unknowns.
        $previous = $ownership.GetValue($root)
        if ($null -ne $previous) {
            if ($ownership.GetValueKind($root) -ne [Microsoft.Win32.RegistryValueKind]::MultiString) { throw 'Invalid installed file inventory.' }
            $current = [Collections.Generic.HashSet[string]]::new([string[]]$files, [StringComparer]::OrdinalIgnoreCase)
            $retired = @(foreach ($entry in $previous) {
                if (!$current.Contains($entry)) {
                    [pscustomobject]@{ Path = (Get-CheckedTarget $entry); Directory = $entry.EndsWith('\') }
                }
            })
            foreach ($item in ($retired | Sort-Object { $_.Path.Length } -Descending)) {
                if ($item.Directory) {
                    if ([IO.Directory]::Exists($item.Path) -and [IO.Directory]::GetFileSystemEntries($item.Path).Length -eq 0) {
                        [IO.Directory]::Delete($item.Path, $false)
                    }
                }
                elseif ([IO.File]::Exists($item.Path)) { [IO.File]::Delete($item.Path) }
            }
        }
    }
    finally { $ownership.Dispose() }
}
finally { $hive.Dispose() }
