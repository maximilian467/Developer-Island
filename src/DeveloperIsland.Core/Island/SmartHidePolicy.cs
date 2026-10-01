using DeveloperIsland.Core.Placement;
using DeveloperIsland.Core.Settings;

namespace DeveloperIsland.Core.Island;

/// <summary>The foreground window as far as Smart Auto-Hide is concerned.</summary>
/// <param name="ProcessName">Executable name without extension, lower case.</param>
/// <param name="IsMaximized">The window is maximized (not merely large).</param>
/// <param name="IsOnIslandMonitor">The window is on the monitor that shows the island.</param>
public sealed record ForegroundInfo(string ProcessName, bool IsMaximized, bool IsOnIslandMonitor);

/// <summary>
/// Smart Auto-Hide: a top-center island covers the tab strip of a maximized browser (or the title bar
/// of any app the user lists), so while such an app is in front the island rests as a notch in the
/// screen edge (or hides). Anywhere else, or for any other app, it rests as the compact capsule.
/// </summary>
public static class SmartHidePolicy
{
    public static readonly IReadOnlyList<string> KnownBrowsers = ["chrome", "msedge", "firefox"];

    /// <summary>A custom position this far below the top edge no longer covers tabs.</summary>
    public const double MaxTopOffset = 24;

    /// <param name="privacyActive">Camera or microphone in use: a full hide becomes the notch, so the indicator stays visible.</param>
    public static RestMode Decide(AppSettings settings, IslandAnchor anchor, double offsetY, ForegroundInfo? foreground, bool privacyActive = false)
    {
        if (!settings.SmartHideEnabled || anchor != IslandAnchor.TopCenter || offsetY > MaxTopOffset || foreground is null)
        {
            return RestMode.Compact;
        }

        var applies = (foreground.IsMaximized || !settings.AutoHideOnlyMaximized)
            && foreground.IsOnIslandMonitor
            && settings.SmartHideProcesses.Contains(NormalizeProcessName(foreground.ProcessName));
        if (!applies)
        {
            return RestMode.Compact;
        }

        return settings.SmartHideBehavior == SmartHideBehavior.Hide && !privacyActive ? RestMode.Hidden : RestMode.Retracted;
    }

    /// <summary>"C:\...\Chrome.EXE" or "chrome.exe" becomes "chrome".</summary>
    public static string NormalizeProcessName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return string.Empty;
        }

        var file = name.Trim().Replace('/', '\\');
        var slash = file.LastIndexOf('\\');
        if (slash >= 0)
        {
            file = file[(slash + 1)..];
        }

        if (file.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            file = file[..^4];
        }

        return file.ToLowerInvariant();
    }
}
