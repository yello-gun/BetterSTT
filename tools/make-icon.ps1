# Generates assets\BetterTTS.ico (PNG-compressed entries at 16-256 px).
Add-Type -AssemblyName System.Drawing
$root = Split-Path $PSScriptRoot -Parent
$out = Join-Path $root 'assets\BetterTTS.ico'
New-Item -ItemType Directory -Force (Split-Path $out) | Out-Null

function New-RoundedRect([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $r * 2
    $p.AddArc($x, $y, $d, $d, 180, 90); $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90); $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure(); $p
}

$sizes = 16, 24, 32, 48, 64, 128, 256
$pngs = foreach ($size in $sizes) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $u = $size / 32.0
    $g.FillEllipse((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(0x4B, 0x8B, 0xF5))), 1 * $u, 1 * $u, 30 * $u, 30 * $u)
    $white = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::White)
    $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::White), (2.4 * $u)
    $pen.StartCap = 'Round'; $pen.EndCap = 'Round'
    $g.FillPath($white, (New-RoundedRect (12.5 * $u) (6 * $u) (7 * $u) (13 * $u) (3.5 * $u)))
    $g.DrawArc($pen, 9 * $u, 9.5 * $u, 14 * $u, 13 * $u, 0, 180)
    $g.DrawLine($pen, 16 * $u, 22.5 * $u, 16 * $u, 26 * $u)
    $g.Dispose()
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    , $ms.ToArray()
}

$fs = [System.IO.File]::Create($out)
$w = New-Object System.IO.BinaryWriter $fs
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]; $len = $pngs[$i].Length
    $w.Write([byte]($s % 256)); $w.Write([byte]($s % 256)); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32); $w.Write([uint32]$len); $w.Write([uint32]$offset)
    $offset += $len
}
foreach ($p in $pngs) { $w.Write($p) }
$w.Close()
Write-Output "Wrote $out"
