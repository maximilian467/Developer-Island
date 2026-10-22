using Avalonia.Controls;
using DeveloperIsland.Core.SystemInfo;
using DeveloperIsland.UI.Components;
using DeveloperIsland.ViewModels;

namespace DeveloperIsland.UI.Expanded;

public sealed partial class SystemPanel : UserControl
{
    private SystemViewModel? _viewModel;
    private string _layoutKey = string.Empty;

    public SystemPanel()
    {
        InitializeComponent();
    }

    public SystemViewModel ViewModel
    {
        get => _viewModel!;
        set
        {
            _viewModel = value;
            DataContext = value;
            value.PropertyChanged += (_, _) => Refresh();
            Refresh();
        }
    }

    private void Refresh()
    {
        var vm = _viewModel!;
        Layout();
        CpuSpark.SetValues(vm.CpuHistory);
        MemorySpark.SetValues(vm.MemoryHistory);
        if (vm.HasGpu)
        {
            GpuSpark.SetValues(vm.GpuHistory);
        }

        // Load colors the line and the bar; a temperature colors only its own text.
        Paint(CpuSpark, CpuBar, vm.CpuLevel);
        Paint(MemorySpark, MemoryBar, vm.MemoryLevel);
        Paint(GpuSpark, GpuBar, vm.GpuLevel);
        CpuCaptionText.Foreground = StatusColors.Text(vm.CpuTemperatureLevel, "TextTertiaryBrush");
        GpuCaptionText.Foreground = StatusColors.Text(vm.GpuTemperatureLevel, "TextTertiaryBrush");

        // The battery meter turns red only when the battery is low and draining.
        BatteryBar.Fill = StatusColors.Resource<Avalonia.Media.IBrush>(vm.BatteryLow ? "CriticalBrush" : "AccentBrush");
    }

    /// <summary>Only the tiles this device has, re-flowed without gaps: up to three in a row, four as two by two.</summary>
    private void Layout()
    {
        var vm = _viewModel!;
        var tiles = new List<Control> { CpuTile, MemoryTile };
        if (vm.HasGpu)
        {
            tiles.Add(GpuTile);
        }

        if (vm.HasBattery)
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
            Tiles.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star));
        }

        for (var r = 0; r < (tiles.Count + columns - 1) / columns; r++)
        {
            Tiles.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        }

        foreach (var tile in new Control[] { CpuTile, MemoryTile, GpuTile, BatteryTile })
        {
            var index = tiles.IndexOf(tile);
            tile.IsVisible = index >= 0;
            if (index >= 0)
            {
                Grid.SetColumn(tile, index % columns);
                Grid.SetRow(tile, index / columns);
            }
        }
    }

    private static void Paint(Sparkline spark, Meter bar, SystemLevel level)
    {
        spark.SetColor(StatusColors.Color(level));
        bar.Fill = StatusColors.Brush(level);
    }
}
