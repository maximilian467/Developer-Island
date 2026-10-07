using DeveloperIsland.Core.HomeCloud;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DeveloperIsland.UI.Island;

/// <summary>The island as a Home Cloud drop zone: the cloud zone on the configured side, text beside it.</summary>
public sealed partial class DropView : UserControl
{
    public DropView()
    {
        InitializeComponent();
    }

    public void SetSide(HomeCloudDropSide side)
    {
        var left = side == HomeCloudDropSide.Left;
        Grid.SetColumn(Zone, left ? 0 : 2);
        Texts.Margin = left ? new Thickness(14, 0, 12, 0) : new Thickness(12, 0, 14, 0);
    }
}
