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
        Assert.Equal(SystemAlert.CpuHigh, SystemAlertPolicy.Evaluate([Cpu(90), Cpu(95), Cpu(99)]));
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
        Assert.Equal(3, samples); // at 0, 5 and 10 s

        monitor.SetVisible(true);
        time.Advance(TimeSpan.FromSeconds(4));
        Assert.Equal(6, samples); // immediately, then every 2 s

        monitor.Configure(false);
        time.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(6, samples);
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
