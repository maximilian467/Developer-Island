using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using DeveloperIsland.UI.Animations;
using DeveloperIsland.UI.Components;
using DeveloperIsland.ViewModels;

namespace DeveloperIsland.UI.Expanded;

/// <summary>
/// The expanded island. Tabs are icons (nine modules do not fit as words in 420 DIP); the selected
/// tab's name sits beside them, and each icon has a tooltip. Panels are separate controls; this view
/// only switches between them.
/// </summary>
public sealed partial class ExpandedView : UserControl
{
    /// <summary>From this many tabs on, the tab buttons are 28 instead of 32 DIP wide.</summary>
    private const int CompactTabCount = 9;

    private readonly Dictionary<IslandTab, Button> _tabButtons = [];
    private readonly Dictionary<IslandTab, Control> _panels;
    private readonly TranslateTransform _indicator = new();
    private IslandViewModel? _viewModel;
    private bool _indicatorPlaced;
    private bool _isShown;
    private double _indicatorX;
    private double _indicatorV;
    private double _indicatorTarget;
    private bool _indicatorAnimating;
    private TimeSpan? _lastFrame;

    public ExpandedView()
    {
        InitializeComponent();
        _panels = new()
        {
            [IslandTab.Usage] = UsageView,
            [IslandTab.Music] = MusicView,
            [IslandTab.Git] = GitView,
            [IslandTab.GitHub] = GitHubView,
            [IslandTab.Focus] = FocusView,
            [IslandTab.Calendar] = CalendarView,
            [IslandTab.Tasks] = TasksView,
            [IslandTab.System] = SystemView,
            [IslandTab.Awake] = AwakeView,
        };
        TabIndicator.RenderTransform = _indicator;
        Tabs.SizeChanged += (_, _) => MoveIndicator(animate: false);
    }

    public event Action? SettingsRequested;

    /// <summary>The panel on screen changed (null when the island is not expanded).</summary>
    public event Action<IslandTab?>? VisiblePanelChanged;

    public IslandViewModel ViewModel
    {
        get => _viewModel!;
        set
        {
            _viewModel = value;
            PrivacyMarks.Source = value.Privacy;
            UsageView.ViewModel = value.Usage;
            MusicView.ViewModel = value.Music;
            FocusView.ViewModel = value.Focus;
            GitView.ViewModel = value.Git;
            GitHubView.ViewModel = value.GitHub;
            CalendarView.ViewModel = value.Calendar;
            TasksView.ViewModel = value.Tasks;
            SystemView.ViewModel = value.System;
            AwakeView.ViewModel = value.Awake;
            value.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(IslandViewModel.Favorites))
                {
                    UpdateStar();
                }

                if (e.PropertyName == nameof(IslandViewModel.SelectedTab))
                {
                    ShowSelectedPanel(reveal: true);
                    MoveIndicator(animate: true);
                }
            };
            value.TabsChanged += () => Dispatcher.UIThread.Post(RebuildTabs);
            RebuildTabs();
        }
    }

    /// <summary>Moves keyboard focus into the island after it expanded.</summary>
    public void FocusFirst(bool keyboard)
    {
        var method = keyboard ? NavigationMethod.Tab : NavigationMethod.Unspecified;
        if (_viewModel is not null && _tabButtons.TryGetValue(_viewModel.SelectedTab, out var tab))
        {
            tab.Focus(method);
        }
        else
        {
            SettingsButton.Focus(method);
        }
    }

    /// <summary>Quick capture: open on Tasks with the cursor in the input.</summary>
    public void FocusTaskCapture() => TasksView.FocusCapture();

    /// <summary>Called when the island becomes visible so the indicator starts in place.</summary>
    public void PrepareForShow()
    {
        _indicatorPlaced = false;
        _isShown = true;
        ShowSelectedPanel(reveal: false);
        MoveIndicator(animate: false);
    }

    /// <summary>The island collapsed: no panel is on screen.</summary>
    public void OnHidden()
    {
        _isShown = false;
        VisiblePanelChanged?.Invoke(null);
    }

    private void RebuildTabs()
    {
        if (_viewModel is null)
        {
            return;
        }

        var tabs = _viewModel.AvailableTabs;
        if (tabs.SequenceEqual(_tabButtons.Keys) && Tabs.Children.Count == tabs.Count)
        {
            UpdateSelectedForeground();
            return;
        }

        Tabs.Children.Clear();
        _tabButtons.Clear();

        // Nine tabs at 32 DIP would leave the section name no room; they shrink a little instead.
        var size = tabs.Count >= CompactTabCount ? 28 : 32;
        TabIndicator.Width = size;
        TabIndicator.Height = size;
        TabIndicator.CornerRadius = new CornerRadius(size / 2.0);
        foreach (var tab in tabs)
        {
            var name = IslandViewModel.TabName(tab);
            var button = new Button
            {
                Theme = StatusColors.Resource<ControlTheme>("IslandIconButton"),
                Width = size,
                Height = size,
                CornerRadius = new CornerRadius(size / 2.0),
                Tag = tab,
                // Usage shows the mark of the provider it stands for (Claude, or OpenAI when Codex is active).
                Content = new ModuleIcon { Module = tab == IslandTab.Usage ? _viewModel.ModuleForTab(tab) : IslandViewModel.TabModule(tab) },
            };
            ToolTip.SetTip(button, name);
            button.Click += OnTabClick;
            button.KeyDown += OnTabKeyDown;
            Tabs.Children.Add(button);
            _tabButtons[tab] = button;
        }

        NoTabsText.IsVisible = tabs.Count == 0;
        ShowSelectedPanel(reveal: false);
        Dispatcher.UIThread.Post(() => MoveIndicator(animate: false), DispatcherPriority.Loaded);
    }

    private void OnTabClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: IslandTab tab } && _viewModel is not null)
        {
            _viewModel.SelectedTab = tab;
            _viewModel.NoteTabOpened(tab);
        }
    }

    private void OnTabKeyDown(object? sender, KeyEventArgs e)
    {
        if (_viewModel is null || e.Key is not (Key.Left or Key.Right))
        {
            return;
        }

        var tabs = _viewModel.AvailableTabs;
        var index = tabs.ToList().IndexOf(_viewModel.SelectedTab);
        if (index < 0 || tabs.Count < 2)
        {
            return;
        }

        index = (index + (e.Key == Key.Right ? 1 : tabs.Count - 1)) % tabs.Count;
        _viewModel.SelectedTab = tabs[index];
        _viewModel.NoteTabOpened(tabs[index]);
        if (_tabButtons.TryGetValue(tabs[index], out var button))
        {
            button.Focus(NavigationMethod.Tab);
        }

        e.Handled = true;
    }

    private void OnSettings(object? sender, RoutedEventArgs e) => SettingsRequested?.Invoke();

    /// <summary>The header star features the selected tab in the compact island.</summary>
    private void UpdateStar()
    {
        var tab = _viewModel?.SelectedTab;
        if (_viewModel is null || tab is not { } selected || !_viewModel.AvailableTabs.Contains(selected))
        {
            StarButton.IsVisible = false;
            return;
        }

        var favorite = _viewModel.IsTabFavorite(selected);
        var name = string.Join(" and ", _viewModel.FavoriteModulesOf(selected).Select(Core.Modules.ModuleCatalog.DisplayName));
        StarButton.IsVisible = true;
        StarGlyph.Glyph = favorite ? "" : "";
        StarButton.Foreground = StatusColors.Resource<IBrush>(favorite ? "TextPrimaryBrush" : "TextTertiaryBrush");
        ToolTip.SetTip(StarButton, favorite ? $"Remove {name} from the compact island" : $"Show {name} in the compact island");
    }

    private void OnStar(object? sender, RoutedEventArgs e)
    {
        if (_viewModel is not null && _viewModel.AvailableTabs.Contains(_viewModel.SelectedTab))
        {
            _viewModel.ToggleTabFavorite(_viewModel.SelectedTab);
        }
    }

    private void ShowSelectedPanel(bool reveal)
    {
        if (_viewModel is null)
        {
            return;
        }

        var available = _viewModel.AvailableTabs;
        var selected = available.Contains(_viewModel.SelectedTab) ? _viewModel.SelectedTab : (IslandTab?)null;
        foreach (var (tab, panel) in _panels)
        {
            panel.IsVisible = tab == selected;
        }

        SectionName.Text = selected is { } s ? IslandViewModel.TabName(s) : string.Empty;
        UpdateStar();
        UpdateSelectedForeground();
        if (selected is { } shown)
        {
            if (shown == IslandTab.Music)
            {
                MusicView.RefreshAudioOutputs();
            }

            if (reveal)
            {
                Motion.FadeIn(_panels[shown], delay: TimeSpan.FromMilliseconds(40), fromY: 4);
            }

            if (_isShown)
            {
                VisiblePanelChanged?.Invoke(shown);
            }
        }
    }

    /// <summary>Slides the selection pill to the selected tab on a snappy spring.</summary>
    private void MoveIndicator(bool animate)
    {
        if (_viewModel is null || !_tabButtons.TryGetValue(_viewModel.SelectedTab, out var button) || button.Bounds.Width <= 0)
        {
            TabIndicator.Opacity = 0;
            return;
        }

        var x = button.TranslatePoint(default, TabHost)?.X ?? 0;
        TabIndicator.Opacity = 1;
        _indicatorTarget = x;
        if (animate && _indicatorPlaced && Motion.IsEnabled)
        {
            if (!_indicatorAnimating)
            {
                _indicatorAnimating = true;
                _lastFrame = null;
                TopLevel.GetTopLevel(this)?.RequestAnimationFrame(OnIndicatorFrame);
            }
        }
        else
        {
            _indicatorAnimating = false;
            _indicatorX = x;
            _indicatorV = 0;
            _indicator.X = x;
        }

        _indicatorPlaced = true;
    }

    private void OnIndicatorFrame(TimeSpan time)
    {
        if (!_indicatorAnimating)
        {
            return;
        }

        var dt = _lastFrame is { } last ? Math.Clamp((time - last).TotalSeconds, 0, 0.05) : 1 / 60.0;
        _lastFrame = time;
        (_indicatorX, _indicatorV) = Motion.SnappySpring.Step(_indicatorX, _indicatorV, _indicatorTarget, dt);
        _indicator.X = _indicatorX;
        if (Math.Abs(_indicatorX - _indicatorTarget) < 0.2 && Math.Abs(_indicatorV) < 5)
        {
            _indicator.X = _indicatorX = _indicatorTarget;
            _indicatorV = 0;
            _indicatorAnimating = false;
            return;
        }

        TopLevel.GetTopLevel(this)?.RequestAnimationFrame(OnIndicatorFrame);
    }

    private void UpdateSelectedForeground()
    {
        if (_viewModel is null)
        {
            return;
        }

        var primary = StatusColors.Resource<IBrush>("TextPrimaryBrush");
        var secondary = StatusColors.Resource<IBrush>("TextSecondaryBrush");
        foreach (var (tab, button) in _tabButtons)
        {
            var selected = tab == _viewModel.SelectedTab;
            button.Foreground = selected ? primary : secondary;
            if (button.Content is ModuleIcon icon)
            {
                icon.Foreground = selected ? primary : secondary;
            }
        }
    }
}
