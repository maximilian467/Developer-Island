using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using DeveloperIsland.UI.Components;
using DeveloperIsland.ViewModels;

namespace DeveloperIsland.UI.Expanded;

public sealed partial class ProviderSummary : UserControl
{
    private AiProviderViewModel? _viewModel;

    public ProviderSummary()
    {
        InitializeComponent();
    }

    public AiProviderViewModel ViewModel
    {
        get => _viewModel!;
        set
        {
            _viewModel = value;
            DataContext = value;
            value.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName is nameof(AiProviderViewModel.ShowWeekly) or "" or null)
                {
                    ApplySegments();
                }
            };
            ApplySegments();
        }
    }

    /// <summary>The selected window gets the raised pill and primary text.</summary>
    private void ApplySegments()
    {
        if (_viewModel is null)
        {
            return;
        }

        var weekly = _viewModel.ShowWeekly;
        CurrentButton.Background = StatusColors.Resource<IBrush>(weekly ? "TransparentBrush" : "SurfaceHoverBrush");
        WeeklyButton.Background = StatusColors.Resource<IBrush>(weekly ? "SurfaceHoverBrush" : "TransparentBrush");
        CurrentButton.Foreground = StatusColors.Resource<IBrush>(weekly ? "TextSecondaryBrush" : "TextPrimaryBrush");
        WeeklyButton.Foreground = StatusColors.Resource<IBrush>(weekly ? "TextPrimaryBrush" : "TextSecondaryBrush");
    }

    private void OnShowCurrent(object? sender, RoutedEventArgs e) => _viewModel!.ShowWeekly = false;

    private void OnShowWeekly(object? sender, RoutedEventArgs e) => _viewModel!.ShowWeekly = true;

    private void OnConnectPlan(object? sender, RoutedEventArgs e) => _viewModel!.RequestConnectPlan();
}
