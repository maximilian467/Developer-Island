using DeveloperIsland.ViewModels;
using Microsoft.UI.Xaml.Controls;

namespace DeveloperIsland.UI.Expanded;

public sealed partial class ProviderSummary : UserControl
{
    public ProviderSummary()
    {
        InitializeComponent();
    }

    public AiProviderViewModel ViewModel { get; set; } = null!;
}
