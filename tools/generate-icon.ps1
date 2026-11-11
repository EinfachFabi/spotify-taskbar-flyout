<#
.SYNOPSIS
    Generates the app icon (Resources\app.ico) in multiple sizes.
    Green circle with a white play symbol and equalizer bars.
.EXAMPLE
    pwsh ./tools/generate-icon.ps1
#>
param(
    [string]$OutFile = (Join-Path $PSScriptRoot '..\src\SpotifyTaskbarFlyout\Resources\app.ico')
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

function New-IconPng([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.PixelOffsetMode = 'HighQuality'
    $g.Clear([System.Drawing.Color]::Transparent)

    $green = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 0x1D, 0xB9, 0x54))
    $white = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::White)

    $m = [Math]::Max(0.5, $size * 0.02)
    $g.FillEllipse($green, $m, $m, $size - 2 * $m, $size - 2 * $m)

    # Play triangle (left)
    $s = $size
    $tri = [System.Drawing.PointF[]]@(
        (New-Object System.Drawing.PointF ($s * 0.27), ($s * 0.30)),
        (New-Object System.Drawing.PointF ($s * 0.53), ($s * 0.50)),
        (New-Object System.Drawing.PointF ($s * 0.27), ($s * 0.70))
    )
    $g.FillPolygon($white, $tri)

    # Equalizer bars (right)
    $barW = $s * 0.075
    $bars = @(@(0.60, 0.40), @(0.71, 0.30), @(0.82, 0.44))
    foreach ($b in $bars) {
        $x = $s * $b[0] - $barW / 2
        $top = $s * $b[1]
        $h = $s * 0.70 - $top
        $g.FillRectangle($white, [single]$x, [single]$top, [single]$barW, [single]$h)
    }

    $g.Dispose()
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    return , $ms.ToArray()
}

$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$images = foreach ($size in $sizes) { , (New-IconPng $size) }

New-Item -ItemType Directory -Force -Path (Split-Path $OutFile) | Out-Null
$fs = [System.IO.File]::Create($OutFile)
$w = New-Object System.IO.BinaryWriter $fs

# ICONDIR
$w.Write([UInt16]0); $w.Write([UInt16]1); $w.Write([UInt16]$sizes.Count)

$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $dim = if ($sizes[$i] -ge 256) { 0 } else { $sizes[$i] }
    $w.Write([byte]$dim); $w.Write([byte]$dim)
    $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([UInt16]1); $w.Write([UInt16]32)
    $w.Write([UInt32]$images[$i].Length); $w.Write([UInt32]$offset)
    $offset += $images[$i].Length
}
foreach ($img in $images) { $w.Write([byte[]]$img) }

$w.Dispose(); $fs.Dispose()
Write-Host "Icon written: $((Resolve-Path $OutFile).Path)"
