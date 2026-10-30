using Avalonia.Controls;
using Avalonia.Interactivity;
using DeveloperIsland.ViewModels;

namespace DeveloperIsland.UI.Expanded;

public sealed partial class ModuleStateView : UserControl
{
    public ModuleStateView()
    {
        InitializeComponent();
    }

    private void OnAction(object? sender, RoutedEventArgs e) => (DataContext as ModuleViewModel)?.RequestAction();
}
