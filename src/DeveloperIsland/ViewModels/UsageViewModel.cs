using DeveloperIsland.Core.Formatting;
using DeveloperIsland.Core.Models;
using DeveloperIsland.Core.Usage;

namespace DeveloperIsland.ViewModels;

/// <summary>AI usage for today plus the contribution-style history graph.</summary>
public sealed class UsageViewModel : ObservableObject
{
    private IReadOnlyDictionary<DateOnly, IReadOnlyDictionary<string, double>> _planPeaks = new Dictionary<DateOnly, IReadOnlyDictionary<string, double>>();
    public const int HistoryWeeks = 26;

    private IReadOnlyList<UsageDay> _days = [];
    private int[] _levels = [];
    private int _inspectedIndex = -1;

    public UsageViewModel()
    {
        Claude.PropertyChanged += (_, _) => OnProvidersChanged();
        Codex.PropertyChanged += (_, _) => OnProvidersChanged();
    }

    /// <summary>Raised after new history was loaded (the graph rebuilds its cells).</summary>
    public event Action? HistoryChanged;

    public AiProviderViewModel Claude { get; } = new(AiProviderKind.Claude);

    public AiProviderViewModel Codex { get; } = new(AiProviderKind.Codex);

    public IReadOnlyList<UsageDay> Days => _days;

    public IReadOnlyList<int> Levels => _levels;

    public bool ShowClaude => Claude.IsEnabled;

    public bool ShowCodex => Codex.IsEnabled;

    public bool ShowBoth => ShowClaude && ShowCodex;

    public bool HasAnyProvider => ShowClaude || ShowCodex;

    public bool HasHistory => _days.Any(d => d.HasUsage);

    public int InspectedIndex => _inspectedIndex;

    public bool IsInspecting => _inspectedIndex >= 0 && _inspectedIndex < _days.Count;

    public bool IsNotInspecting => !IsInspecting;

    public string InspectedDate => IsInspecting ? DisplayFormat.ShortDay(_days[_inspectedIndex].Day) : string.Empty;

    public string InspectedClaude => IsInspecting ? DisplayFormat.Tokens(_days[_inspectedIndex].Claude.Fresh) : string.Empty;

    public string InspectedCodex => IsInspecting ? DisplayFormat.Tokens(_days[_inspectedIndex].Codex.Fresh) : string.Empty;

    /// <summary>Complete breakdown of the inspected day (second readout line).</summary>
    public string InspectedDetail
    {
        get
        {
            if (!IsInspecting)
            {
                return string.Empty;
            }

            var d = _days[_inspectedIndex];
            var all = d.Claude + d.Codex;
            return all.IsZero
                ? "No usage"
                : $"Input {DisplayFormat.Tokens(all.Input)}, cache write {DisplayFormat.Tokens(all.CacheWrite)}, output {DisplayFormat.Tokens(all.Output)}, cache read {DisplayFormat.Tokens(all.CacheRead)}";
        }
    }

    /// <summary>"Claude plan peak 72% (5 h), 41% (week)" for days on which Claude Code reported plan usage.</summary>
    public string InspectedPlan
    {
        get
        {
            if (!IsInspecting || !_planPeaks.TryGetValue(_days[_inspectedIndex].Day, out var windows))
            {
                return string.Empty;
            }

            var parts = new List<string>();
            if (windows.TryGetValue("five_hour", out var current)) parts.Add($"{Math.Round(current):0}% (5 h)");
            if (windows.TryGetValue("seven_day", out var weekly)) parts.Add($"{Math.Round(weekly):0}% (week)");
            return parts.Count == 0 ? string.Empty : "Claude plan peak " + string.Join(", ", parts);
        }
    }

    public bool HasInspectedPlan => InspectedPlan.Length > 0;

    /// <summary>Tooltip: the day's fresh tokens (Claude and Codex together).</summary>
    public string InspectedTooltipTokens => !IsInspecting ? string.Empty
        : _days[_inspectedIndex].FreshTokens == 0 ? "No usage"
        : $"{DisplayFormat.Tokens(_days[_inspectedIndex].FreshTokens)} fresh tokens";

    /// <summary>Tooltip: sessions and the estimated API equivalent, when there was usage.</summary>
    public string InspectedTooltipDetail
    {
        get
        {
            if (!IsInspecting || !_days[_inspectedIndex].HasUsage)
            {
                return string.Empty;
            }

            var d = _days[_inspectedIndex];
            var parts = new List<string>();
            if (d.Sessions > 0) parts.Add(DisplayFormat.Count(d.Sessions, "session", "sessions"));
            parts.Add(d.ApiValuePartial ? "API equivalent unavailable" : $"{DisplayFormat.Euro(d.TotalApiValueEur)} API equivalent");
            return string.Join(" · ", parts);
        }
    }

    /// <summary>The readout grows by one line once plan usage has ever been measured (no jump per day).</summary>
    public double ReadoutHeight => _planPeaks.Count > 0 ? 52 : 36;

    public string InspectedValue => !IsInspecting ? string.Empty
        : _days[_inspectedIndex].ApiValuePartial ? "Unavailable"
        : DisplayFormat.Euro(_days[_inspectedIndex].TotalApiValueEur);

    /// <summary>Spoken description of the inspected day.</summary>
    public string InspectedAccessibleText => IsInspecting
        ? $"{InspectedDate}: Claude {InspectedClaude} fresh tokens, Codex {InspectedCodex} fresh tokens, estimated API equivalent {InspectedValue}. {InspectedDetail}{(HasInspectedPlan ? ". " + InspectedPlan : string.Empty)}"
        : "Usage history, last 26 weeks. Use the arrow keys to inspect a day.";

    public string HistorySummary
    {
        get
        {
            var active = _days.Count(d => d.HasUsage);
            return active == 0 ? "No usage in the last 26 weeks" : $"{DisplayFormat.Count(active, "active day", "active days")}, shaded by fresh tokens";
        }
    }

    /// <summary>Peek text for the hover activity.</summary>
    public string PeekTitle
    {
        get
        {
            var parts = new List<string>();
            if (Claude.IsReady && ShowClaude)
            {
                parts.Add($"Claude {Claude.CompactValue}");
            }

            if (Codex.IsReady && ShowCodex)
            {
                parts.Add($"Codex {Codex.CompactValue}");
            }

            return parts.Count == 0 ? "No AI usage detected" : string.Join("   ", parts);
        }
    }

    /// <summary>Mark for the peek: the tool with more fresh tokens today.</summary>
    public string PeekMark => ShowCodex && Codex.IsReady && (!ShowClaude || !Claude.IsReady || Codex.Snapshot.Today.Fresh > Claude.Snapshot.Today.Fresh) ? "Codex" : "Claude";

    public string PeekSubtitle
    {
        get
        {
            decimal total = 0;
            var any = false;
            foreach (var p in new[] { Claude, Codex })
            {
                if (p.IsEnabled && p.IsReady && p.Snapshot.ApiValueEur is { } v)
                {
                    total += v;
                    any = true;
                }
            }

            return any ? $"{DisplayFormat.Euro(total)} estimated API equivalent today" : "Claude Code and Codex usage appears here";
        }
    }

    /// <summary>Plan usage peaks that were actually measured, per day (never backfilled).</summary>
    public void SetPlanPeaks(IReadOnlyDictionary<DateOnly, IReadOnlyDictionary<string, double>> peaks)
    {
        _planPeaks = peaks;
        OnPropertyChanged(nameof(ReadoutHeight));
        OnPropertyChanged(nameof(InspectedPlan));
        OnPropertyChanged(nameof(HasInspectedPlan));
    }

    public void SetHistory(IReadOnlyList<UsageDay> days)
    {
        _days = days;
        // Intensity follows fresh tokens: cache reads would let one long session flatten every other day.
        _levels = UsageAggregator.IntensityLevels(days.Select(d => d.FreshTokens).ToList());
        if (_inspectedIndex >= days.Count)
        {
            _inspectedIndex = -1;
        }

        OnAllPropertiesChanged();
        HistoryChanged?.Invoke();
    }

    public void Inspect(int index)
    {
        var next = index >= 0 && index < _days.Count ? index : -1;
        if (next == _inspectedIndex)
        {
            return;
        }

        _inspectedIndex = next;
        OnPropertyChanged(nameof(InspectedIndex));
        OnPropertyChanged(nameof(IsInspecting));
        OnPropertyChanged(nameof(IsNotInspecting));
        OnPropertyChanged(nameof(InspectedDate));
        OnPropertyChanged(nameof(InspectedClaude));
        OnPropertyChanged(nameof(InspectedCodex));
        OnPropertyChanged(nameof(InspectedValue));
        OnPropertyChanged(nameof(InspectedDetail));
        OnPropertyChanged(nameof(InspectedAccessibleText));
    }

    private void OnProvidersChanged()
    {
        OnPropertyChanged(nameof(ShowClaude));
        OnPropertyChanged(nameof(ShowCodex));
        OnPropertyChanged(nameof(ShowBoth));
        OnPropertyChanged(nameof(HasAnyProvider));
        OnPropertyChanged(nameof(PeekTitle));
        OnPropertyChanged(nameof(PeekSubtitle));
    }
}
