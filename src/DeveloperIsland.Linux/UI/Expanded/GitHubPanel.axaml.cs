using Avalonia.Controls;
using DeveloperIsland.ViewModels;

namespace DeveloperIsland.UI.Expanded;

public sealed partial class GitHubPanel : UserControl
{
    private GitHubViewModel? _viewModel;

    public GitHubPanel()
    {
        InitializeComponent();
    }

    public GitHubViewModel ViewModel
    {
        get => _viewModel!;
        set
        {
            _viewModel = value;
            DataContext = value;
        }
    }
}
