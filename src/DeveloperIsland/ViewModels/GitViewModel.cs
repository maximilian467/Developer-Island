using DeveloperIsland.Core.Formatting;
using DeveloperIsland.Core.Git;
using DeveloperIsland.Core.Modules;

namespace DeveloperIsland.ViewModels;

/// <summary>One other repository in the Git panel's short list.</summary>
public sealed record GitRepoRow(string Root, string Name, string Branch, string Changes);

/// <summary>Local repository status: the active project first, a few recent ones below.</summary>
public sealed class GitViewModel : ModuleViewModel
{
    public const int MaxOtherRepositories = 3;

    private GitRepoStatus? _active;
    private IReadOnlyList<GitRepoRow> _others = [];
    private DateTimeOffset _now = DateTimeOffset.UtcNow;

    public GitViewModel()
        : base(ModuleId.Git)
    {
    }

    /// <summary>The user picked another repository.</summary>
    public event Action<string>? RepositorySelected;

    public GitRepoStatus? Active => _active;

    public string RepoName => _active?.Name ?? string.Empty;

    public string BranchText => _active is null ? string.Empty
        : _active.Branch ?? (_active.DetachedAt is { } at ? $"detached at {at}" : "detached");

    public string ChangesFigure => _active is null ? string.Empty : _active.IsClean ? "Clean" : _active.Changed.ToString(System.Globalization.CultureInfo.CurrentCulture);

    public string ChangesCaption => _active is null || _active.IsClean ? "no uncommitted changes"
        : _active.Changed == 1 ? "uncommitted change" : "uncommitted changes";

    public string ChangesDetail
    {
        get
        {
            if (_active is null || _active.IsClean)
            {
                return string.Empty;
            }

            var parts = new List<string>();
            if (_active.Staged > 0) parts.Add($"{_active.Staged} staged");
            if (_active.Modified > 0) parts.Add($"{_active.Modified} modified");
            if (_active.Untracked > 0) parts.Add($"{_active.Untracked} new");
            if (_active.Conflicts > 0) parts.Add(DisplayFormat.Count(_active.Conflicts, "conflict", "conflicts"));
            return string.Join(", ", parts);
        }
    }

    public string SyncText => _active switch
    {
        null => string.Empty,
        { HasUpstream: false } => "Not published",
        { Ahead: 0, Behind: 0 } => "Up to date",
        { Ahead: var a, Behind: 0 } => $"{a} to push",
        { Ahead: 0, Behind: var b } => $"{b} to pull",
        { Ahead: var a, Behind: var b } => $"{a} to push, {b} to pull",
    };

    public string SyncGlyph => _active switch
    {
        { HasUpstream: false } => "", // cloud
        { Ahead: 0, Behind: 0 } => "", // check
        _ => "", // sync
    };

    public string LastCommitSubject => _active?.LastCommitSubject ?? "No commits yet";

    public string LastCommitAgo => _active?.LastCommitAt is { } at ? DisplayFormat.Ago(_now - at) : string.Empty;

    /// <summary>The featured repository: its branch.</summary>
    public string CompactText => _active is null ? string.Empty : BranchText;

    /// <summary>"3 changes" or "clean".</summary>
    public string CompactDetail => _active is null ? string.Empty
        : _active.IsClean ? "clean" : DisplayFormat.Count(_active.Changed, "change", "changes");

    public IReadOnlyList<GitRepoRow> Others => _others;

    public bool HasOthers => _others.Count > 0;

    public string AccessibleSummary => _active is null ? "No repository"
        : $"{RepoName}, branch {BranchText}, {(_active.IsClean ? "clean" : $"{_active.Changed} {ChangesCaption}")}, {SyncText}";

    public void Update(ModuleStatus status, IReadOnlyList<GitRepoStatus> repositories, DateTimeOffset now)
    {
        _now = now;
        _active = repositories.FirstOrDefault();
        _others = repositories.Skip(1).Take(MaxOtherRepositories)
            .Select(r => new GitRepoRow(r.Root, r.Name, r.Branch ?? "detached", r.IsClean ? "clean" : DisplayFormat.Count(r.Changed, "change", "changes")))
            .ToList();
        SetStatus(status.IsReady && _active is null ? ModuleStatus.Loading : status);
        OnAllPropertiesChanged();
    }

    public void Select(string root) => RepositorySelected?.Invoke(root);

    protected override string EmptyTitle => "No repository yet";

    protected override string UnavailableTitle => "Git not found";

    protected override string UnavailableActionLabel => "Get Git";
}
