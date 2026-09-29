# Generates three abstract album covers used only by --demo (Assets/demo-art-*.png).
param([string]$OutDir = "$PSScriptRoot\..\src\DeveloperIsland\Assets")
Add-Type -AssemblyName System.Drawing

function New-Cover([string]$path, [int[]]$a, [int[]]$b, [int[]]$orb, [single]$orbX, [single]$orbY) {
    $size = 320
    $bmp = New-Object System.Drawing.Bitmap $size, $size
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $rect = New-Object System.Drawing.Rectangle 0, 0, $size, $size
    $bg = New-Object System.Drawing.Drawing2D.LinearGradientBrush $rect, ([System.Drawing.Color]::FromArgb($a[0], $a[1], $a[2])), ([System.Drawing.Color]::FromArgb($b[0], $b[1], $b[2])), 55
    $g.FillRectangle($bg, $rect)
    $d = 230
    $path2 = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path2.AddEllipse([single]($orbX - $d / 2), [single]($orbY - $d / 2), [single]$d, [single]$d)
    $orbBrush = New-Object System.Drawing.Drawing2D.PathGradientBrush $path2
    $orbBrush.CenterColor = [System.Drawing.Color]::FromArgb(220, $orb[0], $orb[1], $orb[2])
    $orbBrush.SurroundColors = @([System.Drawing.Color]::FromArgb(0, $orb[0], $orb[1], $orb[2]))
    $g.FillPath($orbBrush, $path2)
    $g.Dispose()
    $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
}

New-Cover "$OutDir\demo-art-1.png" @(18, 32, 64) @(10, 12, 20) @(90, 170, 255) 200 110
New-Cover "$OutDir\demo-art-2.png" @(70, 28, 40) @(20, 12, 18) @(255, 150, 90) 110 210
New-Cover "$OutDir\demo-art-3.png" @(34, 36, 40) @(12, 12, 14) @(200, 205, 215) 160 160
Write-Output "Demo art written to $OutDir"
