using DeveloperIsland.Core.Models;
using DeveloperIsland.Core.Pricing;
using DeveloperIsland.Core.Providers;
using DeveloperIsland.Core.Providers.Claude;
using DeveloperIsland.Core.Providers.Codex;
using DeveloperIsland.Core.Storage;
using DeveloperIsland.Core.Usage;

namespace DeveloperIsland.Tests;

public class ProviderIsolationTests
{
    private sealed class ThrowingProvider(AiProviderKind kind) : IAiUsageProvider
    {
        public event Action<AiUsageSnapshot>? SnapshotChanged { add { } remove { } }

        public event Action<AiActivity>? Activity { add { } remove { } }

        public AiProviderKind Kind => kind;

        public AiUsageSnapshot Snapshot => throw new InvalidOperationException("parser exploded");

        public Task StartAsync(CancellationToken cancellationToken) => throw new InvalidOperationException("parser exploded");

        public void Dispose() => throw new InvalidOperationException("dispose exploded");
    }

    private sealed class HealthyProvider(AiProviderKind kind) : IAiUsageProvider
    {
        public event Action<AiUsageSnapshot>? SnapshotChanged;

        public event Action<AiActivity>? Activity { add { } remove { } }

        public AiProviderKind Kind => kind;

        public AiUsageSnapshot Snapshot { get; private set; } = AiUsageSnapshot.Initial(kind);

        public Task StartAsync(CancellationToken cancellationToken)
        {
            Snapshot = Snapshot with { State = ProviderState.Ready, Today = new TokenCounts(5, 5, 0, 0, 0) };
            SnapshotChanged?.Invoke(Snapshot);
            return Task.CompletedTask;
        }

        public void Dispose()
        {
        }
    }

    [Fact]
    public async Task A_failing_provider_is_reported_as_error_without_affecting_others()
    {
        using var host = new ProviderHost();
        var published = new List<AiUsageSnapshot>();
        host.SnapshotChanged += published.Add;

        await host.AddAsync(new ThrowingProvider(AiProviderKind.Codex), TestContext.Current.CancellationToken);
        await host.AddAsync(new HealthyProvider(AiProviderKind.Claude), TestContext.Current.CancellationToken);

        Assert.Equal(ProviderState.Error, host.GetSnapshot(AiProviderKind.Codex).State);
        Assert.NotNull(host.GetSnapshot(AiProviderKind.Codex).ErrorMessage);
        Assert.Equal(ProviderState.Ready, host.GetSnapshot(AiProviderKind.Claude).State);
        Assert.Contains(published, s => s.Provider == AiProviderKind.Codex && s.State == ProviderState.Error);
    }

    [Fact]
    public async Task Exceptions_in_ui_handlers_do_not_escape_the_host()
    {
        using var host = new ProviderHost();
        host.SnapshotChanged += _ => throw new InvalidOperationException("ui bug");

        await host.AddAsync(new HealthyProvider(AiProviderKind.Claude), TestContext.Current.CancellationToken);

        Assert.Equal(ProviderState.Ready, host.GetSnapshot(AiProviderKind.Claude).State);
    }

    [Fact]
    public async Task Removing_a_provider_disposes_it_safely_and_reports_disabled()
    {
        var host = new ProviderHost();
        await host.AddAsync(new ThrowingProvider(AiProviderKind.Codex), TestContext.Current.CancellationToken);

        host.Remove(AiProviderKind.Codex);
        host.Dispose();

        Assert.Equal(ProviderState.Disabled, host.GetSnapshot(AiProviderKind.Codex).State);
    }

    [Fact]
    public async Task Missing_installation_is_reported_as_not_detected()
    {
        using var db = UsageDatabase.OpenInMemory();
        var history = new UsageHistoryService(db, new ModelPricingService());
        var root = Path.Combine(TestData.TempDirectory(), ".claude-not-installed");
        using var host = new ProviderHost();

        await host.AddAsync(new ClaudeUsageProvider(root, history, db), TestContext.Current.CancellationToken);

        Assert.Equal(ProviderState.NotDetected, host.GetSnapshot(AiProviderKind.Claude).State);
    }

    [Fact]
    public async Task Claude_provider_reads_existing_and_appended_transcripts()
    {
        using var db = UsageDatabase.OpenInMemory();
        var history = new UsageHistoryService(db, new ModelPricingService());
        var root = TestData.TempDirectory();
        var project = Directory.CreateDirectory(Path.Combine(root, "projects", "c--code-island")).FullName;
        var file = Path.Combine(project, "session.jsonl");
        var now = DateTimeOffset.Now;
        File.WriteAllText(file, TestData.ClaudeAssistantLine("m1", "r1", now, input: 100, output: 10, cacheCreation: 0, cacheRead: 0) + "\n");

        using var provider = new ClaudeUsageProvider(root, history, db);
        var ready = new TaskCompletionSource();
        provider.SnapshotChanged += s =>
        {
            if (s.State == ProviderState.Ready && s.Today.Processed == 220)
            {
                ready.TrySetResult();
            }
        };

        await provider.StartAsync(TestContext.Current.CancellationToken);
        Assert.Equal(110, provider.Snapshot.Today.Processed);
        Assert.Equal("island", provider.Snapshot.CurrentProject);
        Assert.Equal(1, provider.Snapshot.SessionsToday);

        // The watcher picks up appended lines (duplicate block lines are ignored).
        File.AppendAllText(file,
            TestData.ClaudeAssistantLine("m1", "r1", now, input: 100, output: 10, cacheCreation: 0, cacheRead: 0) + "\n" +
            TestData.ClaudeAssistantLine("m2", "r2", now, input: 100, output: 10, cacheCreation: 0, cacheRead: 0) + "\n");

        var finished = await Task.WhenAny(ready.Task, Task.Delay(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        Assert.Same(ready.Task, finished);
    }

    [Fact]
    public async Task Codex_provider_reports_rate_limit_from_local_logs()
    {
        using var db = UsageDatabase.OpenInMemory();
        var history = new UsageHistoryService(db, new ModelPricingService());
        var root = TestData.TempDirectory();
        var day = Directory.CreateDirectory(Path.Combine(root, "sessions", "2026", "09", "28")).FullName;
        var now = DateTimeOffset.Now;
        File.WriteAllLines(Path.Combine(day, "rollout-a.jsonl"),
        [
            TestData.CodexMeta("sess", @"C:\code\api"),
            TestData.CodexTurnContext("gpt-5-codex", @"C:\code\api"),
            TestData.CodexUsageRecord("resp_1", now, 1000, 400, 20),
            TestData.CodexTokenCount(now, 1020, 1000, 400, 20, usedPercent: 31, resetsAt: now.AddHours(2).ToUnixTimeSeconds()),
        ]);

        using var provider = new CodexUsageProvider(root, history, db);
        await provider.StartAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ProviderState.Ready, provider.Snapshot.State);
        Assert.Equal(1020, provider.Snapshot.Today.Processed);
        Assert.Equal(31, provider.Snapshot.LimitPercent);
        Assert.Equal("api", provider.Snapshot.CurrentProject);
        Assert.Equal("gpt-5-codex", provider.Snapshot.CurrentModel);
    }

    [Fact]
    public async Task Expired_rate_limit_windows_are_not_shown()
    {
        using var db = UsageDatabase.OpenInMemory();
        var history = new UsageHistoryService(db, new ModelPricingService());
        var root = TestData.TempDirectory();
        var day = Directory.CreateDirectory(Path.Combine(root, "sessions", "2026")).FullName;
        var now = DateTimeOffset.Now;
        File.WriteAllLines(Path.Combine(day, "rollout-b.jsonl"),
        [
            TestData.CodexTokenCount(now.AddHours(-6), 100, 100, 0, 0, usedPercent: 80, resetsAt: now.AddHours(-1).ToUnixTimeSeconds()),
        ]);

        using var provider = new CodexUsageProvider(root, history, db);
        await provider.StartAsync(TestContext.Current.CancellationToken);

        Assert.Null(provider.Snapshot.LimitPercent);
    }
}
