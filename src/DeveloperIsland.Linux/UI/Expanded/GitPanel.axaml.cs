using Avalonia.Controls;
using Avalonia.Interactivity;
using DeveloperIsland.ViewModels;

namespace DeveloperIsland.UI.Expanded;

public sealed partial class GitPanel : UserControl
{
    private GitViewModel? _viewModel;

    public GitPanel()
    {
        InitializeComponent();
    }

    public GitViewModel ViewModel
    {
        get => _viewModel!;
        set
        {
            _viewModel = value;
            DataContext = value;
        }
    }

    private void OnSelectRepo(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string root })
        {
            ViewModel.Select(root);
        }
    }
}
