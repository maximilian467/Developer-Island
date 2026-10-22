using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using DeveloperIsland.ViewModels;

namespace DeveloperIsland.UI.Expanded;

public sealed partial class MusicPanel : UserControl
{
    private MusicViewModel? _viewModel;

    public MusicPanel()
    {
        InitializeComponent();
        Timeline.SeekRequested += fraction => _ = _viewModel?.SeekToFractionAsync(fraction);
    }

    public MusicViewModel ViewModel
    {
        get => _viewModel!;
        set
        {
            _viewModel = value;
            DataContext = value;
            value.PropertyChanged += (_, _) => Timeline.Duration = value.Snapshot.Duration;
        }
    }

    public void RefreshAudioOutputs() => _ = _viewModel?.AudioOutput.RefreshAsync();

    public void FocusPrimary() => PlayPauseButton.Focus(NavigationMethod.Tab);

    private void OnPrevious(object? sender, RoutedEventArgs e) => _ = _viewModel!.PreviousAsync();

    private void OnNext(object? sender, RoutedEventArgs e) => _ = _viewModel!.NextAsync();

    private void OnPlayPause(object? sender, RoutedEventArgs e) => _ = _viewModel!.TogglePlayPauseAsync();

    private void OnRefreshAudioOutputs(object? sender, RoutedEventArgs e) => RefreshAudioOutputs();

    private void OnSwitchAudioOutput(object? sender, RoutedEventArgs e) => _ = _viewModel!.AudioOutput.SwitchAsync();
}
