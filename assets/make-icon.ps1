<#
  Generates assets/islet.ico (16-256 px) and assets/icon.png from code — original artwork, no third-party assets.
  Run: powershell -ExecutionPolicy Bypass -File assets/make-icon.ps1
#>
Add-Type -AssemblyName System.Drawing
$dir = Split-Path -Parent $MyInvocation.MyCommand.Path

function New-RoundedRect([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = 2 * $r
    $p.AddArc($x, $y, $d, $d, 180, 90); $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90); $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure(); return $p
}

function New-Icon([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'; $g.Clear([System.Drawing.Color]::Transparent)
    $s = $size / 256.0

    # Tile: blue -> violet -> coral gradient
    $tile = New-RoundedRect (8*$s) (8*$s) (240*$s) (240*$s) (56*$s)
    $grad = New-Object System.Drawing.Drawing2D.LinearGradientBrush((New-Object System.Drawing.PointF 0,0), (New-Object System.Drawing.PointF $size,$size), [System.Drawing.Color]::FromArgb(58,123,255), [System.Drawing.Color]::FromArgb(255,110,90))
    $blend = New-Object System.Drawing.Drawing2D.ColorBlend 3
    $blend.Colors = @([System.Drawing.Color]::FromArgb(58,123,255), [System.Drawing.Color]::FromArgb(142,91,255), [System.Drawing.Color]::FromArgb(255,110,90))
    $blend.Positions = @([float]0, [float]0.55, [float]1)
    $grad.InterpolationColors = $blend
    $g.FillPath($grad, $tile)

    # The island: a black notch hanging from the top edge of an inner "screen"
    $island = New-RoundedRect (44*$s) (60*$s) (168*$s) (78*$s) (39*$s)
    $g.FillPath([System.Drawing.Brushes]::Black, $island)

    # Blip's eyes
    $eye = [System.Drawing.Brushes]::White
    $g.FillPath($eye, (New-RoundedRect (98*$s) (80*$s) (18*$s) (38*$s) (9*$s)))
    $g.FillPath($eye, (New-RoundedRect (140*$s) (80*$s) (18*$s) (38*$s) (9*$s)))

    # A bead dropping off the island
    if ($size -ge 32) {
        $bead = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(235, 48, 209, 88))
        $g.FillEllipse($bead, 116*$s, 160*$s, 24*$s, 28*$s)
    }
    $g.Dispose()
    return $bmp
}

$sizes = 16, 24, 32, 48, 64, 128, 256
$pngs = foreach ($n in $sizes) {
    $bmp = New-Icon $n; $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    if ($n -eq 256) { $bmp.Save((Join-Path $dir 'icon.png'), [System.Drawing.Imaging.ImageFormat]::Png) }
    $bmp.Dispose(); , $ms.ToArray()
}

# ICO container with PNG-compressed entries (supported since Windows Vista)
$out = New-Object System.IO.MemoryStream
$w = New-Object System.IO.BinaryWriter $out
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $n = $sizes[$i]; $b = if ($n -ge 256) { 0 } else { $n }
    $w.Write([byte]$b); $w.Write([byte]$b); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32); $w.Write([uint32]$pngs[$i].Length); $w.Write([uint32]$offset)
    $offset += $pngs[$i].Length
}
foreach ($p in $pngs) { $w.Write([byte[]]$p) }
[System.IO.File]::WriteAllBytes((Join-Path $dir 'islet.ico'), $out.ToArray())
"wrote islet.ico and icon.png"
