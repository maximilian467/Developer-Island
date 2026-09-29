# Captures a burst of frames around the top-center of the primary screen (motion inspection).
param([int]$Frames = 14, [int]$IntervalMs = 28, [int]$Width = 620, [int]$Height = 420, [string]$Prefix = "$env:TEMP\burst")
Add-Type -AssemblyName System.Drawing, System.Windows.Forms
Add-Type -Namespace W -Name B -MemberDefinition '[DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr v);'
[void][W.B]::SetProcessDpiAwarenessContext([IntPtr]::new(-4))
$b = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
$x = $b.X + [int](($b.Width - $Width) / 2)
$bitmaps = @()
$sw = [Diagnostics.Stopwatch]::StartNew()
for ($i = 0; $i -lt $Frames; $i++) {
    $bmp = New-Object System.Drawing.Bitmap $Width, $Height
    $g = [System.Drawing.Graphics]::FromImage($bmp); $g.CopyFromScreen($x, $b.Y, 0, 0, $bmp.Size); $g.Dispose()
    $bitmaps += , @($sw.ElapsedMilliseconds, $bmp)
    Start-Sleep -Milliseconds $IntervalMs
}
# Contact sheet: frames stacked in a grid (2 columns).
$cols = 2; $rows = [Math]::Ceiling($bitmaps.Count / $cols); $scale = 0.5
$sheet = New-Object System.Drawing.Bitmap ([int]($Width * $scale * $cols)), ([int]($Height * $scale * $rows))
$sg = [System.Drawing.Graphics]::FromImage($sheet)
for ($i = 0; $i -lt $bitmaps.Count; $i++) {
    $c = $i % $cols; $r = [Math]::Floor($i / $cols)
    $sg.DrawImage($bitmaps[$i][1], [int]($c * $Width * $scale), [int]($r * $Height * $scale), [int]($Width * $scale), [int]($Height * $scale))
    $sg.DrawString("$($bitmaps[$i][0]) ms", (New-Object System.Drawing.Font "Segoe UI", 9), [System.Drawing.Brushes]::Yellow, [single]($c * $Width * $scale + 4), [single]($r * $Height * $scale + 4))
}
$sheet.Save("$Prefix-sheet.png", [System.Drawing.Imaging.ImageFormat]::Png)
Write-Output "$Prefix-sheet.png"
