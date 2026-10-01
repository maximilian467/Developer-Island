using System.Globalization;
using DeveloperIsland.Core.Models;
using DeveloperIsland.Core.Usage;
using Microsoft.Data.Sqlite;

namespace DeveloperIsland.Core.Storage;

/// <summary>Read position of one log file, so providers only parse appended bytes.</summary>
public sealed record FileCursor(string Path, long Length, long LastWriteUtcTicks, long Offset, string? State);

/// <summary>A finished (or stopped) focus session.</summary>
public sealed record FocusSessionRecord(
    DateTimeOffset StartedAt,
    DateTimeOffset EndedAt,
    DateOnly Day,
    int PlannedSeconds,
    int FocusedSeconds,
    bool Completed);

/// <summary>Focus totals of one day.</summary>
public sealed record FocusDay(DateOnly Day, int FocusedSeconds, int Sessions);

/// <summary>
/// Local SQLite store for aggregated metadata: usage events (tokens, model, timestamps), daily totals,
/// focus sessions and file read cursors. It never stores prompt, chat or code content.
/// One connection is shared and guarded by a lock; all operations are short.
/// </summary>
public sealed class UsageDatabase : IDisposable
{
    /// <summary>Ordered schema migrations. Append only; never edit a shipped step.</summary>
    private static readonly string[] Migrations =
    [
        // 1: initial schema
        """
        CREATE TABLE usage_events (
            provider    INTEGER NOT NULL,
            event_key   TEXT    NOT NULL,
            ts_ms       INTEGER NOT NULL,
            day         TEXT    NOT NULL,
            model       TEXT    NOT NULL,
            input       INTEGER NOT NULL,
            output      INTEGER NOT NULL,
            cache_w5m   INTEGER NOT NULL,
            cache_w1h   INTEGER NOT NULL,
            cache_read  INTEGER NOT NULL,
            session_id  TEXT,
            project     TEXT,
            PRIMARY KEY (provider, event_key)
        ) WITHOUT ROWID;
        CREATE INDEX ix_usage_events_day ON usage_events (day, provider);

        CREATE TABLE daily_usage (
            day            TEXT    NOT NULL,
            provider       INTEGER NOT NULL,
            input          INTEGER NOT NULL,
            output         INTEGER NOT NULL,
            cache_w5m      INTEGER NOT NULL,
            cache_w1h      INTEGER NOT NULL,
            cache_read     INTEGER NOT NULL,
            total_tokens   INTEGER NOT NULL,
            api_value_eur  REAL    NOT NULL,
            partial        INTEGER NOT NULL,
            sessions       INTEGER NOT NULL,
            PRIMARY KEY (day, provider)
        ) WITHOUT ROWID;

        CREATE TABLE file_cursors (
            path        TEXT    PRIMARY KEY,
            length      INTEGER NOT NULL,
            mtime_ticks INTEGER NOT NULL,
            offset      INTEGER NOT NULL,
            state       TEXT
        );

        CREATE TABLE focus_sessions (
            id               INTEGER PRIMARY KEY AUTOINCREMENT,
            started_ms       INTEGER NOT NULL,
            ended_ms         INTEGER NOT NULL,
            day              TEXT    NOT NULL,
            planned_seconds  INTEGER NOT NULL,
            focused_seconds  INTEGER NOT NULL,
            completed        INTEGER NOT NULL
        );
        CREATE INDEX ix_focus_sessions_day ON focus_sessions (day);
        """,

        // 2: the highest plan usage actually reported per day and window (never computed or backfilled).
        """
        CREATE TABLE plan_usage_peaks (
            day          TEXT    NOT NULL,
            provider     INTEGER NOT NULL,
            window       TEXT    NOT NULL,
            peak_percent REAL    NOT NULL,
            measured_ms  INTEGER NOT NULL,
            PRIMARY KEY (day, provider, window)
        ) WITHOUT ROWID;
        """,
    ];

    private readonly SqliteConnection _connection;
    private readonly object _gate = new();

    private UsageDatabase(SqliteConnection connection)
    {
        _connection = connection;
    }

    public int SchemaVersion { get; private set; }

    public static int LatestSchemaVersion => Migrations.Length;

    /// <summary>Opens (and creates or migrates) the database file.</summary>
    public static UsageDatabase Open(string path)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path))!);
        var builder = new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false };
        return OpenConnection(builder.ToString(), wal: true);
    }

    /// <summary>A private in-memory database, used by demo mode and tests. Nothing touches disk.</summary>
    public static UsageDatabase OpenInMemory() => OpenConnection("Data Source=:memory:", wal: false);

    private static UsageDatabase OpenConnection(string connectionString, bool wal)
    {
        var connection = new SqliteConnection(connectionString);
        connection.Open();
        var db = new UsageDatabase(connection);
        if (wal)
        {
            db.Execute("PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;");
        }

        db.Migrate();
        return db;
    }

    private void Migrate()
    {
        lock (_gate)
        {
            var version = Convert.ToInt32(Scalar("PRAGMA user_version;"), CultureInfo.InvariantCulture);
            for (var i = version; i < Migrations.Length; i++)
            {
                using var tx = _connection.BeginTransaction();
                using (var cmd = _connection.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandText = Migrations[i];
                    cmd.ExecuteNonQuery();
                }

                using (var cmd = _connection.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandText = $"PRAGMA user_version = {i + 1};";
                    cmd.ExecuteNonQuery();
                }

                tx.Commit();
            }

            SchemaVersion = Math.Max(version, Migrations.Length);
        }
    }

    /// <summary>Inserts new events; duplicates are ignored. Returns only the events that were new, with their local day.</summary>
    public IReadOnlyList<(UsageEvent Event, DateOnly Day)> InsertEvents(IEnumerable<UsageEvent> events, TimeZoneInfo zone)
    {
        var inserted = new List<(UsageEvent, DateOnly)>();
        lock (_gate)
        {
            using var tx = _connection.BeginTransaction();
            using var cmd = _connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT OR IGNORE INTO usage_events
                    (provider, event_key, ts_ms, day, model, input, output, cache_w5m, cache_w1h, cache_read, session_id, project)
                VALUES ($p, $k, $ts, $day, $model, $in, $out, $w5, $w1, $cr, $sid, $proj);
                """;
            var p = cmd.Parameters.Add("$p", SqliteType.Integer);
            var k = cmd.Parameters.Add("$k", SqliteType.Text);
            var ts = cmd.Parameters.Add("$ts", SqliteType.Integer);
            var day = cmd.Parameters.Add("$day", SqliteType.Text);
            var model = cmd.Parameters.Add("$model", SqliteType.Text);
            var input = cmd.Parameters.Add("$in", SqliteType.Integer);
            var output = cmd.Parameters.Add("$out", SqliteType.Integer);
            var w5 = cmd.Parameters.Add("$w5", SqliteType.Integer);
            var w1 = cmd.Parameters.Add("$w1", SqliteType.Integer);
            var cr = cmd.Parameters.Add("$cr", SqliteType.Integer);
            var sid = cmd.Parameters.Add("$sid", SqliteType.Text);
            var proj = cmd.Parameters.Add("$proj", SqliteType.Text);

            foreach (var e in events)
            {
                var localDay = UsageAggregator.LocalDay(e.Timestamp, zone);
                p.Value = (int)e.Provider;
                k.Value = e.Key;
                ts.Value = e.Timestamp.ToUnixTimeMilliseconds();
                day.Value = FormatDay(localDay);
                model.Value = e.Model;
                input.Value = e.Tokens.Input;
                output.Value = e.Tokens.Output;
                w5.Value = e.Tokens.CacheWrite5m;
                w1.Value = e.Tokens.CacheWrite1h;
                cr.Value = e.Tokens.CacheRead;
                sid.Value = (object?)e.SessionId ?? DBNull.Value;
                proj.Value = (object?)e.Project ?? DBNull.Value;
                if (cmd.ExecuteNonQuery() > 0)
                {
                    inserted.Add((e, localDay));
                }
            }

            tx.Commit();
        }

        return inserted;
    }

    public IReadOnlyList<UsageEvent> GetEvents(AiProviderKind provider, DateOnly day)
    {
        lock (_gate)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                SELECT event_key, ts_ms, model, input, output, cache_w5m, cache_w1h, cache_read, session_id, project
                FROM usage_events WHERE day = $day AND provider = $p ORDER BY ts_ms;
                """;
            cmd.Parameters.AddWithValue("$day", FormatDay(day));
            cmd.Parameters.AddWithValue("$p", (int)provider);
            using var reader = cmd.ExecuteReader();
            var list = new List<UsageEvent>();
            while (reader.Read())
            {
                list.Add(new UsageEvent(
                    provider,
                    reader.GetString(0),
                    DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(1)),
                    reader.GetString(2),
                    new TokenCounts(reader.GetInt64(3), reader.GetInt64(4), reader.GetInt64(5), reader.GetInt64(6), reader.GetInt64(7)),
                    reader.IsDBNull(8) ? null : reader.GetString(8),
                    reader.IsDBNull(9) ? null : reader.GetString(9)));
            }

            return list;
        }
    }

    public void UpsertDaily(DailyUsage daily)
    {
        lock (_gate)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                INSERT INTO daily_usage (day, provider, input, output, cache_w5m, cache_w1h, cache_read, total_tokens, api_value_eur, partial, sessions)
                VALUES ($day, $p, $in, $out, $w5, $w1, $cr, $total, $value, $partial, $sessions)
                ON CONFLICT (day, provider) DO UPDATE SET
                    input = excluded.input, output = excluded.output, cache_w5m = excluded.cache_w5m,
                    cache_w1h = excluded.cache_w1h, cache_read = excluded.cache_read, total_tokens = excluded.total_tokens,
                    api_value_eur = excluded.api_value_eur, partial = excluded.partial, sessions = excluded.sessions;
                """;
            cmd.Parameters.AddWithValue("$day", FormatDay(daily.Day));
            cmd.Parameters.AddWithValue("$p", (int)daily.Provider);
            cmd.Parameters.AddWithValue("$in", daily.Tokens.Input);
            cmd.Parameters.AddWithValue("$out", daily.Tokens.Output);
            cmd.Parameters.AddWithValue("$w5", daily.Tokens.CacheWrite5m);
            cmd.Parameters.AddWithValue("$w1", daily.Tokens.CacheWrite1h);
            cmd.Parameters.AddWithValue("$cr", daily.Tokens.CacheRead);
            cmd.Parameters.AddWithValue("$total", daily.Tokens.Processed);
            cmd.Parameters.AddWithValue("$value", (double)daily.ApiValueEur);
            cmd.Parameters.AddWithValue("$partial", daily.ApiValuePartial ? 1 : 0);
            cmd.Parameters.AddWithValue("$sessions", daily.Sessions);
            cmd.ExecuteNonQuery();
        }
    }

    public IReadOnlyList<DailyUsage> GetDaily(DateOnly from, DateOnly to)
    {
        lock (_gate)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                SELECT day, provider, input, output, cache_w5m, cache_w1h, cache_read, api_value_eur, partial, sessions
                FROM daily_usage WHERE day >= $from AND day <= $to ORDER BY day, provider;
                """;
            cmd.Parameters.AddWithValue("$from", FormatDay(from));
            cmd.Parameters.AddWithValue("$to", FormatDay(to));
            using var reader = cmd.ExecuteReader();
            var list = new List<DailyUsage>();
            while (reader.Read())
            {
                list.Add(new DailyUsage(
                    ParseDay(reader.GetString(0)),
                    (AiProviderKind)reader.GetInt32(1),
                    new TokenCounts(reader.GetInt64(2), reader.GetInt64(3), reader.GetInt64(4), reader.GetInt64(5), reader.GetInt64(6)),
                    (decimal)reader.GetDouble(7),
                    reader.GetInt32(8) != 0,
                    reader.GetInt32(9)));
            }

            return list;
        }
    }

    /// <summary>Keeps the highest reported plan usage of a day and window ("five_hour", "seven_day").</summary>
    public void RecordPlanPeak(DateOnly day, AiProviderKind provider, string window, double percent, DateTimeOffset measuredAt)
    {
        lock (_gate)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                INSERT INTO plan_usage_peaks (day, provider, window, peak_percent, measured_ms)
                VALUES ($day, $provider, $window, $percent, $ms)
                ON CONFLICT (day, provider, window) DO UPDATE SET
                    peak_percent = MAX(peak_percent, excluded.peak_percent),
                    measured_ms = excluded.measured_ms;
                """;
            cmd.Parameters.AddWithValue("$day", FormatDay(day));
            cmd.Parameters.AddWithValue("$provider", (int)provider);
            cmd.Parameters.AddWithValue("$window", window);
            cmd.Parameters.AddWithValue("$percent", percent);
            cmd.Parameters.AddWithValue("$ms", measuredAt.ToUnixTimeMilliseconds());
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>Recorded plan peaks per day for one provider: day to (window to percent).</summary>
    public IReadOnlyDictionary<DateOnly, IReadOnlyDictionary<string, double>> GetPlanPeaks(AiProviderKind provider, DateOnly from, DateOnly to)
    {
        lock (_gate)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT day, window, peak_percent FROM plan_usage_peaks WHERE provider = $provider AND day >= $from AND day <= $to;";
            cmd.Parameters.AddWithValue("$provider", (int)provider);
            cmd.Parameters.AddWithValue("$from", FormatDay(from));
            cmd.Parameters.AddWithValue("$to", FormatDay(to));
            using var reader = cmd.ExecuteReader();
            var result = new Dictionary<DateOnly, Dictionary<string, double>>();
            while (reader.Read())
            {
                var day = ParseDay(reader.GetString(0));
                if (!result.TryGetValue(day, out var windows))
                {
                    result[day] = windows = new Dictionary<string, double>(StringComparer.Ordinal);
                }

                windows[reader.GetString(1)] = reader.GetDouble(2);
            }

            return result.ToDictionary(kv => kv.Key, kv => (IReadOnlyDictionary<string, double>)kv.Value);
        }
    }

    public FileCursor? GetCursor(string path)
    {
        lock (_gate)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT length, mtime_ticks, offset, state FROM file_cursors WHERE path = $path;";
            cmd.Parameters.AddWithValue("$path", path);
            using var reader = cmd.ExecuteReader();
            return reader.Read()
                ? new FileCursor(path, reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), reader.IsDBNull(3) ? null : reader.GetString(3))
                : null;
        }
    }

    public IReadOnlyDictionary<string, FileCursor> GetCursors(string pathPrefix)
    {
        lock (_gate)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT path, length, mtime_ticks, offset, state FROM file_cursors WHERE substr(path, 1, length($prefix)) = $prefix;";
            cmd.Parameters.AddWithValue("$prefix", pathPrefix);
            using var reader = cmd.ExecuteReader();
            var map = new Dictionary<string, FileCursor>(StringComparer.OrdinalIgnoreCase);
            while (reader.Read())
            {
                var path = reader.GetString(0);
                map[path] = new FileCursor(path, reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3), reader.IsDBNull(4) ? null : reader.GetString(4));
            }

            return map;
        }
    }

    public void SetCursor(FileCursor cursor)
    {
        lock (_gate)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                INSERT INTO file_cursors (path, length, mtime_ticks, offset, state) VALUES ($path, $len, $mt, $off, $state)
                ON CONFLICT (path) DO UPDATE SET length = excluded.length, mtime_ticks = excluded.mtime_ticks,
                    offset = excluded.offset, state = excluded.state;
                """;
            cmd.Parameters.AddWithValue("$path", cursor.Path);
            cmd.Parameters.AddWithValue("$len", cursor.Length);
            cmd.Parameters.AddWithValue("$mt", cursor.LastWriteUtcTicks);
            cmd.Parameters.AddWithValue("$off", cursor.Offset);
            cmd.Parameters.AddWithValue("$state", (object?)cursor.State ?? DBNull.Value);
            cmd.ExecuteNonQuery();
        }
    }

    public void InsertFocusSession(FocusSessionRecord session)
    {
        lock (_gate)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                INSERT INTO focus_sessions (started_ms, ended_ms, day, planned_seconds, focused_seconds, completed)
                VALUES ($s, $e, $day, $planned, $focused, $done);
                """;
            cmd.Parameters.AddWithValue("$s", session.StartedAt.ToUnixTimeMilliseconds());
            cmd.Parameters.AddWithValue("$e", session.EndedAt.ToUnixTimeMilliseconds());
            cmd.Parameters.AddWithValue("$day", FormatDay(session.Day));
            cmd.Parameters.AddWithValue("$planned", session.PlannedSeconds);
            cmd.Parameters.AddWithValue("$focused", session.FocusedSeconds);
            cmd.Parameters.AddWithValue("$done", session.Completed ? 1 : 0);
            cmd.ExecuteNonQuery();
        }
    }

    public IReadOnlyList<FocusDay> GetFocusDays(DateOnly from, DateOnly to)
    {
        lock (_gate)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                SELECT day, SUM(focused_seconds), COUNT(*) FROM focus_sessions
                WHERE day >= $from AND day <= $to GROUP BY day ORDER BY day;
                """;
            cmd.Parameters.AddWithValue("$from", FormatDay(from));
            cmd.Parameters.AddWithValue("$to", FormatDay(to));
            using var reader = cmd.ExecuteReader();
            var list = new List<FocusDay>();
            while (reader.Read())
            {
                list.Add(new FocusDay(ParseDay(reader.GetString(0)), reader.GetInt32(1), reader.GetInt32(2)));
            }

            return list;
        }
    }

    /// <summary>Deletes raw events older than the cutoff; daily totals are kept.</summary>
    public int PruneEvents(DateOnly olderThan)
    {
        lock (_gate)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "DELETE FROM usage_events WHERE day < $day;";
            cmd.Parameters.AddWithValue("$day", FormatDay(olderThan));
            return cmd.ExecuteNonQuery();
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _connection.Dispose();
        }
    }

    internal static string FormatDay(DateOnly day) => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    internal static DateOnly ParseDay(string text) => DateOnly.ParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture);

    private void Execute(string sql)
    {
        lock (_gate)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = sql;
            cmd.ExecuteNonQuery();
        }
    }

    private object? Scalar(string sql)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar();
    }
}
