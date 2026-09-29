using DeveloperIsland.Core.Formatting;
using DeveloperIsland.Core.Models;

namespace DeveloperIsland.ViewModels;

/// <summary>Display state of one AI tool (Claude Code or Codex).</summary>
public sealed class AiProviderViewModel : ObservableObject
{
    private AiUsageSnapshot _snapshot;
    private bool _isEnabled = true;

    public AiProviderViewModel(AiProviderKind kind)
    {
        Kind = kind;
        _snapshot = AiUsageSnapshot.Initial(kind);
    }

    public AiProviderKind Kind { get; }

    public AiUsageSnapshot Snapshot => _snapshot;

    public string DisplayName => Kind == AiProviderKind.Claude ? "Claude Code" : "Codex";

    public string ShortName => Kind == AiProviderKind.Claude ? "Claude" : "Codex";

    public bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            if (SetProperty(ref _isEnabled, value))
            {
                OnAllPropertiesChanged();
            }
        }
    }

    public ProviderState State => _isEnabled ? _snapshot.State : ProviderState.Disabled;

    public bool IsReady => State == ProviderState.Ready;

    public bool IsUnavailable => State is ProviderState.NotDetected or ProviderState.Error or ProviderState.Starting;

    public bool IsError => State == ProviderState.Error;

    public bool HasUsageToday => IsReady && _snapshot.Today.Total > 0;

    /// <summary>The API value line is only shown when there is something to value.</summary>
    public bool ShowValue => HasUsageToday;

    public bool IsActive => IsReady && _snapshot.IsActive;

    public bool HasLimit => IsReady && _snapshot.LimitPercent is not null;

    /// <summary>Whether the provider earns a place in the compact capsule.</summary>
    public bool IsWorthShowingCompact => IsReady && (_snapshot.Today.Total > 0 || HasLimit || _snapshot.IsActive);

    /// <summary>"68%" when the tool reports its limit locally, otherwise today's tokens.</summary>
    public string CompactValue => State switch
    {
        ProviderState.Ready when _snapshot.LimitPercent is { } p => $"{Math.Round(p):0}%",
        ProviderState.Ready => DisplayFormat.Tokens(_snapshot.Today.Total),
        ProviderState.Error => "Error",
        _ => string.Empty,
    };

    public string CompactAccessibleText => State == ProviderState.Ready
        ? _snapshot.LimitPercent is { } p
            ? $"{ShortName} {Math.Round(p):0} percent of limit used"
            : $"{ShortName} {DisplayFormat.Tokens(_snapshot.Today.Total)} tokens today"
        : $"{ShortName} {StatusText}";

    public string TokensText => DisplayFormat.Tokens(_snapshot.Today.Total);

    public string TokensUnit => _snapshot.Today.Total == 1 ? "token" : "tokens";

    public string ValueText => _snapshot.ApiValueEur is { } value ? DisplayFormat.Euro(value) : "Unavailable";

    public string ValueCaption => _snapshot.ApiValuePartial ? "API equivalent, partly priced" : "API equivalent";

    /// <summary>Limit usage when known, otherwise sessions.</summary>
    public string DetailText
    {
        get
        {
            if (_snapshot.LimitPercent is { } p)
            {
                return $"{Math.Round(p):0}% of {DisplayFormat.LimitWindow(_snapshot.LimitWindowMinutes ?? 0)}";
            }

            return _snapshot.SessionsToday > 0
                ? $"{DisplayFormat.Count(_snapshot.SessionsToday, "session", "sessions")} today"
                : "No sessions today";
        }
    }

    /// <summary>"Opus 5.5 in developer_island".</summary>
    public string ContextText
    {
        get
        {
            var model = DisplayFormat.ModelLabel(_snapshot.CurrentModel);
            var project = _snapshot.CurrentProject;
            return (model.Length > 0, !string.IsNullOrEmpty(project)) switch
            {
                (true, true) => $"{model} in {project}",
                (true, false) => model,
                (false, true) => project!,
                _ => string.Empty,
            };
        }
    }

    public string StatusText => State switch
    {
        ProviderState.Starting => "Reading logs…",
        ProviderState.NotDetected => "Not detected",
        ProviderState.Error => "Couldn't read logs",
        ProviderState.Disabled => "Turned off",
        _ => string.Empty,
    };

    public string StatusHint => State switch
    {
        ProviderState.NotDetected => Kind == AiProviderKind.Claude
            ? "Usage appears once Claude Code runs on this PC."
            : "Usage appears once Codex runs on this PC.",
        ProviderState.Error => "Details are in the log. The rest of the island keeps working.",
        _ => string.Empty,
    };

    public void Update(AiUsageSnapshot snapshot)
    {
        _snapshot = snapshot;
        OnAllPropertiesChanged();
    }
}
