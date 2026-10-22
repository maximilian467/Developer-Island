using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace DeveloperIsland.UI.Components;

/// <summary>A 3 DIP progress line: the track, filled from the left by <see cref="Fraction"/>.</summary>
public sealed class Meter : Control
{
    public static readonly StyledProperty<double> FractionProperty = AvaloniaProperty.Register<Meter, double>(nameof(Fraction));

    public static readonly StyledProperty<IBrush?> FillProperty = AvaloniaProperty.Register<Meter, IBrush?>(nameof(Fill));

    static Meter()
    {
        AffectsRender<Meter>(FractionProperty, FillProperty);
    }

    public Meter()
    {
        Height = 3;
        IsHitTestVisible = false;
    }

    public double Fraction
    {
        get => GetValue(FractionProperty);
        set => SetValue(FractionProperty, value);
    }

    public IBrush? Fill
    {
        get => GetValue(FillProperty);
        set => SetValue(FillProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        var w = Bounds.Width;
        var h = Bounds.Height;
        context.FillRectangle(StatusColors.Resource<IBrush>("ProgressTrackBrush"), new Rect(0, 0, w, h), (float)(h / 2));
        var f = Math.Clamp(Fraction, 0, 1);
        if (f > 0)
        {
            context.FillRectangle(Fill ?? StatusColors.Resource<IBrush>("AccentBrush"), new Rect(0, 0, Math.Max(h, w * f), h), (float)(h / 2));
        }
    }
}
