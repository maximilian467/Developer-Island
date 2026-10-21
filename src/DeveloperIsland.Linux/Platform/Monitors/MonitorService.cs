using Avalonia.Controls;
using DeveloperIsland.Core.Placement;

namespace DeveloperIsland.Platform.Monitors;

/// <summary>
/// Displays with their work areas (the screen minus panels such as GNOME's top bar) and scale, from
/// Avalonia's screen list (XRandR monitors on X11 and XWayland). The device name is the output name
/// (eDP-1, DP-2, HDMI-1), which stays the same for a monitor on the same port.
/// </summary>
internal static class MonitorService
{
    private static Screens? s_screens;

    /// <summary>Raised on the UI thread when monitors are added, removed or rearranged.</summary>
    public static event Action? Changed;

    /// <summary>Any window's screen list will do; the island's is attached at start.</summary>
    public static void Attach(Screens screens)
    {
        if (ReferenceEquals(s_screens, screens))
        {
            return;
        }

        if (s_screens is not null)
        {
            s_screens.Changed -= OnChanged;
        }

        s_screens = screens;
        screens.Changed += OnChanged;
    }

    public static IReadOnlyList<MonitorInfo> GetMonitors()
    {
        var monitors = new List<MonitorInfo>();
        var screens = s_screens?.All ?? [];
        var index = 0;
        foreach (var screen in screens)
        {
            index++;
            var name = string.IsNullOrWhiteSpace(screen.DisplayName) ? $"Display {index}" : screen.DisplayName!;
            monitors.Add(new MonitorInfo(
                name,
                ToRect(screen.Bounds),
                ToRect(screen.WorkingArea),
                screen.Scaling,
                screen.IsPrimary,
                name));
        }

        if (monitors.Count > 0 && !monitors.Any(m => m.IsPrimary))
        {
            // Some setups report no primary output: the first one plays that part.
            monitors[0] = monitors[0] with { IsPrimary = true };
        }

        if (monitors.Count == 0)
        {
            monitors.Add(new MonitorInfo("Display 1", new PixelRect(0, 0, 1920, 1080), new PixelRect(0, 32, 1920, 1048), 1.0, true));
        }

        return monitors;
    }

    /// <summary>Friendly label for Settings, e.g. "DP-2 (2560 × 1440)".</summary>
    public static string DisplayName(MonitorInfo monitor)
    {
        var primary = monitor.IsPrimary ? ", primary" : string.Empty;
        return $"{monitor.DeviceName} ({monitor.Bounds.Width} × {monitor.Bounds.Height}{primary})";
    }

    /// <summary>The monitor containing a screen point, or the nearest one.</summary>
    public static MonitorInfo FromPoint(int x, int y)
    {
        var monitors = GetMonitors();
        return monitors.FirstOrDefault(m => m.Bounds.Contains(x, y))
            ?? monitors.MinBy(m => Distance(m.Bounds, x, y))!;
    }

    private static double Distance(PixelRect r, int x, int y)
    {
        var dx = Math.Max(0, Math.Max(r.X - x, x - r.Right));
        var dy = Math.Max(0, Math.Max(r.Y - y, y - r.Bottom));
        return dx * (double)dx + dy * (double)dy;
    }

    private static void OnChanged(object? sender, EventArgs e) => Changed?.Invoke();

    private static PixelRect ToRect(Avalonia.PixelRect r) => new(r.X, r.Y, r.Width, r.Height);
}
