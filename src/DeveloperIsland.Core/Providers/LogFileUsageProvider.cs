using DeveloperIsland.Core.Diagnostics;
using DeveloperIsland.Core.Models;
using DeveloperIsland.Core.Storage;
using DeveloperIsland.Core.Usage;

namespace DeveloperIsland.Core.Providers;

/// <summary>
/// Shared machinery for providers that read JSONL logs written by a local tool.
/// <list type="bullet">
/// <item>An initial scan on a background thread only parses bytes appended since the last run (file cursors).</item>
/// <item>A <see cref="FileSystemWatcher"/> then drives incremental reads, debounced, with no polling.</item>
/// <item>One timer fires at local midnight so "today" rolls over, and one clears the active flag after inactivity.</item>
/// </list>
/// </summary>
public abstract class LogFileUsageProvider : IAiUsageProvider
{
    /// <summary>Offset after the last complete line, plus optional parser state to persist.</summary>
    protected readonly record struct ParseResult(long Offset, string? State);

    /// <summary>History older than this is not backfilled on first run.</summary>
    protected static readonly TimeSpan BackfillWindow = TimeSpan.FromDays(190);

    /// <summary>A tool counts as active while it wrote usage within this window.</summary>
    protected static readonly TimeSpan ActiveWindow = TimeSpan.FromMinutes(3);

    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(900);

    private readonly object _gate = new();
    private readonly HashSet<string> _pendingFiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _readLock = new(1, 1);
    private FileSystemWatcher? _watcher;
    private ITimer? _debounceTimer;
    private ITimer? _midnightTimer;
    private ITimer? _activityTimer;
    private CancellationTokenSource? _cts;
    private bool _fullRescanRequested;
    private AiUsageSnapshot _snapshot;
    private bool _disposed;

    protected LogFileUsageProvider(AiProviderKind kind, string rootDirectory, UsageHistoryService history, UsageDatabase database, TimeProvider? time = null)
    {
        Kind = kind;
        RootDirectory = rootDirectory;
        History = history;
        Database = database;
        Time = time ?? TimeProvider.System;
        _snapshot = AiUsageSnapshot.Initial(kind);
    }

    public event Action<AiUsageSnapshot>? SnapshotChanged;

    public event Action<AiActivity>? Activity;

    public AiProviderKind Kind { get; }

    public AiUsageSnapshot Snapshot
    {
        get
        {
            lock (_gate)
            {
                return _snapshot;
            }
        }
    }

    protected string RootDirectory { get; }

    protected UsageHistoryService History { get; }

    protected UsageDatabase Database { get; }

    protected TimeProvider Time { get; }

    /// <summary>The folder whose <c>*.jsonl</c> files are parsed.</summary>
    protected abstract string LogDirectory { get; }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (!Directory.Exists(RootDirectory))
        {
            Log.Info("provider", "Tool not detected", new { provider = Kind.ToString() });
            SetSnapshot(AiUsageSnapshot.Initial(Kind) with { State = ProviderState.NotDetected, Day = History.Today });
            WatchForInstallation();
            return Task.CompletedTask;
        }

        return Task.Run(() => RunInitialScan(_cts.Token), _cts.Token);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        _cts?.Cancel();
        _watcher?.Dispose();
        _debounceTimer?.Dispose();
        _midnightTimer?.Dispose();
        _activityTimer?.Dispose();
        OnDisposing();
        GC.SuppressFinalize(this);
    }

    /// <summary>Parses one file from its cursor, appending events. Returns where to resume next time.</summary>
    protected abstract ParseResult ParseFile(string path, long offset, string? state, List<UsageEvent> events, CancellationToken cancellationToken);

    /// <summary>Adds provider-specific live information (limits, current project, live sessions).</summary>
    protected virtual AiUsageSnapshot Enrich(AiUsageSnapshot snapshot) => snapshot;

    protected virtual void OnInitialScanCompleted()
    {
    }

    /// <summary>Restores metadata from an unchanged file. False requests a cursor upgrade.</summary>
    protected virtual bool TryRestoreFileState(FileCursor cursor) => true;

    protected virtual void OnDisposing()
    {
    }

    protected void RaiseActivity(AiActivity activity) => Activity?.Invoke(activity);

    /// <summary>Recomputes today's snapshot from the database and publishes it.</summary>
    protected void RefreshSnapshot()
    {
        try
        {
            var today = History.Today;
            var totals = History.GetTodayTotals(Kind);
            var now = Time.GetUtcNow();
            var last = totals.Latest?.Timestamp;
            var snapshot = new AiUsageSnapshot
            {
                Provider = Kind,
                State = ProviderState.Ready,
                Day = today,
                Today = totals.Tokens,
                ApiValueEur = totals.DisplayValueEur,
                ApiValuePartial = totals.HasPricedTokens && totals.HasUnpricedTokens,
                SessionsToday = totals.Sessions,
                CurrentModel = totals.Latest?.Model,
                CurrentProject = totals.Latest?.Project,
                CurrentProjectPath = totals.Latest?.ProjectPath,
                LastActivity = last,
                IsActive = last is { } l && now - l < ActiveWindow,
            };
            snapshot = Enrich(snapshot);
            SetSnapshot(snapshot);

            if (snapshot.IsActive && last is { } lastActivity)
            {
                // Clear the active flag once the window has passed without new writes.
                _activityTimer?.Dispose();
                _activityTimer = Time.CreateTimer(_ => RefreshSnapshot(), null, ActiveRecheckDelay(now, lastActivity), Timeout.InfiniteTimeSpan);
            }
        }
        catch (Exception ex)
        {
            Log.Error("provider", "Snapshot refresh failed", ex, new { provider = Kind.ToString() });
            SetSnapshot(Snapshot with { State = ProviderState.Error, ErrorMessage = $"Couldn't read {Kind} data. Details are in the log." });
        }
    }

    /// <summary>
    /// When to look again whether the provider is still active. A busy live session can keep it active
    /// after the last write is older than the window; the delay then restarts instead of going negative
    /// (which made the timer throw and the provider report an error).
    /// </summary>
    internal static TimeSpan ActiveRecheckDelay(DateTimeOffset now, DateTimeOffset lastActivity)
    {
        var due = ActiveWindow - (now - lastActivity) + TimeSpan.FromSeconds(1);
        return due >= TimeSpan.FromSeconds(1) ? due : ActiveWindow;
    }

    private void SetSnapshot(AiUsageSnapshot snapshot)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _snapshot = snapshot;
        }

        SnapshotChanged?.Invoke(snapshot);
    }

    private void RunInitialScan(CancellationToken ct)
    {
        try
        {
            SetSnapshot(Snapshot with { State = ProviderState.Starting, Day = History.Today });
            StartWatcher();
            ScanAll(ct);
            ScheduleMidnight();
            OnInitialScanCompleted();
            RefreshSnapshot();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Log.Error("provider", "Initial scan failed", ex, new { provider = Kind.ToString() });
            SetSnapshot(Snapshot with { State = ProviderState.Error, ErrorMessage = $"Couldn't read {Kind} data. Details are in the log." });
        }
    }

    private void ScanAll(CancellationToken ct)
    {
        if (!Directory.Exists(LogDirectory))
        {
            return;
        }

        var started = Environment.TickCount64;
        var cursors = Database.GetCursors(LogDirectory);
        var cutoff = Time.GetUtcNow().UtcDateTime - BackfillWindow;
        var files = 0;
        var events = 0;

        foreach (var path in Directory.EnumerateFiles(LogDirectory, "*.jsonl", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();
            var info = new FileInfo(path);
            if (info.LastWriteTimeUtc < cutoff)
            {
                continue;
            }

            cursors.TryGetValue(path, out var cursor);
            if (cursor is not null && cursor.Length == info.Length && cursor.LastWriteUtcTicks == info.LastWriteTimeUtc.Ticks
                && TryRestoreFileState(cursor))
            {
                continue;
            }

            events += ProcessFile(path, cursor, ct);
            files++;
        }

        Log.Info("provider", "Scan finished", new { provider = Kind.ToString(), files, events, ms = Environment.TickCount64 - started });
    }

    private int ProcessFile(string path, FileCursor? cursor, CancellationToken ct)
    {
        _readLock.Wait(ct);
        try
        {
            cursor ??= Database.GetCursor(path);
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                return 0;
            }

            var offset = cursor?.Offset ?? 0;
            var state = cursor?.State;
            if (offset > info.Length)
            {
                offset = 0;
                state = null;
            }

            var events = new List<UsageEvent>();
            var result = ParseFile(path, offset, state, events, ct);
            if (events.Count > 0)
            {
                // Chunked so one huge backfill does not hold the database lock for long.
                foreach (var chunk in events.Chunk(2000))
                {
                    History.Ingest(chunk);
                }
            }

            Database.SetCursor(new FileCursor(path, info.Length, info.LastWriteTimeUtc.Ticks, result.Offset, result.State));
            return events.Count;
        }
        catch (IOException ex)
        {
            // Typically a file being rotated or locked for a moment; the next change retries it.
            Log.Debug("provider", "File skipped", new { provider = Kind.ToString(), error = ex.GetType().Name });
            return 0;
        }
        finally
        {
            _readLock.Release();
        }
    }

    private void StartWatcher()
    {
        var directory = Directory.Exists(LogDirectory) ? LogDirectory : RootDirectory;
        _watcher = new FileSystemWatcher(directory)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.DirectoryName,
            InternalBufferSize = 64 * 1024,
        };
        _watcher.Changed += OnFileEvent;
        _watcher.Created += OnFileEvent;
        _watcher.Renamed += (s, e) => OnFileEvent(s, e);
        _watcher.Error += (_, e) =>
        {
            Log.Warn("provider", "Watcher overflow; scheduling a rescan", new { provider = Kind.ToString() }, e.GetException());
            lock (_gate)
            {
                _fullRescanRequested = true;
            }

            ScheduleFlush();
        };
        ConfigureWatcher(_watcher);
        _watcher.EnableRaisingEvents = true;
    }

    /// <summary>Lets subclasses react to additional files (for example live session metadata).</summary>
    protected virtual void ConfigureWatcher(FileSystemWatcher watcher)
    {
    }

    /// <summary>Called for changes that are not <c>*.jsonl</c> usage logs.</summary>
    protected virtual bool OnOtherFileChanged(string path) => false;

    private void OnFileEvent(object sender, FileSystemEventArgs e)
    {
        var path = e.FullPath;
        if (path.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase)
            && path.StartsWith(LogDirectory, StringComparison.OrdinalIgnoreCase))
        {
            lock (_gate)
            {
                _pendingFiles.Add(path);
            }

            ScheduleFlush();
        }
        else if (OnOtherFileChanged(path))
        {
            ScheduleFlush();
        }
    }

    private void ScheduleFlush()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _debounceTimer ??= Time.CreateTimer(_ => Flush(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            _debounceTimer.Change(Debounce, Timeout.InfiniteTimeSpan);
        }
    }

    private void Flush()
    {
        string[] files;
        bool rescan;
        lock (_gate)
        {
            files = _pendingFiles.ToArray();
            _pendingFiles.Clear();
            rescan = _fullRescanRequested;
            _fullRescanRequested = false;
        }

        var ct = _cts?.Token ?? CancellationToken.None;
        try
        {
            if (rescan)
            {
                ScanAll(ct);
            }
            else
            {
                foreach (var file in files)
                {
                    ProcessFile(file, null, ct);
                }
            }

            RefreshSnapshot();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Log.Error("provider", "Incremental read failed", ex, new { provider = Kind.ToString() });
        }
    }

    private void ScheduleMidnight()
    {
        var now = Time.GetLocalNow();
        var next = new DateTimeOffset(now.Date.AddDays(1), now.Offset);
        var due = next - now + TimeSpan.FromSeconds(2);
        _midnightTimer?.Dispose();
        _midnightTimer = Time.CreateTimer(_ =>
        {
            RefreshSnapshot();
            ScheduleMidnight();
        }, null, due, Timeout.InfiniteTimeSpan);
    }

    /// <summary>Waits (without polling) for the tool's folder to appear, then starts normally.</summary>
    private void WatchForInstallation()
    {
        var parent = Path.GetDirectoryName(RootDirectory.TrimEnd('\\', '/'));
        if (parent is null || !Directory.Exists(parent))
        {
            return;
        }

        var name = Path.GetFileName(RootDirectory.TrimEnd('\\', '/'));
        _watcher = new FileSystemWatcher(parent, name) { NotifyFilter = NotifyFilters.DirectoryName };
        _watcher.Created += (_, _) =>
        {
            _watcher?.Dispose();
            _watcher = null;
            Log.Info("provider", "Tool folder appeared", new { provider = Kind.ToString() });
            _ = Task.Run(() => RunInitialScan(_cts?.Token ?? CancellationToken.None));
        };
        _watcher.EnableRaisingEvents = true;
    }
}
