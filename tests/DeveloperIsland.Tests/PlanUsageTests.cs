using DeveloperIsland.Core.Models;
using DeveloperIsland.Core.Plan;
using DeveloperIsland.Core.Storage;

namespace DeveloperIsland.Tests;

public class PlanUsageTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 1, 9, 0, 0, TimeSpan.Zero);

    private const string StatusLine = """
        {"session_id":"abc","cwd":"/home/me/secret-project","model":{"id":"claude-opus-5-5"},"cost":{"total_cost_usd":1.23},
         "rate_limits":{"five_hour":{"used_percentage":72.4,"resets_at":1790000000},"seven_day":{"used_percentage":41,"resets_at":1790500000}}}
        """;

    [Fact]
    public void Reads_both_windows_from_the_documented_status_line_input()
    {
        var usage = ClaudeStatusLine.Extract(StatusLine, Now)!;

        Assert.Equal(72.4, usage.Current!.UsedPercent);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1790000000), usage.Current.ResetsAt);
        Assert.Equal(41, usage.Weekly!.UsedPercent);
        Assert.Equal(Now, usage.MeasuredAt);
    }

    [Theory]
    [InlineData("""{"model":{"id":"x"}}""")]                                   // API users: no rate_limits
    [InlineData("""{"rate_limits":{}}""")]                                     // before the first response
    [InlineData("""{"rate_limits":{"five_hour":{"used_percentage":"high"}}}""")] // malformed
    [InlineData("not json")]
    [InlineData("")]
    public void Missing_or_malformed_plan_data_yields_nothing(string json)
    {
        Assert.Null(ClaudeStatusLine.Extract(json, Now));
    }

    [Fact]
    public void Only_one_window_is_fine_and_percentages_are_clamped()
    {
        var usage = ClaudeStatusLine.Extract("""{"rate_limits":{"seven_day":{"used_percentage":130,"resets_at":1790500000}}}""", Now)!;

        Assert.Null(usage.Current);
        Assert.Equal(100, usage.Weekly!.UsedPercent);
    }

    [Fact]
    public void A_window_that_has_reset_no_longer_reports_its_old_percentage()
    {
        var usage = new PlanUsage(new PlanWindow(72, Now.AddHours(1)), new PlanWindow(41, Now.AddDays(3)), Now);

        Assert.NotNull(usage.CurrentAt(Now.AddMinutes(30)));
        Assert.Null(usage.CurrentAt(Now.AddHours(2)));
        Assert.NotNull(usage.WeeklyAt(Now.AddHours(2)));
    }

    [Fact]
    public void Store_keeps_numbers_only()
    {
        var path = Path.Combine(TestData.TempDirectory(), "claude-plan.json");
        var store = new PlanUsageStore(path);
        store.Save(ClaudeStatusLine.Extract(StatusLine, Now)!);

        var text = File.ReadAllText(path);
        Assert.DoesNotContain("secret", text);
        Assert.DoesNotContain("opus", text);
        Assert.Equal(72.4, store.Load()!.Current!.UsedPercent);
    }

    [Fact]
    public void Plan_service_reports_unavailable_until_data_arrives_and_follows_the_file()
    {
        var path = Path.Combine(TestData.TempDirectory(), "claude-plan.json");
        using var service = new PlanUsageService(new PlanUsageStore(path));
        var arrived = new TaskCompletionSource<PlanUsage?>();
        service.Configure(true);
        Assert.Null(service.Usage);

        service.Changed += u => { if (u is not null) arrived.TrySetResult(u); };
        new PlanUsageStore(path).Save(new PlanUsage(new PlanWindow(10, Now.AddHours(1)), null, Now));

        Assert.Equal(10, arrived.Task.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult()!.Current!.UsedPercent);
        service.Configure(false);
        Assert.Null(service.Usage);
    }

    [Fact]
    public void Plan_peaks_keep_the_daily_maximum_and_are_never_invented()
    {
        using var db = UsageDatabase.OpenInMemory();
        var day = new DateOnly(2026, 10, 1);
        db.RecordPlanPeak(day, AiProviderKind.Claude, "five_hour", 40, Now);
        db.RecordPlanPeak(day, AiProviderKind.Claude, "five_hour", 72, Now);
        db.RecordPlanPeak(day, AiProviderKind.Claude, "five_hour", 12, Now);

        var peaks = db.GetPlanPeaks(AiProviderKind.Claude, day.AddDays(-7), day);
        Assert.Equal(72, peaks[day]["five_hour"]);
        Assert.False(peaks.ContainsKey(day.AddDays(-1)));
        Assert.Empty(db.GetPlanPeaks(AiProviderKind.Codex, day.AddDays(-7), day));
    }
}

public class ClaudeStatusLineSetupTests
{
    private static string Settings(string json)
    {
        var path = Path.Combine(TestData.TempDirectory(), "settings.json");
        File.WriteAllText(path, json);
        return path;
    }

    [Fact]
    public void Connect_adds_only_the_status_line_and_keeps_every_other_setting()
    {
        var path = Settings("""{ "model": "opus", "permissions": { "allow": ["Bash(git:*)"] } }""");

        Assert.Equal(PlanConnection.Connected, ClaudeStatusLineSetup.Connect(path, @"C:\Apps\DeveloperIsland.exe"));

        var text = File.ReadAllText(path);
        Assert.Contains("\"model\": \"opus\"", text);
        Assert.Contains("Bash(git:*)", text);
        Assert.Contains("--claude-statusline", text);
        Assert.True(File.Exists(path + ".developer-island.bak"));
        Assert.Equal(PlanConnection.Connected, ClaudeStatusLineSetup.Inspect(path));
    }

    [Fact]
    public void An_existing_status_line_is_never_replaced()
    {
        var path = Settings("""{ "statusLine": { "type": "command", "command": "my-status.sh" } }""");

        Assert.Equal(PlanConnection.OtherStatusLine, ClaudeStatusLineSetup.Connect(path, @"C:\DI.exe"));
        Assert.Contains("my-status.sh", File.ReadAllText(path));
        Assert.Equal(PlanConnection.OtherStatusLine, ClaudeStatusLineSetup.Disconnect(path));
        Assert.Contains("my-status.sh", File.ReadAllText(path));
    }

    [Fact]
    public void Disconnect_removes_only_our_entry()
    {
        var path = Settings("""{ "theme": "dark" }""");
        ClaudeStatusLineSetup.Connect(path, @"C:\DI.exe");

        Assert.Equal(PlanConnection.NotConnected, ClaudeStatusLineSetup.Disconnect(path));
        var text = File.ReadAllText(path);
        Assert.DoesNotContain("statusLine", text);
        Assert.Contains("dark", text);
    }

    [Fact]
    public void Missing_settings_are_created_and_unreadable_ones_left_alone()
    {
        var dir = TestData.TempDirectory();
        var missing = Path.Combine(dir, "new", "settings.json");
        Assert.Equal(PlanConnection.Connected, ClaudeStatusLineSetup.Connect(missing, @"C:\DI.exe"));

        var broken = Settings("{ this is not json");
        Assert.Equal(PlanConnection.Unreadable, ClaudeStatusLineSetup.Connect(broken, @"C:\DI.exe"));
        Assert.Equal("{ this is not json", File.ReadAllText(broken));
    }
}

public class PlanStatusTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 1, 9, 0, 0, TimeSpan.Zero);

    private static PlanUsage Measured(TimeSpan ago, TimeSpan resetsIn) =>
        new(new PlanWindow(40, Now + resetsIn), new PlanWindow(20, Now.AddDays(3)), Now - ago);

    [Fact]
    public void Connected_without_data_is_not_reported_as_available()
    {
        Assert.Equal(PlanDataState.WaitingForData, PlanStatus.StateOf(PlanConnection.Connected, null, Now));
        Assert.Contains("terminal", PlanStatus.Reason(PlanDataState.WaitingForData));
    }

    [Fact]
    public void Recent_data_is_receiving_and_old_data_is_stale()
    {
        Assert.Equal(PlanDataState.Receiving, PlanStatus.StateOf(PlanConnection.Connected, Measured(TimeSpan.FromMinutes(5), TimeSpan.FromHours(2)), Now));
        Assert.Equal(PlanDataState.Stale, PlanStatus.StateOf(PlanConnection.Connected, Measured(TimeSpan.FromHours(3), TimeSpan.FromHours(2)), Now));
    }

    [Fact]
    public void Connection_problems_win_over_data()
    {
        var usage = Measured(TimeSpan.FromMinutes(1), TimeSpan.FromHours(1));
        Assert.Equal(PlanDataState.NotConnected, PlanStatus.StateOf(PlanConnection.NotConnected, usage, Now));
        Assert.Equal(PlanDataState.OtherStatusLine, PlanStatus.StateOf(PlanConnection.OtherStatusLine, null, Now));
        Assert.Equal(PlanDataState.Unreadable, PlanStatus.StateOf(PlanConnection.Unreadable, null, Now));
    }
}
