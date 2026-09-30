using DeveloperIsland.Core.Models;
using DeveloperIsland.Core.Storage;

namespace DeveloperIsland.Tests;

public class SqlitePersistenceTests
{
    private readonly string _path = Path.Combine(TestData.TempDirectory(), "usage.db");
    private static readonly DateTimeOffset T = new(2026, 9, 24, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void New_database_is_migrated_to_the_latest_schema()
    {
        using var db = UsageDatabase.Open(_path);

        Assert.Equal(UsageDatabase.LatestSchemaVersion, db.SchemaVersion);
    }

    [Fact]
    public void Reopening_keeps_the_schema_and_does_not_rerun_migrations()
    {
        UsageDatabase.Open(_path).Dispose();

        using var db = UsageDatabase.Open(_path);

        Assert.Equal(UsageDatabase.LatestSchemaVersion, db.SchemaVersion);
    }

    [Fact]
    public void Events_persist_across_reopen_and_duplicates_are_ignored()
    {
        using (var db = UsageDatabase.Open(_path))
        {
            var first = db.InsertEvents([TestData.Event(AiProviderKind.Claude, "k1", T, 100)], TimeZoneInfo.Utc);
            var again = db.InsertEvents([TestData.Event(AiProviderKind.Claude, "k1", T, 100)], TimeZoneInfo.Utc);

            Assert.Single(first);
            Assert.Empty(again);
        }

        using var reopened = UsageDatabase.Open(_path);
        var events = reopened.GetEvents(AiProviderKind.Claude, new DateOnly(2026, 9, 24));
        var e = Assert.Single(events);
        Assert.Equal("k1", e.Key);
        Assert.Equal(T, e.Timestamp);
        Assert.Equal(100, e.Tokens.Processed);
    }

    [Fact]
    public void Same_key_for_different_providers_is_not_a_duplicate()
    {
        using var db = UsageDatabase.Open(_path);

        db.InsertEvents([TestData.Event(AiProviderKind.Claude, "k", T, 1), TestData.Event(AiProviderKind.Codex, "k", T, 1)], TimeZoneInfo.Utc);

        Assert.Single(db.GetEvents(AiProviderKind.Claude, new DateOnly(2026, 9, 24)));
        Assert.Single(db.GetEvents(AiProviderKind.Codex, new DateOnly(2026, 9, 24)));
    }

    [Fact]
    public void Daily_usage_upserts_replace_the_previous_row()
    {
        var day = new DateOnly(2026, 9, 24);
        using var db = UsageDatabase.Open(_path);

        db.UpsertDaily(new DailyUsage(day, AiProviderKind.Codex, new TokenCounts(1, 2, 3, 4, 5), 1.5m, false, 1));
        db.UpsertDaily(new DailyUsage(day, AiProviderKind.Codex, new TokenCounts(10, 20, 30, 40, 50), 6.02m, true, 2));

        var row = Assert.Single(db.GetDaily(day, day));
        Assert.Equal(150, row.Tokens.Processed);
        Assert.Equal(6.02m, row.ApiValueEur);
        Assert.True(row.ApiValuePartial);
        Assert.Equal(2, row.Sessions);
    }

    [Fact]
    public void File_cursors_round_trip_with_parser_state()
    {
        using var db = UsageDatabase.Open(_path);
        var root = Path.Combine("C:", "logs");
        db.SetCursor(new FileCursor(Path.Combine(root, "a.jsonl"), 100, 42, 90, "{\"model\":\"gpt-5\"}"));
        db.SetCursor(new FileCursor(Path.Combine(root, "a.jsonl"), 200, 43, 180, null));
        db.SetCursor(new FileCursor(Path.Combine("D:", "other.jsonl"), 1, 1, 1, null));

        var cursor = db.GetCursor(Path.Combine(root, "a.jsonl"));
        Assert.Equal(new FileCursor(Path.Combine(root, "a.jsonl"), 200, 43, 180, null), cursor);
        Assert.Single(db.GetCursors(root));
    }

    [Fact]
    public void Focus_sessions_are_grouped_per_day()
    {
        using var db = UsageDatabase.Open(_path);
        var day = new DateOnly(2026, 9, 24);
        db.InsertFocusSession(new FocusSessionRecord(T, T.AddMinutes(25), day, 1500, 1500, true));
        db.InsertFocusSession(new FocusSessionRecord(T, T.AddMinutes(10), day, 3000, 600, false));

        var focus = Assert.Single(db.GetFocusDays(day, day));
        Assert.Equal(2100, focus.FocusedSeconds);
        Assert.Equal(2, focus.Sessions);
    }

    [Fact]
    public void Pruning_removes_old_raw_events_but_keeps_daily_totals()
    {
        using var db = UsageDatabase.Open(_path);
        var old = new DateOnly(2025, 1, 1);
        db.InsertEvents([TestData.Event(AiProviderKind.Claude, "old", new DateTimeOffset(2025, 1, 1, 12, 0, 0, TimeSpan.Zero), 5)], TimeZoneInfo.Utc);
        db.UpsertDaily(new DailyUsage(old, AiProviderKind.Claude, new TokenCounts(5, 0, 0, 0, 0), 0, false, 1));

        Assert.Equal(1, db.PruneEvents(new DateOnly(2026, 1, 1)));
        Assert.Empty(db.GetEvents(AiProviderKind.Claude, old));
        Assert.Single(db.GetDaily(old, old));
    }

    [Fact]
    public void The_database_never_contains_message_content()
    {
        using (var db = UsageDatabase.Open(_path))
        {
            var line = TestData.ClaudeAssistantLine("m", "r", T);
            Assert.True(DeveloperIsland.Core.Usage.ClaudeLogParser.TryParse(line, out var e));
            db.InsertEvents([e], TimeZoneInfo.Utc);
        }

        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        var bytes = File.ReadAllBytes(_path);
        var wal = _path + "-wal";
        var text = System.Text.Encoding.UTF8.GetString(bytes) + (File.Exists(wal) ? System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(wal)) : string.Empty);
        Assert.DoesNotContain("SECRET", text);
    }
}
