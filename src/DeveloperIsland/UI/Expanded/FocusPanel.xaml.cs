using System.Globalization;
using DeveloperIsland.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DeveloperIsland.UI.Expanded;

public sealed partial class FocusPanel : UserControl
{
    public FocusPanel()
    {
        InitializeComponent();
    }

    public FocusViewModel ViewModel { get; set; } = null!;

    public void FocusPrimary()
    {
        if (ViewModel.IsActive)
        {
            PauseButton.Focus(FocusState.Keyboard);
        }
        else
        {
            Preset25.Focus(FocusState.Keyboard);
        }
    }

    private void OnPreset(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string tag } && int.TryParse(tag, NumberStyles.Integer, CultureInfo.InvariantCulture, out var minutes))
        {
            ViewModel.Start(minutes);
        }
    }

    private void OnOpenCustom(object sender, RoutedEventArgs e)
    {
        ViewModel.OpenCustom();
        MinusButton.Focus(FocusState.Programmatic);
    }

    private void OnCloseCustom(object sender, RoutedEventArgs e)
    {
        ViewModel.CloseCustom();
        Preset25.Focus(FocusState.Programmatic);
    }

    private void OnAdjust(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string tag } && int.TryParse(tag, NumberStyles.Integer, CultureInfo.InvariantCulture, out var delta))
        {
            ViewModel.AdjustCustom(delta);
        }
    }

    private void OnStartCustom(object sender, RoutedEventArgs e) => ViewModel.StartCustom();

    private void OnTogglePause(object sender, RoutedEventArgs e) => ViewModel.TogglePause();

    private void OnStop(object sender, RoutedEventArgs e) => ViewModel.Stop();
}
