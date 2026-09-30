using DeveloperIsland.Core.Focus;
using DeveloperIsland.Core.Formatting;
using DeveloperIsland.Core.Modules;
using DeveloperIsland.Core.Providers;

namespace DeveloperIsland.ViewModels;

/// <summary>A section of the expanded island. Claude and Codex share the Usage tab.</summary>
public enum IslandTab
{
    Usage,
    Music,
    Git,
    GitHub,
    Focus,
    Calendar,
    Tasks,
    System,
}

/// <summary>
/// Root view model: decides what the compact capsule shows, which tabs exist and in which order, and
/// what the medium state ("live activity") says for each kind of event.
/// </summary>
public sealed class IslandViewModel : ObservableObject
{
    private IslandTab _selectedTab = IslandTab.Usage;
    private ActivityKind? _lastEventKind;
    private IReadOnlyList<ModuleId> _order = ModuleCatalog.DefaultOrder;
    private IslandTab? _pendingTab;

    public IslandViewModel(UsageViewModel usage, MusicViewModel music, FocusViewModel focus, ModuleViewModels modules, bool isDemo)
    {
        Usage = usage;
        Music = music;
        Focus = focus;
        Git = modules.Git;
        GitHub = modules.GitHub;
        Calendar = modules.Calendar;
        Tasks = modules.Tasks;
        System = modules.System;
        IsDemo = isDemo;
        Usage.PropertyChanged += (_, _) => OnCompactChanged();
        Usage.Claude.PropertyChanged += (_, _) => OnCompactChanged();
        Usage.Codex.PropertyChanged += (_, _) => OnCompactChanged();
        Music.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is not (nameof(MusicViewModel.PositionText) or nameof(MusicViewModel.Progress)))
            {
                OnCompactChanged();
            }
        };
        Music.ArtChanged += () =>
        {
            if (Activity.Kind == ActivityKind.Music && Activity.Title == Music.Title)
            {
                Activity.SetArt(Music.Art);
            }
        };
        Focus.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is "" or null or nameof(FocusViewModel.IsEnabled))
            {
                OnCompactChanged();
            }
        };
        Calendar.PropertyChanged += (_, _) => OnCompactChanged();
        System.PropertyChanged += (_, _) => OnCompactChanged();
        GitHub.PropertyChanged += (_, _) => OnCompactChanged();
    }

    /// <summary>Requests the medium state for an event (duration null means the default).</summary>
    public event Action<TimeSpan?>? ActivityRequested;

    /// <summary>The tabs changed (a module was turned on or off, or reordered).</summary>
    public event Action? TabsChanged;

    public UsageViewModel Usage { get; }

    public MusicViewModel Music { get; }

    public FocusViewModel Focus { get; }

    public GitViewModel Git { get; }

    public GitHubViewModel GitHub { get; }

    public CalendarViewModel Calendar { get; }

    public TasksViewModel Tasks { get; }

    public SystemViewModel System { get; }

    public ActivityViewModel Activity { get; } = new();

    public bool IsDemo { get; }

    // Compact capsule -----------------------------------------------------------------------

    public bool CompactShowFocus => Focus.IsActive;

    public bool CompactShowCalendar => Calendar.HasCompact;

    public bool CompactShowSystem => System.HasCompact;

    public bool CompactShowGitHub => GitHub.HasCompactSignal;

    public bool CompactShowMusic => Music.IsEnabled && Music.IsPlaying;

    public bool CompactShowClaude => Usage.ShowClaude && ShowProvider(Usage.Claude, Usage.Codex);

    public bool CompactShowCodex => Usage.ShowCodex && ShowProvider(Usage.Codex, Usage.Claude);

    public bool CompactIsEmpty => !CompactShowFocus && !CompactShowCalendar && !CompactShowSystem && !CompactShowGitHub
        && !CompactShowMusic && !CompactShowClaude && !CompactShowCodex;

    public string CompactAccessibleName
    {
        get
        {
            var parts = new List<string> { "Developer Island" };
            if (CompactShowFocus) parts.Add(Focus.AccessibleSummary);
            if (CompactShowCalendar) parts.Add(Calendar.CompactText);
            if (CompactShowSystem) parts.Add(System.CompactText);
            if (CompactShowGitHub) parts.Add(GitHub.CompactText);
            if (CompactShowClaude) parts.Add(Usage.Claude.CompactAccessibleText);
            if (CompactShowCodex) parts.Add(Usage.Codex.CompactAccessibleText);
            if (CompactShowMusic) parts.Add(Music.AccessibleSummary);
            return string.Join(", ", parts);
        }
    }

    /// <summary>Something time-critical is in the capsule; AI usage then shrinks to one provider.</summary>
    private bool HasUrgentSignal => Focus.IsActive || Calendar.HasCompact || System.HasCompact || GitHub.HasCompactSignal;

    // Tabs ------------------------------------------------------------------------------------

    public static string TabName(IslandTab tab) => tab switch
    {
        IslandTab.GitHub => "GitHub",
        _ => tab.ToString(),
    };

    /// <summary>The module whose icon represents a tab.</summary>
    public static ModuleId TabModule(IslandTab tab) => tab switch
    {
        IslandTab.Usage => ModuleId.Claude,
        IslandTab.Music => ModuleId.Music,
        IslandTab.Git => ModuleId.Git,
        IslandTab.GitHub => ModuleId.GitHub,
        IslandTab.Focus => ModuleId.Focus,
        IslandTab.Calendar => ModuleId.Calendar,
        IslandTab.Tasks => ModuleId.Tasks,
        _ => ModuleId.System,
    };

    public bool IsTabAvailable(IslandTab tab) => tab switch
    {
        IslandTab.Usage => Usage.HasAnyProvider,
        IslandTab.Music => Music.IsEnabled,
        IslandTab.Focus => Focus.IsEnabled,
        IslandTab.Git => Git.IsEnabled,
        IslandTab.GitHub => GitHub.IsEnabled,
        IslandTab.Calendar => Calendar.IsEnabled,
        IslandTab.Tasks => Tasks.IsEnabled,
        IslandTab.System => System.IsEnabled,
        _ => false,
    };

    /// <summary>Enabled tabs in the user's module order (Usage sits where Claude or Codex comes first).</summary>
    public IReadOnlyList<IslandTab> AvailableTabs
    {
        get
        {
            var tabs = new List<IslandTab>();
            foreach (var module in _order)
            {
                var tab = module switch
                {
                    ModuleId.Claude or ModuleId.Codex => IslandTab.Usage,
                    ModuleId.Music => IslandTab.Music,
                    ModuleId.Git => IslandTab.Git,
                    ModuleId.GitHub => IslandTab.GitHub,
                    ModuleId.Focus => IslandTab.Focus,
                    ModuleId.Calendar => IslandTab.Calendar,
                    ModuleId.Tasks => IslandTab.Tasks,
                    _ => IslandTab.System,
                };
                if (!tabs.Contains(tab) && IsTabAvailable(tab))
                {
                    tabs.Add(tab);
                }
            }

            return tabs;
        }
    }

    public bool HasTabs => AvailableTabs.Count > 0;

    public bool HasNoTabs => !HasTabs;

    public IslandTab SelectedTab
    {
        get => _selectedTab;
        set => SetProperty(ref _selectedTab, value);
    }

    public void SetModuleOrder(IReadOnlyList<ModuleId> order)
    {
        if (!order.SequenceEqual(_order))
        {
            _order = order;
            EnsureValidTab();
        }
    }

    /// <summary>Picks the tab that matches what the user just saw, before expanding.</summary>
    public void SelectTabForExpand()
    {
        IslandTab? preferred = _pendingTab ?? _lastEventKind switch
        {
            ActivityKind.Music => IslandTab.Music,
            ActivityKind.Focus => IslandTab.Focus,
            ActivityKind.Usage => IslandTab.Usage,
            ActivityKind.Calendar => IslandTab.Calendar,
            ActivityKind.System => IslandTab.System,
            _ => null,
        };
        _lastEventKind = null;
        _pendingTab = null;

        preferred ??= Focus.IsActive ? IslandTab.Focus : null;
        var tabs = AvailableTabs;
        if (preferred is { } p && tabs.Contains(p))
        {
            SelectedTab = p;
        }
        else if (!tabs.Contains(_selectedTab) && tabs.Count > 0)
        {
            SelectedTab = tabs[0];
        }
    }

    /// <summary>Opens straight on a tab (quick capture, notifications).</summary>
    public void PrepareExpandTo(IslandTab tab)
    {
        _lastEventKind = null;
        _pendingTab = tab;
        if (AvailableTabs.Contains(tab))
        {
            SelectedTab = tab;
        }
    }

    public void EnsureValidTab()
    {
        var tabs = AvailableTabs;
        if (!tabs.Contains(_selectedTab) && tabs.Count > 0)
        {
            SelectedTab = tabs[0];
        }

        OnPropertyChanged(nameof(HasTabs));
        OnPropertyChanged(nameof(HasNoTabs));
        OnPropertyChanged(nameof(AvailableTabs));
        TabsChanged?.Invoke();
    }

    // Activity (medium state) -------------------------------------------------------------------

    /// <summary>Fills the activity with the most relevant live information for a hover peek.</summary>
    public void PreparePeek()
    {
        _lastEventKind = null;
        if (Focus.IsActive)
        {
            SetFocusActivity(Focus.IsPaused ? "Focus paused" : "Focus", Focus.StatusText);
        }
        else if (Calendar.HasCompact)
        {
            SetCalendarActivity();
        }
        else if (System.HasCompact)
        {
            Activity.Set(ActivityKind.System, System.CompactText, System.BatteryLow ? "Plug in soon" : "Your PC is busy", glyph: System.CompactGlyph);
        }
        else if (Music.IsEnabled && Music.HasTrack && Music.IsPlaying)
        {
            SetMusicActivity();
        }
        else if (Usage.HasAnyProvider)
        {
            Activity.Set(ActivityKind.Usage, Usage.PeekTitle, Usage.PeekSubtitle, mark: Usage.PeekMark);
        }
        else if (Music.IsEnabled && Music.HasTrack)
        {
            SetMusicActivity();
        }
        else
        {
            Activity.Set(ActivityKind.Info, "Developer Island", "Click to open", glyph: "");
        }
    }

    public void OnTrackChanged()
    {
        if (!Music.IsEnabled)
        {
            return;
        }

        SetMusicActivity();
        _lastEventKind = ActivityKind.Music;
        ActivityRequested?.Invoke(null);
    }

    public void OnFocusStarted(FocusSnapshot snapshot)
    {
        if (!Focus.IsEnabled)
        {
            return;
        }

        SetFocusActivity("Focus started", $"{DisplayFormat.Duration(snapshot.Planned)} session");
        _lastEventKind = ActivityKind.Focus;
        ActivityRequested?.Invoke(TimeSpan.FromSeconds(3));
    }

    public void OnFocusEnded(bool completed, TimeSpan focused)
    {
        if (!Focus.IsEnabled)
        {
            return;
        }

        Activity.Set(
            ActivityKind.Focus,
            completed ? "Focus complete" : "Focus ended",
            $"{DisplayFormat.Duration(focused)} focused",
            glyph: completed ? "" : "",
            accentGlyph: completed);
        _lastEventKind = ActivityKind.Focus;
        ActivityRequested?.Invoke(TimeSpan.FromSeconds(completed ? 6 : 3.5));
    }

    /// <summary>An event is about to start: announce it once (called by the host at the lead time).</summary>
    public void OnEventSoon()
    {
        if (!Calendar.HasCompact)
        {
            return;
        }

        SetCalendarActivity();
        _lastEventKind = ActivityKind.Calendar;
        ActivityRequested?.Invoke(TimeSpan.FromSeconds(5));
    }

    public void OnAiActivity(AiActivity activity)
    {
        var provider = activity.Provider == Core.Models.AiProviderKind.Claude ? Usage.Claude : Usage.Codex;
        if (!provider.IsEnabled)
        {
            return;
        }

        var trailing = activity.Kind == AiActivityKind.SessionStarted && provider.IsReady ? provider.CompactValue : string.Empty;
        Activity.Set(ActivityKind.Usage, activity.Title, activity.Detail ?? string.Empty, trailing, mark: provider.ShortName);
        _lastEventKind = ActivityKind.Usage;
        ActivityRequested?.Invoke(null);
    }

    /// <summary>Keeps a visible focus activity's countdown current.</summary>
    public void RefreshActivityTrailing()
    {
        if (Activity.Kind == ActivityKind.Focus && Focus.IsActive)
        {
            Activity.SetTrailing(Focus.RemainingText);
        }
    }

    private void SetMusicActivity()
    {
        Activity.Set(ActivityKind.Music, Music.Title, Music.Artist, string.Empty, glyph: "", art: Music.Art);
    }

    private void SetFocusActivity(string title, string subtitle)
    {
        Activity.Set(ActivityKind.Focus, title, subtitle, Focus.RemainingText, glyph: "", accentGlyph: Focus.IsRunning);
    }

    private void SetCalendarActivity()
    {
        Activity.Set(ActivityKind.Calendar, Calendar.NextTitle, Calendar.NextWhen, string.Empty, glyph: "", accentGlyph: true);
    }

    /// <summary>
    /// A provider appears in the capsule when it has something to say today. With something urgent
    /// in the capsule (focus, an event, an alert), only one provider is shown to keep it short. When
    /// nothing else is shown, a detected Claude Code still anchors the capsule.
    /// </summary>
    private bool ShowProvider(AiProviderViewModel provider, AiProviderViewModel other)
    {
        if (provider.IsWorthShowingCompact)
        {
            if (!HasUrgentSignal)
            {
                return true;
            }

            // With something urgent showing, prefer the active provider, else Claude.
            var otherWins = other.IsEnabled && other.IsWorthShowingCompact
                && (other.IsActive && !provider.IsActive || (other.IsActive == provider.IsActive && other.Kind < provider.Kind));
            return !otherWins;
        }

        var nothingElse = !HasUrgentSignal && !(Music.IsEnabled && Music.IsPlaying)
            && !(other.IsEnabled && other.IsWorthShowingCompact);
        return nothingElse && provider.Kind == Core.Models.AiProviderKind.Claude && provider.IsReady;
    }

    private void OnCompactChanged()
    {
        OnPropertyChanged(nameof(CompactShowFocus));
        OnPropertyChanged(nameof(CompactShowCalendar));
        OnPropertyChanged(nameof(CompactShowSystem));
        OnPropertyChanged(nameof(CompactShowGitHub));
        OnPropertyChanged(nameof(CompactShowMusic));
        OnPropertyChanged(nameof(CompactShowClaude));
        OnPropertyChanged(nameof(CompactShowCodex));
        OnPropertyChanged(nameof(CompactIsEmpty));
        OnPropertyChanged(nameof(CompactAccessibleName));
    }
}

/// <summary>The view models of the modules added after V1, grouped to keep constructors short.</summary>
public sealed record ModuleViewModels(GitViewModel Git, GitHubViewModel GitHub, CalendarViewModel Calendar, TasksViewModel Tasks, SystemViewModel System)
{
    public static ModuleViewModels Create() => new(new GitViewModel(), new GitHubViewModel(), new CalendarViewModel(), new TasksViewModel(), new SystemViewModel());

    public IEnumerable<ModuleViewModel> All => [Git, GitHub, Calendar, Tasks, System];
}
