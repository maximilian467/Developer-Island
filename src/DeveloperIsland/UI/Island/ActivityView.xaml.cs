using DeveloperIsland.ViewModels;
using Microsoft.UI.Xaml.Controls;

namespace DeveloperIsland.UI.Island;

public sealed partial class ActivityView : UserControl
{
    public ActivityView()
    {
        InitializeComponent();
    }

    public ActivityViewModel ViewModel { get; set; } = null!;
}
