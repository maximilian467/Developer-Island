using DeveloperIsland.Core.Models;
using DeveloperIsland.Core.Providers;
using DeveloperIsland.Core.Storage;
using DeveloperIsland.Core.Usage;

namespace DeveloperIsland.Core.Demo;

/// <summary>
/// Mock data for <c>--demo</c>. It only ever lives in an in-memory database and in-memory providers,
/// so it can never leak into the real usage history.
/// </summary>
public static class DemoData
{
    public const string ClaudeModel = "claude-opus-5-5";
    public const string CodexModel = "gpt-5-codex";

    /// <summary>Fills the (in-memory) history with about 26 weeks of plausible, uneven usage.</summary>
    public static void SeedHistory(UsageHistoryService history, UsageDatabase focusDb, TimeProvider time)
    {
        var random = new Random(1847);
        var today = history.Today;
        var events = new List<UsageEvent>();
        var zoneOffset = time.GetLocalNow().Offset;

        for (var back = 181; back >= 0; back--)
        {
            var day = today.AddDays(-back);
            var weekend = day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;
            var activity = weekend ? 0.25 : 0.85;
            if (back > 0 && random.NextDouble() > activity)
            {
                continue;
            }

            // Today is pinned so screenshots show the documented demo values.
            var claudeTarget = back == 0 ? 2_683_950 : (long)(random.NextDouble() * random.NextDouble() * 3_400_000);
            var codexTarget = back == 0 ? 618_950 : (random.NextDouble() < 0.55 ? (long)(random.NextDouble() * 1_100_000) : 0);
            AddDay(events, AiProviderKind.Claude, ClaudeModel, day, zoneOffset, claudeTarget, random, sessions: back == 0 ? 3 : 1 + random.Next(4));
            AddDay(events, AiProviderKind.Codex, CodexModel, day, zoneOffset, codexTarget, random, sessions: back == 0 ? 2 : 1 + random.Next(2));

            if (back > 0 && !weekend && random.NextDouble() < 0.6)
            {
                var start = new DateTimeOffset(day.ToDateTime(new TimeOnly(9 + random.Next(8), 0)), zoneOffset);
                var minutes = new[] { 25, 50, 90 }[random.Next(3)];
                focusDb.InsertFocusSession(new FocusSessionRecord(start, start.AddMinutes(minutes), day, minutes * 60, minutes * 60, true));
            }
        }

        foreach (var chunk in events.Chunk(4000))
        {
            history.Ingest(chunk);
        }
    }

    public static AiUsageSnapshot ClaudeSnapshot(UsageHistoryService history) => Snapshot(history, AiProviderKind.Claude) with
    {
        LimitPercent = 68,
        LimitWindowMinutes = 300,
        CurrentProject = "developer-island",
        IsActive = true,
    };

    public static AiUsageSnapshot CodexSnapshot(UsageHistoryService history) => Snapshot(history, AiProviderKind.Codex) with
    {
        LimitPercent = 31,
        LimitWindowMinutes = 300,
        CurrentProject = "api-gateway",
    };

    private static AiUsageSnapshot Snapshot(UsageHistoryService history, AiProviderKind kind)
    {
        var totals = history.GetTodayTotals(kind);
        return new AiUsageSnapshot
        {
            Provider = kind,
            State = ProviderState.Ready,
            Day = history.Today,
            Today = totals.Tokens,
            ApiValueEur = totals.DisplayValueEur,
            SessionsToday = totals.Sessions,
            CurrentModel = totals.Latest?.Model,
            LastActivity = totals.Latest?.Timestamp,
        };
    }

    private static void AddDay(List<UsageEvent> events, AiProviderKind provider, string model, DateOnly day, TimeSpan offset, long target, Random random, int sessions)
    {
        if (target <= 0)
        {
            return;
        }

        const int perDay = 12;
        var remaining = target;
        for (var i = 0; i < perDay && remaining > 0; i++)
        {
            var share = i == perDay - 1 ? remaining : Math.Min(remaining, (long)(target / perDay * (0.6 + random.NextDouble() * 0.8)));
            remaining -= share;

            // Typical agent traffic: mostly cache reads, a little fresh input, some output.
            var cacheRead = (long)(share * 0.86);
            var cacheWrite = (long)(share * 0.07);
            var output = (long)(share * 0.03);
            var input = share - cacheRead - cacheWrite - output;
            var time = new DateTimeOffset(day.ToDateTime(new TimeOnly(9, 0)), offset).AddMinutes(i * 37 + random.Next(20));
            events.Add(new UsageEvent(
                provider,
                $"demo:{provider}:{day:yyyyMMdd}:{i}",
                time,
                model,
                new TokenCounts(input, output, cacheWrite, 0, cacheRead),
                $"demo-session-{i % sessions}",
                provider == AiProviderKind.Claude ? "developer-island" : "api-gateway"));
        }
    }
}

/// <summary>An AI provider that serves a fixed demo snapshot and one sample activity.</summary>
public sealed class DemoAiProvider : IAiUsageProvider
{
    private readonly TimeProvider _time;
    private ITimer? _timer;

    public DemoAiProvider(AiUsageSnapshot snapshot, TimeProvider? time = null)
    {
        Snapshot = snapshot;
        _time = time ?? TimeProvider.System;
    }

    public event Action<AiUsageSnapshot>? SnapshotChanged;

    public event Action<AiActivity>? Activity;

    public AiProviderKind Kind => Snapshot.Provider;

    public AiUsageSnapshot Snapshot { get; }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        SnapshotChanged?.Invoke(Snapshot);
        if (Kind == AiProviderKind.Claude)
        {
            _timer = _time.CreateTimer(
                _ => Activity?.Invoke(new AiActivity(Kind, AiActivityKind.SessionStarted, "Claude Code session started", "developer-island")),
                null,
                TimeSpan.FromSeconds(14),
                Timeout.InfiniteTimeSpan);
        }

        return Task.CompletedTask;
    }

    public void Dispose() => _timer?.Dispose();
}
