using System.ComponentModel;
using System.Runtime.CompilerServices;
using DeveloperIsland.Core.SystemInfo;
using DeveloperIsland.UI.Components;
using DeveloperIsland.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;

namespace DeveloperIsland.UI.Expanded;

public sealed partial class SystemPanel : UserControl, INotifyPropertyChanged
{
    private SystemViewModel _viewModel = null!;
    private string _layoutKey = string.Empty;

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
            value.PropertyChanged += (_, _) => Refresh();
            Refresh();
        }
    }

    /// <summary>The meter turns red only when the battery is low and draining.</summary>
    public Brush BatteryBrush => (Brush)Application.Current.Resources[_viewModel?.BatteryLow == true ? "CriticalBrush" : "AccentBrush"];

    /// <summary>The CPU caption (its temperature when known, else "all cores"; "busy for a while" under sustained load).</summary>
    public string CpuCaption => _viewModel?.CpuCaption ?? string.Empty;

    private void Refresh()
    {
        Layout();
        CpuSpark.SetValues(_viewModel.CpuHistory);
        MemorySpark.SetValues(_viewModel.MemoryHistory);
        if (_viewModel.HasGpu)
        {
            GpuSpark.SetValues(_viewModel.GpuHistory);
        }

        // Load colors the line and the bar; a temperature colors only its own text.
        Paint(CpuSpark, CpuBar, _viewModel.CpuLevel);
        Paint(MemorySpark, MemoryBar, _viewModel.MemoryLevel);
        Paint(GpuSpark, GpuBar, _viewModel.GpuLevel);
        CpuCaptionText.Foreground = StatusColors.Text(_viewModel.CpuTemperatureLevel, "TextTertiaryBrush");
        GpuCaptionText.Foreground = StatusColors.Text(_viewModel.GpuTemperatureLevel, "TextTertiaryBrush");
        Raise(nameof(BatteryBrush));
        Raise(nameof(CpuCaption));
    }

    /// <summary>
    /// Only the tiles this device has, re-flowed without gaps: up to three in a row, four as two
    /// by two. Rebuilt only when the set of tiles changes.
    /// </summary>
    private void Layout()
    {
        var tiles = new List<FrameworkElement> { CpuTile, MemoryTile };
        if (_viewModel.HasGpu)
        {
            tiles.Add(GpuTile);
        }

        if (_viewModel.HasBattery)
        {
            tiles.Add(BatteryTile);
        }

        var key = string.Join(",", tiles.Select(t => t.Name));
        if (key == _layoutKey)
        {
            return;
        }

        _layoutKey = key;
        var columns = tiles.Count == 4 ? 2 : tiles.Count;
        Tiles.ColumnDefinitions.Clear();
        Tiles.RowDefinitions.Clear();
        for (var c = 0; c < columns; c++)
        {
            Tiles.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        }

        for (var r = 0; r < (tiles.Count + columns - 1) / columns; r++)
        {
            Tiles.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        }

        foreach (var tile in new FrameworkElement[] { CpuTile, MemoryTile, GpuTile, BatteryTile })
        {
            var index = tiles.IndexOf(tile);
            tile.Visibility = index >= 0 ? Visibility.Visible : Visibility.Collapsed;
            if (index >= 0)
            {
                Grid.SetColumn(tile, index % columns);
                Grid.SetRow(tile, index / columns);
            }
        }
    }

    private static void Paint(Sparkline spark, Rectangle bar, SystemLevel level)
    {
        spark.SetColor(StatusColors.Color(level));
        if (bar.Fill is not SolidColorBrush brush || brush.Color != StatusColors.Color(level))
        {
            bar.Fill = StatusColors.Brush(level);
        }
    }

    private void Raise([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
