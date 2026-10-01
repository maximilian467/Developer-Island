using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.UI;
using Path = Microsoft.UI.Xaml.Shapes.Path;

namespace DeveloperIsland.UI.Components;

/// <summary>
/// A calm 60-second history: a thin accent line over a faint fill, on a fixed 0-100 scale so a
/// quiet machine looks quiet. Redrawn only when new values arrive (once a second, only while the
/// System panel or a featured System capsule is visible).
/// </summary>
public sealed class Sparkline : UserControl
{
    private readonly Path _area = new();
    private readonly Path _line = new() { StrokeThickness = 1.3, StrokeLineJoin = PenLineJoin.Round };
    private IReadOnlyList<double> _values = [];

    public Sparkline()
    {
        IsTabStop = false;
        Height = 24;
        var accent = (Color)Application.Current.Resources["AccentColor"];
        _area.Fill = new SolidColorBrush(Color.FromArgb(0x2E, accent.R, accent.G, accent.B));
        _line.Stroke = new SolidColorBrush(accent);
        Content = new Grid { Children = { _area, _line } };
        SizeChanged += (_, _) => Render();
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAccessibilityView(this, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
    }

    /// <summary>Number of points the full width represents (one per second).</summary>
    public int Capacity { get; set; } = 60;

    /// <summary>Values 0-100, oldest first.</summary>
    public void SetValues(IReadOnlyList<double> values)
    {
        _values = values;
        Render();
    }

    private void Render()
    {
        var width = ActualWidth;
        var height = ActualHeight;
        if (width <= 0 || height <= 0 || _values.Count < 2)
        {
            _area.Data = null;
            _line.Data = null;
            return;
        }

        // Newest point on the right edge; a fresh history grows in from the right.
        var step = width / (Capacity - 1);
        var start = width - (_values.Count - 1) * step;
        Point At(int i) => new(start + i * step, height - 1 - Math.Clamp(_values[i], 0, 100) / 100 * (height - 2));

        var line = new PathFigure { StartPoint = At(0) };
        var area = new PathFigure { StartPoint = new Point(start, height), IsClosed = true };
        area.Segments.Add(new LineSegment { Point = At(0) });
        for (var i = 1; i < _values.Count; i++)
        {
            line.Segments.Add(new LineSegment { Point = At(i) });
            area.Segments.Add(new LineSegment { Point = At(i) });
        }

        area.Segments.Add(new LineSegment { Point = new Point(width, height) });
        _line.Data = new PathGeometry { Figures = { line } };
        _area.Data = new PathGeometry { Figures = { area } };
    }
}
