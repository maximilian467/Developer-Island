using DeveloperIsland.Core.GitHub;
using DeveloperIsland.Core.Modules;
using DeveloperIsland.Core.Settings;
using DeveloperIsland.Core.SystemInfo;
using DeveloperIsland.Core.Tasks;
using Microsoft.Extensions.Time.Testing;

namespace DeveloperIsland.Tests;

public class ModuleCatalogTests
{
    [Fact]
    public void Default_order_matches_the_product_order()
    {
        Assert.Equal(
            [ModuleId.Claude, ModuleId.Codex, ModuleId.Music, ModuleId.Git, ModuleId.GitHub, ModuleId.Focus, ModuleId.Calendar, ModuleId.Tasks, ModuleId.System],
            new AppSettings().Order);
    }

    [Fact]
    public void Saved_order_is_kept_and_new_modules_are_appended()
    {
        var order = ModuleCatalog.NormalizeOrder(["Focus", "music", "Weather", "Focus", "Claude"]);

        Assert.Equal(ModuleId.Focus, order[0]);
        Assert.Equal(ModuleId.Music, order[1]);
        Assert.Equal(ModuleId.Claude, order[2]);
        Assert.Equal(9, order.Count);
        Assert.Equal(ModuleId.Codex, order[3]);
    }

    [Fact]
    public void Moving_is_clamped_at_the_ends()
    {
        var order = ModuleCatalog.DefaultOrder;

        Assert.Equal(ModuleId.Codex, ModuleCatalog.Move(order, ModuleId.Codex, -1)[0]);
        Assert.Equal(order, ModuleCatalog.Move(order, ModuleId.Claude, -1));
        Assert.Equal(ModuleId.Tasks, ModuleCatalog.Move(order, ModuleId.Tasks, 5)[^1]);
    }

    [Fact]
    public void Module_settings_round_trip_and_are_sanitised()
    {
        var path = Path.Combine(TestData.TempDirectory(), "settings.json");
        File.WriteAllText(path, """{ "moduleOrder": ["System", "Nope"], "gitEnabled": false, "calendarSources": [" https://a/x.ics ", "https://a/x.ics", ""] }""");

        var s = new SettingsStore(path).Current;

        Assert.Equal(ModuleId.System, s.Order[0]);
        Assert.False(s.IsEnabled(ModuleId.Git));
        Assert.True(s.IsEnabled(ModuleId.Tasks));
        Assert.Equal(["https://a/x.ics"], s.CalendarSources);

        var store = new SettingsStore(path);
        store.Update(x => x.SetEnabled(ModuleId.System, false));
        Assert.False(new SettingsStore(path).Current.SystemEnabled);
    }
}

public class TaskStoreTests
{
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 9, 30, 9, 0, 0, TimeSpan.Zero));

    [Fact]
    public void Add_complete_delete_and_reorder_persist()
    {
        var path = Path.Combine(TestData.TempDirectory(), "tasks.json");
        var store = new TaskStore(path, _time);
        var a = store.Add("  Write   report ")!;
        var b = store.Add("Review PR")!;
        var c = store.Add("Book room")!;

        Assert.Equal("Write report", a.Title);
        store.SetDone(a.Id, true);
        store.Move(c.Id, -1);
        store.Delete(b.Id);

        var reloaded = new TaskStore(path, _time).Items;
        Assert.Equal(["Write report", "Book room"], reloaded.Select(t => t.Title));
        Assert.True(reloaded[0].Done);
        Assert.Equal(_time.GetUtcNow(), reloaded[0].CompletedAt);
    }

    [Fact]
    public void New_tasks_go_above_completed_ones()
    {
        var store = new TaskStore(null, _time);
        var done = store.Add("Done already")!;
        store.SetDone(done.Id, true);

        store.Add("New");

        Assert.Equal(["New", "Done already"], store.Items.Select(t => t.Title));
        Assert.Equal(1, store.OpenCount);
    }

    [Fact]
    public void Blank_titles_are_ignored_and_long_ones_trimmed()
    {
        var store = new TaskStore(null, _time);

        Assert.Null(store.Add("   "));
        Assert.Equal(TaskStore.MaxTitleLength, store.Add(new string('x', 500))!.Title.Length);
    }

    [Fact]
    public void Clear_completed_and_uncomplete()
    {
        var store = new TaskStore(null, _time);
        var a = store.Add("A")!;
        var b = store.Add("B")!;
        store.SetDone(a.Id, true);
        store.SetDone(b.Id, true);
        store.SetDone(b.Id, false);

        store.ClearCompleted();

        Assert.Equal(["B"], store.Items.Select(t => t.Title));
        Assert.Null(store.Items[0].CompletedAt);
    }

    [Fact]
    public void Corrupt_file_starts_empty_and_is_kept_aside()
    {
        var path = Path.Combine(TestData.TempDirectory(), "tasks.json");
        File.WriteAllText(path, "[{ broken");

        Assert.Empty(new TaskStore(path, _time).Items);
        Assert.True(File.Exists(path + ".broken"));
    }
}

public class GitHubModuleTests
{
    [Fact]
    public void Parses_user_counts_repository_and_ci()
    {
        Assert.Equal("octo", GitHubJson.Login("""{ "login": "octo", "id": 1 }"""));
        Assert.Equal(3, GitHubJson.ArrayLength("""[{}, {}, {}]"""));
        Assert.Equal(12, GitHubJson.TotalCount("""{ "total_count": 12, "items": [] }"""));

        var repo = GitHubJson.Repository("""{ "full_name": "o/r", "stargazers_count": 42, "open_issues_count": 7 }""", openPullRequests: 3);
        Assert.Equal(new GitHubRepo("o/r", 42, 4, 3), repo);
    }

    [Theory]
    [InlineData("completed", "success", CiState.Passed)]
    [InlineData("completed", "failure", CiState.Failed)]
    [InlineData("completed", "cancelled", CiState.Failed)]
    [InlineData("in_progress", null, CiState.Running)]
    [InlineData("queued", null, CiState.Running)]
    public void Maps_workflow_runs(string status, string? conclusion, CiState expected)
    {
        var json = $$"""{ "workflow_runs": [ { "name": "CI", "head_branch": "main", "status": "{{status}}", "conclusion": {{(conclusion is null ? "null" : $"\"{conclusion}\"")}}, "updated_at": "2026-09-30T10:00:00Z" } ] }""";

        var run = GitHubJson.LatestRun(json)!;

        Assert.Equal(expected, run.State);
        Assert.Equal("CI", run.Workflow);
        Assert.Equal("main", run.Branch);
        Assert.Null(GitHubJson.LatestRun("""{ "workflow_runs": [] }"""));
    }

    [Fact]
    public async Task Without_gh_the_module_explains_how_to_connect()
    {
        using var service = new GitHubService(findGh: () => null);
        service.Configure(true);
        await service.RefreshAsync();

        Assert.Equal(ModuleState.Unavailable, service.Status.State);
        Assert.Contains("gh auth login", service.Status.Message);
        Assert.Null(service.Snapshot);
    }

    [Fact]
    public void Disabled_module_does_not_run()
    {
        var looked = false;
        using var service = new GitHubService(findGh: () => { looked = true; return null; });

        Assert.Equal(ModuleState.Disabled, service.Status.State);
        Assert.False(looked);
    }
}

public class SystemModuleTests
{
    private static SystemSample Cpu(double percent, int? battery = null, bool charging = false) =>
        new(percent, 8UL << 30, 16UL << 30, battery, charging);

    [Fact]
    public void A_single_spike_is_not_an_alert()
    {
        Assert.Equal(SystemAlert.None, SystemAlertPolicy.Evaluate([Cpu(10), Cpu(10), Cpu(99)]));

        // 15 seconds at one sample per second.
        var busy = Enumerable.Repeat(Cpu(95), SystemAlertPolicy.CpuHighSamples).ToList();
        Assert.Equal(SystemAlert.CpuHigh, SystemAlertPolicy.Evaluate(busy));
        Assert.Equal(SystemAlert.None, SystemAlertPolicy.Evaluate(busy.Skip(1).ToList()));
        busy[^5] = Cpu(40);
        Assert.Equal(SystemAlert.None, SystemAlertPolicy.Evaluate(busy));
    }

    [Fact]
    public void Low_battery_alerts_only_while_discharging()
    {
        Assert.Equal(SystemAlert.BatteryLow, SystemAlertPolicy.Evaluate([Cpu(5, battery: 15)]));
        Assert.Equal(SystemAlert.None, SystemAlertPolicy.Evaluate([Cpu(5, battery: 15, charging: true)]));
        Assert.Equal(SystemAlert.None, SystemAlertPolicy.Evaluate([Cpu(5, battery: 60)]));
        Assert.Equal(SystemAlert.None, SystemAlertPolicy.Evaluate([Cpu(5)]));
    }

    [Fact]
    public void Memory_percent()
    {
        Assert.Equal(50, Cpu(0).MemoryPercent, 3);
    }

    [Fact]
    public void Disabled_monitor_never_samples()
    {
        var time = new FakeTimeProvider();
        var samples = 0;
        using var monitor = new SystemMonitor(() => { samples++; return Cpu(10); }, time);

        time.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(0, samples);

        monitor.Configure(true);
        time.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal(11, samples); // at 0, 1, ... 10 s

        monitor.SetVisible(true);
        time.Advance(TimeSpan.FromSeconds(4));
        Assert.Equal(15, samples); // still once a second

        monitor.Configure(false);
        time.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(15, samples);
        Assert.Equal(ModuleState.Disabled, monitor.Status.State);
    }

    [Fact]
    public void Failed_readings_report_an_error_state()
    {
        using var monitor = new SystemMonitor(() => null, new FakeTimeProvider());
        monitor.Configure(true);

        Assert.Equal(ModuleState.Error, monitor.Status.State);
    }
}

public class CompactFavoritesTests
{
    private static readonly IReadOnlyList<ModuleId> Order = ModuleCatalog.DefaultOrder;

    [Fact]
    public void One_favorite_is_always_shown()
    {
        Assert.Equal(ModuleId.Music, CompactSelector.Select([ModuleId.Music], ModuleId.Claude, _ => true, turn: 5));
    }

    [Fact]
    public void Several_favorites_take_turns_and_skip_silent_ones()
    {
        IReadOnlyList<ModuleId> favorites = [ModuleId.Claude, ModuleId.Music, ModuleId.Focus];
        bool Speaks(ModuleId m) => m != ModuleId.Music; // nothing playing

        Assert.Equal(ModuleId.Claude, CompactSelector.Select(favorites, null, Speaks, 0));
        Assert.Equal(ModuleId.Focus, CompactSelector.Select(favorites, null, Speaks, 1));
        Assert.Equal(ModuleId.Claude, CompactSelector.Select(favorites, null, Speaks, 2));
        Assert.True(CompactSelector.Rotates(favorites));
    }

    [Fact]
    public void When_no_favorite_speaks_they_still_rotate()
    {
        Assert.Equal(ModuleId.Focus, CompactSelector.Select([ModuleId.Music, ModuleId.Focus], null, _ => false, 1));
    }

    [Fact]
    public void Without_favorites_the_last_opened_module_is_shown_else_the_classic_summary()
    {
        Assert.Equal(ModuleId.Calendar, CompactSelector.Select([], ModuleId.Calendar, _ => true, 3));
        Assert.Null(CompactSelector.Select([], null, _ => true, 3));
        Assert.False(CompactSelector.Rotates([ModuleId.Calendar]));
    }

    [Fact]
    public void Disabled_modules_are_never_favorites_and_order_follows_the_tabs()
    {
        var favorites = CompactSelector.EffectiveFavorites(["System", "Claude", "Nope", "Music"], Order, m => m != ModuleId.Music);

        Assert.Equal([ModuleId.Claude, ModuleId.System], favorites);
    }

    [Fact]
    public void Favorites_persist_and_are_sanitised()
    {
        var path = Path.Combine(TestData.TempDirectory(), "settings.json");
        var store = new SettingsStore(path);
        store.Update(s =>
        {
            s.SetFavorite(ModuleId.Calendar, true);
            s.SetFavorite(ModuleId.Claude, true);
            s.SetFavorite(ModuleId.Calendar, false);
            s.FavoriteModules.Add("weather");
            s.FavoriteModules.Add("claude");
        });

        var reloaded = new SettingsStore(path).Current;
        Assert.Equal(["Claude"], reloaded.FavoriteModules);
        Assert.True(reloaded.IsFavorite(ModuleId.Claude));
    }

    [Fact]
    public void A_single_module_tab_star_toggles_that_module()
    {
        Assert.False(TabFavorite.IsFavorite([ModuleId.System], [ModuleId.Music]));
        AssertToggle(TabFavorite.Toggle([ModuleId.System], [ModuleId.Music]), true, ModuleId.System);
        AssertToggle(TabFavorite.Toggle([ModuleId.System], [ModuleId.System, ModuleId.Music]), false, ModuleId.System);
    }

    [Fact]
    public void The_usage_star_with_one_provider_is_that_providers_star()
    {
        // Claude Code only (Codex off): the star features Claude, like any other tab.
        AssertToggle(TabFavorite.Toggle([ModuleId.Claude], []), true, ModuleId.Claude);
        Assert.True(TabFavorite.IsFavorite([ModuleId.Claude], [ModuleId.Claude]));
    }

    [Fact]
    public void The_usage_star_with_both_providers_features_and_clears_both()
    {
        IReadOnlyList<ModuleId> usage = [ModuleId.Claude, ModuleId.Codex];

        AssertToggle(TabFavorite.Toggle(usage, [ModuleId.Music]), true, ModuleId.Claude, ModuleId.Codex);
        AssertToggle(TabFavorite.Toggle(usage, [ModuleId.Claude, ModuleId.Codex]), false, ModuleId.Claude, ModuleId.Codex);

        // A star saved by an earlier version for one provider only: the tab shows filled, and a click clears it.
        Assert.True(TabFavorite.IsFavorite(usage, [ModuleId.Codex]));
        AssertToggle(TabFavorite.Toggle(usage, [ModuleId.Codex]), false, ModuleId.Claude, ModuleId.Codex);
    }

    private static void AssertToggle((IReadOnlyList<ModuleId> Modules, bool Favorite) toggle, bool favorite, params ModuleId[] modules)
    {
        Assert.Equal(favorite, toggle.Favorite);
        Assert.Equal(modules, toggle.Modules);
    }

    [Fact]
    public void Last_active_module_persists_in_its_own_file()
    {
        var path = Path.Combine(TestData.TempDirectory(), "ui-state.json");
        new UiStateStore(path).SetLastActive(ModuleId.Calendar);

        Assert.Equal(ModuleId.Calendar, new UiStateStore(path).LastActiveModule);
        Assert.Null(new UiStateStore(Path.Combine(TestData.TempDirectory(), "missing.json")).LastActiveModule);
    }

    [Fact]
    public void A_corrupt_ui_state_starts_fresh()
    {
        var path = Path.Combine(TestData.TempDirectory(), "ui-state.json");
        File.WriteAllText(path, "{ nope");

        Assert.Null(new UiStateStore(path).LastActiveModule);
    }
}

public class HardwareMonitoringTests
{
    private static SystemSample Sample(double cpu = 10, double? gpu = null, int? battery = null, params TemperatureReading[] temps) =>
        new(cpu, 8UL << 30, 16UL << 30, battery, false) { GpuPercent = gpu, Temperatures = temps };

    [Fact]
    public void History_keeps_the_last_sixty_seconds()
    {
        var time = new FakeTimeProvider();
        var n = 0;
        using var monitor = new SystemMonitor(() => Sample(cpu: ++n), time);
        monitor.Configure(true);

        time.Advance(TimeSpan.FromSeconds(90));

        var history = monitor.History;
        Assert.Equal(SystemMonitor.HistoryLength, history.Count);
        Assert.Equal(n, history[^1].CpuPercent);           // newest last
        Assert.Equal(n - 59, history[0].CpuPercent);       // oldest first
    }

    [Fact]
    public void Capabilities_list_only_what_the_device_reports()
    {
        var laptop = HardwareCapabilities.From([Sample(gpu: 3, battery: 80, temps: new TemperatureReading(TemperatureKind.ThermalZone, "TZ0", 51))]);
        Assert.True(laptop.HasGpu);
        Assert.True(laptop.HasBattery);
        Assert.Equal([TemperatureKind.ThermalZone], laptop.Temperatures);
        Assert.False(laptop.HasFans);
        Assert.False(laptop.Has(TemperatureKind.Cpu));

        var desktop = HardwareCapabilities.From([Sample()]);
        Assert.False(desktop.HasGpu);
        Assert.False(desktop.HasBattery);
        Assert.Empty(desktop.Temperatures);
    }

    [Fact]
    public void A_sensor_that_blinks_out_once_does_not_reshuffle_the_layout()
    {
        var samples = new[] { Sample(gpu: 5), Sample(gpu: null), Sample(gpu: 7) };

        Assert.True(HardwareCapabilities.From(samples).HasGpu);
    }

    [Fact]
    public void Hottest_reading_per_component()
    {
        var sample = Sample(temps:
        [
            new TemperatureReading(TemperatureKind.Cpu, "Core 1", 61),
            new TemperatureReading(TemperatureKind.Cpu, "Package", 66),
            new TemperatureReading(TemperatureKind.Storage, "NVMe", 41),
        ]);

        Assert.Equal(66, sample.Hottest(TemperatureKind.Cpu));
        Assert.Equal(41, sample.Hottest(TemperatureKind.Storage));
        Assert.Null(sample.Hottest(TemperatureKind.Gpu));
    }

    [Fact]
    public void Manual_fan_control_needs_a_crash_safe_interface()
    {
        // Controllable fans and rights are not enough: without a guaranteed fallback to firmware
        // control on a crash, manual control is never offered.
        Assert.False(new FanControlCapability(HasControllableFans: true, RevertsToFirmwareOnCrash: false, HasRights: true, MinimumPercent: 20).IsManualAllowed);
        Assert.False(new FanControlCapability(true, true, HasRights: false, 20).IsManualAllowed);
        Assert.False(FanControlCapability.None.IsManualAllowed);
        Assert.True(new FanControlCapability(true, true, true, 20).IsManualAllowed);
    }

    [Fact]
    public void Fan_safety_starts_in_auto_and_refuses_manual_when_not_allowed()
    {
        var fan = new FanSafetyState(new FanControlCapability(true, RevertsToFirmwareOnCrash: false, true, 20));

        Assert.Equal(FanMode.Auto, fan.Mode);
        Assert.False(fan.RequestManual(65));
        Assert.Equal(FanMode.Auto, fan.Mode);
    }

    [Fact]
    public void Fan_safety_clamps_to_the_minimum_and_falls_back_to_auto()
    {
        var fan = new FanSafetyState(new FanControlCapability(true, true, true, MinimumPercent: 25));

        Assert.True(fan.RequestManual(0));       // never off
        Assert.Equal(25, fan.ManualPercent);
        Assert.True(fan.RequestManual(140));
        Assert.Equal(100, fan.ManualPercent);
        Assert.False(fan.RequestManual(double.NaN));
        Assert.Equal(FanMode.Auto, fan.Mode);

        foreach (var failure in new Action[] { fan.OnFailure, fan.OnSensorLost, fan.OnExit })
        {
            fan.RequestManual(60);
            failure();
            Assert.Equal(FanMode.Auto, fan.Mode);
        }
    }
}
