# Shows a plain window behind the island for clean screenshots (keeps private windows out of captures).
param([string]$Color = "#2C2C2E", [int]$Height = 720, [int]$Seconds = 120, [switch]$TopMost)
Add-Type -AssemblyName System.Windows.Forms, System.Drawing
Add-Type -Namespace W -Name K -MemberDefinition '[DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr v);'
[void][W.K]::SetProcessDpiAwarenessContext([IntPtr]::new(-4))
$screen = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
$form = New-Object System.Windows.Forms.Form
$form.FormBorderStyle = 'None'; $form.StartPosition = 'Manual'; $form.ShowInTaskbar = $false
$form.Location = New-Object System.Drawing.Point $screen.X, $screen.Y
$form.Size = New-Object System.Drawing.Size $screen.Width, $Height
$form.BackColor = [System.Drawing.ColorTranslator]::FromHtml($Color)
$form.TopMost = [bool]$TopMost
$timer = New-Object System.Windows.Forms.Timer; $timer.Interval = $Seconds * 1000; $timer.Add_Tick({ $form.Close() }); $timer.Start()
[System.Windows.Forms.Application]::Run($form)
