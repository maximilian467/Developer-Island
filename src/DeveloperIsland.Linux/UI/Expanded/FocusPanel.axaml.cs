using System.Globalization;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using DeveloperIsland.ViewModels;

namespace DeveloperIsland.UI.Expanded;

public sealed partial class FocusPanel : UserControl
{
    private FocusViewModel? _viewModel;

    public FocusPanel()
    {
        InitializeComponent();
    }

    public FocusViewModel ViewModel
    {
        get => _viewModel!;
        set
        {
            _viewModel = value;
            DataContext = value;
        }
    }

    public void FocusPrimary()
    {
        if (ViewModel.IsActive)
        {
            PauseButton.Focus(NavigationMethod.Tab);
        }
        else
        {
            Preset25.Focus(NavigationMethod.Tab);
        }
    }

    private void OnPreset(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string tag } && int.TryParse(tag, NumberStyles.Integer, CultureInfo.InvariantCulture, out var minutes))
        {
            ViewModel.Start(minutes);
        }
    }

    private void OnOpenCustom(object? sender, RoutedEventArgs e)
    {
        ViewModel.OpenCustom();
        MinusButton.Focus();
    }

    private void OnCloseCustom(object? sender, RoutedEventArgs e)
    {
        ViewModel.CloseCustom();
        Preset25.Focus();
    }

    private void OnAdjust(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string tag } && int.TryParse(tag, NumberStyles.Integer, CultureInfo.InvariantCulture, out var delta))
        {
            ViewModel.AdjustCustom(delta);
        }
    }

    private void OnStartCustom(object? sender, RoutedEventArgs e) => ViewModel.StartCustom();

    private void OnTogglePause(object? sender, RoutedEventArgs e) => ViewModel.TogglePause();

    private void OnStop(object? sender, RoutedEventArgs e) => ViewModel.Stop();
}
