using DeveloperIsland.Core.Focus;
using DeveloperIsland.Core.Formatting;
using DeveloperIsland.Core.Providers;

namespace DeveloperIsland.ViewModels;

public enum IslandTab
{
    Usage,
    Music,
    Focus,
}

/// <summary>
/// Root view model: decides what the compact capsule shows, which tabs exist, and what the medium
/// state ("live activity") says for each kind of event.
/// </summary>
public sealed class IslandViewModel : ObservableObject
{
    private IslandTab _selectedTab = IslandTab.Usage;
    private ActivityKind? _lastEventKind;

    public IslandViewModel(UsageViewModel usage, MusicViewModel music, FocusViewModel focus, bool isDemo)
    {
        Usage = usage;
        Music = music;
        Focus = focus;
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
    }

    /// <summary>Requests the medium state for an event (duration null means the default).</summary>
    public event Action<TimeSpan?>? ActivityRequested;

    /// <summary>The tabs changed (a module was turned on or off).</summary>
    public event Action? TabsChanged;

    public UsageViewModel Usage { get; }

    public MusicViewModel Music { get; }

    public FocusViewModel Focus { get; }

    public ActivityViewModel Activity { get; } = new();

    public bool IsDemo { get; }

    // Compact capsule -----------------------------------------------------------------------

    public bool CompactShowFocus => Focus.IsActive;

    public bool CompactShowMusic => Music.IsEnabled && Music.IsPlaying;

    public bool CompactShowClaude => Usage.ShowClaude && ShowProvider(Usage.Claude, Usage.Codex);

    public bool CompactShowCodex => Usage.ShowCodex && ShowProvider(Usage.Codex, Usage.Claude);

    public bool CompactIsEmpty => !CompactShowFocus && !CompactShowMusic && !CompactShowClaude && !CompactShowCodex;

    public string CompactAccessibleName
    {
        get
        {
            var parts = new List<string> { "Developer Island" };
            if (CompactShowFocus)
            {
                parts.Add(Focus.AccessibleSummary);
            }

            if (CompactShowClaude)
            {
                parts.Add(Usage.Claude.CompactAccessibleText);
            }

            if (CompactShowCodex)
            {
                parts.Add(Usage.Codex.CompactAccessibleText);
            }

            if (CompactShowMusic)
            {
                parts.Add(Music.AccessibleSummary);
            }

            return string.Join(", ", parts);
        }
    }

    // Tabs ------------------------------------------------------------------------------------

    public bool IsUsageTabAvailable => Usage.HasAnyProvider;

    public bool IsMusicTabAvailable => Music.IsEnabled;

    public bool IsFocusTabAvailable => Focus.IsEnabled;

    public IReadOnlyList<IslandTab> AvailableTabs
    {
        get
        {
            var tabs = new List<IslandTab>(3);
            if (IsUsageTabAvailable) tabs.Add(IslandTab.Usage);
            if (IsMusicTabAvailable) tabs.Add(IslandTab.Music);
            if (IsFocusTabAvailable) tabs.Add(IslandTab.Focus);
            return tabs;
        }
    }

    public bool HasTabs => AvailableTabs.Count > 0;

    public bool HasNoTabs => !HasTabs;

    public IslandTab SelectedTab
    {
        get => _selectedTab;
        set
        {
            if (SetProperty(ref _selectedTab, value))
            {
                OnPropertyChanged(nameof(IsUsageSelected));
                OnPropertyChanged(nameof(IsMusicSelected));
                OnPropertyChanged(nameof(IsFocusSelected));
            }
        }
    }

    public bool IsUsageSelected => IsUsageTabAvailable && _selectedTab == IslandTab.Usage;

    public bool IsMusicSelected => IsMusicTabAvailable && _selectedTab == IslandTab.Music;

    public bool IsFocusSelected => IsFocusTabAvailable && _selectedTab == IslandTab.Focus;

    /// <summary>Picks the tab that matches what the user just saw, before expanding.</summary>
    public void SelectTabForExpand()
    {
        IslandTab? preferred = _lastEventKind switch
        {
            ActivityKind.Music => IslandTab.Music,
            ActivityKind.Focus => IslandTab.Focus,
            ActivityKind.Usage => IslandTab.Usage,
            _ => null,
        };
        _lastEventKind = null;

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

    public void EnsureValidTab()
    {
        var tabs = AvailableTabs;
        if (!tabs.Contains(_selectedTab) && tabs.Count > 0)
        {
            SelectedTab = tabs[0];
        }

        OnPropertyChanged(nameof(IsUsageTabAvailable));
        OnPropertyChanged(nameof(IsMusicTabAvailable));
        OnPropertyChanged(nameof(IsFocusTabAvailable));
        OnPropertyChanged(nameof(HasTabs));
        OnPropertyChanged(nameof(HasNoTabs));
        OnPropertyChanged(nameof(IsUsageSelected));
        OnPropertyChanged(nameof(IsMusicSelected));
        OnPropertyChanged(nameof(IsFocusSelected));
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

    /// <summary>
    /// A provider appears in the capsule when it has something to say today. With a focus session
    /// running, only one provider is shown to keep the capsule short. When nothing else is shown,
    /// a detected Claude Code still anchors the capsule.
    /// </summary>
    private bool ShowProvider(AiProviderViewModel provider, AiProviderViewModel other)
    {
        if (provider.IsWorthShowingCompact)
        {
            if (!Focus.IsActive)
            {
                return true;
            }

            // With focus running, prefer the active provider, else Claude.
            var otherWins = other.IsEnabled && other.IsWorthShowingCompact
                && (other.IsActive && !provider.IsActive || (other.IsActive == provider.IsActive && other.Kind < provider.Kind));
            return !otherWins;
        }

        var nothingElse = !Focus.IsActive && !(Music.IsEnabled && Music.IsPlaying)
            && !(other.IsEnabled && other.IsWorthShowingCompact);
        return nothingElse && provider.Kind == Core.Models.AiProviderKind.Claude && provider.IsReady;
    }

    private void OnCompactChanged()
    {
        OnPropertyChanged(nameof(CompactShowFocus));
        OnPropertyChanged(nameof(CompactShowMusic));
        OnPropertyChanged(nameof(CompactShowClaude));
        OnPropertyChanged(nameof(CompactShowCodex));
        OnPropertyChanged(nameof(CompactIsEmpty));
        OnPropertyChanged(nameof(CompactAccessibleName));
    }
}
