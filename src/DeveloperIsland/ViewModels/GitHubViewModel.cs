using System.Globalization;
using DeveloperIsland.Core.Formatting;
using DeveloperIsland.Core.GitHub;
using DeveloperIsland.Core.Modules;

namespace DeveloperIsland.ViewModels;

/// <summary>Read-only GitHub signals. The compact island only hears about a failing CI run.</summary>
public sealed class GitHubViewModel : ModuleViewModel
{
    private GitHubSnapshot? _snapshot;
    private DateTimeOffset _now = DateTimeOffset.UtcNow;

    public GitHubViewModel()
        : base(ModuleId.GitHub)
    {
    }

    public string AccountText => _snapshot?.Login is { } login ? $"@{login}" : string.Empty;

    public string NotificationsFigure => Figure(_snapshot?.Notifications ?? 0, GitHubService.NotificationCap);

    public string NotificationsCaption => (_snapshot?.Notifications ?? 0) == 1 ? "notification" : "notifications";

    public string PullRequestsFigure => Figure(_snapshot?.MyOpenPullRequests ?? 0);

    public string PullRequestsCaption => "your open PRs";

    public string ReviewsFigure => Figure(_snapshot?.ReviewRequests ?? 0);

    public string ReviewsCaption => (_snapshot?.ReviewRequests ?? 0) == 1 ? "review request" : "review requests";

    public bool HasRepository => _snapshot?.Repository is not null;

    public string RepositoryName => _snapshot?.Repository?.NameWithOwner ?? string.Empty;

    public string RepositoryDetail => _snapshot?.Repository is { } r
        ? $"{DisplayFormat.Count(r.Stars, "star", "stars")} · {DisplayFormat.Count(r.OpenIssues, "issue", "issues")} · {DisplayFormat.Count(r.OpenPullRequests, "PR", "PRs")}"
        : string.Empty;

    public bool HasCi => _snapshot?.Ci is { State: not CiState.None };

    public string CiText => _snapshot?.Ci is { } ci
        ? ci.State switch
        {
            CiState.Passed => "Checks passed",
            CiState.Failed => "Checks failed",
            CiState.Running => "Checks running",
            _ => string.Empty,
        }
        : string.Empty;

    public string CiDetail => _snapshot?.Ci is { } ci
        ? string.Join(" · ", new[] { ci.Workflow, ci.Branch, ci.UpdatedAt is { } at ? DisplayFormat.Ago(_now - at) : null }.Where(s => !string.IsNullOrEmpty(s)))
        : string.Empty;

    public bool CiFailed => _snapshot?.Ci?.State == CiState.Failed;

    public bool CiPassed => _snapshot?.Ci?.State == CiState.Passed;

    public bool CiRunning => _snapshot?.Ci?.State == CiState.Running;

    /// <summary>The compact island shows a failing CI run for the active repository, nothing else.</summary>
    public bool HasCompactSignal => IsReady && CiFailed;

    public string CompactText => "CI failed";

    public string AccessibleSummary => IsReady
        ? $"GitHub {AccountText}: {NotificationsFigure} {NotificationsCaption}, {PullRequestsFigure} open pull requests, {ReviewsFigure} {ReviewsCaption}{(HasCi ? ", " + CiText : string.Empty)}"
        : StateTitle;

    public void Update(ModuleStatus status, GitHubSnapshot? snapshot, DateTimeOffset now)
    {
        _now = now;
        _snapshot = snapshot;
        SetStatus(status.IsReady && snapshot is null ? ModuleStatus.Loading : status);
        OnAllPropertiesChanged();
    }

    protected override string UnavailableTitle => "Connect GitHub";

    protected override string UnavailableActionLabel => "Get GitHub CLI";

    private static string Figure(int value, int? cap = null) =>
        cap is { } c && value >= c ? $"{c}+" : value.ToString(CultureInfo.CurrentCulture);
}
