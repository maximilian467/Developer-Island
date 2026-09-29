using DeveloperIsland.ViewModels;
using Microsoft.UI.Xaml.Controls;

namespace DeveloperIsland.UI.Island;

public sealed partial class CompactView : UserControl
{
    public CompactView()
    {
        InitializeComponent();
    }

    public IslandViewModel ViewModel { get; set; } = null!;
}
