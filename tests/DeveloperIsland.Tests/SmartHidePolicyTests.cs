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

public class AutoHideAppTests
{
    [Fact]
    public void Older_settings_become_app_rules_and_keep_each_browser_state()
    {
        var path = Path.Combine(TestData.TempDirectory(), "settings.json");
        File.WriteAllText(path, """{ "smartHideProcesses": ["chrome", "code"] }""");

        var s = new SettingsStore(path).Current;

        Assert.Equal(["chrome", "msedge", "firefox", "code"], s.AutoHideApps.Select(a => a.Process));
        Assert.Equal([true, false, false, true], s.AutoHideApps.Select(a => a.Enabled));
        Assert.Equal("Google Chrome", s.AutoHideApps[0].Name);
        Assert.Equal(["chrome", "code"], s.SmartHideProcesses);
    }

    [Fact]
    public void Fresh_settings_hide_for_all_three_browsers()
    {
        var s = new SettingsStore(Path.Combine(TestData.TempDirectory(), "settings.json")).Current;

        Assert.Equal(3, s.AutoHideApps.Count);
        Assert.All(s.AutoHideApps, a => Assert.True(a.Enabled));
        Assert.True(s.AutoHideOnlyMaximized);
    }

    [Fact]
    public void Added_apps_and_switches_persist_and_drive_the_process_list()
    {
        var path = Path.Combine(TestData.TempDirectory(), "settings.json");
        var store = new SettingsStore(path);
        store.Update(s =>
        {
            s.AutoHideApps.Add(new AutoHideApp { Process = @"C:\Apps\Code.exe", Name = "Visual Studio Code", Path = @"C:\Apps\Code.exe" });
            s.AutoHideApps.First(a => a.Process == "firefox").Enabled = false;
        });

        var reloaded = new SettingsStore(path).Current;
        var code = Assert.Single(reloaded.AutoHideApps, a => a.Process == "code");
        Assert.Equal("Visual Studio Code", code.Name);
        Assert.Equal(@"C:\Apps\Code.exe", code.Path);
        Assert.Equal(["chrome", "msedge", "code"], reloaded.SmartHideProcesses);
    }

    [Fact]
    public void Duplicates_and_blank_entries_are_dropped()
    {
        var rules = AutoHideRules.Normalize(
            [new AutoHideApp { Process = "Code.exe" }, new AutoHideApp { Process = "code" }, new AutoHideApp { Process = " " }],
            []);

        var only = Assert.Single(rules);
        Assert.Equal("code", only.Process);
        Assert.Equal("code", only.Name);
    }

    [Fact]
    public void Without_the_maximized_requirement_any_window_of_the_app_retracts()
    {
        var restored = new ForegroundInfo("code", IsMaximized: false, IsOnIslandMonitor: true);
        var settings = new AppSettings { SmartHideProcesses = ["code"] };

        Assert.Equal(RestMode.Compact, SmartHidePolicy.Decide(settings, IslandAnchor.TopCenter, 0, restored));

        settings.AutoHideOnlyMaximized = false;
        Assert.Equal(RestMode.Retracted, SmartHidePolicy.Decide(settings, IslandAnchor.TopCenter, 0, restored));
    }
}
