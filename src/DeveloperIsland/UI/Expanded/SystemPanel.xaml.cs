using System.ComponentModel;
using System.Runtime.CompilerServices;
using DeveloperIsland.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace DeveloperIsland.UI.Expanded;

public sealed partial class SystemPanel : UserControl, INotifyPropertyChanged
{
    private SystemViewModel _viewModel = null!;

    public SystemPanel()
    {
        InitializeComponent();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public SystemViewModel ViewModel
    {
        get => _viewModel;
        set
        {
            _viewModel = value;
            StateView.ViewModel = value;
            value.PropertyChanged += (_, _) =>
            {
                BatteryColumn.Width = value.HasBattery ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
                // An empty third column still gets its 8 px gap; let it hang outside the panel.
                Tiles.Margin = new Thickness(0, 0, value.HasBattery ? 0 : -8, 0);
                Raise(nameof(BatteryBrush));
                Raise(nameof(CpuCaption));
            };
        }
    }

    /// <summary>The meter turns red only when the battery is low and draining.</summary>
    public Brush BatteryBrush => (Brush)Application.Current.Resources[_viewModel?.BatteryLow == true ? "CriticalBrush" : "AccentBrush"];

    public string CpuCaption => _viewModel?.CpuHigh == true ? "busy for a while" : "all cores";

    private void Raise([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
