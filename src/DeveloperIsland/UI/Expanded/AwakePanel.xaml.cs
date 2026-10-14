using System.Globalization;
using DeveloperIsland.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DeveloperIsland.UI.Expanded;

public sealed partial class AwakePanel : UserControl
{
    public AwakePanel()
    {
        InitializeComponent();
    }

    public AwakeViewModel ViewModel { get; set; } = null!;

    private void OnPreset(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string tag } && int.TryParse(tag, NumberStyles.Integer, CultureInfo.InvariantCulture, out var minutes))
        {
            ViewModel.Start(minutes);
        }
    }

    private void OnIndefinite(object sender, RoutedEventArgs e) => ViewModel.StartIndefinite();

    private void OnOpenCustom(object sender, RoutedEventArgs e)
    {
        ViewModel.OpenCustom();
        MinusButton.Focus(FocusState.Programmatic);
    }

    private void OnCloseCustom(object sender, RoutedEventArgs e)
    {
        ViewModel.CloseCustom();
        Preset1.Focus(FocusState.Programmatic);
    }

    private void OnAdjust(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string tag } && int.TryParse(tag, NumberStyles.Integer, CultureInfo.InvariantCulture, out var delta))
        {
            ViewModel.AdjustCustom(delta);
        }
    }

    private void OnStartCustom(object sender, RoutedEventArgs e) => ViewModel.StartCustom();

    private void OnExtend(object sender, RoutedEventArgs e) => ViewModel.Extend(60);

    private void OnStop(object sender, RoutedEventArgs e) => ViewModel.Stop();
}
