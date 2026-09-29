using System.Numerics;
using DeveloperIsland.UI.Animations;
using DeveloperIsland.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace DeveloperIsland.UI.Expanded;

public sealed partial class ExpandedView : UserControl
{
    private IslandViewModel _viewModel = null!;
    private bool _indicatorPlaced;

    public ExpandedView()
    {
        InitializeComponent();
        ElementCompositionPreview.SetIsTranslationEnabled(TabIndicator, true);
        Tabs.SizeChanged += (_, _) => MoveIndicator(animate: false);
    }

    public event Action? SettingsRequested;

    public IslandViewModel ViewModel
    {
        get => _viewModel;
        set
        {
            _viewModel = value;
            UsageView.ViewModel = value.Usage;
            MusicView.ViewModel = value.Music;
            FocusView.ViewModel = value.Focus;
            value.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(IslandViewModel.SelectedTab))
                {
                    MoveIndicator(animate: true);
                    RevealSelectedPanel();
                }
            };
            value.TabsChanged += () => DispatcherQueue.TryEnqueue(() => MoveIndicator(animate: false));
        }
    }

    /// <summary>Moves keyboard focus into the island after it expanded.</summary>
    public void FocusFirst(FocusState state)
    {
        var tab = SelectedTabButton();
        if (tab is not null && tab.Visibility == Visibility.Visible)
        {
            tab.Focus(state);
        }
        else
        {
            SettingsButton.Focus(state);
        }
    }

    /// <summary>Called when the island becomes visible so the indicator starts in place.</summary>
    public void PrepareForShow()
    {
        _indicatorPlaced = false;
        MoveIndicator(animate: false);
    }

    private Button? SelectedTabButton() => _viewModel.SelectedTab switch
    {
        IslandTab.Usage => UsageTab,
        IslandTab.Music => MusicTab,
        _ => FocusTab,
    };

    private void OnTabClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string tag } && Enum.TryParse<IslandTab>(tag, out var tab))
        {
            _viewModel.SelectedTab = tab;
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
        SelectedTabButton()?.Focus(FocusState.Keyboard);
        e.Handled = true;
    }

    private void OnSettings(object sender, RoutedEventArgs e) => SettingsRequested?.Invoke();

    private void MoveIndicator(bool animate)
    {
        if (_viewModel is null)
        {
            return;
        }

        var button = SelectedTabButton();
        if (button is null || button.Visibility != Visibility.Visible || button.ActualWidth <= 0)
        {
            TabIndicator.Opacity = 0;
            return;
        }

        var x = (float)button.TransformToVisual(TabHost).TransformPoint(default).X;
        TabIndicator.Width = button.ActualWidth;
        TabIndicator.Opacity = 1;
        UpdateSelectedForeground();

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
        var primary = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextPrimaryBrush"];
        var secondary = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextSecondaryBrush"];
        UsageTab.Foreground = _viewModel.IsUsageSelected ? primary : secondary;
        MusicTab.Foreground = _viewModel.IsMusicSelected ? primary : secondary;
        FocusTab.Foreground = _viewModel.IsFocusSelected ? primary : secondary;
        AutomationSelection(UsageTab, _viewModel.IsUsageSelected);
        AutomationSelection(MusicTab, _viewModel.IsMusicSelected);
        AutomationSelection(FocusTab, _viewModel.IsFocusSelected);
    }

    private static void AutomationSelection(Button tab, bool selected) =>
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetItemStatus(tab, selected ? "Selected" : string.Empty);

    private void RevealSelectedPanel()
    {
        UIElement panel = _viewModel.SelectedTab switch
        {
            IslandTab.Usage => UsageView,
            IslandTab.Music => MusicView,
            _ => FocusView,
        };
        Motion.FadeIn(panel, delay: TimeSpan.FromMilliseconds(40), fromY: 4);
    }
}
