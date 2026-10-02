using System.Numerics;
using DeveloperIsland.UI.Animations;
using DeveloperIsland.UI.Components;
using DeveloperIsland.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace DeveloperIsland.UI.Expanded;

/// <summary>
/// The expanded island. Tabs are icons (nine modules do not fit as words in 420 DIP without becoming
/// a cramped dashboard); the selected tab's name sits beside them, and each icon has a tooltip and an
/// accessible name. Panels are separate controls; this view only switches between them.
/// </summary>
public sealed partial class ExpandedView : UserControl
{
    private readonly Dictionary<IslandTab, Button> _tabButtons = [];
    private readonly Dictionary<IslandTab, FrameworkElement> _panels;
    private IslandViewModel _viewModel = null!;
    private bool _indicatorPlaced;
    private bool _isShown;

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
        };
        ElementCompositionPreview.SetIsTranslationEnabled(TabIndicator, true);
        Tabs.SizeChanged += (_, _) => MoveIndicator(animate: false);
    }

    public event Action? SettingsRequested;

    /// <summary>The panel on screen changed (null when the island is not expanded).</summary>
    public event Action<IslandTab?>? VisiblePanelChanged;

    public IslandViewModel ViewModel
    {
        get => _viewModel;
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
            value.TabsChanged += () => DispatcherQueue.TryEnqueue(RebuildTabs);
            RebuildTabs();
        }
    }

    /// <summary>Moves keyboard focus into the island after it expanded.</summary>
    public void FocusFirst(FocusState state)
    {
        if (_tabButtons.TryGetValue(_viewModel.SelectedTab, out var tab))
        {
            tab.Focus(state);
        }
        else
        {
            SettingsButton.Focus(state);
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
        foreach (var tab in tabs)
        {
            var name = IslandViewModel.TabName(tab);
            var button = new Button
            {
                Style = (Style)Application.Current.Resources["IslandIconButtonStyle"],
                Width = 32,
                Height = 32,
                CornerRadius = new CornerRadius(16),
                Tag = tab,
                // Usage shows the mark of the provider it stands for (Claude, or OpenAI when Codex is active).
                Content = new ModuleIcon { Module = tab == IslandTab.Usage ? _viewModel.ModuleForTab(tab) : IslandViewModel.TabModule(tab) },
            };
            AutomationProperties.SetName(button, name);
            ToolTipService.SetToolTip(button, name);
            button.Click += OnTabClick;
            button.KeyDown += OnTabKeyDown;
            Tabs.Children.Add(button);
            _tabButtons[tab] = button;
        }

        NoTabsText.Visibility = tabs.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ShowSelectedPanel(reveal: false);
        DispatcherQueue.TryEnqueue(() => MoveIndicator(animate: false));
    }

    private void OnTabClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: IslandTab tab })
        {
            _viewModel.SelectedTab = tab;
            _viewModel.NoteTabOpened(tab);
        }
    }

    private void OnTabKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key is not (VirtualKey.Left or VirtualKey.Right))
        {
            return;
        }

        var tabs = _viewModel.AvailableTabs;
        var index = tabs.ToList().IndexOf(_viewModel.SelectedTab);
        if (index < 0 || tabs.Count < 2)
        {
            return;
        }

        index = (index + (e.Key == VirtualKey.Right ? 1 : tabs.Count - 1)) % tabs.Count;
        _viewModel.SelectedTab = tabs[index];
        _viewModel.NoteTabOpened(tabs[index]);
        if (_tabButtons.TryGetValue(tabs[index], out var button))
        {
            button.Focus(FocusState.Keyboard);
        }

        e.Handled = true;
    }

    private void OnSettings(object sender, RoutedEventArgs e) => SettingsRequested?.Invoke();

    /// <summary>
    /// The header star features the selected tab in the compact island. Every tab has the same star
    /// in the same place; on Usage it stands for the enabled providers (see <see cref="Core.Modules.TabFavorite"/>).
    /// </summary>
    private void UpdateStar()
    {
        var tab = _viewModel?.SelectedTab;
        if (_viewModel is null || tab is not { } selected || !_viewModel.AvailableTabs.Contains(selected))
        {
            StarButton.Visibility = Visibility.Collapsed;
            return;
        }

        var favorite = _viewModel.IsTabFavorite(selected);
        var name = string.Join(" and ", _viewModel.FavoriteModulesOf(selected).Select(Core.Modules.ModuleCatalog.DisplayName));
        StarButton.Visibility = Visibility.Visible;
        StarButton.Content = favorite ? "\uE735" : "\uE734";
        StarButton.Foreground = (Brush)Application.Current.Resources[favorite ? "TextPrimaryBrush" : "TextTertiaryBrush"];
        var label = favorite ? $"Remove {name} from the compact island" : $"Show {name} in the compact island";
        AutomationProperties.SetName(StarButton, label);
        ToolTipService.SetToolTip(StarButton, label);
    }

    private void OnStar(object sender, RoutedEventArgs e)
    {
        if (_viewModel.AvailableTabs.Contains(_viewModel.SelectedTab))
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
            panel.Visibility = tab == selected ? Visibility.Visible : Visibility.Collapsed;
        }

        SectionName.Text = selected is { } s ? IslandViewModel.TabName(s) : string.Empty;
        UpdateStar();
        UpdateSelectedForeground();
        if (selected is { } shown)
        {
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

    private void MoveIndicator(bool animate)
    {
        if (_viewModel is null || !_tabButtons.TryGetValue(_viewModel.SelectedTab, out var button) || button.ActualWidth <= 0)
        {
            TabIndicator.Opacity = 0;
            return;
        }

        var x = (float)button.TransformToVisual(TabHost).TransformPoint(default).X;
        TabIndicator.Opacity = 1;

        var visual = ElementCompositionPreview.GetElementVisual(TabIndicator);
        var target = new Vector3(x, 0, 0);
        if (animate && _indicatorPlaced && Motion.IsEnabled)
        {
            Motion.SpringTranslation(visual, target, Motion.SnappySpring);
        }
        else
        {
            visual.StopAnimation("Translation");
            visual.Properties.InsertVector3("Translation", target);
        }

        _indicatorPlaced = true;
    }

    private void UpdateSelectedForeground()
    {
        var primary = (Brush)Application.Current.Resources["TextPrimaryBrush"];
        var secondary = (Brush)Application.Current.Resources["TextSecondaryBrush"];
        foreach (var (tab, button) in _tabButtons)
        {
            var selected = tab == _viewModel.SelectedTab;
            button.Foreground = selected ? primary : secondary;
            if (button.Content is ModuleIcon icon)
            {
                icon.Foreground = selected ? primary : secondary;
            }

            AutomationProperties.SetItemStatus(button, selected ? "Selected" : string.Empty);
        }
    }
}
