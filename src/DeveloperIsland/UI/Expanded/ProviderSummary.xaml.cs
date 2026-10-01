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
            };
            ApplyStar();
        }
    }

    /// <summary>A filled star in the primary color; an outline in the tertiary color.</summary>
    private void ApplyStar() =>
        StarButton.Foreground = (Brush)Application.Current.Resources[_viewModel.IsFavorite ? "TextPrimaryBrush" : "TextTertiaryBrush"];

    private void OnStar(object sender, RoutedEventArgs e) => ViewModel.RequestFavoriteToggle();
}
