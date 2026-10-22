using Avalonia.Controls;
using DeveloperIsland.ViewModels;

namespace DeveloperIsland.UI.Island;

public sealed partial class ActivityView : UserControl
{
    public ActivityView()
    {
        InitializeComponent();
    }

    public ActivityViewModel ViewModel
    {
        get => (ActivityViewModel)DataContext!;
        set => DataContext = value;
    }
}
