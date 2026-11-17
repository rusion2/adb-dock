#requires -Version 5.1
<#
.SYNOPSIS
Render the editable SVG icon into the app's multi-resolution Windows ICO.
.EXAMPLE
powershell.exe -NoProfile -STA -ExecutionPolicy Bypass -File .\scripts\GenerateIcon.ps1
#>
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationCore, WindowsBase

$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$assetRoot = Join-Path $projectRoot 'src\AdbDock.App\Assets'
$source = Join-Path $assetRoot 'AppIcon.svg'
$output = Join-Path $assetRoot 'App.ico'
$previewDirectory = Join-Path $projectRoot 'artifacts\app-icon'
New-Item -ItemType Directory -Path $previewDirectory -Force | Out-Null
[xml]$svg = Get-Content -LiteralPath $source -Raw

function Get-SvgNumber {
    param([Xml.XmlElement]$Element, [string]$Name, [double]$Default = 0)
    $value = $Element.GetAttribute($Name)
    if (-not $value) { return $Default }
    return [double]::Parse($value, [Globalization.CultureInfo]::InvariantCulture)
}

function Get-SvgBrush {
    param([string]$Value)
    if (-not $Value -or $Value -eq 'none') { return $null }
    $brush = New-Object Windows.Media.SolidColorBrush ([Windows.Media.ColorConverter]::ConvertFromString($Value))
    $brush.Freeze()
    return $brush
}

function Get-IconDrawing {
    $drawing = New-Object Windows.Media.DrawingGroup
    $context = $drawing.Open()
    try {
        foreach ($element in $svg.DocumentElement.ChildNodes) {
            if ($element -isnot [Xml.XmlElement] -or $element.LocalName -eq 'title') { continue }
            $fill = Get-SvgBrush $element.GetAttribute('fill')
            $stroke = Get-SvgBrush $element.GetAttribute('stroke')
            $pen = $null
            if ($null -ne $stroke) {
                $pen = New-Object Windows.Media.Pen $stroke, (Get-SvgNumber $element 'stroke-width' 1)
                if ($element.GetAttribute('stroke-linecap') -eq 'round') {
                    $pen.StartLineCap = [Windows.Media.PenLineCap]::Round
                    $pen.EndLineCap = [Windows.Media.PenLineCap]::Round
                }
                $pen.LineJoin = [Windows.Media.PenLineJoin]::Round
            }
            switch ($element.LocalName) {
                'rect' {
                    $bounds = New-Object Windows.Rect (Get-SvgNumber $element 'x'), (Get-SvgNumber $element 'y'), (Get-SvgNumber $element 'width'), (Get-SvgNumber $element 'height')
                    $radius = Get-SvgNumber $element 'rx'
                    $context.DrawRoundedRectangle($fill, $pen, $bounds, $radius, $radius)
                }
                'path' { $context.DrawGeometry($fill, $pen, [Windows.Media.Geometry]::Parse($element.GetAttribute('d'))) }
                'circle' {
                    $center = New-Object Windows.Point (Get-SvgNumber $element 'cx'), (Get-SvgNumber $element 'cy')
                    $radius = Get-SvgNumber $element 'r'
                    $context.DrawEllipse($fill, $pen, $center, $radius, $radius)
                }
                default { throw ('Unsupported icon SVG element: ' + $element.LocalName) }
            }
        }
    }
    finally { $context.Close() }
    $drawing.Freeze()
    return $drawing
}

function Get-FramePng {
    param([Windows.Media.DrawingGroup]$Drawing, [int]$Size)
    $visual = New-Object Windows.Media.DrawingVisual
    $context = $visual.RenderOpen()
    try {
        $context.PushTransform((New-Object Windows.Media.ScaleTransform ($Size / 64.0), ($Size / 64.0)))
        $context.DrawDrawing($Drawing)
        $context.Pop()
    }
    finally { $context.Close() }
    $bitmap = New-Object Windows.Media.Imaging.RenderTargetBitmap $Size, $Size, 96, 96, ([Windows.Media.PixelFormats]::Pbgra32)
    $bitmap.Render($visual)
    $encoder = New-Object Windows.Media.Imaging.PngBitmapEncoder
    $encoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
    $buffer = New-Object IO.MemoryStream
    try { $encoder.Save($buffer); return ,$buffer.ToArray() }
    finally { $buffer.Dispose() }
}

$drawing = Get-IconDrawing
$sizes = @(16, 20, 24, 32, 40, 48, 64, 128, 256)
$frames = @($sizes | ForEach-Object { ,(Get-FramePng $drawing $_) })
$stream = [IO.File]::Create($output)
$writer = New-Object IO.BinaryWriter $stream
try {
    $writer.Write([uint16]0)
    $writer.Write([uint16]1)
    $writer.Write([uint16]$sizes.Count)
    $offset = 6 + 16 * $sizes.Count
    for ($index = 0; $index -lt $sizes.Count; $index++) {
        $sizeByte = if ($sizes[$index] -eq 256) { 0 } else { $sizes[$index] }
        $writer.Write([byte]$sizeByte)
        $writer.Write([byte]$sizeByte)
        $writer.Write([byte]0)
        $writer.Write([byte]0)
        $writer.Write([uint16]1)
        $writer.Write([uint16]32)
        $writer.Write([uint32]$frames[$index].Length)
        $writer.Write([uint32]$offset)
        $offset += $frames[$index].Length
    }
    foreach ($frame in $frames) { $writer.Write([byte[]]$frame) }
}
finally { $writer.Dispose(); $stream.Dispose() }
foreach ($size in @(16, 32, 64, 256)) {
    [IO.File]::WriteAllBytes((Join-Path $previewDirectory ('AppIcon-' + $size + '.png')), (Get-FramePng $drawing $size))
}
Write-Output ('Icon: ' + $output)
Write-Output ('Frames: ' + ($sizes -join ', '))
Write-Output ('Previews: ' + $previewDirectory)
