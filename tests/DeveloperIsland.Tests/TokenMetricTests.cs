using System.Globalization;
using DeveloperIsland.Core.Models;
using DeveloperIsland.Core.Pricing;
using DeveloperIsland.Core.Settings;
using DeveloperIsland.Core.Storage;
using DeveloperIsland.Core.Usage;
using Microsoft.Extensions.Time.Testing;

namespace DeveloperIsland.Tests;

/// <summary>The "Token metric" setting: which figure is shown, how it is labelled, and how days add up.</summary>
public class TokenMetricTests
{
    private static readonly TimeZoneInfo Berlin = TryZone("W. Europe Standard Time") ?? TryZone("Europe/Berlin")!;

    // A cache-heavy Claude Code turn and a Codex turn with cached input.
    private static readonly TokenCounts Claude = new(Input: 56, Output: 30_040, CacheWrite5m: 0, CacheWrite1h: 2_448_946, CacheRead: 17_711_603);
    private static readonly TokenCounts Codex = new(Input: 4_486, Output: 103, CacheWrite5m: 0, CacheWrite1h: 0, CacheRead: 21_376);

    [Fact]
    public void Fresh_leaves_out_cache_reads()
    {
        Assert.Equal(56 + 2_448_946 + 30_040, Claude.Of(TokenMetric.Fresh));
        Assert.Equal(Claude.Fresh, Claude.Of(TokenMetric.Fresh));
    }

    [Fact]
    public void All_processed_adds_cache_reads()
    {
        Assert.Equal(56 + 2_448_946 + 30_040 + 17_711_603, Claude.Of(TokenMetric.AllProcessed));
        Assert.Equal(Claude.Processed, Claude.Of(TokenMetric.AllProcessed));
    }

    [Theory]
    [InlineData(TokenMetric.Fresh, 2_479_042 + 4_589)]
    [InlineData(TokenMetric.AllProcessed, 2_479_042 + 17_711_603 + 4_589 + 21_376)]
    public void A_day_sums_Claude_and_Codex_in_both_modes(TokenMetric metric, long expected)
    {
        var day = new UsageDay(new DateOnly(2026, 10, 4), Claude, Codex, 0m, 0m, 2);

        Assert.Equal(expected, day.Of(metric));
        Assert.Equal(expected, TokenMetrics.Sum([Claude, Codex], metric));
    }

    [Fact]
    public void Both_modes_count_the_same_categories_for_both_providers()
    {
        var day = new UsageDay(new DateOnly(2026, 10, 4), Claude, Codex, 0m, 0m, 2);

        Assert.Equal(day.Claude.Of(TokenMetric.AllProcessed) - day.Claude.Of(TokenMetric.Fresh), Claude.CacheRead);
        Assert.Equal(day.Codex.Of(TokenMetric.AllProcessed) - day.Codex.Of(TokenMetric.Fresh), Codex.CacheRead);
    }

    [Fact]
    public void Every_figure_says_what_it_counts()
    {
        using var _ = new CultureScope("en-US");

        Assert.Equal("4.58M fresh tokens", TokenMetrics.Format(4_580_000, TokenMetric.Fresh));
        Assert.Equal("390M tokens incl. cache reads", TokenMetrics.Format(390_000_000, TokenMetric.AllProcessed));
        Assert.Equal("390M incl. cache reads", TokenMetrics.Compact(390_000_000, TokenMetric.AllProcessed));

        // Fresh stays as the capsule always showed it.
        Assert.Equal("4.58M", TokenMetrics.Compact(4_580_000, TokenMetric.Fresh));
    }

    [Fact]
    public void Breakdown_names_whether_cache_reads_are_in_the_figure()
    {
        using var _ = new CultureScope("en-US");

        Assert.Equal(
            "Fresh tokens: input 56, cache write 2.45M, output 30k · cache read 17.7M not counted",
            TokenMetrics.Breakdown(Claude, TokenMetric.Fresh));
        Assert.Equal(
            "Tokens incl. cache reads: input 56, cache write 2.45M, output 30k, cache read 17.7M",
            TokenMetrics.Breakdown(Claude, TokenMetric.AllProcessed));
    }

    [Fact]
    public void Graph_intensity_follows_the_chosen_metric()
    {
        // A short day with many fresh tokens, and a long session that mostly re-reads its context.
        var busy = new UsageDay(new DateOnly(2026, 10, 3), new TokenCounts(0, 100_000, 0, 900_000, 1_000_000), TokenCounts.Zero, 0m, 0m, 1);
        var cacheHeavy = new UsageDay(new DateOnly(2026, 10, 4), new TokenCounts(0, 50_000, 0, 450_000, 380_000_000), TokenCounts.Zero, 0m, 0m, 1);
        var empty = new UsageDay(new DateOnly(2026, 10, 5), TokenCounts.Zero, TokenCounts.Zero, 0m, 0m, 0);
        UsageDay[] days = [busy, cacheHeavy, empty];

        var fresh = TokenMetrics.IntensityLevels(days, TokenMetric.Fresh);
        var all = TokenMetrics.IntensityLevels(days, TokenMetric.AllProcessed);

        Assert.True(fresh[0] > fresh[1]);
        Assert.True(all[1] > all[0]);
        Assert.Equal(0, fresh[2]);
        Assert.Equal(0, all[2]);
    }

    [Theory]
    [InlineData(TokenMetric.Fresh)]
    [InlineData(TokenMetric.AllProcessed)]
    public void Local_midnight_splits_days_in_both_modes(TokenMetric metric)
    {
        // 4 Oct 2026, 15:00 in Berlin (UTC+2).
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 4, 13, 0, 0, TimeSpan.Zero));
        using var db = UsageDatabase.OpenInMemory();
        var history = new UsageHistoryService(db, new ModelPricingService(), time, Berlin);
        var local = TimeSpan.FromHours(2);
        var turn = new TokenCounts(Input: 1, Output: 10, CacheWrite5m: 0, CacheWrite1h: 100, CacheRead: 10_000);

        history.Ingest(
        [
            Event(AiProviderKind.Claude, "c-23:59", new DateTimeOffset(2026, 10, 3, 23, 59, 0, local), turn),
            Event(AiProviderKind.Claude, "c-00:01", new DateTimeOffset(2026, 10, 4, 0, 1, 0, local), turn),
            Event(AiProviderKind.Codex, "x-23:59", new DateTimeOffset(2026, 10, 3, 23, 59, 0, local), turn),
            Event(AiProviderKind.Codex, "x-00:01", new DateTimeOffset(2026, 10, 4, 0, 1, 0, local), turn),

            // 22:30 UTC on the 3rd is already 00:30 on the 4th in Berlin.
            Event(AiProviderKind.Codex, "x-utc", new DateTimeOffset(2026, 10, 3, 22, 30, 0, TimeSpan.Zero), turn),
        ]);

        var days = history.GetHistory(2);
        var per = turn.Of(metric);

        Assert.Equal((new DateOnly(2026, 10, 3), 2 * per), (days[0].Day, days[0].Of(metric)));
        Assert.Equal((new DateOnly(2026, 10, 4), 3 * per), (days[1].Day, days[1].Of(metric)));
        Assert.Equal(per, days[1].Claude.Of(metric));
        Assert.Equal(2 * per, days[1].Codex.Of(metric));
    }

    [Fact]
    public void Repeated_usage_blocks_of_one_response_count_once_in_both_modes()
    {
        // Claude Code writes the same usage block once per content block (thinking, text, tool use).
        var ts = new DateTimeOffset(2026, 10, 4, 10, 0, 0, TimeSpan.FromHours(2));
        var lines = Enumerable.Range(0, 3)
            .Select(_ => TestData.ClaudeAssistantLine("msg_1", "req_1", ts, input: 3, output: 359, cacheCreation: 22_074, cacheRead: 26_132, cache1h: 22_074, cache5m: 0))
            .Append(TestData.ClaudeAssistantLine("msg_2", "req_2", ts.AddSeconds(5), input: 1, output: 10, cacheCreation: 100, cacheRead: 48_206, cache1h: 100, cache5m: 0));
        var events = lines.Select(l => ClaudeLogParser.TryParse(l, out var e) ? e : null).OfType<UsageEvent>().ToList();

        var unique = UsageAggregator.Deduplicate(events).Select(e => e.Tokens).ToList();

        Assert.Equal(4, events.Count);
        Assert.Equal(2, unique.Count);
        Assert.Equal(3 + 22_074 + 359 + 1 + 100 + 10, TokenMetrics.Sum(unique, TokenMetric.Fresh));
        Assert.Equal(3 + 22_074 + 359 + 26_132 + 1 + 100 + 10 + 48_206, TokenMetrics.Sum(unique, TokenMetric.AllProcessed));
    }

    [Fact]
    public void Setting_defaults_to_fresh_and_round_trips()
    {
        var path = Path.Combine(TestData.TempDirectory(), "settings.json");
        Assert.Equal(TokenMetric.Fresh, new SettingsStore(path).Current.TokenMetric);

        new SettingsStore(path).Update(s => s.TokenMetric = TokenMetric.AllProcessed);

        Assert.Equal(TokenMetric.AllProcessed, new SettingsStore(path).Current.TokenMetric);
        Assert.Contains("\"allProcessed\"", File.ReadAllText(path), StringComparison.OrdinalIgnoreCase);
    }

    private static UsageEvent Event(AiProviderKind provider, string key, DateTimeOffset ts, TokenCounts tokens) =>
        new(provider, key, ts, provider == AiProviderKind.Claude ? "claude-opus-5-5" : "gpt-5.5-codex", tokens, "s1", "proj");

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

    private sealed class CultureScope : IDisposable
    {
        private readonly CultureInfo _previous = CultureInfo.CurrentCulture;

        public CultureScope(string name) => CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(name);

        public void Dispose() => CultureInfo.CurrentCulture = _previous;
    }
}
