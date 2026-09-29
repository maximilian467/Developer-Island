# Captures a region around the top/bottom center of the primary screen for visual inspection.
param(
    [string]$Out = "$env:TEMP\island.png",
    [int]$Width = 700,
    [int]$Height = 420,
    [ValidateSet("top", "bottom", "full", "abs")][string]$Edge = "top",
    [int]$Left = 0,
    [int]$Top = 0
)
Add-Type -AssemblyName System.Drawing, System.Windows.Forms
Add-Type -Namespace W -Name Dpi -MemberDefinition '[DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr v);'
[void][W.Dpi]::SetProcessDpiAwarenessContext([IntPtr]::new(-4))
$b = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
if ($Edge -eq "abs") { $x = $Left; $y = $Top }
elseif ($Edge -eq "full") { $x = $b.X; $y = $b.Y; $Width = $b.Width; $Height = $b.Height }
else {
    $x = $b.X + [int](($b.Width - $Width) / 2)
    $y = if ($Edge -eq 'top') { $b.Y } else { $b.Bottom - $Height }
}
$bmp = New-Object System.Drawing.Bitmap $Width, $Height
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen($x, $y, 0, 0, $bmp.Size)
$bmp.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)
$g.Dispose(); $bmp.Dispose()
Write-Output "$Out ($($b.Width)x$($b.Height))"
