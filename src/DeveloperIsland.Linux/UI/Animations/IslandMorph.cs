using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace DeveloperIsland.UI.Animations;

/// <summary>
/// The visible capsule: fill, a 1 px hairline for edge clarity on dark wallpapers, and in the expanded
/// state a soft shadow (ambient plus key layer).
/// </summary>
internal sealed class CapsuleSurface : Control
{
    private readonly IBrush _fill;
    private readonly IPen _hairline;
    private Rect _rect;
    private double _radius;
    private double _shadow;

    public CapsuleSurface(Color fill, Color hairline)
    {
        _fill = new SolidColorBrush(fill);
        _hairline = new Pen(new SolidColorBrush(hairline), 1);
        IsHitTestVisible = false;
    }

    public void Set(Rect rect, double radius, double shadow)
    {
        _rect = rect;
        _radius = radius;
        _shadow = shadow;
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        if (_rect.Width <= 0 || _rect.Height <= 0)
        {
            return;
        }

        var r = Math.Min(_radius, Math.Min(_rect.Width, _rect.Height) / 2);
        var shape = new RoundedRect(_rect, r);
        if (_shadow > 0.01)
        {
            var a = Math.Clamp(_shadow, 0, 1);
            context.DrawRectangle(_fill, null, shape, new BoxShadows(
                new BoxShadow { OffsetY = 2, Blur = 8, Color = Color.FromArgb((byte)(0x38 * a), 0, 0, 0) },
                [new BoxShadow { OffsetY = 14, Blur = 36, Color = Color.FromArgb((byte)(0x70 * a), 0, 0, 0) }]));
        }

        // Inset half a pixel so the hairline sits on the capsule's edge, not outside it.
        context.DrawRectangle(_fill, _hairline, new RoundedRect(_rect.Deflate(0.5), Math.Max(0, r - 0.5)));
    }
}

/// <summary>
/// Morphs the capsule between states on damped springs, stepped once per frame: offset, size and
/// radius drive the visible shape and the clip of the content layer together, so text never spills
/// outside the capsule mid-morph. Springs retarget from their current value and velocity, so a new
/// state can interrupt a running morph at any time without a jump. Nothing runs between morphs.
/// </summary>
internal sealed class IslandMorph
{
    private const double SettleDistance = 0.25;
    private const double SettleSpeed = 4;

    private readonly CapsuleSurface _surface;
    private readonly Control _content;
    private readonly TopLevel _top;
    private readonly double[] _x = new double[5];   // x, y, width, height, radius
    private readonly double[] _v = new double[5];
    private readonly double[] _target = new double[5];
    private double _shadow;
    private double _shadowFrom;
    private double _shadowTo;
    private TimeSpan _shadowStart;
    private TimeSpan _shadowDuration;
    private TimeSpan? _lastFrame;
    private bool _running;
    private long _generation;
    private Action? _completed;

    public IslandMorph(CapsuleSurface surface, Control content, TopLevel top)
    {
        _surface = surface;
        _content = content;
        _top = top;
    }

    /// <summary>Target rectangle of the running (or finished) morph, in window DIPs.</summary>
    public Rect Target { get; private set; }

    public bool HasTarget => Target.Width > 0;

    /// <summary>
    /// Morphs to <paramref name="rect"/>. <paramref name="completed"/> runs once the springs settle,
    /// unless another morph started meanwhile.
    /// </summary>
    public void MorphTo(Rect rect, double radius, bool elevated, bool animate, Action? completed)
    {
        var generation = ++_generation;
        var first = !HasTarget;
        Target = rect;
        _target[0] = rect.X;
        _target[1] = rect.Y;
        _target[2] = rect.Width;
        _target[3] = rect.Height;
        _target[4] = radius;
        var shadow = elevated ? 1.0 : 0.0;

        if (!animate || first || !Motion.IsEnabled)
        {
            for (var i = 0; i < 5; i++)
            {
                _x[i] = _target[i];
                _v[i] = 0;
            }

            _shadow = shadow;
            _shadowTo = shadow;
            _running = false;
            Apply();
            completed?.Invoke();
            return;
        }

        // Shadow follows the surface: in with the growth, out slightly ahead of the shrink.
        _shadowFrom = _shadow;
        _shadowTo = shadow;
        _shadowStart = TimeSpan.MinValue;
        _shadowDuration = TimeSpan.FromMilliseconds(elevated ? 320 : 140);
        _completed = () =>
        {
            if (generation == _generation)
            {
                completed?.Invoke();
            }
        };

        if (!_running)
        {
            _running = true;
            _lastFrame = null;
            _top.RequestAnimationFrame(OnFrame);
        }
    }

    private void OnFrame(TimeSpan time)
    {
        if (!_running)
        {
            return;
        }

        var dt = _lastFrame is { } last ? Math.Clamp((time - last).TotalSeconds, 0, 0.05) : 1 / 60.0;
        _lastFrame = time;
        if (_shadowStart == TimeSpan.MinValue)
        {
            _shadowStart = time;
        }

        var settled = true;
        for (var i = 0; i < 5; i++)
        {
            (_x[i], _v[i]) = Motion.IslandSpring.Step(_x[i], _v[i], _target[i], dt);
            if (Math.Abs(_x[i] - _target[i]) > SettleDistance || Math.Abs(_v[i]) > SettleSpeed)
            {
                settled = false;
            }
        }

        var t = _shadowDuration.TotalMilliseconds <= 0 ? 1 : Math.Clamp((time - _shadowStart) / _shadowDuration, 0, 1);
        _shadow = _shadowFrom + (_shadowTo - _shadowFrom) * Motion.EaseOut.Ease(t);
        if (t < 1)
        {
            settled = false;
        }

        if (settled)
        {
            Array.Copy(_target, _x, 5);
            Array.Clear(_v);
            _shadow = _shadowTo;
            _running = false;
            Apply();
            var done = _completed;
            _completed = null;
            done?.Invoke();
            return;
        }

        Apply();
        _top.RequestAnimationFrame(OnFrame);
    }

    private void Apply()
    {
        var rect = new Rect(_x[0], _x[1], Math.Max(0, _x[2]), Math.Max(0, _x[3]));

        // Radius never exceeds half the shortest side, so the shape stays a true pill mid-morph.
        var radius = Math.Max(0, Math.Min(_x[4], Math.Min(rect.Width, rect.Height) / 2));
        _surface.Set(rect, radius, _shadow);
        _content.Clip = new RectangleGeometry(rect, radius, radius);
    }
}
