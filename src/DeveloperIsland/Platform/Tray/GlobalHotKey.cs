using System.Runtime.InteropServices;
using DeveloperIsland.Core.Diagnostics;
using DeveloperIsland.Core.Settings;

namespace DeveloperIsland.Platform.Tray;

/// <summary>
/// System-wide shortcut (default Ctrl+Alt+Space) that toggles the island. Registered on the hidden
/// host window. If another app already owns the combination, registration fails, is logged and
/// reported through <see cref="IsInUse"/> so Settings can suggest another preset.
/// </summary>
internal sealed class GlobalHotKey : IDisposable
{
    private const int Id = 0x4449; // "DI"
    private const uint MOD_ALT = 0x1;
    private const uint MOD_CONTROL = 0x2;
    private const uint MOD_SHIFT = 0x4;
    private const uint MOD_NOREPEAT = 0x4000;

    private readonly HostWindow _host;
    private bool _registered;
    private ShortcutGesture _gesture = ShortcutGesture.Default;

    public GlobalHotKey(HostWindow host)
    {
        _host = host;
        _host.HotKey += OnHotKey;
    }

    public event Action? Pressed;

    /// <summary>Enabled, but another app owns the combination.</summary>
    public bool IsInUse { get; private set; }

    public void Apply(bool enabled, ShortcutGesture gesture)
    {
        if (_registered && (!enabled || gesture != _gesture))
        {
            UnregisterHotKey(_host.Handle, Id);
            _registered = false;
        }

        _gesture = gesture;
        IsInUse = false;
        if (enabled && !_registered)
        {
            var modifiers = MOD_NOREPEAT | (gesture.Ctrl ? MOD_CONTROL : 0) | (gesture.Alt ? MOD_ALT : 0) | (gesture.Shift ? MOD_SHIFT : 0);
            _registered = RegisterHotKey(_host.Handle, Id, modifiers, gesture.VirtualKey);
            IsInUse = !_registered;
            if (!_registered)
            {
                Log.Warn("hotkey", "Shortcut is in use by another app", new { shortcut = gesture.Text, error = Marshal.GetLastWin32Error() });
            }
        }
    }

    public void Dispose()
    {
        Apply(false, _gesture);
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
