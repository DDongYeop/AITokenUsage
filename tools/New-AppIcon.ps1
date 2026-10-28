<#
.SYNOPSIS
    Draws the AITokenUsage app icon (two usage bars on a rounded tile) and writes a multi-size .ico.
.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File tools/New-AppIcon.ps1 -PreviewPath "$env:TEMP\aitu-icon.png"
#>
[CmdletBinding()]
param(
    [string]$OutPath,
    [string]$PreviewPath
)

# $PSScriptRoot is empty inside a param() default under Windows PowerShell 5.1 -File, so resolve the default here.
if (-not $OutPath) {
    $scriptDir = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Path }
    $OutPath = Join-Path $scriptDir '..\src\AITokenUsage\Assets\AppIcon.ico'
}

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$sizes = @(16, 20, 24, 32, 40, 48, 64, 256)

function New-RoundedRectPath {
    param([single]$X, [single]$Y, [single]$Width, [single]$Height, [single]$Radius)

    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = [single]($Radius * 2)
    $path.AddArc($X, $Y, $d, $d, 180, 90)
    $path.AddArc($X + $Width - $d, $Y, $d, $d, 270, 90)
    $path.AddArc($X + $Width - $d, $Y + $Height - $d, $d, $d, 0, 90)
    $path.AddArc($X, $Y + $Height - $d, $d, $d, 90, 90)
    $path.CloseFigure()
    return $path
}

function Add-Bar {
    param($Graphics, [single]$Scale, [single]$Top, [single]$Fraction, [System.Drawing.Color]$Color)

    $left = [single](3 * $Scale)
    $width = [single](10 * $Scale)
    $height = [single](2.2 * $Scale)
    $y = [single]($Top * $Scale)
    $radius = [single]($height / 2)

    $trackPath = New-RoundedRectPath $left $y $width $height $radius
    $trackBrush = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(64, 255, 255, 255))
    try { $Graphics.FillPath($trackBrush, $trackPath) } finally { $trackBrush.Dispose(); $trackPath.Dispose() }

    $fillWidth = [single][Math]::Max($height, $width * $Fraction)
    $fillPath = New-RoundedRectPath $left $y $fillWidth $height $radius
    $fillBrush = New-Object System.Drawing.SolidBrush $Color
    try { $Graphics.FillPath($fillBrush, $fillPath) } finally { $fillBrush.Dispose(); $fillPath.Dispose() }
}

function New-IconBitmap {
    param([int]$Size)

    $bitmap = New-Object System.Drawing.Bitmap $Size, $Size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    try {
        $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $graphics.Clear([System.Drawing.Color]::Transparent)

        $scale = [single]($Size / 16.0)
        $tilePath = New-RoundedRectPath ([single](0.5 * $scale)) ([single](0.5 * $scale)) ([single](15 * $scale)) ([single](15 * $scale)) ([single](3.5 * $scale))
        $tileBrush = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 0x24, 0x24, 0x28))
        try { $graphics.FillPath($tileBrush, $tilePath) } finally { $tileBrush.Dispose(); $tilePath.Dispose() }

        Add-Bar $graphics $scale 4.6 0.62 ([System.Drawing.Color]::FromArgb(255, 0xD9, 0x77, 0x57))
        Add-Bar $graphics $scale 9.2 0.38 ([System.Drawing.Color]::FromArgb(255, 0x10, 0xA3, 0x7F))
    }
    finally {
        $graphics.Dispose()
    }

    return $bitmap
}

function Get-PngBytes {
    param([System.Drawing.Bitmap]$Bitmap)

    $stream = New-Object System.IO.MemoryStream
    try {
        $Bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
        return , $stream.ToArray()
    }
    finally {
        $stream.Dispose()
    }
}

function Get-DibBytes {
    param([System.Drawing.Bitmap]$Bitmap)

    $size = $Bitmap.Width
    $rect = New-Object System.Drawing.Rectangle 0, 0, $size, $size
    $data = $Bitmap.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::ReadOnly, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    try {
        $stride = $data.Stride
        $pixels = New-Object byte[] ($stride * $size)
        [System.Runtime.InteropServices.Marshal]::Copy($data.Scan0, $pixels, 0, $pixels.Length)
    }
    finally {
        $Bitmap.UnlockBits($data)
    }

    $maskStride = [int]([Math]::Floor(($size + 31) / 32) * 4)
    $stream = New-Object System.IO.MemoryStream
    $writer = New-Object System.IO.BinaryWriter $stream
    try {
        # BITMAPINFOHEADER: height is doubled (XOR image + AND mask).
        $writer.Write([int32]40)
        $writer.Write([int32]$size)
        $writer.Write([int32]($size * 2))
        $writer.Write([int16]1)
        $writer.Write([int16]32)
        $writer.Write([int32]0)
        $writer.Write([int32]($size * $size * 4 + $maskStride * $size))
        $writer.Write([int32]0)
        $writer.Write([int32]0)
        $writer.Write([int32]0)
        $writer.Write([int32]0)

        # XOR image: BGRA rows, bottom-up.
        for ($row = $size - 1; $row -ge 0; $row--) {
            $writer.Write($pixels, $row * $stride, $size * 4)
        }

        # AND mask: all zero, the alpha channel decides transparency.
        $writer.Write((New-Object byte[] ($maskStride * $size)))
        $writer.Flush()
        return , $stream.ToArray()
    }
    finally {
        $writer.Dispose()
    }
}

$images = foreach ($size in $sizes) {
    $bitmap = New-IconBitmap $size
    try {
        if ($size -eq 256 -and $PreviewPath) {
            $previewFull = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($PreviewPath)
            $bitmap.Save($previewFull, [System.Drawing.Imaging.ImageFormat]::Png)
        }

        $bytes = if ($size -ge 256) { Get-PngBytes $bitmap } else { Get-DibBytes $bitmap }
        [pscustomobject]@{ Size = $size; Bytes = [byte[]]$bytes }
    }
    finally {
        $bitmap.Dispose()
    }
}

$outFull = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutPath)
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $outFull) | Out-Null

$file = [System.IO.File]::Create($outFull)
$writer = New-Object System.IO.BinaryWriter $file
try {
    # ICONDIR
    $writer.Write([int16]0)
    $writer.Write([int16]1)
    $writer.Write([int16]$images.Count)

    # ICONDIRENTRY x N
    $offset = 6 + 16 * $images.Count
    foreach ($image in $images) {
        $dimension = if ($image.Size -ge 256) { 0 } else { $image.Size }
        $writer.Write([byte]$dimension)
        $writer.Write([byte]$dimension)
        $writer.Write([byte]0)
        $writer.Write([byte]0)
        $writer.Write([int16]1)
        $writer.Write([int16]32)
        $writer.Write([int32]$image.Bytes.Length)
        $writer.Write([int32]$offset)
        $offset += $image.Bytes.Length
    }

    foreach ($image in $images) {
        $writer.Write($image.Bytes)
    }
}
finally {
    $writer.Dispose()
}

Write-Host "Wrote $outFull ($($images.Count) images: $($sizes -join ', '))"
