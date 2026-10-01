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
    private IReadOnlyList<ModuleId> _favorites = [];
    private ModuleId? _lastActive;
    private ModuleId? _compactModule;
    private long _turn;
    private bool _refreshingCompact;

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
        Activity.Privacy = Privacy;
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
        Git.PropertyChanged += (_, _) => OnPrimaryChanged(ModuleId.Git);
        Tasks.PropertyChanged += (_, _) => OnPrimaryChanged(ModuleId.Tasks);
        Focus.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(FocusViewModel.RemainingText))
            {
                OnPrimaryChanged(ModuleId.Focus);
            }
        };
        Usage.Claude.FavoriteToggleRequested += _ => ToggleFavorite(ModuleId.Claude);
        Usage.Codex.FavoriteToggleRequested += _ => ToggleFavorite(ModuleId.Codex);
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

    /// <summary>Camera and microphone in use: shown in every state, independent of the module shown.</summary>
    public PrivacyViewModel Privacy { get; } = new();

    public bool IsDemo { get; }

    // Compact capsule -----------------------------------------------------------------------

    // Time-critical signals stay in the capsule in both modes, unless the featured module already shows them.
    public bool CompactShowFocus => Focus.IsActive && CompactModule != ModuleId.Focus;

    public bool CompactShowCalendar => Calendar.HasCompact && CompactModule != ModuleId.Calendar;

    public bool CompactShowSystem => System.HasCompact && CompactModule != ModuleId.System;

    public bool CompactShowGitHub => GitHub.HasCompactSignal && CompactModule != ModuleId.GitHub;

    // The classic summary (AI usage and music art) is shown when no module is featured.
    public bool CompactShowMusic => !IsFeaturing && Music.IsEnabled && Music.IsPlaying;

    public bool CompactShowClaude => !IsFeaturing && Usage.ShowClaude && ShowProvider(Usage.Claude, Usage.Codex);

    public bool CompactShowCodex => !IsFeaturing && Usage.ShowCodex && ShowProvider(Usage.Codex, Usage.Claude);

    // Featured module (favorites, or the module opened last) ----------------------------------------

    /// <summary>Raised when the user stars or unstars a module (the host persists it).</summary>
    public event Action<ModuleId, bool>? FavoriteChanged;

    /// <summary>Raised when the user opens a module (the host remembers it).</summary>
    public event Action<ModuleId>? ModuleOpened;

    /// <summary>The featured module changed (the compact view cross-fades).</summary>
    public event Action? CompactModuleChanged;

    public IReadOnlyList<ModuleId> Favorites => _favorites;

    public bool IsFavorite(ModuleId module) => _favorites.Contains(module);

    /// <summary>The module the compact island features, or null for the classic summary.</summary>
    public ModuleId? CompactModule => _compactModule;

    public bool IsFeaturing => _compactModule is not null;

    public bool CompactRotates => CompactSelector.Rotates(_favorites.Where(IsModuleEnabled).ToList());

    public string CompactPrimaryLabel => _compactModule switch
    {
        ModuleId.Claude => Usage.Claude.ShortName,
        ModuleId.Codex => Usage.Codex.ShortName,
        ModuleId.Focus when !Focus.IsActive => "Focus",
        _ => string.Empty,
    };

    public bool HasCompactPrimaryLabel => CompactPrimaryLabel.Length > 0;

    public string CompactPrimaryValue => _compactModule switch
    {
        ModuleId.Claude => ProviderValue(Usage.Claude),
        ModuleId.Codex => ProviderValue(Usage.Codex),
        ModuleId.Music => Music.HasTrack ? Music.Title : "Nothing playing",
        ModuleId.Focus => Focus.IsActive ? Focus.RemainingText : "Ready",
        ModuleId.Calendar => Calendar.CompactNextText,
        ModuleId.System => System.CompactSummary,
        ModuleId.Git => Git.CompactText,
        ModuleId.GitHub => GitHub.CompactSummary,
        ModuleId.Tasks => Tasks.CompactText,
        _ => string.Empty,
    };

    /// <summary>A secondary part after a dot (Git changes). Claude and Codex show no money in the capsule.</summary>
    public string CompactPrimaryDetail => _compactModule switch
    {
        ModuleId.Git => Git.CompactDetail,
        _ => string.Empty,
    };

    public bool HasCompactPrimaryDetail => CompactPrimaryDetail.Length > 0;

    /// <summary>The featured music shows its artwork instead of an icon.</summary>
    public bool CompactPrimaryShowsArt => _compactModule == ModuleId.Music && Music.HasTrack;

    /// <summary>A running focus session shows the accent dot, like the classic capsule.</summary>
    public bool CompactPrimaryShowsDot => _compactModule == ModuleId.Focus && Focus.IsActive;

    public bool CompactPrimaryShowsIcon => _compactModule is not null && !CompactPrimaryShowsArt && !CompactPrimaryShowsDot;

    public string CompactPrimaryAccessibleName => _compactModule is { } m
        ? string.Join(" ", new[] { ModuleCatalog.DisplayName(m), CompactPrimaryValue, CompactPrimaryDetail }.Where(t => t.Length > 0))
        : string.Empty;

    /// <summary>Applies the saved favorites (already filtered to enabled modules, in tab order).</summary>
    public void SetFavorites(IReadOnlyList<ModuleId> favorites)
    {
        _favorites = favorites;
        Usage.Claude.IsFavorite = favorites.Contains(ModuleId.Claude);
        Usage.Codex.IsFavorite = favorites.Contains(ModuleId.Codex);
        OnPropertyChanged(nameof(Favorites));
        RefreshCompactModule();
    }

    public void SetLastActive(ModuleId? module)
    {
        _lastActive = module;
        RefreshCompactModule();
    }

    public void ToggleFavorite(ModuleId module) => FavoriteChanged?.Invoke(module, !IsFavorite(module));

    /// <summary>Next featured favorite (called by the host every rotation interval while compact).</summary>
    public void AdvanceCompactRotation()
    {
        _turn++;
        RefreshCompactModule();
    }

    private bool HasCompactContent(ModuleId module) => module switch
    {
        ModuleId.Claude => Usage.Claude.IsWorthShowingCompact,
        ModuleId.Codex => Usage.Codex.IsWorthShowingCompact,
        ModuleId.Music => Music.IsEnabled && Music.HasTrack,
        ModuleId.Focus => Focus.IsActive,
        ModuleId.Calendar => Calendar.IsReady && Calendar.HasNext,
        ModuleId.System => System.IsReady,
        ModuleId.Git => Git.IsReady && Git.Active is not null,
        ModuleId.GitHub => GitHub.IsReady,
        ModuleId.Tasks => Tasks.IsReady && Tasks.OpenCount > 0,
        _ => false,
    };

    private bool IsModuleEnabled(ModuleId module) => module switch
    {
        ModuleId.Claude => Usage.Claude.IsEnabled,
        ModuleId.Codex => Usage.Codex.IsEnabled,
        ModuleId.Music => Music.IsEnabled,
        ModuleId.Focus => Focus.IsEnabled,
        ModuleId.Git => Git.IsEnabled,
        ModuleId.GitHub => GitHub.IsEnabled,
        ModuleId.Calendar => Calendar.IsEnabled,
        ModuleId.Tasks => Tasks.IsEnabled,
        ModuleId.System => System.IsEnabled,
        _ => false,
    };

    private static string ProviderValue(AiProviderViewModel provider) =>
        provider.CompactPrimaryValue is { Length: > 0 } value ? value : provider.StatusText;

    private void RefreshCompactModule()
    {
        var favorites = _favorites.Where(IsModuleEnabled).ToList();
        var last = _lastActive is { } l && IsModuleEnabled(l) ? l : (ModuleId?)null;
        var next = CompactSelector.Select(favorites, last, HasCompactContent, _turn);
        if (next != _compactModule)
        {
            _compactModule = next;
            OnCompactChanged();
            CompactModuleChanged?.Invoke();
        }
        else
        {
            OnPrimaryChanged(next);
        }
    }

    /// <summary>A module's content changed; refresh the capsule only if it is the featured one.</summary>
    private void OnPrimaryChanged(ModuleId? module)
    {
        if (module is not null && module == _compactModule)
        {
            OnPropertyChanged(nameof(CompactPrimaryLabel));
            OnPropertyChanged(nameof(HasCompactPrimaryLabel));
            OnPropertyChanged(nameof(CompactPrimaryValue));
            OnPropertyChanged(nameof(CompactPrimaryDetail));
            OnPropertyChanged(nameof(HasCompactPrimaryDetail));
            OnPropertyChanged(nameof(CompactPrimaryShowsArt));
            OnPropertyChanged(nameof(CompactPrimaryShowsDot));
            OnPropertyChanged(nameof(CompactPrimaryShowsIcon));
            OnPropertyChanged(nameof(CompactPrimaryAccessibleName));
        }
    }

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

    /// <summary>The tab that shows a module (Claude and Codex share Usage).</summary>
    public static IslandTab TabFor(ModuleId module) => module switch
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

    /// <summary>The module a tab stands for (Usage: the active provider, else Claude, else Codex).</summary>
    public ModuleId ModuleForTab(IslandTab tab) => tab switch
    {
        IslandTab.Usage when Usage.Codex.IsEnabled && (!Usage.Claude.IsEnabled || (Usage.Codex.IsActive && !Usage.Claude.IsActive)) => ModuleId.Codex,
        IslandTab.Usage => ModuleId.Claude,
        _ => TabModule(tab),
    };

    /// <summary>
    /// The user picked a tab (click or arrow keys). Only this changes the module opened last; the
    /// tab the island opens on by itself never does.
    /// </summary>
    public void NoteTabOpened(IslandTab tab)
    {
        // Usage holds both providers: keep the one already featured if it is one of them.
        var module = tab == IslandTab.Usage && _compactModule is ModuleId.Claude or ModuleId.Codex
            ? _compactModule.Value
            : ModuleForTab(tab);
        ModuleOpened?.Invoke(module);
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
        ModuleId? announced = _lastEventKind switch
        {
            ActivityKind.Music => ModuleId.Music,
            ActivityKind.Focus => ModuleId.Focus,
            ActivityKind.Usage => ModuleId.Claude,
            ActivityKind.Calendar => ModuleId.Calendar,
            ActivityKind.System => ModuleId.System,
            _ => null,
        };
        var requested = _pendingTab is { } pending ? TabModule(pending) : (ModuleId?)null;
        _lastEventKind = null;
        _pendingTab = null;

        IslandTab? preferred = IslandContent.ChooseTabOnOpen(requested, announced, _compactModule, Focus.IsActive) is { } module ? TabFor(module) : null;
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
        var facts = new IslandFacts(Focus.IsActive, Calendar.HasCompact, System.HasCompact, Music.IsEnabled && Music.HasTrack && Music.IsPlaying, Music.IsEnabled && Music.HasTrack, Usage.HasAnyProvider);
        if (IslandContent.ChoosePeek(_compactModule, facts) == PeekContent.Featured && _compactModule is { } featured)
        {
            SetFeaturedActivity(featured);
            return;
        }

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

    /// <summary>The hover peek of the featured module (no API equivalent here; that lives in the expanded view).</summary>
    private void SetFeaturedActivity(ModuleId module)
    {
        switch (module)
        {
            case ModuleId.Claude or ModuleId.Codex:
                var provider = module == ModuleId.Claude ? Usage.Claude : Usage.Codex;
                Activity.Set(ActivityKind.Usage, provider.DisplayName, provider.PeekLine, provider.PlanPeekTrailing, mark: provider.ShortName);
                break;
            case ModuleId.Music when Music.IsEnabled && Music.HasTrack:
                SetMusicActivity();
                break;
            case ModuleId.Music:
                Activity.Set(ActivityKind.Music, "Music", "Nothing playing", glyph: "\uE8D6");
                break;
            case ModuleId.Focus when Focus.IsActive:
                SetFocusActivity(Focus.IsPaused ? "Focus paused" : "Focus", Focus.StatusText);
                break;
            case ModuleId.Focus:
                Activity.Set(ActivityKind.Focus, "Focus", Focus.TodayText, glyph: "\uE916");
                break;
            case ModuleId.Calendar:
                if (Calendar.HasNext)
                {
                    SetCalendarActivity();
                }
                else
                {
                    Activity.Set(ActivityKind.Calendar, "Calendar", Calendar.NextWhen, glyph: "\uE787");
                }

                break;
            case ModuleId.System:
                Activity.Set(ActivityKind.System, System.CompactSummary, System.PeekLine, module: ModuleId.System);
                break;
            case ModuleId.Git:
                Activity.Set(ActivityKind.Info, Git.Active?.Name ?? "Git", Git.Active is null ? Git.StateTitle : $"{Git.BranchText} · {Git.CompactDetail}", module: ModuleId.Git);
                break;
            case ModuleId.GitHub:
                Activity.Set(ActivityKind.Info, "GitHub", GitHub.IsReady ? GitHub.CompactSummary : GitHub.StateTitle, module: ModuleId.GitHub);
                break;
            case ModuleId.Tasks:
                Activity.Set(ActivityKind.Info, "Tasks", Tasks.SummaryText, module: ModuleId.Tasks);
                break;
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
        // A module's state can change which favorite has something to say.
        if (!_refreshingCompact)
        {
            _refreshingCompact = true;
            try
            {
                RefreshCompactModule();
            }
            finally
            {
                _refreshingCompact = false;
            }
        }

        OnPropertyChanged(nameof(IsFeaturing));
        OnPrimaryChanged(_compactModule);
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
