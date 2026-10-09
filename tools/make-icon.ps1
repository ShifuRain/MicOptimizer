# Draws the Mic Optimizer app icon (ink tile, vermilion waveform bars) and writes a multi-size .ico.
# Usage: powershell -File tools\make-icon.ps1 [-Out src\MicOptimizer.App\Assets\app.ico]
param([string]$Out = "$PSScriptRoot\..\src\MicOptimizer.App\Assets\app.ico")

Add-Type -AssemblyName System.Drawing

function New-IconBitmap([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.Clear([System.Drawing.Color]::Transparent)

    $ink    = [System.Drawing.ColorTranslator]::FromHtml('#1E2024')
    $accent = [System.Drawing.ColorTranslator]::FromHtml('#E4572E')

    function RoundRect($x, $y, $w, $h, $r) {
        $p = New-Object System.Drawing.Drawing2D.GraphicsPath
        $d = $r * 2
        $p.AddArc($x, $y, $d, $d, 180, 90); $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
        $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90); $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
        $p.CloseFigure(); $p
    }

    $g.FillPath((New-Object System.Drawing.SolidBrush $ink), (RoundRect 0 0 $size $size ($size * 0.22)))

    # Five bars, tallest in the middle: a voice waveform.
    $heights = 0.26, 0.5, 0.76, 0.46, 0.24
    $barW = $size * 0.1
    $gap = $size * 0.055
    $total = $heights.Count * $barW + ($heights.Count - 1) * $gap
    $x = ($size - $total) / 2
    $brush = New-Object System.Drawing.SolidBrush $accent
    foreach ($h in $heights) {
        $bh = $size * $h
        $g.FillPath($brush, (RoundRect $x (($size - $bh) / 2) $barW $bh ($barW / 2)))
        $x += $barW + $gap
    }
    $g.Dispose()
    $bmp
}

$sizes = 16, 24, 32, 48, 64, 128, 256
$pngs = foreach ($s in $sizes) {
    $bmp = New-IconBitmap $s
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    , $ms.ToArray()
}

New-Item -ItemType Directory -Force (Split-Path $Out) | Out-Null
$fs = [System.IO.File]::Create($Out)
$w = New-Object System.IO.BinaryWriter $fs
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]
    $w.Write([byte]($(if ($s -ge 256) { 0 } else { $s }))); $w.Write([byte]($(if ($s -ge 256) { 0 } else { $s })))
    $w.Write([byte]0); $w.Write([byte]0); $w.Write([uint16]1); $w.Write([uint16]32)
    $w.Write([uint32]$pngs[$i].Length); $w.Write([uint32]$offset)
    $offset += $pngs[$i].Length
}
foreach ($p in $pngs) { $w.Write($p) }
$w.Dispose(); $fs.Dispose()
"wrote $((Resolve-Path $Out).Path) ($((Get-Item $Out).Length) bytes, sizes: $($sizes -join ', '))"
