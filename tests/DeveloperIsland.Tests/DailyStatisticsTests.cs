using DeveloperIsland.Core.Models;
using DeveloperIsland.Core.Pricing;
using DeveloperIsland.Core.Storage;
using DeveloperIsland.Core.Usage;
using Microsoft.Extensions.Time.Testing;

namespace DeveloperIsland.Tests;

public class DailyStatisticsTests
{
    private static readonly TimeZoneInfo Berlin = TimeZoneInfo.CreateCustomTimeZone("Test+2", TimeSpan.FromHours(2), "Test+2", "Test+2");

    [Fact]
    public void Events_are_bucketed_by_local_calendar_day()
    {
        // 23:30 UTC on the 23rd is 01:30 on the 24th at UTC+2.
        var late = new DateTimeOffset(2026, 9, 23, 23, 30, 0, TimeSpan.Zero);
        var early = new DateTimeOffset(2026, 9, 23, 21, 30, 0, TimeSpan.Zero);

        var daily = UsageAggregator.AggregateDaily(
            [TestData.Event(AiProviderKind.Claude, "a", late, 100), TestData.Event(AiProviderKind.Claude, "b", early, 50)],
            new ModelPricingService(),
            Berlin);

        Assert.Equal(2, daily.Count);
        Assert.Equal((new DateOnly(2026, 9, 23), 50L), (daily[0].Day, daily[0].Tokens.Processed));
        Assert.Equal((new DateOnly(2026, 9, 24), 100L), (daily[1].Day, daily[1].Tokens.Processed));
    }

    [Fact]
    public void Daily_rows_count_distinct_sessions_and_dedupe_events()
    {
        var t = new DateTimeOffset(2026, 9, 24, 8, 0, 0, TimeSpan.Zero);
        var daily = UsageAggregator.AggregateDaily(
        [
            TestData.Event(AiProviderKind.Claude, "a", t, 100, session: "s1"),
            TestData.Event(AiProviderKind.Claude, "a", t, 100, session: "s1"),
            TestData.Event(AiProviderKind.Claude, "b", t, 100, session: "s2"),
            TestData.Event(AiProviderKind.Codex, "c", t, 70, model: "gpt-5", session: "x"),
        ], new ModelPricingService(), TimeZoneInfo.Utc);

        var claude = daily.Single(d => d.Provider == AiProviderKind.Claude);
        Assert.Equal(200, claude.Tokens.Processed);
        Assert.Equal(2, claude.Sessions);
        Assert.Equal(70, daily.Single(d => d.Provider == AiProviderKind.Codex).Tokens.Processed);
    }

    [Fact]
    public void Merge_by_day_combines_providers_for_the_graph()
    {
        var day = new DateOnly(2026, 9, 24);
        var merged = UsageAggregator.MergeByDay(
        [
            new DailyUsage(day, AiProviderKind.Claude, new TokenCounts(1_820_000, 0, 0, 0, 0), 12.4m, false, 3),
            new DailyUsage(day, AiProviderKind.Codex, new TokenCounts(620_000, 0, 0, 0, 0), 6.02m, false, 2),
        ]);

        var single = Assert.Single(merged);
        Assert.Equal(2_440_000, single.ProcessedTokens);
        Assert.Equal(18.42m, single.TotalApiValueEur);
        Assert.Equal(5, single.Sessions);
    }

    [Fact]
    public void History_preserves_unknown_pricing_instead_of_showing_zero_as_a_complete_estimate()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 28, 12, 0, 0, TimeSpan.Zero));
        using var db = UsageDatabase.OpenInMemory();
        var history = new UsageHistoryService(db, new ModelPricingService(), time, TimeZoneInfo.Utc);
        history.Ingest([TestData.Event(AiProviderKind.Codex, "unknown", time.GetUtcNow(), 100, model: "unlisted-model")]);
        Assert.True(Assert.Single(history.GetHistory(1)).ApiValuePartial);

        history.Ingest([TestData.Event(AiProviderKind.Claude, "known", time.GetUtcNow(), 1000)]);
        var mixed = Assert.Single(history.GetHistory(1));
        Assert.True(mixed.ApiValuePartial);
        Assert.True(mixed.TotalApiValueEur > 0);
    }

    [Fact]
    public void Intensity_levels_follow_quartiles_and_keep_empty_days_at_zero()
    {
        var levels = UsageAggregator.IntensityLevels([0, 10, 20, 30, 40, 1_000_000]);

        Assert.Equal(0, levels[0]);
        Assert.Equal(1, levels[1]);
        Assert.Equal(4, levels[5]);
        Assert.True(levels.Skip(1).Zip(levels.Skip(2)).All(p => p.First <= p.Second));
    }

    [Fact]
    public void Intensity_levels_of_an_empty_history_are_all_zero()
    {
        Assert.All(UsageAggregator.IntensityLevels([0, 0, 0]), l => Assert.Equal(0, l));
    }

    [Fact]
    public void History_contains_every_day_of_the_range_including_gaps()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 28, 12, 0, 0, TimeSpan.Zero));
        using var db = UsageDatabase.OpenInMemory();
        var history = new UsageHistoryService(db, new ModelPricingService(), time, TimeZoneInfo.Utc);
        history.Ingest([TestData.Event(AiProviderKind.Claude, "x", new DateTimeOffset(2026, 9, 26, 9, 0, 0, TimeSpan.Zero), 500)]);

        var days = history.GetHistory(7);

        Assert.Equal(7, days.Count);
        Assert.Equal(new DateOnly(2026, 9, 22), days[0].Day);
        Assert.Equal(new DateOnly(2026, 9, 28), days[^1].Day);
        Assert.Equal(500, days.Single(d => d.Day == new DateOnly(2026, 9, 26)).Claude.Processed);
        Assert.Equal(6, days.Count(d => !d.HasUsage));
    }

    [Fact]
    public void Incremental_totals_match_a_full_recompute()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 28, 20, 0, 0, TimeSpan.Zero));
        using var db = UsageDatabase.OpenInMemory();
        var pricing = new ModelPricingService(new PricingOverrides { UsdToEur = 1m });
        var history = new UsageHistoryService(db, pricing, time, TimeZoneInfo.Utc);
        var random = new Random(7);
        var all = new List<UsageEvent>();

        // Many batches over today and older days, with duplicates inside and across batches.
        for (var batch = 0; batch < 40; batch++)
        {
            var events = new List<UsageEvent>();
            for (var i = 0; i < 25; i++)
            {
                var daysBack = random.Next(0, 6);
                var key = $"k{random.Next(0, 600)}";
                var e = TestData.Event(
                    random.Next(2) == 0 ? AiProviderKind.Claude : AiProviderKind.Codex,
                    key,
                    time.GetUtcNow().AddDays(-daysBack).AddMinutes(-random.Next(600)),
                    random.Next(1000, 90000),
                    model: random.Next(3) == 0 ? "gpt-6-astra" : "claude-opus-5-5",
                    session: $"s{random.Next(4)}");
                events.Add(e);
            }

            history.Ingest(events);
            all.AddRange(events);
        }

        var today = history.Today;
        foreach (var provider in new[] { AiProviderKind.Claude, AiProviderKind.Codex })
        {
            for (var back = 0; back < 6; back++)
            {
                var day = today.AddDays(-back);

                // Expected: first occurrence of each key wins, exactly like INSERT OR IGNORE.
                var expected = UsageAggregator.Summarize(
                    UsageAggregator.Deduplicate(all).Where(e => e.Provider == provider && UsageAggregator.LocalDay(e.Timestamp, TimeZoneInfo.Utc) == day),
                    pricing);
                var actual = history.GetTotals(provider, day);
                var stored = db.GetDaily(day, day).SingleOrDefault(d => d.Provider == provider);

                Assert.Equal(expected.Tokens, actual.Tokens);
                Assert.Equal(expected.ApiValueEur, actual.ApiValueEur);
                Assert.Equal(expected.Sessions, actual.Sessions);
                Assert.Equal(expected.Tokens.Processed, stored?.Tokens.Processed ?? 0);
                Assert.Equal(expected.ApiValueEur, stored?.ApiValueEur ?? 0m);
            }
        }
    }

    [Fact]
    public void Ingest_recomputes_daily_totals_and_notifies_changed_days()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 28, 12, 0, 0, TimeSpan.Zero));
        using var db = UsageDatabase.OpenInMemory();
        var history = new UsageHistoryService(db, new ModelPricingService(new PricingOverrides { UsdToEur = 1m }), time, TimeZoneInfo.Utc);
        IReadOnlyCollection<DateOnly>? changed = null;
        history.Changed += days => changed = days;
        var t = new DateTimeOffset(2026, 9, 28, 9, 0, 0, TimeSpan.Zero);

        history.Ingest([TestData.Event(AiProviderKind.Claude, "1", t, 1_000_000)]);
        history.Ingest([TestData.Event(AiProviderKind.Claude, "1", t, 1_000_000), TestData.Event(AiProviderKind.Claude, "2", t, 1_000_000)]);

        var today = history.GetTodayTotals(AiProviderKind.Claude);
        Assert.Equal(2_000_000, today.Tokens.Processed);
        Assert.Equal(8m, today.ApiValueEur); // 2M input tokens of Opus 5.5 at $4
        Assert.Equal([new DateOnly(2026, 9, 28)], changed);
        Assert.Equal(8m, history.GetHistory(1)[0].ClaudeApiValueEur);
    }
}
