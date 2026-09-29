# Generates Assets/DeveloperIsland.ico (multi-resolution, PNG frames) and a 256px PNG.
# The mark is deliberately geometric: a near-black rounded square carrying a white capsule.
param([string]$OutDir = "$PSScriptRoot\..\src\DeveloperIsland\Assets")
Add-Type -AssemblyName System.Drawing

function New-RoundedPath([single]$x, [single]$y, [single]$w, [single]$h, [single]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = 2 * $r
    $p.AddArc($x, $y, $d, $d, 180, 90)
    $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    return $p
}

function New-IconBitmap([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.PixelOffsetMode = 'HighQuality'
    $g.Clear([System.Drawing.Color]::Transparent)
    $s = [single]$size
    $inset = [single][Math]::Max(0.5, $s * 0.04)
    $bg = New-RoundedPath $inset $inset ($s - 2 * $inset) ($s - 2 * $inset) ($s * 0.225)
    $g.FillPath((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 10, 10, 11))), $bg)
    $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(70, 255, 255, 255)), ([single][Math]::Max(1, $s / 64))
    $g.DrawPath($pen, $bg)
    # The capsule: 58% wide, 17% tall, sitting in the upper third (top-center, like the island).
    $cw = $s * 0.58; $ch = [single][Math]::Max(3, $s * 0.17)
    $cx = ($s - $cw) / 2; $cy = $s * 0.26
    $pill = New-RoundedPath $cx $cy $cw $ch ($ch / 2)
    $g.FillPath((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 245, 245, 247))), $pill)
    $g.Dispose()
    return $bmp
}

$sizes = 16, 20, 24, 32, 40, 48, 64, 256
$frames = @()
foreach ($sz in $sizes) {
    $bmp = New-IconBitmap $sz
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $frames += , @($sz, $ms.ToArray())
    if ($sz -eq 256) { $bmp.Save("$OutDir\DeveloperIsland-256.png", [System.Drawing.Imaging.ImageFormat]::Png) }
    $bmp.Dispose()
}
$out = New-Object System.IO.MemoryStream
$w = New-Object System.IO.BinaryWriter $out
$w.Write([UInt16]0); $w.Write([UInt16]1); $w.Write([UInt16]$frames.Count)
$offset = 6 + 16 * $frames.Count
foreach ($f in $frames) {
    $sz = $f[0]; $data = $f[1]
    $b = if ($sz -ge 256) { 0 } else { $sz }
    $w.Write([byte]$b); $w.Write([byte]$b); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([UInt16]1); $w.Write([UInt16]32)
    $w.Write([UInt32]$data.Length); $w.Write([UInt32]$offset)
    $offset += $data.Length
}
foreach ($f in $frames) { $w.Write([byte[]]$f[1]) }
$w.Flush()
[System.IO.File]::WriteAllBytes("$OutDir\DeveloperIsland.ico", $out.ToArray())
Write-Output "Icon written to $OutDir"
