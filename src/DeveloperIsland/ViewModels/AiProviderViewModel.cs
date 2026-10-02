using System.Globalization;
using DeveloperIsland.Core.Formatting;
using DeveloperIsland.Core.Models;
using DeveloperIsland.Core.Plan;

namespace DeveloperIsland.ViewModels;

/// <summary>Display state of one AI tool (Claude Code or Codex).</summary>
public sealed class AiProviderViewModel : ObservableObject
{
    private AiUsageSnapshot _snapshot;
    private bool _isEnabled = true;
    private PlanUsage? _plan;
    private PlanConnection _planConnection = PlanConnection.NotConnected;
    private bool _showWeekly;

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

    /// <summary>"Connect…" under an unavailable plan: open the place in Settings that connects it.</summary>
    public event Action? ConnectPlanRequested;

    public void RequestConnectPlan() => ConnectPlanRequested?.Invoke();

    public ProviderState State => _isEnabled ? _snapshot.State : ProviderState.Disabled;

    public bool IsReady => State == ProviderState.Ready;

    public bool IsUnavailable => State is ProviderState.NotDetected or ProviderState.Error or ProviderState.Starting;

    public bool IsError => State == ProviderState.Error;

    public bool HasUsageToday => IsReady && _snapshot.Today.Processed > 0;

    /// <summary>The API value line is only shown when there is something to value.</summary>
    public bool ShowValue => HasUsageToday;

    public bool IsActive => IsReady && _snapshot.IsActive;

    public bool HasLimit => IsReady && _snapshot.LimitPercent is not null;

    /// <summary>Whether the provider earns a place in the compact capsule.</summary>
    public bool IsWorthShowingCompact => IsReady && (_snapshot.Today.Processed > 0 || HasLimit || _snapshot.IsActive);

    /// <summary>
    /// "72%" when the plan's current window is known (Claude: reported by Claude Code; Codex: its
    /// local rate-limit record), otherwise today's fresh tokens (see TokenCounts). Never a percentage
    /// derived from token counts.
    /// </summary>
    public string CompactValue => State switch
    {
        ProviderState.Ready when CurrentWindow is { } w => $"{Math.Round(w.UsedPercent):0}%",
        ProviderState.Ready when _snapshot.LimitPercent is { } p => $"{Math.Round(p):0}%",
        ProviderState.Ready => DisplayFormat.Tokens(_snapshot.Today.Fresh),
        ProviderState.Error => "Error",
        _ => string.Empty,
    };

    /// <summary>"€8.42": today's estimated API equivalent for the compact island, when there is usage to value.</summary>
    public string CompactEuro => IsReady && HasUsageToday && _snapshot.ApiValueEur is { } value ? DisplayFormat.Euro(value) : string.Empty;

    /// <summary>
    /// The featured value in the compact island: today's fresh tokens and, when known, the current
    /// (5-hour) plan window: "1.12M · 72%", else "1.12M tokens". No money here; the estimated API
    /// equivalent lives in the expanded view.
    /// </summary>
    public string CompactPrimaryValue => !IsReady ? string.Empty
        : CurrentWindow is { } w ? $"{DisplayFormat.Tokens(_snapshot.Today.Fresh)} · {Math.Round(w.UsedPercent):0}%"
        : _snapshot.LimitPercent is { } p ? $"{DisplayFormat.Tokens(_snapshot.Today.Fresh)} · {Math.Round(p):0}%"
        : $"{DisplayFormat.Tokens(_snapshot.Today.Fresh)} tokens";

    /// <summary>Hover peek: today's tokens; the plan window goes to the trailing slot.</summary>
    public string PeekLine => !IsReady ? StatusText
        : CurrentWindow is { } w && w.ResetsAt != DateTimeOffset.MaxValue
            ? $"{DisplayFormat.Tokens(_snapshot.Today.Fresh)} fresh tokens · {ResetText(w.ResetsAt, DateTimeOffset.UtcNow).ToLowerInvariant()}"
            : $"{DisplayFormat.Tokens(_snapshot.Today.Fresh)} fresh tokens today";

    public string PlanPeekTrailing => IsReady && (CurrentWindow ?? (_snapshot.LimitPercent is { } p ? new PlanWindow(p, DateTimeOffset.MaxValue) : null)) is { } window
        ? $"{Math.Round(window.UsedPercent):0}%"
        : string.Empty;

    public string CompactAccessibleText => State == ProviderState.Ready
        ? _snapshot.LimitPercent is { } p
            ? $"{ShortName} {Math.Round(p):0} percent of limit used"
            : $"{ShortName} {DisplayFormat.Tokens(_snapshot.Today.Fresh)} fresh tokens today"
        : $"{ShortName} {StatusText}";

    /// <summary>Headline figure: fresh tokens (processed without a cache hit).</summary>
    public string TokensText => DisplayFormat.Tokens(_snapshot.Today.Fresh);

    public string TokensUnit => "fresh tokens";

    // Breakdown of today's usage. Field meanings are documented on TokenCounts.
    public string InputText => DisplayFormat.Tokens(_snapshot.Today.Input);

    public string CacheWriteText => DisplayFormat.Tokens(_snapshot.Today.CacheWrite);

    public string OutputText => DisplayFormat.Tokens(_snapshot.Today.Output);

    public string CacheReadText => DisplayFormat.Tokens(_snapshot.Today.CacheRead);

    public string ProcessedText => DisplayFormat.Tokens(_snapshot.Today.Processed);

    public string SessionsText => _snapshot.SessionsToday.ToString(System.Globalization.CultureInfo.CurrentCulture);

    public string ValueText => _snapshot.ApiValueEur is { } value ? DisplayFormat.Euro(value) : "Unavailable";

    public string ValueCaption => _snapshot.ApiValuePartial ? "estimated API equivalent today, partly priced" : "estimated API equivalent today";

    // Plan usage --------------------------------------------------------------------------------------
    // Claude: the 5-hour and 7-day windows Claude Code reports to its status line. Codex: the windows
    // recorded in its own logs. Shown only when measured; otherwise "Unavailable".

    /// <summary>The current (5-hour) window, while it has not reset.</summary>
    public PlanWindow? CurrentWindow => Kind == AiProviderKind.Claude
        ? _plan?.CurrentAt(DateTimeOffset.UtcNow)
        : _snapshot.LimitPercent is { } p && (_snapshot.LimitResetsAt is not { } r || r > DateTimeOffset.UtcNow)
            ? new PlanWindow(p, _snapshot.LimitResetsAt ?? DateTimeOffset.MaxValue)
            : null;

    /// <summary>The weekly (7-day) window, while it has not reset.</summary>
    public PlanWindow? WeeklyWindow => Kind == AiProviderKind.Claude
        ? _plan?.WeeklyAt(DateTimeOffset.UtcNow)
        : _snapshot.WeeklyLimitPercent is { } p && (_snapshot.WeeklyLimitResetsAt is not { } r || r > DateTimeOffset.UtcNow)
            ? new PlanWindow(p, _snapshot.WeeklyLimitResetsAt ?? DateTimeOffset.MaxValue)
            : null;

    public bool HasPlan => IsReady && (CurrentWindow is not null || WeeklyWindow is not null);

    public bool HasNoPlan => IsReady && !HasPlan;

    /// <summary>Both windows are known: offer the Current / Weekly switch.</summary>
    public bool HasBothPlanWindows => IsReady && CurrentWindow is not null && WeeklyWindow is not null;

    public bool ShowWeekly
    {
        get => _showWeekly && WeeklyWindow is not null || CurrentWindow is null && WeeklyWindow is not null;
        set
        {
            if (_showWeekly != value)
            {
                _showWeekly = value;
                OnPlanChanged();
            }
        }
    }

    public bool ShowCurrent => !ShowWeekly;

    private PlanWindow? ShownWindow => ShowWeekly ? WeeklyWindow : CurrentWindow;

    public string PlanPercentText => ShownWindow is { } w ? $"{Math.Round(w.UsedPercent):0}%" : "Unavailable";

    public double PlanFraction => ShownWindow is { } w ? Math.Clamp(w.UsedPercent / 100, 0, 1) : 0;

    public string PlanWindowName => ShowWeekly ? "Weekly" : "Current";

    /// <summary>"Resets in 2 h 14 min", plus when the figure was measured if that was a while ago.</summary>
    public string PlanResetText
    {
        get
        {
            var reset = ShownWindow is { } w && w.ResetsAt != DateTimeOffset.MaxValue ? ResetText(w.ResetsAt, DateTimeOffset.UtcNow) : string.Empty;
            if (Kind == AiProviderKind.Claude && _plan is { } plan && DateTimeOffset.UtcNow - plan.MeasuredAt > TimeSpan.FromMinutes(15))
            {
                var age = $"as of {DisplayFormat.Ago(DateTimeOffset.UtcNow - plan.MeasuredAt)}";
                return reset.Length > 0 ? $"{reset} · {age}" : age;
            }

            return reset;
        }
    }

    /// <summary>What the plan link can deliver: connected is not the same as available.</summary>
    public PlanDataState PlanState => PlanStatus.StateOf(_planConnection, _plan, DateTimeOffset.UtcNow);

    /// <summary>Why plan usage is missing, in one short line.</summary>
    public string PlanUnavailableReason => Kind == AiProviderKind.Claude
        ? PlanStatus.Reason(PlanState)
        : "Codex has not recorded a limit yet.";

    public bool CanConnectPlan => Kind == AiProviderKind.Claude && HasNoPlan && _planConnection == PlanConnection.NotConnected;

    /// <summary>"Reset in 2 h 14 min" within a day, else the weekday ("Resets Monday").</summary>
    public static string ResetText(DateTimeOffset resetsAt, DateTimeOffset now)
    {
        var left = resetsAt - now;
        if (left <= TimeSpan.Zero)
        {
            return "Reset";
        }

        if (left < TimeSpan.FromHours(24))
        {
            var hours = (int)left.TotalHours;
            var minutes = Math.Max(1, (int)Math.Ceiling(left.TotalMinutes - hours * 60));
            if (minutes == 60)
            {
                hours++;
                minutes = 0;
            }

            return hours == 0 ? $"Resets in {minutes}{DisplayFormat.Nbsp}min"
                : minutes == 0 ? $"Resets in {hours}{DisplayFormat.Nbsp}h"
                : $"Resets in {hours}{DisplayFormat.Nbsp}h {minutes}{DisplayFormat.Nbsp}min";
        }

        var local = resetsAt.ToLocalTime();
        return $"Resets {local.ToString("dddd", CultureInfo.CurrentCulture)}";
    }

    /// <summary>New plan usage from Claude Code (Claude only).</summary>
    public void SetPlan(PlanUsage? plan, PlanConnection connection)
    {
        _plan = plan;
        _planConnection = connection;
        OnPlanChanged();
    }

    /// <summary>Re-evaluates reset countdowns and expired windows (time passed).</summary>
    public void RefreshPlanClock() => OnPlanChanged();

    private void OnPlanChanged()
    {
        foreach (var name in new[]
        {
            nameof(CurrentWindow), nameof(WeeklyWindow), nameof(HasPlan), nameof(HasNoPlan), nameof(HasBothPlanWindows),
            nameof(ShowWeekly), nameof(ShowCurrent), nameof(PlanPercentText), nameof(PlanFraction), nameof(PlanWindowName),
            nameof(PlanResetText), nameof(PlanUnavailableReason), nameof(PlanState), nameof(CanConnectPlan), nameof(CompactValue),
            nameof(CompactPrimaryValue), nameof(CompactAccessibleText), nameof(DetailSummary),
        })
        {
            OnPropertyChanged(name);
        }
    }

    // Exact detail (tooltip on the headline) ------------------------------------------------------------

    /// <summary>Every recorded figure of today with exact counts, for the hover detail.</summary>
    public string DetailSummary
    {
        get
        {
            if (!IsReady)
            {
                return StatusText;
            }

            var t = _snapshot.Today;
            string N(long v) => v.ToString("N0", CultureInfo.CurrentCulture);
            var lines = new List<string>
            {
                $"Today: {N(t.Fresh)} fresh tokens",
            };
            if (CurrentWindow is { } current)
            {
                lines.Add($"Plan, current: {Math.Round(current.UsedPercent):0}%{(PlanResetLine(current))}");
            }

            if (WeeklyWindow is { } weekly)
            {
                lines.Add($"Plan, weekly: {Math.Round(weekly.UsedPercent):0}%{(PlanResetLine(weekly))}");
            }

            lines.Add(string.Empty);
            lines.Add($"Input            {N(t.Input)}");
            lines.Add($"Cache write   {N(t.CacheWrite)}");
            lines.Add($"Output          {N(t.Output)}");
            lines.Add($"Cache read    {N(t.CacheRead)}");
            lines.Add($"Processed     {N(t.Processed)}");
            lines.Add($"Sessions       {_snapshot.SessionsToday.ToString(CultureInfo.CurrentCulture)}");
            lines.Add(string.Empty);
            lines.Add($"Estimated API equivalent today: {ValueText}{(_snapshot.ApiValuePartial ? " (partly priced)" : string.Empty)}");
            lines.Add("An estimate at list prices, not what you pay.");
            return string.Join(Environment.NewLine, lines);
        }
    }

    private static string PlanResetLine(PlanWindow window) =>
        window.ResetsAt == DateTimeOffset.MaxValue ? string.Empty : $", {ResetText(window.ResetsAt, DateTimeOffset.UtcNow).ToLowerInvariant()}";

    /// <summary>Limit usage when known, otherwise sessions.</summary>
    public string DetailText
    {
        get
        {
            if (_snapshot.LimitPercent is { } p)
            {
                return $"{Math.Round(p):0}% of {DisplayFormat.LimitWindow(_snapshot.LimitWindowMinutes ?? 0)}";
            }

            // Sessions are listed in the breakdown; without usage there is no breakdown.
            return HasUsageToday ? string.Empty : "No sessions today";
        }
    }

    public bool HasDetail => DetailText.Length > 0;

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

    /// <summary>The plan windows for the usage history readout (Codex plan data comes with the snapshot).</summary>
    public PlanUsage? Plan => _plan;
}
