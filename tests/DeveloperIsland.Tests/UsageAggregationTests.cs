using DeveloperIsland.Core.Models;
using DeveloperIsland.Core.Pricing;
using DeveloperIsland.Core.Usage;

namespace DeveloperIsland.Tests;

public class UsageAggregationTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 24, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Claude_parser_reads_usage_metadata_only()
    {
        var line = TestData.ClaudeAssistantLine("msg_1", "req_1", T0, input: 3, output: 359, cacheCreation: 22074, cacheRead: 26132, cache1h: 22074, cache5m: 0);

        Assert.True(ClaudeLogParser.TryParse(line, out var e));
        Assert.Equal(AiProviderKind.Claude, e.Provider);
        Assert.Equal("msg_1:req_1", e.Key);
        Assert.Equal("claude-opus-5-5", e.Model);
        Assert.Equal(new TokenCounts(3, 359, 0, 22074, 26132), e.Tokens);
        Assert.Equal("island", e.Project);
        Assert.Equal(T0, e.Timestamp);

        // The parsed record carries no content at all.
        Assert.DoesNotContain("SECRET", e.ToString());
    }

    [Fact]
    public void Claude_cache_creation_without_split_counts_as_5_minute_writes()
    {
        var line = TestData.ClaudeAssistantLine("m", "r", T0, cacheCreation: 900);

        Assert.True(ClaudeLogParser.TryParse(line, out var e));
        Assert.Equal(900, e.Tokens.CacheWrite5m);
        Assert.Equal(0, e.Tokens.CacheWrite1h);
    }

    [Theory]
    [InlineData("""{"type":"user","message":{"role":"user","content":"hi"},"timestamp":"2026-09-24T10:00:00Z"}""")]
    [InlineData("""{"type":"assistant","timestamp":"2026-09-24T10:00:00Z","message":{"model":"<synthetic>","usage":{"input_tokens":5,"output_tokens":5}}}""")]
    [InlineData("""{"type":"assistant","timestamp":"2026-09-24T10:00:00Z","message":{"model":"claude-opus-5","usage":{"input_tokens":0,"output_tokens":0}}}""")]
    [InlineData("""{"type":"assistant","message":{"model":"claude-opus-5","usage":{"input_tokens":1,""")]
    [InlineData("not json but mentions \"usage\" and \"assistant\" anyway")]
    public void Claude_parser_ignores_irrelevant_or_broken_lines(string line)
    {
        Assert.False(ClaudeLogParser.TryParse(line, out _));
    }

    [Fact]
    public void Repeated_content_blocks_of_one_response_are_counted_once()
    {
        // Claude Code writes one line per content block; all share message id and usage.
        var lines = Enumerable.Range(0, 3).Select(_ => TestData.ClaudeAssistantLine("msg_A", "req_A", T0, input: 100, output: 50, cacheCreation: 0, cacheRead: 0));
        var events = lines.Select(l => ClaudeLogParser.TryParse(l, out var e) ? e : null).OfType<UsageEvent>().ToList();

        var totals = UsageAggregator.Summarize(events, new ModelPricingService());

        Assert.Equal(3, events.Count);
        Assert.Equal(150, totals.Tokens.Processed);
    }

    [Fact]
    public void Codex_usage_records_split_cached_input_and_use_response_ids()
    {
        var parser = new CodexLogParser();
        parser.TryParse(TestData.CodexMeta("sess-1", @"C:\code\api-gateway"), out _);
        parser.TryParse(TestData.CodexTurnContext("gpt-5-codex", @"C:\code\api-gateway"), out _);

        Assert.True(parser.TryParse(TestData.CodexUsageRecord("resp_1", T0, input: 25862, cached: 21376, output: 103), out var e));
        Assert.Equal("resp_1", e.Key);
        Assert.Equal("gpt-5-codex", e.Model);
        Assert.Equal("api-gateway", e.Project);
        Assert.Equal("sess-1", e.SessionId);
        Assert.Equal(new TokenCounts(25862 - 21376, 103, 0, 0, 21376), e.Tokens);
        Assert.Equal(25965, e.Tokens.Processed);
    }

    [Fact]
    public void Codex_token_count_is_ignored_when_usage_records_exist()
    {
        var parser = new CodexLogParser();
        parser.TryParse(TestData.CodexUsageRecord("resp_1", T0, 1000, 0, 10), out _);

        Assert.False(parser.TryParse(TestData.CodexTokenCount(T0, total: 1010, lastInput: 1000, lastCached: 0, lastOutput: 10), out _));
        Assert.NotNull(parser.LastRateLimit);
    }

    [Fact]
    public void Codex_legacy_token_counts_skip_repeated_totals()
    {
        var parser = new CodexLogParser();
        var events = new List<UsageEvent>();
        foreach (var line in new[]
        {
            TestData.CodexTokenCount(T0, total: 500, lastInput: 450, lastCached: 100, lastOutput: 50),
            TestData.CodexTokenCount(T0.AddSeconds(1), total: 500, lastInput: 450, lastCached: 100, lastOutput: 50), // duplicate emission
            TestData.CodexTokenCount(T0.AddSeconds(2), total: 800, lastInput: 280, lastCached: 0, lastOutput: 20),
        })
        {
            if (parser.TryParse(line, out var e))
            {
                events.Add(e);
            }
        }

        Assert.Equal(2, events.Count);
        Assert.Equal(800, events.Sum(e => e.Tokens.Processed));
    }

    [Fact]
    public void Codex_rate_limit_is_read_from_token_count_events()
    {
        var parser = new CodexLogParser();
        parser.TryParse(TestData.CodexTokenCount(T0, 10, 10, 0, 0, usedPercent: 31, resetsAt: 1_790_213_413), out _);

        Assert.NotNull(parser.LastRateLimit);
        Assert.Equal(31, parser.LastRateLimit!.UsedPercent);
        Assert.Equal(300, parser.LastRateLimit.WindowMinutes);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1_790_213_413), parser.LastRateLimit.ResetsAt);
    }

    [Fact]
    public void Codex_parser_state_survives_serialization_between_reads()
    {
        var first = new CodexLogParser();
        first.TryParse(TestData.CodexMeta("sess-9", @"C:\code\web"), out _);
        first.TryParse(TestData.CodexTurnContext("gpt-5-codex", @"C:\code\web"), out _);
        var json = System.Text.Json.JsonSerializer.Serialize(first.State);

        var resumed = new CodexLogParser(System.Text.Json.JsonSerializer.Deserialize<CodexFileState>(json));
        Assert.True(resumed.TryParse(TestData.CodexUsageRecord("resp_2", T0, 100, 0, 5), out var e));

        Assert.Equal("gpt-5-codex", e.Model);
        Assert.Equal("web", e.Project);
    }

    [Fact]
    public void Summary_marks_partial_value_when_some_models_are_unpriced()
    {
        var events = new[]
        {
            TestData.Event(AiProviderKind.Codex, "a", T0, 1_000_000, model: "gpt-5-codex"),
            TestData.Event(AiProviderKind.Codex, "b", T0, 1_000_000, model: "gpt-6-astra"),
        };

        var totals = UsageAggregator.Summarize(events, new ModelPricingService(new PricingOverrides { UsdToEur = 1m }));

        Assert.True(totals.HasPricedTokens);
        Assert.True(totals.HasUnpricedTokens);
        Assert.Equal(1.25m, totals.DisplayValueEur);
        Assert.Equal(2_000_000, totals.Tokens.Processed);
    }

    [Fact]
    public void Summary_has_no_value_when_nothing_is_priced()
    {
        var totals = UsageAggregator.Summarize([TestData.Event(AiProviderKind.Codex, "a", T0, 10, model: "gpt-6-astra")], new ModelPricingService());

        Assert.Null(totals.DisplayValueEur);
    }

    [Fact]
    public void Tail_reader_leaves_an_unterminated_line_for_later()
    {
        var path = Path.Combine(TestData.TempDirectory(), "t.jsonl");
        File.WriteAllText(path, "{\"a\":1}\n{\"b\":2}\r\n{\"c\":");
        var lines = new List<string>();

        var offset = JsonlTailReader.ReadLines(path, 0, lines.Add, TestContext.Current.CancellationToken);
        Assert.Equal(["{\"a\":1}", "{\"b\":2}"], lines);

        File.AppendAllText(path, "3}\n");
        lines.Clear();
        JsonlTailReader.ReadLines(path, offset, lines.Add, TestContext.Current.CancellationToken);
        Assert.Equal(["{\"c\":3}"], lines);
    }

    [Fact]
    public void Tail_reader_restarts_after_truncation()
    {
        var path = Path.Combine(TestData.TempDirectory(), "t.jsonl");
        File.WriteAllText(path, "{\"x\":1}\n");
        var lines = new List<string>();

        JsonlTailReader.ReadLines(path, 10_000, lines.Add, TestContext.Current.CancellationToken);

        Assert.Single(lines);
    }
}
