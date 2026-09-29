# Test helper: moves the cursor (physical pixels) and optionally clicks. Used for visual checks only.
param([int]$X, [int]$Y, [switch]$Click, [switch]$Key, [string]$KeyName = "", [int]$DragToX = 0, [int]$DragToY = 0)
Add-Type -Namespace W -Name M -MemberDefinition @'
[DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
[DllImport("user32.dll")] public static extern void mouse_event(uint f, uint dx, uint dy, uint d, IntPtr e);
[DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, IntPtr extra);
[DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr v);
'@
[void][W.M]::SetProcessDpiAwarenessContext([IntPtr]::new(-4))
if (($X -or $Y) -and -not ($DragToX -or $DragToY)) { [void][W.M]::SetCursorPos($X - 2, $Y); Start-Sleep -Milliseconds 20; [W.M]::mouse_event(1, 1, 0, 0, [IntPtr]::Zero); Start-Sleep -Milliseconds 20; [W.M]::mouse_event(1, 1, 0, 0, [IntPtr]::Zero) }
if ($Click) { Start-Sleep -Milliseconds 60; [W.M]::mouse_event(2, 0, 0, 0, [IntPtr]::Zero); Start-Sleep -Milliseconds 40; [W.M]::mouse_event(4, 0, 0, 0, [IntPtr]::Zero) }
if ($KeyName) {
    $vk = @{ Escape = 0x1B; Tab = 0x09; Right = 0x27; Left = 0x25; Space = 0x20; Enter = 0x0D }[$KeyName]
    [W.M]::keybd_event([byte]$vk, 0, 0, [IntPtr]::Zero); Start-Sleep -Milliseconds 30; [W.M]::keybd_event([byte]$vk, 0, 2, [IntPtr]::Zero)
}
# Drag: -DragToX/-DragToY with -X/-Y as the start (physical pixels).
if ($DragToX -or $DragToY) {
    [void][W.M]::SetCursorPos($X - 3, $Y); Start-Sleep -Milliseconds 30; [W.M]::mouse_event(1, 3, 0, 0, [IntPtr]::Zero); Start-Sleep -Milliseconds 120
    [W.M]::mouse_event(2, 0, 0, 0, [IntPtr]::Zero); Start-Sleep -Milliseconds 80
    $steps = 24
    for ($i = 1; $i -le $steps; $i++) {
        [void][W.M]::SetCursorPos([int]($X + ($DragToX - $X) * $i / $steps), [int]($Y + ($DragToY - $Y) * $i / $steps))
        [W.M]::mouse_event(1, 0, 0, 0, [IntPtr]::Zero)
        Start-Sleep -Milliseconds 16
    }
    Start-Sleep -Milliseconds 80
    [W.M]::mouse_event(4, 0, 0, 0, [IntPtr]::Zero)
}
