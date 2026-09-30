namespace DeveloperIsland.Core.Settings;

/// <summary>
/// A global shortcut from a short list of presets. Presets rather than free recording: every option
/// is known to be typeable and unlikely to steal a common app shortcut.
/// </summary>
public sealed record ShortcutGesture(string Text, bool Ctrl, bool Alt, bool Shift, uint VirtualKey)
{
    public static readonly ShortcutGesture Default = new("Ctrl + Alt + Space", Ctrl: true, Alt: true, Shift: false, 0x20);

    public static readonly IReadOnlyList<ShortcutGesture> Presets =
    [
        Default,
        new("Ctrl + Shift + Space", Ctrl: true, Alt: false, Shift: true, 0x20),
        new("Alt + Shift + Space", Ctrl: false, Alt: true, Shift: true, 0x20),
        new("Ctrl + Alt + I", Ctrl: true, Alt: true, Shift: false, 'I'),
    ];

    /// <summary>The preset with this text, or the default for anything unknown.</summary>
    public static ShortcutGesture FromText(string? text) =>
        Presets.FirstOrDefault(p => string.Equals(p.Text, text?.Trim(), StringComparison.OrdinalIgnoreCase)) ?? Default;
}
