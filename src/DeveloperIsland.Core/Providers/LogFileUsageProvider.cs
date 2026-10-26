using DeveloperIsland.Core.Diagnostics;
using DeveloperIsland.Core.Models;
using DeveloperIsland.Core.Storage;
using DeveloperIsland.Core.Usage;
using DeveloperIsland.Core.Wsl;

namespace DeveloperIsland.Core.Providers;

/// <summary>
/// Shared machinery for providers that read JSONL logs written by a local tool.
/// <list type="bullet">
/// <item>An initial scan on a background thread only parses bytes appended since the last run (file cursors).</item>
/// <item>A <see cref="FileSystemWatcher"/> then drives incremental reads, debounced, with no polling.</item>
/// <item>One timer fires at local midnight so "today" rolls over, and one clears the active flag after inactivity.</item>
/// <item>Copies of the tool inside WSL distros (<see cref="SetWslFolders"/>) count into the same totals.
/// The WSL share delivers no reliable change notifications, so those folders are polled: recently
/// written files every 5 s, the whole folder every minute. Every event is stored once per key, so a
/// file reached twice is never counted twice.</item>
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

    private static readonly TimeSpan WslPollInterval = TimeSpan.FromSeconds(5);

    private static readonly TimeSpan WslRescanInterval = TimeSpan.FromMinutes(1);

    /// <summary>Files written this recently are checked on every WSL poll; older ones at the next rescan.</summary>
    private static readonly TimeSpan HotWindow = TimeSpan.FromMinutes(15);

    private readonly object _gate = new();
    private readonly HashSet<string> _pendingFiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _readLock = new(1, 1);
    private FileSystemWatcher? _watcher;
    private ITimer? _debounceTimer;
    private ITimer? _midnightTimer;
    private ITimer? _activityTimer;
    private ITimer? _wslTimer;
    private Func<CancellationToken, Task<IReadOnlyList<string>>>? _wslFolders;
    private readonly Dictionary<string, PolledFolder> _polled = new(StringComparer.OrdinalIgnoreCase);
    private int _wslPolling;
    private bool _started;
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

    /// <summary>The subfolder of a tool folder whose <c>*.jsonl</c> files are parsed.</summary>
    protected abstract string LogFolderName { get; }

    /// <summary>The folder whose <c>*.jsonl</c> files are parsed.</summary>
    protected string LogDirectory => Path.Combine(RootDirectory, LogFolderName);

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        lock (_gate)
        {
            _started = true;
            UpdateWslTimerLocked();
        }

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
        lock (_gate)
        {
            _wslTimer?.Dispose();
            _wslTimer = null;
        }

        OnDisposing();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// The tool's folders inside WSL distros (<c>\\wsl.localhost\&lt;distro&gt;\home\&lt;user&gt;\.claude</c>),
    /// asked on every poll so started and stopped distros are followed; null stops reading WSL.
    /// </summary>
    public void SetWslFolders(Func<CancellationToken, Task<IReadOnlyList<string>>>? folders)
    {
        lock (_gate)
        {
            _wslFolders = folders;
            if (folders is null)
            {
                _polled.Clear();
            }

            UpdateWslTimerLocked();
        }
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

    private void ScanAll(CancellationToken ct) => ScanDirectory(LogDirectory, null, null, ct);

    /// <summary>
    /// Reads every log in <paramref name="directory"/> that changed since its cursor. With
    /// <paramref name="hot"/>, also records the recently written files for the next WSL polls.
    /// </summary>
    private int ScanDirectory(string directory, Func<string?, string?>? mapPath, Dictionary<string, FileStamp>? hot, CancellationToken ct)
    {
        if (!Directory.Exists(directory))
        {
            return 0;
        }

        var started = Environment.TickCount64;
        var cursors = Database.GetCursors(directory);
        var cutoff = Time.GetUtcNow().UtcDateTime - BackfillWindow;
        var hotCutoff = Time.GetUtcNow().UtcDateTime - HotWindow;
        var files = 0;
        var events = 0;
        hot?.Clear();

        foreach (var path in Directory.EnumerateFiles(directory, "*.jsonl", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();
            var info = new FileInfo(path);
            if (info.LastWriteTimeUtc < cutoff)
            {
                continue;
            }

            if (hot is not null && info.LastWriteTimeUtc >= hotCutoff)
            {
                hot[path] = new FileStamp(info.Length, info.LastWriteTimeUtc.Ticks);
            }

            cursors.TryGetValue(path, out var cursor);
            if (cursor is not null && cursor.Length == info.Length && cursor.LastWriteUtcTicks == info.LastWriteTimeUtc.Ticks
                && TryRestoreFileState(cursor))
            {
                continue;
            }

            events += ProcessFile(path, cursor, ct, mapPath);
            files++;
        }

        // WSL folders are rescanned every minute; only scans that found something are worth a line.
        if (hot is null || files > 0)
        {
            Log.Info("provider", "Scan finished", new { provider = Kind.ToString(), wsl = hot is not null, files, events, ms = Environment.TickCount64 - started });
        }

        return events;
    }

    private int ProcessFile(string path, FileCursor? cursor, CancellationToken ct, Func<string?, string?>? mapPath = null)
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
            if (mapPath is not null)
            {
                // Tools inside WSL record Linux working folders; the Git module needs the Windows path.
                for (var i = 0; i < events.Count; i++)
                {
                    events[i] = events[i] with { ProjectPath = mapPath(events[i].ProjectPath) };
                }
            }

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

    private void UpdateWslTimerLocked()
    {
        var needed = _started && !_disposed && _wslFolders is not null;
        if (needed && _wslTimer is null)
        {
            _wslTimer = Time.CreateTimer(_ => _ = PollWslAsync(), null, TimeSpan.FromSeconds(1), WslPollInterval);
        }
        else if (!needed && _wslTimer is not null)
        {
            _wslTimer.Dispose();
            _wslTimer = null;
        }
    }

    /// <summary>
    /// One WSL poll: asks which folders exist now (running distros only), rescans each folder every
    /// minute and checks its recently written files in between. Never runs twice at once.
    /// </summary>
    internal async Task PollWslAsync()
    {
        if (Interlocked.Exchange(ref _wslPolling, 1) == 1)
        {
            return;
        }

        try
        {
            Func<CancellationToken, Task<IReadOnlyList<string>>>? source;
            lock (_gate)
            {
                source = _wslFolders;
            }

            var ct = _cts?.Token ?? CancellationToken.None;
            if (source is null || ct.IsCancellationRequested)
            {
                return;
            }

            // The Windows folder is read by the watcher; a WSL folder that is the same place (for
            // example CLAUDE_CONFIG_DIR pointing into WSL) is not read a second time.
            var own = WslPath.Normalize(RootDirectory.TrimEnd('\\', '/'));
            var folders = (await source(ct)).Select(f => WslPath.Normalize(f.TrimEnd('\\', '/')))
                .Where(f => !string.Equals(f, own, StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var now = Time.GetUtcNow();
            var events = 0;
            var found = false;
            foreach (var folder in folders)
            {
                ct.ThrowIfCancellationRequested();
                var directory = Path.Combine(folder, LogFolderName);
                var distro = WslPath.DistroOf(folder);
                Func<string?, string?>? map = distro is null ? null : p => WslPath.ToWindows(distro, p);
                PolledFolder? state;
                lock (_gate)
                {
                    if (!_polled.TryGetValue(folder, out state))
                    {
                        state = new PolledFolder();
                        _polled[folder] = state;
                    }
                }

                if (now - state.LastScan >= WslRescanInterval)
                {
                    var first = state.LastScan == DateTimeOffset.MinValue;
                    events += await Task.Run(() => ScanDirectory(directory, map, state.Hot, ct), ct);
                    state.LastScan = now;
                    found |= first && Directory.Exists(directory);
                    continue;
                }

                foreach (var (path, stamp) in state.Hot.ToList())
                {
                    var info = new FileInfo(path);
                    if (!info.Exists)
                    {
                        state.Hot.Remove(path);
                        continue;
                    }

                    var current = new FileStamp(info.Length, info.LastWriteTimeUtc.Ticks);
                    if (current != stamp)
                    {
                        events += ProcessFile(path, null, ct, map);
                        state.Hot[path] = current;
                    }
                }
            }

            lock (_gate)
            {
                foreach (var gone in _polled.Keys.Where(k => !folders.Contains(k, StringComparer.OrdinalIgnoreCase)).ToList())
                {
                    _polled.Remove(gone);
                }
            }

            if (events > 0 || found)
            {
                if (_midnightTimer is null)
                {
                    // Usage only from WSL: no scan of the Windows folder scheduled the day roll-over.
                    ScheduleMidnight();
                }

                RefreshSnapshot();
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A distro stopping mid-read: the next poll asks again which folders exist.
            Log.Debug("provider", "WSL poll skipped", new { provider = Kind.ToString(), error = ex.GetType().Name });
        }
        catch (Exception ex)
        {
            Log.Error("provider", "WSL poll failed", ex, new { provider = Kind.ToString() });
        }
        finally
        {
            Interlocked.Exchange(ref _wslPolling, 0);
        }
    }

    private readonly record struct FileStamp(long Length, long LastWriteTicks);

    private sealed class PolledFolder
    {
        public DateTimeOffset LastScan { get; set; } = DateTimeOffset.MinValue;

        public Dictionary<string, FileStamp> Hot { get; } = new(StringComparer.OrdinalIgnoreCase);
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
