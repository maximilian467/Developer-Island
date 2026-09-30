using DeveloperIsland.Core.Models;
using DeveloperIsland.Core.Pricing;
using DeveloperIsland.Core.Storage;
using DeveloperIsland.Core.Usage;
using Microsoft.Extensions.Time.Testing;

namespace DeveloperIsland.Tests;

/// <summary>
/// Token semantics and "today" boundaries. Figures mirror a real Claude Code day: almost all new
/// context arrives as cache writes, and cache reads dominate the processed total.
/// </summary>
public class TokenAccountingTests
{
    private static readonly TimeZoneInfo Berlin = TryZone("W. Europe Standard Time") ?? TryZone("Europe/Berlin")!;

    [Fact]
    public void Fresh_counts_everything_processed_without_a_cache_hit()
    {
        var t = new TokenCounts(Input: 56, Output: 30_040, CacheWrite5m: 0, CacheWrite1h: 2_448_946, CacheRead: 17_711_603);

        Assert.Equal(56 + 2_448_946 + 30_040, t.Fresh);
        Assert.Equal(t.Fresh + 17_711_603, t.Processed);
    }

    [Fact]
    public void Claude_cache_reads_are_not_part_of_fresh_tokens()
    {
        var line = TestData.ClaudeAssistantLine("m", "r", DateTimeOffset.UtcNow, input: 3, output: 359, cacheCreation: 22074, cacheRead: 26132, cache1h: 22074, cache5m: 0);

        Assert.True(ClaudeLogParser.TryParse(line, out var e));
        Assert.Equal(3 + 22074 + 359, e.Tokens.Fresh);
        Assert.Equal(3 + 22074 + 359 + 26132, e.Tokens.Processed);
    }

    [Fact]
    public void Codex_cached_input_counts_as_cache_read_not_fresh()
    {
        var parser = new CodexLogParser();
        Assert.True(parser.TryParse(TestData.CodexUsageRecord("r1", DateTimeOffset.UtcNow, input: 25862, cached: 21376, output: 103), out var e));

        Assert.Equal(25862 - 21376 + 103, e.Tokens.Fresh);
        Assert.Equal(21376, e.Tokens.CacheRead);
    }

    [Fact]
    public void Cache_tokens_are_priced_at_cache_rates_not_the_input_rate()
    {
        var pricing = new ModelPricingService(new PricingOverrides { UsdToEur = 1m });

        // Opus 5.5: input $4, cache read $0.20, 1 h cache write 2 x input = $8 per MTok.
        var read = pricing.Estimate("claude-opus-5-5", new TokenCounts(0, 0, 0, 0, 1_000_000)).ValueEur;
        var write1h = pricing.Estimate("claude-opus-5-5", new TokenCounts(0, 0, 0, 1_000_000, 0)).ValueEur;
        var input = pricing.Estimate("claude-opus-5-5", new TokenCounts(1_000_000, 0, 0, 0, 0)).ValueEur;

        Assert.Equal(0.20m, read);
        Assert.Equal(8m, write1h);
        Assert.Equal(4m, input);
    }

    [Fact]
    public void Today_is_the_local_calendar_day_from_midnight_until_now()
    {
        // 29 Sep 2026, 15:00 in Berlin (UTC+2).
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 29, 13, 0, 0, TimeSpan.Zero));
        using var db = UsageDatabase.OpenInMemory();
        var history = new UsageHistoryService(db, new ModelPricingService(), time, Berlin);
        var local = TimeSpan.FromHours(2);

        history.Ingest(
        [
            TestData.Event(AiProviderKind.Claude, "yesterday-23:59", new DateTimeOffset(2026, 9, 28, 23, 59, 0, local), 1),
            TestData.Event(AiProviderKind.Claude, "today-00:01", new DateTimeOffset(2026, 9, 29, 0, 1, 0, local), 10),
            TestData.Event(AiProviderKind.Claude, "today-12:00", new DateTimeOffset(2026, 9, 29, 12, 0, 0, local), 100),
            TestData.Event(AiProviderKind.Claude, "tomorrow-00:30", new DateTimeOffset(2026, 9, 30, 0, 30, 0, local), 1000),

            // 23:30 UTC on the 28th is already 01:30 on the 29th in Berlin.
            TestData.Event(AiProviderKind.Claude, "utc-evening", new DateTimeOffset(2026, 9, 28, 23, 30, 0, TimeSpan.Zero), 10_000),
        ]);

        Assert.Equal(new DateOnly(2026, 9, 29), history.Today);
        Assert.Equal(10_110, history.GetTodayTotals(AiProviderKind.Claude).Tokens.Fresh);
        Assert.Equal(1, history.GetTotals(AiProviderKind.Claude, new DateOnly(2026, 9, 28)).Tokens.Fresh);
    }

    [Fact]
    public void Day_boundaries_follow_daylight_saving_changes()
    {
        // 25 Oct 2026: Berlin switches from UTC+2 to UTC+1 at 03:00 local.
        Assert.Equal(new DateOnly(2026, 10, 25), UsageAggregator.LocalDay(new DateTimeOffset(2026, 10, 24, 22, 30, 0, TimeSpan.Zero), Berlin));
        Assert.Equal(new DateOnly(2026, 10, 25), UsageAggregator.LocalDay(new DateTimeOffset(2026, 10, 25, 22, 59, 0, TimeSpan.Zero), Berlin));
        Assert.Equal(new DateOnly(2026, 10, 26), UsageAggregator.LocalDay(new DateTimeOffset(2026, 10, 25, 23, 0, 0, TimeSpan.Zero), Berlin));
    }

    [Fact]
    public void History_intensity_uses_fresh_tokens_so_cache_heavy_days_do_not_dominate()
    {
        var quiet = new UsageDay(new DateOnly(2026, 9, 1), new TokenCounts(0, 100_000, 0, 200_000, 0), TokenCounts.Zero, 0m, 0m, 1);
        var cacheHeavy = new UsageDay(new DateOnly(2026, 9, 2), new TokenCounts(0, 90_000, 0, 180_000, 250_000_000), TokenCounts.Zero, 0m, 0m, 1);

        Assert.True(cacheHeavy.ProcessedTokens > 100 * quiet.ProcessedTokens);
        Assert.True(cacheHeavy.FreshTokens < quiet.FreshTokens);
    }

    private static TimeZoneInfo? TryZone(string id)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(id);
        }
        catch (TimeZoneNotFoundException)
        {
            return null;
        }
    }
}
