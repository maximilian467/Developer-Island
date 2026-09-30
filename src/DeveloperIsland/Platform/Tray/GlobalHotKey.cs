using System.Runtime.InteropServices;
using DeveloperIsland.Core.Diagnostics;

namespace DeveloperIsland.Platform.Tray;

/// <summary>
/// System-wide shortcut (default Ctrl+Alt+Space) that toggles the island. Registered on the hidden
/// host window. If another app already owns the combination, registration fails quietly and is logged.
/// </summary>
internal sealed class GlobalHotKey : IDisposable
{
    private const int Id = 0x4449; // "DI"
    private const uint MOD_ALT = 0x1;
    private const uint MOD_CONTROL = 0x2;
    private const uint MOD_NOREPEAT = 0x4000;
    private const uint VK_SPACE = 0x20;

    private readonly HostWindow _host;
    private bool _registered;

    public GlobalHotKey(HostWindow host)
    {
        _host = host;
        _host.HotKey += OnHotKey;
    }

    public event Action? Pressed;

    public const string DisplayText = "Ctrl + Alt + Space";

    public bool IsRegistered => _registered;

    public void SetEnabled(bool enabled)
    {
        if (enabled && !_registered)
        {
            _registered = RegisterHotKey(_host.Handle, Id, MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, VK_SPACE);
            if (!_registered)
            {
                Log.Warn("hotkey", "Shortcut is in use by another app", new { shortcut = DisplayText, error = Marshal.GetLastWin32Error() });
            }
        }
        else if (!enabled && _registered)
        {
            UnregisterHotKey(_host.Handle, Id);
            _registered = false;
        }
    }

    public void Dispose()
    {
        SetEnabled(false);
        _host.HotKey -= OnHotKey;
    }

    private void OnHotKey(int id)
    {
        if (id == Id)
        {
            Pressed?.Invoke();
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint vk);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
