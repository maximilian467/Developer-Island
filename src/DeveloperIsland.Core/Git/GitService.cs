using DeveloperIsland.Core.Diagnostics;
using DeveloperIsland.Core.Modules;

namespace DeveloperIsland.Core.Git;

/// <summary>
/// Local Git status for a handful of known repositories: the ones added in Settings and the project
/// folders of recent Claude Code and Codex sessions. Never searches the disk.
/// <list type="bullet">
/// <item>Event-driven: a watcher on each repository's <c>.git</c> folder reacts to commits, checkouts,
/// staging and fetches (HEAD, index, refs). Nothing polls.</item>
/// <item>Working-tree edits do not touch <c>.git</c>, so the panel refreshes when it is shown and when
/// an AI session reports activity in that folder.</item>
/// <item>Reads use <c>--no-optional-locks</c>, so git never rewrites the index on our behalf (which
/// would also retrigger the watcher).</item>
/// </list>
/// </summary>
public sealed class GitService : IDisposable
{
    public const int MaxRecent = 5;
    private static readonly TimeSpan ActivityThrottle = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(600);
    private static readonly StringComparer PathComparer = StringComparer.OrdinalIgnoreCase;

    private readonly Func<string?> _findGit;
    private readonly TimeProvider _time;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _readLock = new(1, 1);
    private readonly Dictionary<string, GitRepoStatus> _status = new(PathComparer);
    private readonly Dictionary<string, string> _errors = new(PathComparer);
    private readonly Dictionary<string, FileSystemWatcher> _watchers = new(PathComparer);
    private readonly Dictionary<string, CancellationTokenSource> _pending = new(PathComparer);
    private readonly Dictionary<string, DateTimeOffset> _lastRead = new(PathComparer);
    private List<string> _configured = [];
    private List<string> _recent = [];
    private string? _active;
    private string? _git;
    private bool _enabled;
    private bool _disposed;

    public GitService(Func<string?>? findGit = null, TimeProvider? time = null)
    {
        _findGit = findGit ?? (() => ProcessRunner.FindExecutable("git", @"%ProgramFiles%\Git\cmd\git.exe"));
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Raised on a background thread when any repository's status changed.</summary>
    public event Action? Changed;

    /// <summary>The list of recent project repositories changed (to persist it).</summary>
    public event Action<IReadOnlyList<string>>? RecentChanged;

    public ModuleStatus Status
    {
        get
        {
            lock (_gate)
            {
                if (!_enabled)
                {
                    return ModuleStatus.Disabled;
                }

                if (_git is null)
                {
                    return new ModuleStatus(ModuleState.Unavailable, "Git is not installed. Install Git for Windows to see branches and changes here.");
                }

                var roots = RootsLocked();
                if (roots.Count == 0)
                {
                    return new ModuleStatus(ModuleState.Empty, "Start Claude Code or Codex in a repository, or add one in Settings.");
                }

                if (roots.Any(_status.ContainsKey))
                {
                    return ModuleStatus.Ready;
                }

                return roots.All(_errors.ContainsKey)
                    ? new ModuleStatus(ModuleState.Error, "Git could not read these repositories.")
                    : ModuleStatus.Loading;
            }
        }
    }

    /// <summary>Repositories with a status: the active one first, then the others.</summary>
    public IReadOnlyList<GitRepoStatus> Repositories
    {
        get
        {
            lock (_gate)
            {
                return RootsLocked().Where(_status.ContainsKey).Select(r => _status[r]).ToList();
            }
        }
    }

    public GitRepoStatus? Active => Repositories.FirstOrDefault();

    public IReadOnlyList<string> Recent
    {
        get
        {
            lock (_gate)
            {
                return _recent.ToList();
            }
        }
    }

    /// <summary>Applies settings. Starts or stops watchers; reads what is new.</summary>
    public void Configure(bool enabled, IEnumerable<string> configured, IEnumerable<string> recent)
    {
        List<string> toRead;
        lock (_gate)
        {
            _enabled = enabled && !_disposed;
            _git = _enabled ? _findGit() : null;
            _configured = Distinct(configured.Select(GitStatusParser.FindRepositoryRoot)).ToList();
            if (_recent.Count == 0)
            {
                _recent = Distinct(recent.Select(GitStatusParser.FindRepositoryRoot)).Take(MaxRecent).ToList();
            }

            var roots = _enabled && _git is not null ? RootsLocked() : [];
            foreach (var stale in _watchers.Keys.Where(k => !roots.Contains(k, PathComparer)).ToList())
            {
                _watchers[stale].Dispose();
                _watchers.Remove(stale);
            }

            foreach (var root in roots)
            {
                Watch(root);
            }

            toRead = roots.Where(r => !_status.ContainsKey(r)).ToList();
        }

        foreach (var root in toRead)
        {
            _ = ReadAsync(root);
        }

        Changed?.Invoke();
    }

    /// <summary>
    /// An AI session is working in <paramref name="path"/>: make its repository the active one and
    /// refresh it (at most every 10 s, since sessions write constantly).
    /// </summary>
    public void NoteProjectPath(string? path)
    {
        var root = GitStatusParser.FindRepositoryRoot(path);
        if (root is null)
        {
            return;
        }

        bool listChanged;
        bool read;
        List<string> recent;
        lock (_gate)
        {
            if (!_enabled || _git is null)
            {
                return;
            }

            listChanged = _recent.Count == 0 || !PathComparer.Equals(_recent[0], root);
            _recent.RemoveAll(r => PathComparer.Equals(r, root));
            _recent.Insert(0, root);
            if (_recent.Count > MaxRecent)
            {
                _recent.RemoveRange(MaxRecent, _recent.Count - MaxRecent);
            }

            _active = root;
            recent = _recent.ToList();
            Watch(root);
            read = !_lastRead.TryGetValue(root, out var last) || _time.GetUtcNow() - last >= ActivityThrottle;
        }

        if (listChanged)
        {
            RecentChanged?.Invoke(recent);
            Changed?.Invoke();
        }

        if (read)
        {
            _ = ReadAsync(root);
        }
    }

    /// <summary>Makes a repository the active one (the user picked it).</summary>
    public void SetActive(string root)
    {
        lock (_gate)
        {
            _active = RootsLocked().FirstOrDefault(r => PathComparer.Equals(r, root)) ?? _active;
        }

        Changed?.Invoke();
    }

    /// <summary>Re-reads every repository not read in the last few seconds (the panel was opened).</summary>
    public async Task RefreshAsync()
    {
        List<string> roots;
        lock (_gate)
        {
            if (!_enabled)
            {
                return;
            }

            _git ??= _findGit();
            var now = _time.GetUtcNow();
            roots = RootsLocked().Where(r => !_lastRead.TryGetValue(r, out var last) || now - last > TimeSpan.FromSeconds(3)).ToList();
        }

        foreach (var root in roots)
        {
            await ReadAsync(root);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _enabled = false;
            foreach (var watcher in _watchers.Values)
            {
                watcher.Dispose();
            }

            _watchers.Clear();
            foreach (var pending in _pending.Values)
            {
                pending.Cancel();
            }

            _pending.Clear();
        }
    }

    private List<string> RootsLocked()
    {
        var roots = new List<string>();
        if (_active is not null)
        {
            roots.Add(_active);
        }

        roots.AddRange(_recent);
        roots.AddRange(_configured);
        return Distinct(roots).ToList();
    }

    private static IEnumerable<string> Distinct(IEnumerable<string?> roots) =>
        roots.Where(r => r is not null).Select(r => r!).Distinct(PathComparer);

    private void Watch(string root)
    {
        if (_watchers.ContainsKey(root))
        {
            return;
        }

        var gitDir = GitStatusParser.GitDirectory(root);
        if (gitDir is null)
        {
            return;
        }

        try
        {
            var watcher = new FileSystemWatcher(gitDir)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.DirectoryName,
            };
            FileSystemEventHandler handler = (_, e) => OnGitChanged(root, gitDir, e.FullPath);
            watcher.Changed += handler;
            watcher.Created += handler;
            watcher.Deleted += handler;
            watcher.Renamed += (_, e) => OnGitChanged(root, gitDir, e.FullPath);
            watcher.EnableRaisingEvents = true;
            _watchers[root] = watcher;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            Log.Debug("git", "Repository not watched", new { error = ex.GetType().Name });
        }
    }

    /// <summary>Only files that change what the module shows; objects and logs are ignored.</summary>
    internal static bool IsRelevant(string gitDir, string fullPath)
    {
        var relative = Path.GetRelativePath(gitDir, fullPath).Replace('\\', '/');
        return relative is "HEAD" or "index" or "packed-refs" or "FETCH_HEAD" or "ORIG_HEAD" or "MERGE_HEAD" or "config"
            || relative.StartsWith("refs/", StringComparison.Ordinal);
    }

    private void OnGitChanged(string root, string gitDir, string fullPath)
    {
        if (!IsRelevant(gitDir, fullPath))
        {
            return;
        }

        CancellationTokenSource cts;
        lock (_gate)
        {
            if (_pending.TryGetValue(root, out var previous))
            {
                previous.Cancel();
            }

            cts = new CancellationTokenSource();
            _pending[root] = cts;
        }

        _ = Task.Delay(Debounce, cts.Token).ContinueWith(
            _ => ReadAsync(root),
            cts.Token,
            TaskContinuationOptions.OnlyOnRanToCompletion,
            TaskScheduler.Default);
    }

    private async Task ReadAsync(string root)
    {
        string? git;
        lock (_gate)
        {
            git = _git;
            if (!_enabled || git is null)
            {
                return;
            }
        }

        await _readLock.WaitAsync();
        try
        {
            var status = await ReadRepositoryAsync(git, root);
            lock (_gate)
            {
                _lastRead[root] = _time.GetUtcNow();
                if (status is null)
                {
                    _errors[root] = "unreadable";
                    _status.Remove(root);
                }
                else
                {
                    _errors.Remove(root);
                    _status[root] = status;
                }
            }

            Changed?.Invoke();
        }
        catch (Exception ex)
        {
            Log.Debug("git", "Repository read failed", new { error = ex.GetType().Name });
        }
        finally
        {
            _readLock.Release();
        }
    }

    private async Task<GitRepoStatus?> ReadRepositoryAsync(string git, string root)
    {
        var status = await ProcessRunner.RunAsync(git, ["--no-optional-locks", "-C", root, "status", "--porcelain=v2", "--branch"], timeout: TimeSpan.FromSeconds(10));
        if (!status.Succeeded)
        {
            Log.Debug("git", "git status failed", new { exit = status.ExitCode });
            return null;
        }

        var parsed = GitStatusParser.ParseStatus(root, status.Output);
        var log = await ProcessRunner.RunAsync(git, ["--no-optional-locks", "-C", root, "log", "-1", "--format=%ct%x1f%s"], timeout: TimeSpan.FromSeconds(10));
        var (at, subject) = log.Succeeded ? GitStatusParser.ParseLastCommit(log.Output) : (null, null);
        var remote = await ProcessRunner.RunAsync(git, ["-C", root, "config", "--get", "remote.origin.url"], timeout: TimeSpan.FromSeconds(5));

        return parsed with
        {
            LastCommitAt = at,
            LastCommitSubject = subject,
            GitHubRepository = remote.Succeeded ? GitStatusParser.ParseGitHubRepository(remote.Output) : null,
            UpdatedAt = _time.GetUtcNow(),
        };
    }
}
