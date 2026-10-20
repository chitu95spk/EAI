# Generates EasyAI.ico (16/24/32/48/64/128/256, PNG frames) and EasyAI.png (256) from the supplied logo.
# Run from the project root (the lead runs it once; output goes to EasyLabel\src\EasyAI.Wpf\Assets):
#   powershell -NoProfile -ExecutionPolicy Bypass -File "tmp\EAI\EAI-UI\make-icon.ps1"
# The logo is navy on a white background: white connected to the image border becomes transparent (rounded corners).
param(
    [string]$Logo = (Join-Path $PSScriptRoot 'logo.png'),
    [string]$OutDir = (Join-Path $PSScriptRoot '..\..\..\EasyLabel\src\EasyAI.Wpf\Assets')
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$OutDir = [System.IO.Path]::GetFullPath($OutDir)
$root = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\..'))
if (-not $OutDir.StartsWith($root, [System.StringComparison]::OrdinalIgnoreCase)) { throw "Output folder is outside the project: $OutDir" }
[System.IO.Directory]::CreateDirectory($OutDir) | Out-Null

$loaded = [System.Drawing.Bitmap]::FromFile($Logo)
$w = $loaded.Width
$h = $loaded.Height
$src = New-Object System.Drawing.Bitmap $w, $h, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
$g = [System.Drawing.Graphics]::FromImage($src)
$g.DrawImage($loaded, 0, 0, $w, $h)
$g.Dispose()
$loaded.Dispose()

# Flood fill from the border: near-white pixels reachable from the edge become transparent.
function IsNearWhite([System.Drawing.Color]$c) { return ($c.R -gt 232 -and $c.G -gt 232 -and $c.B -gt 232) }
$visited = New-Object 'bool[]' ($w * $h)
$queue = New-Object System.Collections.Generic.Queue[int]
for ($x = 0; $x -lt $w; $x++) { $queue.Enqueue($x); $queue.Enqueue(($h - 1) * $w + $x) }
for ($y = 0; $y -lt $h; $y++) { $queue.Enqueue($y * $w); $queue.Enqueue($y * $w + $w - 1) }
while ($queue.Count -gt 0) {
    $i = $queue.Dequeue()
    if ($visited[$i]) { continue }
    $visited[$i] = $true
    $px = $i % $w
    $py = [int][math]::Floor($i / $w)
    if (-not (IsNearWhite ($src.GetPixel($px, $py)))) { continue }
    $src.SetPixel($px, $py, [System.Drawing.Color]::FromArgb(0, 255, 255, 255))
    if ($px -gt 0) { $queue.Enqueue($i - 1) }
    if ($px -lt $w - 1) { $queue.Enqueue($i + 1) }
    if ($py -gt 0) { $queue.Enqueue($i - $w) }
    if ($py -lt $h - 1) { $queue.Enqueue($i + $w) }
}

# Bounding box of the opaque part, padded to a square.
$minX = $w; $minY = $h; $maxX = 0; $maxY = 0
for ($y = 0; $y -lt $h; $y++) {
    for ($x = 0; $x -lt $w; $x++) {
        if ($src.GetPixel($x, $y).A -gt 0) {
            if ($x -lt $minX) { $minX = $x }
            if ($x -gt $maxX) { $maxX = $x }
            if ($y -lt $minY) { $minY = $y }
            if ($y -gt $maxY) { $maxY = $y }
        }
    }
}
$side = [math]::Max($maxX - $minX + 1, $maxY - $minY + 1)
$cx = ($minX + $maxX + 1) / 2.0
$cy = ($minY + $maxY + 1) / 2.0
$square = New-Object System.Drawing.Rectangle ([int][math]::Round($cx - $side / 2.0)), ([int][math]::Round($cy - $side / 2.0)), $side, $side

function RenderPng([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $gr = [System.Drawing.Graphics]::FromImage($bmp)
    $gr.Clear([System.Drawing.Color]::Transparent)
    $gr.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $gr.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
    $gr.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $gr.DrawImage($src, (New-Object System.Drawing.Rectangle 0, 0, $size, $size), $square, [System.Drawing.GraphicsUnit]::Pixel)
    $gr.Dispose()
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    return , $ms.ToArray()
}

$sizes = 16, 24, 32, 48, 64, 128, 256
$frames = @()
foreach ($s in $sizes) { $frames += , (RenderPng $s) }

# ICO container with PNG frames.
$ico = New-Object System.IO.MemoryStream
$bw = New-Object System.IO.BinaryWriter $ico
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($k = 0; $k -lt $sizes.Count; $k++) {
    $s = $sizes[$k]
    $dim = if ($s -ge 256) { 0 } else { $s }
    $bw.Write([byte]$dim); $bw.Write([byte]$dim); $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([uint16]1); $bw.Write([uint16]32)
    $bw.Write([uint32]$frames[$k].Length); $bw.Write([uint32]$offset)
    $offset += $frames[$k].Length
}
foreach ($f in $frames) { $bw.Write($f) }
$bw.Flush()
[System.IO.File]::WriteAllBytes((Join-Path $OutDir 'EasyAI.ico'), $ico.ToArray())
[System.IO.File]::WriteAllBytes((Join-Path $OutDir 'EasyAI.png'), $frames[$sizes.Count - 1])
$src.Dispose()
Write-Host "Wrote $(Join-Path $OutDir 'EasyAI.ico') and EasyAI.png"
