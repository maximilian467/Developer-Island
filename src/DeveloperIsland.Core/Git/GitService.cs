using DeveloperIsland.Core.Diagnostics;
using DeveloperIsland.Core.Modules;
using DeveloperIsland.Core.Wsl;

namespace DeveloperIsland.Core.Git;

/// <summary>Why a repository has no status.</summary>
public enum GitReadFailure
{
    Unreadable,
    WslMissing,
    DistroStopped,
    GitMissingInDistro,
}

/// <summary>
/// Local Git status for a handful of known repositories: the ones added in Settings and the project
/// folders of recent Claude Code and Codex sessions. Never searches the disk.
/// <list type="bullet">
/// <item>Event-driven: a watcher on each repository's <c>.git</c> folder reacts to commits, checkouts,
/// staging and fetches (HEAD, index, refs). Nothing polls.</item>
/// <item>Working-tree edits do not touch <c>.git</c>, so the panel refreshes when it is shown, and
/// while it is shown, when an AI session reports activity in that folder. The compact island shows
/// no Git data, so a hidden panel costs no git processes.</item>
/// <item>Reads use <c>--no-optional-locks</c>, so git never rewrites the index on our behalf (which
/// would also retrigger the watcher).</item>
/// <item>Repositories inside WSL (<c>\\wsl.localhost\…</c>) run the distro's git through
/// <c>wsl.exe</c> and only while the distro runs, so reading never boots one. The WSL share sends no
/// reliable change notifications, so instead of a watcher they are re-read every 10 s while the panel
/// is on screen.</item>
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
    private readonly IWslRuntime? _wsl;
    private readonly Func<string, IReadOnlyList<string>, TimeSpan, Task<ProcessResult>> _run;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _readLock = new(1, 1);
    private readonly Dictionary<string, GitRepoStatus> _status = new(PathComparer);
    private readonly Dictionary<string, GitReadFailure> _errors = new(PathComparer);
    private readonly Dictionary<string, FileSystemWatcher> _watchers = new(PathComparer);
    private readonly Dictionary<string, CancellationTokenSource> _pending = new(PathComparer);
    private readonly Dictionary<string, DateTimeOffset> _lastRead = new(PathComparer);
    private readonly Dictionary<string, string?> _wslRoots = new(PathComparer);
    private List<string> _configured = [];
    private List<string> _recent = [];
    private string? _active;
    private string? _git;
    private ITimer? _wslPoll;
    private bool _enabled;
    private bool _visible;
    private bool _disposed;

    public GitService(
        Func<string?>? findGit = null,
        TimeProvider? time = null,
        IWslRuntime? wsl = null,
        Func<string, IReadOnlyList<string>, TimeSpan, Task<ProcessResult>>? run = null)
    {
        _findGit = findGit ?? (() => ProcessRunner.FindExecutable("git", @"%ProgramFiles%\Git\cmd\git.exe"));
        _time = time ?? TimeProvider.System;
        _wsl = wsl;
        _run = run ?? ((file, args, timeout) => ProcessRunner.RunAsync(file, args, timeout: timeout));
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

                var roots = ReadableRootsLocked();
                if (_git is null && roots.Count == 0)
                {
                    return new ModuleStatus(ModuleState.Unavailable, "Git is not installed. Install Git for Windows to see branches and changes here.");
                }

                if (roots.Count == 0)
                {
                    return new ModuleStatus(ModuleState.Empty, "Start Claude Code or Codex in a repository, or add one in Settings.");
                }

                if (roots.Any(_status.ContainsKey))
                {
                    return ModuleStatus.Ready;
                }

                return roots.All(_errors.ContainsKey) ? FailureStatus(roots[0], _errors[roots[0]]) : ModuleStatus.Loading;
            }
        }
    }

    /// <summary>
    /// A line under the panel's content when some repositories are missing for a known reason (a
    /// stopped distro); null otherwise.
    /// </summary>
    public string? Note
    {
        get
        {
            lock (_gate)
            {
                var stopped = ReadableRootsLocked()
                    .Where(r => _errors.TryGetValue(r, out var failure) && failure == GitReadFailure.DistroStopped)
                    .Select(r => WslPath.DistroOf(r)!)
                    .Distinct(PathComparer)
                    .ToList();
                return StoppedNote(stopped);
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
            _configured = Distinct(configured.Select(KnownRoot)).ToList();
            if (_recent.Count == 0)
            {
                _recent = Distinct(recent.Select(KnownRoot)).Take(MaxRecent).ToList();
            }

            var roots = _enabled ? ReadableRootsLocked() : [];
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
            UpdateWslPollLocked();
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
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        if (!WslPath.IsWslPath(path))
        {
            NoteRoot(GitStatusParser.FindRepositoryRoot(path));
            return;
        }

        // Inside WSL the walk up to .git goes over the WSL share: done once per folder, off the UI thread.
        var folder = WslPath.Normalize(path);
        lock (_gate)
        {
            if (_wslRoots.TryGetValue(folder, out var known))
            {
                NoteRoot(known);
                return;
            }
        }

        _ = Task.Run(() =>
        {
            var root = GitStatusParser.FindRepositoryRoot(folder) is { } found ? WslPath.Normalize(found) : null;
            lock (_gate)
            {
                _wslRoots[folder] = root;
            }

            NoteRoot(root);
        });
    }

    /// <summary>The Git panel is (or is no longer) on screen.</summary>
    public void SetVisible(bool visible)
    {
        lock (_gate)
        {
            _visible = visible;
            UpdateWslPollLocked();
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
            roots = ReadableRootsLocked().Where(r => !_lastRead.TryGetValue(r, out var last) || now - last > TimeSpan.FromSeconds(3)).ToList();
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
            _wslPoll?.Dispose();
            _wslPoll = null;
        }
    }

    /// <summary>
    /// A saved repository path as a root. Windows folders are looked up on disk; WSL paths are taken as
    /// saved, since looking at them would start a stopped distro.
    /// </summary>
    private static string? KnownRoot(string? path) =>
        WslPath.IsWslPath(path) ? WslPath.Normalize(path!) : GitStatusParser.FindRepositoryRoot(path);

    private void NoteRoot(string? root)
    {
        if (root is null)
        {
            return;
        }

        bool listChanged;
        bool read;
        List<string> recent;
        lock (_gate)
        {
            if (!_enabled || !CanReadLocked(root))
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
            UpdateWslPollLocked();
            // First sight of a repository: read once (branch and remote feed GitHub). Afterwards only
            // while the panel is on screen.
            read = !_lastRead.TryGetValue(root, out var last) || (_visible && _time.GetUtcNow() - last >= ActivityThrottle);
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

    /// <summary>Roots a program exists for: Windows ones need Git for Windows, WSL ones are tried with wsl.exe.</summary>
    private List<string> ReadableRootsLocked() => RootsLocked().Where(CanReadLocked).ToList();

    private bool CanReadLocked(string root) => _git is not null || WslPath.IsWslPath(root);

    private static IEnumerable<string> Distinct(IEnumerable<string?> roots) =>
        roots.Where(r => r is not null).Select(r => r!).Distinct(PathComparer);

    /// <summary>The module state when no repository could be read, worded for why the first one failed.</summary>
    public static ModuleStatus FailureStatus(string root, GitReadFailure failure)
    {
        var distro = WslPath.DistroOf(root) ?? "WSL";
        return failure switch
        {
            GitReadFailure.WslMissing => new ModuleStatus(ModuleState.Error, "This repository is inside WSL, which is not installed or not turned on.", "WSL not found"),
            GitReadFailure.DistroStopped => new ModuleStatus(ModuleState.Error, "Start it to see its repositories here. Developer Island never starts a distro on its own.", $"{distro} is not running"),
            GitReadFailure.GitMissingInDistro => new ModuleStatus(ModuleState.Error, $"Install Git inside {distro} (for example: sudo apt install git) to see its repositories here.", $"Git not found in {distro}"),
            _ => new ModuleStatus(ModuleState.Error, "Git could not read these repositories."),
        };
    }

    /// <summary>The note under the panel when stopped distros hide some repositories; null for none.</summary>
    public static string? StoppedNote(IReadOnlyList<string> distros) => distros.Count switch
    {
        0 => null,
        1 => $"{distros[0]} is not running, so its repositories are not shown.",
        _ => $"{string.Join(" and ", distros)} are not running, so their repositories are not shown.",
    };

    /// <summary>
    /// WSL repositories get no watcher, so while the panel is on screen they are re-read on a timer
    /// (every 10 s, the same throttle as AI activity). Hidden panel or no WSL repository: no timer.
    /// </summary>
    private void UpdateWslPollLocked()
    {
        var needed = _enabled && _visible && RootsLocked().Any(WslPath.IsWslPath);
        if (needed && _wslPoll is null)
        {
            _wslPoll = _time.CreateTimer(_ => PollWsl(), null, ActivityThrottle, ActivityThrottle);
        }
        else if (!needed && _wslPoll is not null)
        {
            _wslPoll.Dispose();
            _wslPoll = null;
        }
    }

    private void PollWsl()
    {
        List<string> due;
        lock (_gate)
        {
            if (!_enabled || !_visible)
            {
                return;
            }

            var now = _time.GetUtcNow();
            due = RootsLocked().Where(WslPath.IsWslPath)
                .Where(r => !_lastRead.TryGetValue(r, out var last) || now - last >= ActivityThrottle - TimeSpan.FromSeconds(1))
                .ToList();
        }

        foreach (var root in due)
        {
            _ = ReadAsync(root);
        }
    }

    private void Watch(string root)
    {
        // The WSL share delivers no reliable change notifications; those repositories are polled instead.
        if (_watchers.ContainsKey(root) || WslPath.IsWslPath(root))
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
            if (!_visible)
            {
                // Editors touch the index constantly; with the panel hidden nothing shows it, and the
                // panel re-reads when opened. Only a branch switch matters now (GitHub follows it),
                // and HEAD can be read without starting git.
                if (Path.GetFileName(fullPath) == "HEAD" && Path.GetDirectoryName(fullPath) == gitDir.TrimEnd(Path.DirectorySeparatorChar))
                {
                    _ = Task.Run(() => UpdateBranchFromHead(root, gitDir));
                }

                return;
            }

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

    private void UpdateBranchFromHead(string root, string gitDir)
    {
        var branch = GitStatusParser.ReadHeadBranch(gitDir);
        lock (_gate)
        {
            if (!_status.TryGetValue(root, out var status) || status.Branch == branch)
            {
                return;
            }

            _status[root] = status with { Branch = branch };
        }

        Changed?.Invoke();
    }

    private async Task ReadAsync(string root)
    {
        string? git;
        lock (_gate)
        {
            git = _git;
            if (!_enabled || !CanReadLocked(root))
            {
                return;
            }
        }

        await _readLock.WaitAsync();
        try
        {
            var (status, failure) = await ReadRepositoryAsync(git, root);
            lock (_gate)
            {
                _lastRead[root] = _time.GetUtcNow();
                if (status is null)
                {
                    _errors[root] = failure;
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

    private async Task<(GitRepoStatus? Status, GitReadFailure Failure)> ReadRepositoryAsync(string? git, string root)
    {
        var distro = WslPath.DistroOf(root);
        var wsl = _wsl?.Executable;
        if (distro is not null)
        {
            if (wsl is null)
            {
                return (null, GitReadFailure.WslMissing);
            }

            if (!await _wsl!.IsRunningAsync(distro))
            {
                return (null, GitReadFailure.DistroStopped);
            }
        }

        // Repositories come from AI sessions' working folders, so a repository's own config must not
        // start programs here: core.fsmonitor would run its command on every status.
        var status = await RunGitAsync(root, GitCommandLine.Status, git, wsl, TimeSpan.FromSeconds(10));
        if (status is not { Succeeded: true })
        {
            Log.Debug("git", "git status failed", new { exit = status?.ExitCode, wsl = distro is not null });
            return (null, distro is not null && await IsGitMissingInDistroAsync(distro, wsl!) ? GitReadFailure.GitMissingInDistro : GitReadFailure.Unreadable);
        }

        var parsed = GitStatusParser.ParseStatus(root, status.Output);
        var log = await RunGitAsync(root, GitCommandLine.LastCommit, git, wsl, TimeSpan.FromSeconds(10));
        var (at, subject) = log is { Succeeded: true } ? GitStatusParser.ParseLastCommit(log.Output) : (null, null);
        var remote = await RunGitAsync(root, GitCommandLine.OriginUrl, git, wsl, TimeSpan.FromSeconds(5));

        return (parsed with
        {
            LastCommitAt = at,
            LastCommitSubject = subject,
            GitHubRepository = remote is { Succeeded: true } ? GitStatusParser.ParseGitHubRepository(remote.Output) : null,
            UpdatedAt = _time.GetUtcNow(),
        }, default);
    }

    private async Task<ProcessResult?> RunGitAsync(string root, IReadOnlyList<string> arguments, string? git, string? wsl, TimeSpan timeout) =>
        GitCommandLine.For(root, arguments, git, wsl) is { } command ? await _run(command.FileName, command.Arguments, timeout) : null;

    /// <summary>After a failed read in WSL: does the distro have git at all? (<c>git --version</c> in <c>/</c>.)</summary>
    private async Task<bool> IsGitMissingInDistroAsync(string distro, string wsl)
    {
        try
        {
            var probe = await _run(wsl, WslCommand.Exec(distro, "/", "git", GitCommandLine.Version), TimeSpan.FromSeconds(10));
            return !probe.Succeeded;
        }
        catch (Exception ex) when (ex is OperationCanceledException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }
}
