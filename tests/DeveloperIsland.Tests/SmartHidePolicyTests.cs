using DeveloperIsland.Core.Island;
using DeveloperIsland.Core.Placement;
using DeveloperIsland.Core.Settings;

namespace DeveloperIsland.Tests;

public class SmartHidePolicyTests
{
    private static readonly ForegroundInfo MaximizedChrome = new("chrome", IsMaximized: true, IsOnIslandMonitor: true);

    [Fact]
    public void Maximized_browser_in_front_retracts_a_top_center_island()
    {
        Assert.Equal(RestMode.Retracted, SmartHidePolicy.Decide(new AppSettings(), IslandAnchor.TopCenter, 0, MaximizedChrome));
    }

    [Theory]
    [InlineData("msedge")]
    [InlineData("firefox")]
    [InlineData(@"C:\Program Files\Mozilla Firefox\FIREFOX.EXE")]
    public void All_default_browsers_are_recognised_by_executable(string process)
    {
        var fg = MaximizedChrome with { ProcessName = process };

        Assert.Equal(RestMode.Retracted, SmartHidePolicy.Decide(new AppSettings(), IslandAnchor.TopCenter, 0, fg));
    }

    [Fact]
    public void Restored_browser_windows_do_not_retract()
    {
        Assert.Equal(RestMode.Compact, SmartHidePolicy.Decide(new AppSettings(), IslandAnchor.TopCenter, 0, MaximizedChrome with { IsMaximized = false }));
    }

    [Fact]
    public void Other_apps_and_other_monitors_do_not_retract()
    {
        Assert.Equal(RestMode.Compact, SmartHidePolicy.Decide(new AppSettings(), IslandAnchor.TopCenter, 0, MaximizedChrome with { ProcessName = "code" }));
        Assert.Equal(RestMode.Compact, SmartHidePolicy.Decide(new AppSettings(), IslandAnchor.TopCenter, 0, MaximizedChrome with { IsOnIslandMonitor = false }));
    }

    [Fact]
    public void Only_the_top_center_anchor_near_the_edge_retracts()
    {
        Assert.Equal(RestMode.Compact, SmartHidePolicy.Decide(new AppSettings(), IslandAnchor.BottomCenter, 0, MaximizedChrome));
        Assert.Equal(RestMode.Compact, SmartHidePolicy.Decide(new AppSettings(), IslandAnchor.TopCenter, 200, MaximizedChrome));
    }

    [Fact]
    public void Settings_can_disable_or_switch_to_full_hide()
    {
        Assert.Equal(RestMode.Compact, SmartHidePolicy.Decide(new AppSettings { SmartHideEnabled = false }, IslandAnchor.TopCenter, 0, MaximizedChrome));
        Assert.Equal(RestMode.Hidden, SmartHidePolicy.Decide(new AppSettings { SmartHideBehavior = SmartHideBehavior.Hide }, IslandAnchor.TopCenter, 0, MaximizedChrome));
    }

    [Fact]
    public void Browsers_can_be_turned_off_individually()
    {
        var settings = new AppSettings { SmartHideProcesses = ["msedge", "firefox"] };

        Assert.Equal(RestMode.Compact, SmartHidePolicy.Decide(settings, IslandAnchor.TopCenter, 0, MaximizedChrome));
    }

    [Fact]
    public void Process_list_is_normalised_when_loaded()
    {
        var path = Path.Combine(TestData.TempDirectory(), "settings.json");
        File.WriteAllText(path, """{ "smartHideProcesses": ["Chrome.exe", "chrome", " FIREFOX "] }""");

        Assert.Equal(["chrome", "firefox"], new SettingsStore(path).Current.SmartHideProcesses);
    }
}
