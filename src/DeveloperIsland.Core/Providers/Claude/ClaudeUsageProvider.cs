using System.Diagnostics;
using System.Text.Json;
using DeveloperIsland.Core.Diagnostics;
using DeveloperIsland.Core.Models;
using DeveloperIsland.Core.Storage;
using DeveloperIsland.Core.Usage;

namespace DeveloperIsland.Core.Providers.Claude;

/// <summary>
/// Claude Code usage from local transcripts in <c>~/.claude/projects</c> (or <c>CLAUDE_CONFIG_DIR</c>).
/// Live session state (working directory, busy/idle) comes from <c>~/.claude/sessions/*.json</c>.
/// Claude Code does not store plan-limit percentages locally, so <see cref="AiUsageSnapshot.LimitPercent"/>
/// stays null instead of being guessed.
/// </summary>
public sealed class ClaudeUsageProvider : LogFileUsageProvider
{
    private readonly HashSet<string> _knownSessions = new(StringComparer.Ordinal);
    private readonly object _liveGate = new();
    private IReadOnlyList<LiveSession> _live = [];
    private bool _liveInitialized;

    public ClaudeUsageProvider(string rootDirectory, UsageHistoryService history, UsageDatabase database, TimeProvider? time = null)
        : base(AiProviderKind.Claude, rootDirectory, history, database, time)
    {
    }

    protected override string LogDirectory => Path.Combine(RootDirectory, "projects");

    private string SessionsDirectory => Path.Combine(RootDirectory, "sessions");

    /// <summary>Resolves the Claude Code data folder for the current user.</summary>
    public static string DefaultRoot()
    {
        var configured = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            // CLAUDE_CONFIG_DIR may list several folders separated by commas; the first one is used.
            return configured.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)[0];
        }

        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");
    }

    protected override ParseResult ParseFile(string path, long offset, string? state, List<UsageEvent> events, CancellationToken cancellationToken)
    {
        var newOffset = JsonlTailReader.ReadLines(path, offset, line =>
        {
            if (ClaudeLogParser.TryParse(line, out var e))
            {
                events.Add(e);
            }
        }, cancellationToken);
        return new ParseResult(newOffset, null);
    }

    protected override void ConfigureWatcher(FileSystemWatcher watcher)
    {
        // The watcher root is ~/.claude/projects; live sessions live in a sibling folder.
        if (!Directory.Exists(SessionsDirectory))
        {
            return;
        }

        var sessions = new FileSystemWatcher(SessionsDirectory, "*.json")
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite,
        };
        FileSystemEventHandler handler = (_, _) => OnLiveSessionsChanged();
        sessions.Changed += handler;
        sessions.Created += handler;
        sessions.Deleted += handler;
        sessions.EnableRaisingEvents = true;
        _sessionsWatcher = sessions;
    }

    private FileSystemWatcher? _sessionsWatcher;
    private ITimer? _liveDebounce;

    protected override void OnInitialScanCompleted()
    {
        ReadLiveSessions(raiseEvents: false);
    }

    protected override void OnDisposing()
    {
        _sessionsWatcher?.Dispose();
        _liveDebounce?.Dispose();
    }

    protected override AiUsageSnapshot Enrich(AiUsageSnapshot snapshot)
    {
        IReadOnlyList<LiveSession> live;
        lock (_liveGate)
        {
            live = _live;
        }

        var busy = live.Where(s => s.IsBusy).OrderByDescending(s => s.UpdatedAt).FirstOrDefault();
        var mostRecent = live.OrderByDescending(s => s.UpdatedAt).FirstOrDefault();
        var project = (busy ?? mostRecent)?.Project ?? snapshot.CurrentProject;
        return snapshot with
        {
            CurrentProject = project,
            IsActive = snapshot.IsActive || busy is not null,
        };
    }

    private void OnLiveSessionsChanged()
    {
        _liveDebounce ??= Time.CreateTimer(_ =>
        {
            ReadLiveSessions(raiseEvents: true);
            RefreshSnapshot();
        }, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _liveDebounce.Change(TimeSpan.FromMilliseconds(500), Timeout.InfiniteTimeSpan);
    }

    private void ReadLiveSessions(bool raiseEvents)
    {
        var sessions = new List<LiveSession>();
        try
        {
            if (Directory.Exists(SessionsDirectory))
            {
                foreach (var file in Directory.EnumerateFiles(SessionsDirectory, "*.json"))
                {
                    if (TryReadLiveSession(file, out var session))
                    {
                        sessions.Add(session);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Debug("provider", "Live sessions unavailable", new { error = ex.GetType().Name });
        }

        List<LiveSession> started = [];
        lock (_liveGate)
        {
            foreach (var s in sessions)
            {
                if (_knownSessions.Add(s.SessionId) && _liveInitialized)
                {
                    started.Add(s);
                }
            }

            _live = sessions;
            _liveInitialized = true;
        }

        if (raiseEvents)
        {
            foreach (var s in started)
            {
                RaiseActivity(new AiActivity(AiProviderKind.Claude, AiActivityKind.SessionStarted, "Claude Code session started", s.Project));
            }
        }
    }

    /// <summary>Reads only pid, session id, cwd, status and timestamps from a live session file.</summary>
    private static bool TryReadLiveSession(string file, out LiveSession session)
    {
        session = default!;
        try
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var doc = JsonDocument.Parse(stream);
            var root = doc.RootElement;
            var id = ClaudeLogParser.GetString(root, "sessionId");
            if (id is null)
            {
                return false;
            }

            if (root.TryGetProperty("pid", out var pid) && pid.TryGetInt32(out var pidValue) && !IsProcessAlive(pidValue))
            {
                return false;
            }

            var updated = root.TryGetProperty("updatedAt", out var u) && u.TryGetInt64(out var ms)
                ? DateTimeOffset.FromUnixTimeMilliseconds(ms)
                : File.GetLastWriteTimeUtc(file);
            session = new LiveSession(
                id,
                ClaudeLogParser.ProjectNameFromPath(ClaudeLogParser.GetString(root, "cwd")),
                string.Equals(ClaudeLogParser.GetString(root, "status"), "busy", StringComparison.OrdinalIgnoreCase),
                updated);
            return true;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool IsProcessAlive(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    private sealed record LiveSession(string SessionId, string? Project, bool IsBusy, DateTimeOffset UpdatedAt);
}
