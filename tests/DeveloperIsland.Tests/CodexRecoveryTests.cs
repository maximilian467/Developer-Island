using DeveloperIsland.Core.Models;
using DeveloperIsland.Core.Pricing;
using DeveloperIsland.Core.Providers.Codex;
using DeveloperIsland.Core.Storage;
using DeveloperIsland.Core.Usage;
using Microsoft.Extensions.Time.Testing;

namespace DeveloperIsland.Tests;

public class CodexRecoveryTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Restart_restores_limit_from_unchanged_file_without_duplicate_usage(bool legacyCursor)
    {
        var root = TestData.TempDirectory();
        var folder = Directory.CreateDirectory(Path.Combine(root, "sessions")).FullName;
        var path = Path.Combine(folder, "rollout.jsonl");
        var now = DateTimeOffset.UtcNow;
        File.WriteAllLines(path,
        [
            TestData.CodexMeta("s1", @"C:\code\example"),
            TestData.CodexTokenCount(now, 100, 100, 0, 0, 31, now.AddHours(2).ToUnixTimeSeconds()),
        ]);
        var dbPath = Path.Combine(root, "usage.db");
        using (var db = UsageDatabase.Open(dbPath))
        {
            var history = new UsageHistoryService(db, new ModelPricingService());
            using var first = new CodexUsageProvider(root, history, db);
            await first.StartAsync(TestContext.Current.CancellationToken);
            Assert.Equal(31, first.Snapshot.LimitPercent);
            if (legacyCursor)
            {
                // V1's original cursor has counters but no persisted limit or format version.
                db.SetCursor(db.GetCursor(path)! with { State = "{\"SessionId\":\"s1\",\"LastTotal\":100}" });
            }
        }

        using var reopened = UsageDatabase.Open(dbPath);
        var restoredHistory = new UsageHistoryService(reopened, new ModelPricingService());
        using var restored = new CodexUsageProvider(root, restoredHistory, reopened);
        var activities = new List<object>();
        restored.Activity += activities.Add;
        await restored.StartAsync(TestContext.Current.CancellationToken);
        Assert.Equal(31, restored.Snapshot.LimitPercent);
        Assert.Equal(100, restored.Snapshot.Today.Processed);
        Assert.Empty(activities);
    }

    [Fact]
    public async Task Limit_expires_without_another_file_write()
    {
        var now = DateTimeOffset.UtcNow;
        var time = new FakeTimeProvider(now);
        using var db = UsageDatabase.OpenInMemory();
        var history = new UsageHistoryService(db, new ModelPricingService(), time);
        var root = TestData.TempDirectory();
        var folder = Directory.CreateDirectory(Path.Combine(root, "sessions")).FullName;
        File.WriteAllLines(Path.Combine(folder, "rollout.jsonl"),
        [TestData.CodexTokenCount(now, 100, 100, 0, 0, 75, now.AddMinutes(10).ToUnixTimeSeconds())]);
        using var provider = new CodexUsageProvider(root, history, db, time);
        await provider.StartAsync(TestContext.Current.CancellationToken);
        Assert.Equal(75, provider.Snapshot.LimitPercent);
        time.Advance(TimeSpan.FromMinutes(4)); // Active indicator expires first.
        Assert.Equal(75, provider.Snapshot.LimitPercent);
        time.Advance(TimeSpan.FromMinutes(7));
        Assert.Null(provider.Snapshot.LimitPercent);
    }

    [Theory]
    [InlineData("[{\"type\":\"token_count\"}]")]
    [InlineData("{\"type\":\"token_usage_record\",\"payload\":{\"usage\":null},\"timestamp\":\"2026-09-28T10:00:00Z\"}")]
    [InlineData("{\"type\":\"event_msg\",\"timestamp\":\"2026-09-28T10:00:00Z\",\"payload\":{\"type\":\"token_count\",\"info\":{\"total_token_usage\":42}}}")]
    [InlineData("{\"type\":\"event_msg\",\"timestamp\":\"2026-09-28T10:00:00Z\",\"payload\":{\"type\":\"token_count\",\"info\":{\"total_token_usage\":{\"total_tokens\":100},\"last_token_usage\":[]}}}")]
    public void Foreign_field_shapes_do_not_abort_the_next_usage_record(string line)
    {
        var parser = new CodexLogParser();
        Assert.False(parser.TryParse(line, out _));
        Assert.True(parser.TryParse(TestData.CodexUsageRecord("r1", DateTimeOffset.UtcNow, 10, 0, 1), out var record));
        Assert.Equal(11, record.Tokens.Processed);
    }

    [Theory]
    [InlineData("-1", "300", "1900000000")]
    [InlineData("101", "300", "1900000000")]
    [InlineData("1e999", "300", "1900000000")]
    [InlineData("20", "1.5", "1900000000")]
    [InlineData("20", "2147483648", "1900000000")]
    [InlineData("20", "300", "9223372036854775807")]
    public void Invalid_limits_are_ignored_without_discarding_valid_usage(string percent, string window, string reset)
    {
        var line = """
            {"type":"event_msg","timestamp":"2026-09-28T10:00:00Z","payload":{"type":"token_count",
            "info":{"total_token_usage":{"total_tokens":10},"last_token_usage":{"input_tokens":10}},
            "rate_limits":{"limit_id":"codex","primary":{"used_percent":PERCENT,"window_minutes":WINDOW,"resets_at":RESET}}}}
            """.Replace("PERCENT", percent).Replace("WINDOW", window).Replace("RESET", reset);
        var parser = new CodexLogParser();
        Assert.True(parser.TryParse(line, out _));
        Assert.Null(parser.LastRateLimit);
    }
}
