# Renders the island's soft shadow as a nine-grid texture (Assets/island-shadow.png).
# A rounded rectangle (radius 28) casts an ambient (wide, soft) and a key (tight) shadow layer.
# The runtime stretches the middle of the texture, so the corners stay exact at any size.
param([string]$OutDir = "$PSScriptRoot\..\src\DeveloperIsland\Assets")
Add-Type -AssemblyName System.Drawing

$size = 176; $inset = 40; $radius = 28
$bmp = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.SmoothingMode = 'AntiAlias'
$g.Clear([System.Drawing.Color]::Transparent)

function Add-Layer([int]$spread, [double]$peak, [int]$offsetY) {
    for ($i = $spread; $i -ge 1; $i--) {
        $t = $i / $spread
        # Gaussian-like falloff: each ring adds a little alpha; outer rings contribute least.
        $alpha = [int][Math]::Round(255 * $peak * [Math]::Exp(-4.5 * $t * $t) / $spread * 2.2)
        if ($alpha -lt 1) { continue }
        $x = [single]($inset - $i); $y = [single]($inset - $i + $offsetY)
        $w = [single]($size - 2 * $inset + 2 * $i); $h = $w
        $r = [single]($radius + $i)
        $p = New-Object System.Drawing.Drawing2D.GraphicsPath
        $d = 2 * $r
        $p.AddArc($x, $y, $d, $d, 180, 90); $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
        $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90); $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
        $p.CloseFigure()
        $g.FillPath((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb([Math]::Min(255, $alpha), 0, 0, 0))), $p)
    }
}

Add-Layer 28 0.5 9    # ambient: wide and soft, falls below the island
Add-Layer 8 0.32 2     # key light: tight contact shadow
$g.Dispose()
$bmp.Save("$OutDir\island-shadow.png", [System.Drawing.Imaging.ImageFormat]::Png)
$bmp.Dispose()
Write-Output "Shadow written (inset $inset px)"
