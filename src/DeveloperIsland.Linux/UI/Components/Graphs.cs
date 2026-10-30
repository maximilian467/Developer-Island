using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using DeveloperIsland.Core.Usage;
using DeveloperIsland.ViewModels;

namespace DeveloperIsland.UI.Components;

/// <summary>
/// A calm 60-second history: a thin line over a faint fill, on a fixed 0-100 scale so a quiet machine
/// looks quiet. Redrawn only when new values arrive.
/// </summary>
public sealed class Sparkline : Control
{
    private IReadOnlyList<double> _values = [];
    private Color _color;
    private IBrush _fill = Brushes.Transparent;
    private IPen _pen = new Pen(Brushes.White);

    public Sparkline()
    {
        Height = 24;
        IsHitTestVisible = false;
        SetColor(StatusColors.Resource<Color>("AccentColor"));
    }

    /// <summary>Number of points the full width represents (one per second).</summary>
    public int Capacity { get; set; } = 60;

    public void SetColor(Color color)
    {
        if (color == _color)
        {
            return;
        }

        _color = color;
        _fill = new SolidColorBrush(Color.FromArgb(0x2E, color.R, color.G, color.B));
        _pen = new Pen(new SolidColorBrush(color), 1.3, lineJoin: PenLineJoin.Round);
        InvalidateVisual();
    }

    /// <summary>Values 0-100, oldest first.</summary>
    public void SetValues(IReadOnlyList<double> values)
    {
        _values = values;
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        var width = Bounds.Width;
        var height = Bounds.Height;
        if (width <= 0 || height <= 0 || _values.Count < 2)
        {
            return;
        }

        // Newest point on the right edge; a fresh history grows in from the right.
        var step = width / (Capacity - 1);
        var start = width - (_values.Count - 1) * step;
        Point At(int i) => new(start + i * step, height - 1 - Math.Clamp(_values[i], 0, 100) / 100 * (height - 2));

        var line = new StreamGeometry();
        using (var g = line.Open())
        {
            g.BeginFigure(At(0), false);
            for (var i = 1; i < _values.Count; i++)
            {
                g.LineTo(At(i));
            }

            g.EndFigure(false);
        }

        var area = new StreamGeometry();
        using (var g = area.Open())
        {
            g.BeginFigure(new Point(start, height), true);
            for (var i = 0; i < _values.Count; i++)
            {
                g.LineTo(At(i));
            }

            g.LineTo(new Point(width, height));
            g.EndFigure(true);
        }

        context.DrawGeometry(_fill, null, area);
        context.DrawGeometry(null, _pen, line);
    }
}

/// <summary>
/// A 4 DIP media progress line with a 16 DIP tall hit area. When the player supports seeking, the
/// line accepts click/drag and Left/Right keys (5 s steps); the knob appears on hover or focus.
/// </summary>
public sealed class MediaTimeline : Control
{
    public static readonly StyledProperty<double> ProgressProperty = AvaloniaProperty.Register<MediaTimeline, double>(nameof(Progress));

    public static readonly StyledProperty<bool> CanSeekProperty = AvaloniaProperty.Register<MediaTimeline, bool>(nameof(CanSeek));

    private double? _preview;
    private bool _dragging;

    static MediaTimeline()
    {
        AffectsRender<MediaTimeline>(ProgressProperty, CanSeekProperty, IsPointerOverProperty, IsFocusedProperty);
    }

    public MediaTimeline()
    {
        Height = 16;
        Cursor = new Cursor(StandardCursorType.Hand);
    }

    public double Progress
    {
        get => GetValue(ProgressProperty);
        set => SetValue(ProgressProperty, value);
    }

    public bool CanSeek
    {
        get => GetValue(CanSeekProperty);
        set => SetValue(CanSeekProperty, value);
    }

    /// <summary>Requested seek position as a fraction of the duration.</summary>
    public event Action<double>? SeekRequested;

    /// <summary>Duration used for keyboard steps.</summary>
    public TimeSpan Duration { get; set; }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == CanSeekProperty)
        {
            Focusable = CanSeek;
            Cursor = CanSeek ? new Cursor(StandardCursorType.Hand) : Cursor.Default;
        }
    }

    public override void Render(DrawingContext context)
    {
        var w = Bounds.Width;
        var y = Bounds.Height / 2;
        var p = Math.Clamp(_preview ?? Progress, 0, 1);
        context.FillRectangle(StatusColors.Resource<IBrush>("ProgressTrackBrush"), new Rect(0, y - 2, w, 4), 2);
        if (p > 0)
        {
            context.FillRectangle(StatusColors.Resource<IBrush>("TextPrimaryBrush"), new Rect(0, y - 2, Math.Max(4, w * p), 4), 2);
        }

        if (CanSeek && (IsPointerOver || IsFocused || _dragging))
        {
            context.DrawEllipse(StatusColors.Resource<IBrush>("TextPrimaryBrush"), null, new Point(w * p, y), 6, 6);
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!CanSeek || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        _dragging = true;
        e.Pointer.Capture(this);
        _preview = FractionAt(e.GetPosition(this));
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_dragging)
        {
            _preview = FractionAt(e.GetPosition(this));
            InvalidateVisual();
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (!_dragging)
        {
            return;
        }

        _dragging = false;
        e.Pointer.Capture(null);
        var fraction = FractionAt(e.GetPosition(this));
        _preview = null;
        SeekRequested?.Invoke(fraction);
        e.Handled = true;
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        _dragging = false;
        _preview = null;
        InvalidateVisual();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (!CanSeek || Duration <= TimeSpan.Zero || e.Key is not (Key.Left or Key.Right))
        {
            base.OnKeyDown(e);
            return;
        }

        var step = TimeSpan.FromSeconds(5) / Duration;
        SeekRequested?.Invoke(Math.Clamp(Progress + (e.Key == Key.Right ? step : -step), 0, 1));
        e.Handled = true;
    }

    private double FractionAt(Point p) => Bounds.Width <= 0 ? 0 : Math.Clamp(p.X / Bounds.Width, 0, 1);
}

/// <summary>
/// Contribution-style usage graph: one column per week, one row per weekday, intensity by quartile.
/// Pointer hover, a click and arrow keys inspect a day; a small floating capsule above the day shows
/// its numbers, and the readout below the graph gives the detail, so nothing pops up outside the island.
/// </summary>
public sealed class UsageHeatmap : Control
{
    public const double CellSize = 11;
    public const double Gap = 3;
    private const double Pitch = CellSize + Gap;

    private static readonly Color Accent = Color.FromArgb(255, 0x29, 0x97, 0xFF);
    private static readonly IBrush[] LevelBrushes =
    [
        new SolidColorBrush(Color.FromArgb(0x16, 0xFF, 0xFF, 0xFF)),
        new SolidColorBrush(Color.FromArgb(0x4D, Accent.R, Accent.G, Accent.B)),
        new SolidColorBrush(Color.FromArgb(0x80, Accent.R, Accent.G, Accent.B)),
        new SolidColorBrush(Color.FromArgb(0xB8, Accent.R, Accent.G, Accent.B)),
        new SolidColorBrush(Accent),
    ];

    private static readonly IBrush TipBackground = new SolidColorBrush(Color.FromArgb(0xF2, 0x23, 0x23, 0x26));
    private static readonly IPen TipBorder = new Pen(new SolidColorBrush(Color.FromArgb(0x24, 0xFF, 0xFF, 0xFF)), 1);

    private HeatmapLayout _layout = HeatmapLayout.For([], DayOfWeek.Monday);
    private UsageViewModel? _viewModel;

    public UsageHeatmap()
    {
        Focusable = true;
        ClipToBounds = false;
        Background = Brushes.Transparent;
    }

    /// <summary>A hit-test surface over the whole grid (gaps included).</summary>
    public IBrush Background { get; }

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

    protected override Size MeasureOverride(Size availableSize) =>
        _viewModel is { Days.Count: > 0 } ? new Size(_layout.Columns * Pitch - Gap, 7 * Pitch - Gap) : default;

    public override void Render(DrawingContext context)
    {
        context.FillRectangle(Background, new Rect(Bounds.Size));
        if (_viewModel is not { Days.Count: > 0 } vm)
        {
            return;
        }

        for (var i = 0; i < vm.Days.Count && i < _layout.Positions.Count; i++)
        {
            var (column, row) = _layout.Positions[i];
            var level = i < vm.Levels.Count ? vm.Levels[i] : 0;
            context.FillRectangle(LevelBrushes[Math.Clamp(level, 0, LevelBrushes.Length - 1)], new Rect(column * Pitch, row * Pitch, CellSize, CellSize), 2.5f);
        }

        if (vm.IsInspecting && vm.InspectedIndex < _layout.Positions.Count)
        {
            var (column, row) = _layout.Positions[vm.InspectedIndex];
            context.DrawRectangle(null, new Pen(StatusColors.Resource<IBrush>("TextPrimaryBrush"), 1.5),
                new RoundedRect(new Rect(column * Pitch - 2, row * Pitch - 2, CellSize + 4, CellSize + 4), 4));
            DrawTip(context, vm, column, row);
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        InspectAt(e.GetPosition(this));
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        InspectAt(e.GetPosition(this));
        if (_viewModel is { IsInspecting: true })
        {
            Focus(NavigationMethod.Pointer);
        }

        e.Handled = true;
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);

        // Arrow-key navigation keeps its day; a day inspected by the pointer ends with it.
        if (!IsKeyboardFocusWithin || !IsFocused)
        {
            _viewModel?.Inspect(-1);
        }
    }

    protected override void OnGotFocus(GotFocusEventArgs e)
    {
        base.OnGotFocus(e);
        if (_viewModel is { } vm && !vm.IsInspecting && vm.Days.Count > 0 && e.NavigationMethod == NavigationMethod.Tab)
        {
            vm.Inspect(vm.Days.Count - 1);
        }
    }

    protected override void OnLostFocus(Avalonia.Interactivity.RoutedEventArgs e)
    {
        base.OnLostFocus(e);
        _viewModel?.Inspect(-1);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (_viewModel is not { Days.Count: > 0 } vm)
        {
            base.OnKeyDown(e);
            return;
        }

        var index = vm.IsInspecting ? vm.InspectedIndex : vm.Days.Count - 1;
        var next = e.Key switch
        {
            Key.Left => index - 7,
            Key.Right => index + 7,
            Key.Up => index - 1,
            Key.Down => index + 1,
            Key.Home => 0,
            Key.End => vm.Days.Count - 1,
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

    private void Rebuild()
    {
        if (_viewModel is { Days.Count: > 0 } vm)
        {
            var firstDay = CultureInfo.CurrentCulture.DateTimeFormat.FirstDayOfWeek;
            _layout = HeatmapLayout.For(vm.Days.Select(d => d.Day).ToList(), firstDay);
        }

        InvalidateMeasure();
        InvalidateVisual();
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(UsageViewModel.InspectedIndex) or "" or null)
        {
            InvalidateVisual();
        }
    }

    private void InspectAt(Point point) => _viewModel?.Inspect(_layout.IndexAt(point.X, point.Y, Pitch));

    /// <summary>The floating capsule above the inspected cell (never under the pointer).</summary>
    private void DrawTip(DrawingContext context, UsageViewModel vm, int column, int row)
    {
        var typeface = new Typeface(StatusColors.Resource<FontFamily>("TextFont"));
        var semibold = new Typeface(StatusColors.Resource<FontFamily>("TextFont"), weight: FontWeight.SemiBold);
        var primary = StatusColors.Resource<IBrush>("TextPrimaryBrush");
        var tertiary = StatusColors.Resource<IBrush>("TextTertiaryBrush");
        var lines = new List<FormattedText>
        {
            new(vm.InspectedDate, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, semibold, 12, primary),
            new(vm.InspectedTooltipTokens, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, 12, primary),
        };
        if (vm.InspectedTooltipDetail.Length > 0)
        {
            lines.Add(new FormattedText(vm.InspectedTooltipDetail, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, 11, tertiary));
        }

        var width = lines.Max(l => l.Width) + 20;
        var height = lines.Sum(l => l.Height) + 13 + (lines.Count - 1);
        var x = Math.Clamp(column * Pitch + CellSize / 2 - width / 2, 0, Math.Max(0, Bounds.Width - width));
        var y = row * Pitch - height - 6;
        context.DrawRectangle(TipBackground, TipBorder, new RoundedRect(new Rect(x, y, width, height), 10),
            new BoxShadows(new BoxShadow { OffsetY = 4, Blur = 14, Color = Color.FromArgb(0x66, 0, 0, 0) }));
        var ty = y + 6;
        foreach (var line in lines)
        {
            context.DrawText(line, new Point(x + 10, ty));
            ty += line.Height + 1;
        }
    }
}
