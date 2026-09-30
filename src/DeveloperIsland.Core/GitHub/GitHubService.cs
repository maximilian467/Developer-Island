using System.Text.Json;
using DeveloperIsland.Core.Diagnostics;
using DeveloperIsland.Core.Modules;

namespace DeveloperIsland.Core.GitHub;

public enum CiState
{
    None,
    Running,
    Passed,
    Failed,
}

/// <summary>The latest workflow run for the active repository and branch.</summary>
public sealed record CiRun(CiState State, string? Workflow, string? Branch, DateTimeOffset? UpdatedAt);

/// <summary>The active repository on GitHub.</summary>
public sealed record GitHubRepo(string NameWithOwner, int Stars, int OpenIssues, int OpenPullRequests);

/// <summary>Everything the GitHub module shows. Counts and names only, no issue or PR bodies.</summary>
public sealed record GitHubSnapshot
{
    public string? Login { get; init; }

    /// <summary>Unread notifications (capped at <see cref="GitHubService.NotificationCap"/>).</summary>
    public int Notifications { get; init; }

    public int MyOpenPullRequests { get; init; }

    public int ReviewRequests { get; init; }

    public GitHubRepo? Repository { get; init; }

    public CiRun? Ci { get; init; }

    public DateTimeOffset UpdatedAt { get; init; }
}

/// <summary>Parses the JSON that <c>gh api</c> returns. Pure, so it is tested without GitHub.</summary>
public static class GitHubJson
{
    public static string? Login(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.TryGetProperty("login", out var login) ? login.GetString() : null;
    }

    public static int ArrayLength(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement.GetArrayLength() : 0;
    }

    /// <summary>total_count of a search result.</summary>
    public static int TotalCount(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.TryGetProperty("total_count", out var total) && total.TryGetInt32(out var count) ? count : 0;
    }

    /// <summary>A repository; open_issues_count includes pull requests on GitHub, so they are subtracted.</summary>
    public static GitHubRepo? Repository(string json, int openPullRequests)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (!root.TryGetProperty("full_name", out var name))
        {
            return null;
        }

        var stars = root.TryGetProperty("stargazers_count", out var s) && s.TryGetInt32(out var st) ? st : 0;
        var issues = root.TryGetProperty("open_issues_count", out var i) && i.TryGetInt32(out var it) ? it : 0;
        return new GitHubRepo(name.GetString() ?? string.Empty, stars, Math.Max(0, issues - openPullRequests), openPullRequests);
    }

    public static CiRun? LatestRun(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("workflow_runs", out var runs) || runs.GetArrayLength() == 0)
        {
            return null;
        }

        var run = runs[0];
        string? Str(string name) => run.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        var state = (Str("status"), Str("conclusion")) switch
        {
            ("completed", "success") => CiState.Passed,
            ("completed", "skipped" or "neutral") => CiState.Passed,
            ("completed", _) => CiState.Failed,
            (null, _) => CiState.None,
            _ => CiState.Running,
        };
        var updated = DateTimeOffset.TryParse(Str("updated_at"), out var at) ? at : (DateTimeOffset?)null;
        return new CiRun(state, Str("name"), Str("head_branch"), updated);
    }
}

/// <summary>
/// Read-only GitHub signals through the GitHub CLI (<c>gh</c>), using the account the user already
/// signed in with (<c>gh auth login</c>). No tokens are asked for, stored or read by the app, and no
/// scopes are requested. Without gh, or when signed out, the module says how to connect.
/// Refreshes every 5 minutes while enabled, and when the panel opens if older than a minute.
/// </summary>
public sealed class GitHubService : IDisposable
{
    public const int NotificationCap = 50;
    public static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan PanelStaleAfter = TimeSpan.FromMinutes(1);

    private readonly Func<string?> _findGh;
    private readonly TimeProvider _time;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _fetchLock = new(1, 1);
    private ModuleStatus _status = ModuleStatus.Disabled;
    private GitHubSnapshot? _snapshot;
    private string? _repository;
    private string? _branch;
    private ITimer? _timer;
    private bool _enabled;

    public GitHubService(Func<string?>? findGh = null, TimeProvider? time = null)
    {
        _findGh = findGh ?? (() => ProcessRunner.FindExecutable("gh", @"%ProgramFiles%\GitHub CLI\gh.exe", @"%LocalAppData%\Programs\GitHub CLI\gh.exe"));
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Raised on a background thread.</summary>
    public event Action? Changed;

    public ModuleStatus Status
    {
        get
        {
            lock (_gate)
            {
                return _status;
            }
        }
    }

    public GitHubSnapshot? Snapshot
    {
        get
        {
            lock (_gate)
            {
                return _snapshot;
            }
        }
    }

    public void Configure(bool enabled)
    {
        lock (_gate)
        {
            if (enabled == _enabled)
            {
                return;
            }

            _enabled = enabled;
            _timer?.Dispose();
            _timer = null;
            if (enabled)
            {
                _status = ModuleStatus.Loading;
                _timer = _time.CreateTimer(_ => _ = RefreshAsync(), null, TimeSpan.Zero, RefreshInterval);
            }
            else
            {
                _status = ModuleStatus.Disabled;
                _snapshot = null;
            }
        }

        Changed?.Invoke();
    }

    /// <summary>The repository and branch the Git module is showing ("owner/name").</summary>
    public void SetRepository(string? nameWithOwner, string? branch)
    {
        bool changed;
        lock (_gate)
        {
            changed = _repository != nameWithOwner || _branch != branch;
            _repository = nameWithOwner;
            _branch = branch;
        }

        if (changed)
        {
            _ = RefreshAsync();
        }
    }

    /// <summary>The panel opened: refresh if the data is more than a minute old.</summary>
    public Task RefreshIfStaleAsync()
    {
        lock (_gate)
        {
            if (_snapshot is { } s && _time.GetUtcNow() - s.UpdatedAt < PanelStaleAfter && _status.IsReady)
            {
                return Task.CompletedTask;
            }
        }

        return RefreshAsync();
    }

    public async Task RefreshAsync()
    {
        string? repository;
        string? branch;
        lock (_gate)
        {
            if (!_enabled)
            {
                return;
            }

            repository = _repository;
            branch = _branch;
        }

        if (!await _fetchLock.WaitAsync(0))
        {
            return;
        }

        try
        {
            var (status, snapshot) = await FetchAsync(repository, branch);
            lock (_gate)
            {
                if (!_enabled)
                {
                    return;
                }

                _status = status;
                _snapshot = snapshot ?? (status.State == ModuleState.Error ? _snapshot : null);
            }

            Changed?.Invoke();
        }
        finally
        {
            _fetchLock.Release();
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _enabled = false;
            _timer?.Dispose();
            _timer = null;
        }
    }

    private async Task<(ModuleStatus, GitHubSnapshot?)> FetchAsync(string? repository, string? branch)
    {
        var gh = _findGh();
        if (gh is null)
        {
            return (new ModuleStatus(ModuleState.Unavailable, "Install the GitHub CLI and run “gh auth login” to see pull requests, CI and notifications."), null);
        }

        try
        {
            var user = await Api(gh, "user");
            if (!user.Succeeded)
            {
                var signedOut = user.Error.Contains("auth login", StringComparison.OrdinalIgnoreCase)
                    || user.Error.Contains("not logged", StringComparison.OrdinalIgnoreCase)
                    || user.Error.Contains("401", StringComparison.Ordinal);
                return signedOut
                    ? (new ModuleStatus(ModuleState.Unavailable, "GitHub CLI is installed but signed out. Run “gh auth login” in a terminal."), null)
                    : (new ModuleStatus(ModuleState.Error, "GitHub could not be reached."), null);
            }

            var login = GitHubJson.Login(user.Output);
            var notifications = await Api(gh, $"notifications?per_page={NotificationCap}");
            var mine = login is null ? null : await Api(gh, $"search/issues?q=is:pr+is:open+author:{login}&per_page=1");
            var reviews = login is null ? null : await Api(gh, $"search/issues?q=is:pr+is:open+review-requested:{login}&per_page=1");

            GitHubRepo? repo = null;
            CiRun? ci = null;
            if (repository is not null)
            {
                var prs = await Api(gh, $"search/issues?q=is:pr+is:open+repo:{repository}&per_page=1");
                var info = await Api(gh, $"repos/{repository}");
                if (info.Succeeded)
                {
                    repo = GitHubJson.Repository(info.Output, prs.Succeeded ? GitHubJson.TotalCount(prs.Output) : 0);
                }

                var runs = await Api(gh, branch is null ? $"repos/{repository}/actions/runs?per_page=1" : $"repos/{repository}/actions/runs?per_page=1&branch={Uri.EscapeDataString(branch)}");
                if (runs.Succeeded)
                {
                    ci = GitHubJson.LatestRun(runs.Output);
                }
            }

            return (ModuleStatus.Ready, new GitHubSnapshot
            {
                Login = login,
                Notifications = notifications.Succeeded ? GitHubJson.ArrayLength(notifications.Output) : 0,
                MyOpenPullRequests = mine is { Succeeded: true } ? GitHubJson.TotalCount(mine.Output) : 0,
                ReviewRequests = reviews is { Succeeded: true } ? GitHubJson.TotalCount(reviews.Output) : 0,
                Repository = repo,
                Ci = ci,
                UpdatedAt = _time.GetUtcNow(),
            });
        }
        catch (Exception ex) when (ex is JsonException or OperationCanceledException or System.ComponentModel.Win32Exception or IOException)
        {
            Log.Warn("github", "GitHub refresh failed", new { error = ex.GetType().Name });
            return (new ModuleStatus(ModuleState.Error, "GitHub could not be reached."), null);
        }
    }

    /// <summary>A read-only REST call (gh api defaults to GET without a body).</summary>
    private static Task<ProcessResult> Api(string gh, string path) =>
        ProcessRunner.RunAsync(gh, ["api", "--method", "GET", path], timeout: TimeSpan.FromSeconds(20));
}
