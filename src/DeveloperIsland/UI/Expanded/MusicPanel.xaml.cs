using DeveloperIsland.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DeveloperIsland.UI.Expanded;

public sealed partial class MusicPanel : UserControl
{
    private MusicViewModel _viewModel = null!;

    public MusicPanel()
    {
        InitializeComponent();
        Timeline.SeekRequested += fraction => _ = _viewModel.SeekToFractionAsync(fraction);
    }

    public MusicViewModel ViewModel
    {
        get => _viewModel;
        set
        {
            _viewModel = value;
            value.PropertyChanged += (_, _) => Timeline.Duration = value.Snapshot.Duration;
        }
    }

    public void FocusPrimary() => PlayPauseButton.Focus(FocusState.Keyboard);

    private void OnPrevious(object sender, RoutedEventArgs e) => _ = _viewModel.PreviousAsync();

    private void OnNext(object sender, RoutedEventArgs e) => _ = _viewModel.NextAsync();

    private void OnPlayPause(object sender, RoutedEventArgs e) => _ = _viewModel.TogglePlayPauseAsync();
}
