using DeveloperIsland.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace DeveloperIsland.UI.Expanded;

public sealed partial class ProviderSummary : UserControl
{
    private AiProviderViewModel _viewModel = null!;

    public ProviderSummary()
    {
        InitializeComponent();
    }

    public AiProviderViewModel ViewModel
    {
        get => _viewModel;
        set
        {
            _viewModel = value;
            value.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName is nameof(AiProviderViewModel.IsFavorite) or "" or null)
                {
                    ApplyStar();
                }

                if (e.PropertyName is nameof(AiProviderViewModel.ShowWeekly) or "" or null)
                {
                    ApplySegments();
                }
            };
            ApplyStar();
            ApplySegments();
        }
    }

    /// <summary>The selected window gets the raised pill and primary text.</summary>
    private void ApplySegments()
    {
        var resources = Application.Current.Resources;
        var weekly = _viewModel.ShowWeekly;
        CurrentButton.Background = (Brush)resources[weekly ? "TransparentBrush" : "SurfaceHoverBrush"];
        WeeklyButton.Background = (Brush)resources[weekly ? "SurfaceHoverBrush" : "TransparentBrush"];
        CurrentButton.Foreground = (Brush)resources[weekly ? "TextSecondaryBrush" : "TextPrimaryBrush"];
        WeeklyButton.Foreground = (Brush)resources[weekly ? "TextPrimaryBrush" : "TextSecondaryBrush"];
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetItemStatus(CurrentButton, weekly ? string.Empty : "Selected");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetItemStatus(WeeklyButton, weekly ? "Selected" : string.Empty);
    }

    private void OnShowCurrent(object sender, RoutedEventArgs e) => _viewModel.ShowWeekly = false;

    private void OnShowWeekly(object sender, RoutedEventArgs e) => _viewModel.ShowWeekly = true;

    private void OnConnectPlan(object sender, RoutedEventArgs e) => _viewModel.RequestConnectPlan();

    /// <summary>A filled star in the primary color; an outline in the tertiary color.</summary>
    private void ApplyStar() =>
        StarButton.Foreground = (Brush)Application.Current.Resources[_viewModel.IsFavorite ? "TextPrimaryBrush" : "TextTertiaryBrush"];

    private void OnStar(object sender, RoutedEventArgs e) => ViewModel.RequestFavoriteToggle();
}
