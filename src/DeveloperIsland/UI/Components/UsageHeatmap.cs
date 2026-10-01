using System.Globalization;
using DeveloperIsland.ViewModels;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.System;
using Windows.UI;

namespace DeveloperIsland.UI.Components;

/// <summary>
/// Contribution-style usage graph: one column per week, one row per weekday, intensity by quartile.
/// Pointer hover and arrow keys inspect a day; the readout lives in the panel below the graph,
/// so nothing pops up outside the island.
/// </summary>
public sealed class UsageHeatmap : UserControl
{
    public const double CellSize = 11;
    public const double Gap = 3;
    private const double Pitch = CellSize + Gap;

    private static readonly Color Accent = Color.FromArgb(255, 0x29, 0x97, 0xFF);
    private static readonly SolidColorBrush[] LevelBrushes =
    [
        new(Color.FromArgb(0x16, 0xFF, 0xFF, 0xFF)),
        new(Color.FromArgb(0x4D, Accent.R, Accent.G, Accent.B)),
        new(Color.FromArgb(0x80, Accent.R, Accent.G, Accent.B)),
        new(Color.FromArgb(0xB8, Accent.R, Accent.G, Accent.B)),
        new(Accent),
    ];

    private readonly Canvas _canvas = new();
    private readonly Rectangle _ring;
    private readonly List<Rectangle> _cells = [];
    private readonly List<(int Column, int Row)> _positions = [];
    private readonly Border _tip;
    private readonly TextBlock _tipDate = new();
    private readonly TextBlock _tipTokens = new();
    private readonly TextBlock _tipDetail = new();
    private DeveloperIsland.Core.Usage.HeatmapLayout _layout = DeveloperIsland.Core.Usage.HeatmapLayout.For([], DayOfWeek.Monday);
    private bool _tipShown;
    private UsageViewModel? _viewModel;
    private int _columns;
    private DateOnly _firstDay;

    public UsageHeatmap()
    {
        IsTabStop = true;
        UseSystemFocusVisuals = true;
        FocusVisualMargin = new Thickness(-4);
        Background = new SolidColorBrush(Colors.Transparent);
        _ring = new Rectangle
        {
            Width = CellSize + 4,
            Height = CellSize + 4,
            RadiusX = 4,
            RadiusY = 4,
            Stroke = (Brush)Application.Current.Resources["TextPrimaryBrush"],
            StrokeThickness = 1.5,
            Visibility = Visibility.Collapsed,
            IsHitTestVisible = false,
        };
        Content = _canvas;
        AutomationProperties.SetLiveSetting(this, AutomationLiveSetting.Polite);

        // Tooltip: a small floating capsule in the island's own style (not the system tooltip).
        var resources = Application.Current.Resources;
        _tipDate.Style = (Style)resources["CaptionTextStyle"];
        _tipDate.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
        _tipDate.Foreground = (Brush)resources["TextPrimaryBrush"];
        _tipTokens.Style = (Style)resources["CaptionTextStyle"];
        _tipTokens.Foreground = (Brush)resources["TextPrimaryBrush"];
        Microsoft.UI.Xaml.Documents.Typography.SetNumeralAlignment(_tipTokens, Microsoft.UI.Xaml.FontNumeralAlignment.Tabular);
        _tipDetail.Style = (Style)resources["FineTextStyle"];
        _tip = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xF2, 0x23, 0x23, 0x26)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x24, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(10, 6, 10, 7),
            IsHitTestVisible = false,
            Opacity = 0,
            Child = new StackPanel { Spacing = 1, Children = { _tipDate, _tipTokens, _tipDetail } },
        };
        _tip.Shadow = new ThemeShadow();
        _tip.Translation = new System.Numerics.Vector3(0, 0, 12);
        AutomationProperties.SetAccessibilityView(_tip, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);

        PointerMoved += (_, e) => InspectAt(e.GetCurrentPoint(_canvas).Position);
        PointerExited += (_, _) =>
        {
            if (FocusState == FocusState.Unfocused)
            {
                _viewModel?.Inspect(-1);
            }
        };
        GotFocus += (_, _) =>
        {
            if (_viewModel is { } vm && !vm.IsInspecting && vm.Days.Count > 0)
            {
                vm.Inspect(vm.Days.Count - 1);
            }
        };
        LostFocus += (_, _) => _viewModel?.Inspect(-1);
    }

    public UsageViewModel? ViewModel
    {
        get => _viewModel;
        set
        {
            if (_viewModel is not null)
            {
                _viewModel.HistoryChanged -= Rebuild;
                _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            }

            _viewModel = value;
            if (_viewModel is not null)
            {
                _viewModel.HistoryChanged += Rebuild;
                _viewModel.PropertyChanged += OnViewModelPropertyChanged;
            }

            Rebuild();
        }
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new FrameworkElementAutomationPeer(this);

    protected override void OnKeyDown(KeyRoutedEventArgs e)
    {
        if (_viewModel is not { Days.Count: > 0 } vm)
        {
            base.OnKeyDown(e);
            return;
        }

        var index = vm.IsInspecting ? vm.InspectedIndex : vm.Days.Count - 1;
        var next = e.Key switch
        {
            VirtualKey.Left => index - 7,
            VirtualKey.Right => index + 7,
            VirtualKey.Up => index - 1,
            VirtualKey.Down => index + 1,
            VirtualKey.Home => 0,
            VirtualKey.End => vm.Days.Count - 1,
            _ => int.MinValue,
        };

        if (next == int.MinValue)
        {
            base.OnKeyDown(e);
            return;
        }

        vm.Inspect(Math.Clamp(next, 0, vm.Days.Count - 1));
        e.Handled = true;
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(UsageViewModel.InspectedIndex) or "" or null)
        {
            UpdateRing();
            UpdateTip();
        }
    }

    private void Rebuild()
    {
        // Same range as before (the common case: today got more tokens): recolour in place.
        if (_viewModel is { } current && current.Days.Count == _cells.Count && _cells.Count > 0 && current.Days[0].Day == _firstDay)
        {
            for (var i = 0; i < _cells.Count; i++)
            {
                var level = i < current.Levels.Count ? current.Levels[i] : 0;
                _cells[i].Fill = LevelBrushes[Math.Clamp(level, 0, LevelBrushes.Length - 1)];
            }

            UpdateRing();
            return;
        }

        _canvas.Children.Clear();
        _cells.Clear();
        _positions.Clear();
        if (_viewModel is not { } vm || vm.Days.Count == 0)
        {
            Width = Height = 0;
            return;
        }

        var firstDay = CultureInfo.CurrentCulture.DateTimeFormat.FirstDayOfWeek;
        _layout = DeveloperIsland.Core.Usage.HeatmapLayout.For(vm.Days.Select(d => d.Day).ToList(), firstDay);

        for (var i = 0; i < vm.Days.Count; i++)
        {
            var (column, row) = _layout.Positions[i];
            var level = i < vm.Levels.Count ? vm.Levels[i] : 0;
            var cell = new Rectangle
            {
                Width = CellSize,
                Height = CellSize,
                RadiusX = 2.5,
                RadiusY = 2.5,
                Fill = LevelBrushes[Math.Clamp(level, 0, LevelBrushes.Length - 1)],
                IsHitTestVisible = false,
            };
            Canvas.SetLeft(cell, column * Pitch);
            Canvas.SetTop(cell, row * Pitch);
            _canvas.Children.Add(cell);
            _cells.Add(cell);
            _positions.Add((column, row));
        }

        _columns = _layout.Columns;
        _firstDay = vm.Days[0].Day;
        _canvas.Children.Add(_ring);
        _canvas.Children.Add(_tip);
        Width = _columns * Pitch - Gap;
        Height = 7 * Pitch - Gap;
        UpdateRing();
    }

    private void InspectAt(Windows.Foundation.Point point)
    {
        if (_viewModel is null)
        {
            return;
        }

        _viewModel.Inspect(_layout.IndexAt(point.X, point.Y, Pitch));
    }

    /// <summary>
    /// Shows the inspected day above its cell (never under the pointer), fades in once, follows from
    /// cell to cell without blinking, and disappears as soon as nothing is inspected.
    /// </summary>
    private void UpdateTip()
    {
        if (_viewModel is not { IsInspecting: true } vm || vm.InspectedIndex >= _positions.Count)
        {
            _tip.Opacity = 0;
            _tipShown = false;
            return;
        }

        _tipDate.Text = vm.InspectedDate;
        _tipTokens.Text = vm.InspectedTooltipTokens;
        _tipDetail.Text = vm.InspectedTooltipDetail;
        _tipDetail.Visibility = vm.InspectedTooltipDetail.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        _tip.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
        var size = _tip.DesiredSize;

        var (column, row) = _positions[vm.InspectedIndex];
        var x = column * Pitch + CellSize / 2 - size.Width / 2;
        x = Math.Clamp(x, 0, Math.Max(0, Width - size.Width));
        var y = row * Pitch - size.Height - 6;
        Canvas.SetLeft(_tip, x);
        Canvas.SetTop(_tip, y);
        if (!_tipShown)
        {
            _tipShown = true;
            _tip.Opacity = 1;
            DeveloperIsland.UI.Animations.Motion.FadeIn(_tip, delay: TimeSpan.Zero, fromY: 3);
        }
    }

    private void UpdateRing()
    {
        if (_viewModel is not { IsInspecting: true } vm || vm.InspectedIndex >= _positions.Count)
        {
            _ring.Visibility = Visibility.Collapsed;
            AutomationProperties.SetName(this, _viewModel?.InspectedAccessibleText ?? "Usage history");
            return;
        }

        var (column, row) = _positions[vm.InspectedIndex];
        Canvas.SetLeft(_ring, column * Pitch - 2);
        Canvas.SetTop(_ring, row * Pitch - 2);
        _ring.Visibility = Visibility.Visible;
        AutomationProperties.SetName(this, vm.InspectedAccessibleText);
    }
}
