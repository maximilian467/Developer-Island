using Avalonia.Controls;
using DeveloperIsland.ViewModels;

namespace DeveloperIsland.UI.Expanded;

public sealed partial class CalendarPanel : UserControl
{
    private CalendarViewModel? _viewModel;

    public CalendarPanel()
    {
        InitializeComponent();
    }

    public CalendarViewModel ViewModel
    {
        get => _viewModel!;
        set
        {
            _viewModel = value;
            DataContext = value;
        }
    }
}
