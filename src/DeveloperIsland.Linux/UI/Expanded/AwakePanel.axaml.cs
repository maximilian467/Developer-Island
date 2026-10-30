using System.Globalization;
using Avalonia.Controls;
using Avalonia.Interactivity;
using DeveloperIsland.ViewModels;

namespace DeveloperIsland.UI.Expanded;

public sealed partial class AwakePanel : UserControl
{
    private AwakeViewModel? _viewModel;

    public AwakePanel()
    {
        InitializeComponent();
    }

    public AwakeViewModel ViewModel
    {
        get => _viewModel!;
        set
        {
            _viewModel = value;
            DataContext = value;
        }
    }

    private void OnPreset(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string tag } && int.TryParse(tag, NumberStyles.Integer, CultureInfo.InvariantCulture, out var minutes))
        {
            ViewModel.Start(minutes);
        }
    }

    private void OnIndefinite(object? sender, RoutedEventArgs e) => ViewModel.StartIndefinite();

    private void OnOpenCustom(object? sender, RoutedEventArgs e)
    {
        ViewModel.OpenCustom();
        MinusButton.Focus();
    }

    private void OnCloseCustom(object? sender, RoutedEventArgs e)
    {
        ViewModel.CloseCustom();
        Preset1.Focus();
    }

    private void OnAdjust(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string tag } && int.TryParse(tag, NumberStyles.Integer, CultureInfo.InvariantCulture, out var delta))
        {
            ViewModel.AdjustCustom(delta);
        }
    }

    private void OnStartCustom(object? sender, RoutedEventArgs e) => ViewModel.StartCustom();

    private void OnExtend(object? sender, RoutedEventArgs e) => ViewModel.Extend(60);

    private void OnStop(object? sender, RoutedEventArgs e) => ViewModel.Stop();
}
