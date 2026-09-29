using DeveloperIsland.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DeveloperIsland.UI.Expanded;

public sealed partial class UsagePanel : UserControl
{
    private UsageViewModel _viewModel = null!;

    public UsagePanel()
    {
        InitializeComponent();
    }

    public UsageViewModel ViewModel
    {
        get => _viewModel;
        set
        {
            _viewModel = value;
            Heatmap.ViewModel = value;
            value.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName is nameof(UsageViewModel.ShowBoth) or "" or null)
                {
                    ApplyColumns();
                }
            };
            ApplyColumns();
        }
    }

    /// <summary>With one provider turned off, the other takes the full width.</summary>
    private void ApplyColumns()
    {
        if (_viewModel is null)
        {
            return;
        }

        var single = !_viewModel.ShowBoth;
        ProvidersGrid.ColumnDefinitions[1].Width = single ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        ProvidersGrid.ColumnSpacing = single ? 0 : 24;
        Grid.SetColumn(CodexSummary, single && !_viewModel.ShowClaude ? 0 : 1);
    }
}
