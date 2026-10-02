using DeveloperIsland.Core.Island;

namespace DeveloperIsland.Tests;

public class FullscreenPolicyTests
{
    /// <summary>A borderless window covering the island's monitor: a video, game or slideshow.</summary>
    private static FrontWindow FullscreenApp(string className = "Chrome_WidgetWin_1") => new(
        className, IsOwnProcess: false, IsShellProcess: false, IsVisible: true, IsMaximized: false,
        HasCaption: false, IsOnIslandMonitor: true, CoversMonitor: true);

    [Fact]
    public void A_borderless_window_covering_the_island_monitor_is_a_fullscreen_app()
    {
        Assert.True(FullscreenPolicy.IsFullscreenApp(FullscreenApp()));
        Assert.True(FullscreenPolicy.IsFullscreenApp(FullscreenApp("UnityWndClass")));
    }

    [Theory]
    [InlineData("XamlExplorerHostIslandWindow")] // Alt+Tab, Task View, Snap Assist (Windows 11)
    [InlineData("MultitaskingViewFrame")]        // Alt+Tab (Windows 10)
    [InlineData("ForegroundStaging")]
    [InlineData("Shell_TrayWnd")]
    [InlineData("WorkerW")]
    public void The_task_switcher_and_other_shell_surfaces_never_hide_the_island(string shellClass)
    {
        // Regression: every Alt+Tab hid the island, because the switcher covers the monitor without a caption.
        Assert.False(FullscreenPolicy.IsFullscreenApp(FullscreenApp(shellClass) with { IsShellProcess = true }));

        // Recognized by class even when the shell process is unknown (Explorer restarting).
        Assert.False(FullscreenPolicy.IsFullscreenApp(FullscreenApp(shellClass)));
    }

    [Fact]
    public void Any_monitor_covering_shell_window_is_not_an_app_but_file_explorer_is()
    {
        Assert.False(FullscreenPolicy.IsFullscreenApp(FullscreenApp("SomeFutureShellSurface") with { IsShellProcess = true }));

        // File Explorer runs in the shell process and can go fullscreen with F11.
        Assert.True(FullscreenPolicy.IsFullscreenApp(FullscreenApp(FullscreenPolicy.FileExplorerClass) with { IsShellProcess = true }));
    }

    [Fact]
    public void Ordinary_windows_are_not_fullscreen()
    {
        Assert.False(FullscreenPolicy.IsFullscreenApp(FullscreenApp() with { IsMaximized = true }));
        Assert.False(FullscreenPolicy.IsFullscreenApp(FullscreenApp() with { HasCaption = true }));
        Assert.False(FullscreenPolicy.IsFullscreenApp(FullscreenApp() with { CoversMonitor = false }));
        Assert.False(FullscreenPolicy.IsFullscreenApp(FullscreenApp() with { IsOnIslandMonitor = false }));
    }

    [Fact]
    public void Our_own_and_invisible_windows_are_never_fullscreen_apps()
    {
        // Settings or the island in front: the island must come back, not stay hidden.
        Assert.False(FullscreenPolicy.IsFullscreenApp(FullscreenApp() with { IsOwnProcess = true }));

        // Cloaked or hidden windows (another virtual desktop) are not on screen.
        Assert.False(FullscreenPolicy.IsFullscreenApp(FullscreenApp() with { IsVisible = false }));
    }
}
