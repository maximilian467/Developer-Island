namespace DeveloperIsland.Core.Island;

/// <summary>The window in front, as far as fullscreen detection is concerned.</summary>
/// <param name="ClassName">The window class (Win32).</param>
/// <param name="IsOwnProcess">One of our own windows (the island or Settings).</param>
/// <param name="IsShellProcess">The window belongs to the Windows shell (explorer.exe).</param>
/// <param name="IsVisible">Visible and not cloaked (DWM hides cloaked windows, for example on another virtual desktop).</param>
/// <param name="IsMaximized">The window is maximized.</param>
/// <param name="HasCaption">The window has a title bar.</param>
/// <param name="IsOnIslandMonitor">The window is on the monitor that shows the island.</param>
/// <param name="CoversMonitor">The window covers that whole monitor, taskbar included.</param>
public sealed record FrontWindow(
    string ClassName,
    bool IsOwnProcess,
    bool IsShellProcess,
    bool IsVisible,
    bool IsMaximized,
    bool HasCaption,
    bool IsOnIslandMonitor,
    bool CoversMonitor);

/// <summary>
/// Whether the window in front is a fullscreen app (a video, game or presentation) the island should
/// step aside for. Only real apps count:
/// <list type="bullet">
/// <item>The shell's own surfaces cover the monitor too (the Alt+Tab switcher, Task View, Snap
/// Assist, the desktop) but are not apps: they would hide the island on every app switch. File
/// Explorer runs in the shell process and can go fullscreen (F11), so its windows still count.</item>
/// <item>Our own windows, maximized windows and windows with a title bar are never fullscreen apps.</item>
/// </list>
/// </summary>
public static class FullscreenPolicy
{
    /// <summary>File Explorer folder windows: an app, although it runs in the shell process.</summary>
    public const string FileExplorerClass = "CabinetWClass";

    private static readonly HashSet<string> ShellClasses = new(StringComparer.Ordinal)
    {
        "Progman",
        "WorkerW",
        "Shell_TrayWnd",
        "Shell_SecondaryTrayWnd",
        "XamlExplorerHostIslandWindow", // Alt+Tab, Task View and Snap Assist on Windows 11
        "MultitaskingViewFrame",        // Alt+Tab on Windows 10
        "ForegroundStaging",            // briefly in front while the switcher opens and closes
    };

    public static bool IsFullscreenApp(FrontWindow window)
    {
        if (window.IsOwnProcess || !window.IsVisible || window.IsMaximized || window.HasCaption)
        {
            return false;
        }

        if (ShellClasses.Contains(window.ClassName) || (window.IsShellProcess && window.ClassName != FileExplorerClass))
        {
            return false;
        }

        return window.IsOnIslandMonitor && window.CoversMonitor;
    }
}
