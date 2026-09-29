using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.UI;

namespace DeveloperIsland.UI.Animations;

/// <summary>
/// Draws and morphs the island capsule entirely on the compositor.
/// One property set (offset, size, radius, shadow) drives three consumers through expressions:
/// the visible rounded shape, the clip of the content layer and the shadow.
/// Springs retarget from their current value and velocity, so a new state can interrupt a running
/// morph at any time without a jump.
/// </summary>
/// <remarks>
/// The shadow is a pre-rendered soft rounded rectangle (Assets/island-shadow.png, ambient plus key
/// layer) stretched with a nine-grid brush. LayerVisual/DropShadow was not used: its intermediate
/// surface renders opaque on a per-pixel transparent window, and a texture also needs no offscreen
/// pass per frame.
/// </remarks>
internal sealed class IslandMorph
{
    /// <summary>Shadow texture inset (px) = how far the shadow may extend beyond the capsule (DIPs).</summary>
    private const float ShadowInset = 40f;

    private readonly Compositor _c;
    private readonly ContainerVisual _root;
    private readonly ShapeVisual _shapeVisual;
    private readonly SpriteVisual _shadow;
    private readonly CompositionPropertySet _props;
    private long _generation;

    public IslandMorph(UIElement surfaceHost, UIElement contentHost, Color fill, Color hairline)
    {
        _c = ElementCompositionPreview.GetElementVisual(surfaceHost).Compositor;
        _props = _c.CreatePropertySet();
        _props.InsertVector2("Offset", Vector2.Zero);
        _props.InsertVector2("Size", Vector2.Zero);
        _props.InsertScalar("Radius", 18f);
        _props.InsertScalar("Shadow", 0f);

        // Shadow: only visible in the expanded state (compact stays flat, like the Dynamic Island).
        var surface = LoadedImageSurface.StartLoadFromUri(new Uri("ms-appx:///Assets/island-shadow.png"));
        var nineGrid = _c.CreateNineGridBrush();
        nineGrid.Source = _c.CreateSurfaceBrush(surface);
        nineGrid.SetInsets(ShadowInset);
        _shadow = _c.CreateSpriteVisual();
        _shadow.Brush = nineGrid;
        BindShadow(_shadow);

        // Visible capsule: fill plus a 1 px semi-transparent hairline for edge clarity on dark wallpapers.
        var geometry = _c.CreateRoundedRectangleGeometry();
        Bind(geometry);
        var shape = _c.CreateSpriteShape(geometry);
        shape.FillBrush = _c.CreateColorBrush(fill);
        shape.StrokeBrush = _c.CreateColorBrush(hairline);
        shape.StrokeThickness = 1f;
        _shapeVisual = _c.CreateShapeVisual();
        _shapeVisual.Shapes.Add(shape);

        _root = _c.CreateContainerVisual();
        _root.Children.InsertAtBottom(_shadow);
        _root.Children.InsertAtTop(_shapeVisual);
        ElementCompositionPreview.SetElementChildVisual(surfaceHost, _root);

        // Content is clipped by the same animated shape, so text never spills outside the capsule mid-morph.
        var clipGeometry = _c.CreateRoundedRectangleGeometry();
        Bind(clipGeometry);
        ElementCompositionPreview.GetElementVisual(contentHost).Clip = _c.CreateGeometricClip(clipGeometry);
    }

    /// <summary>Target rectangle of the running (or finished) morph, in window DIPs.</summary>
    public Rect Target { get; private set; }

    public bool HasTarget => Target.Width > 0;

    public void SetCanvasSize(Size size)
    {
        var v = new Vector2((float)size.Width, (float)size.Height);
        _root.Size = v;
        _shapeVisual.Size = v;
    }

    /// <summary>
    /// Morphs to <paramref name="rect"/>. <paramref name="completed"/> runs once the springs settle,
    /// unless another morph started meanwhile.
    /// </summary>
    public void MorphTo(Rect rect, double radius, bool elevated, bool animate, Action? completed)
    {
        var generation = ++_generation;
        var offset = new Vector2((float)rect.X, (float)rect.Y);
        var size = new Vector2((float)rect.Width, (float)rect.Height);
        var shadow = elevated ? 1f : 0f;
        var first = !HasTarget;
        Target = rect;

        if (!animate || first || !Motion.IsEnabled)
        {
            foreach (var name in new[] { "Offset", "Size", "Radius", "Shadow" })
            {
                _props.StopAnimation(name);
            }

            _props.InsertVector2("Offset", offset);
            _props.InsertVector2("Size", size);
            _props.InsertScalar("Radius", (float)radius);
            _props.InsertScalar("Shadow", shadow);
            completed?.Invoke();
            return;
        }

        var batch = _c.CreateScopedBatch(CompositionBatchTypes.Animation);
        _props.StartAnimation("Offset", Motion.Vector2Spring(_c, offset, Motion.IslandSpring));
        _props.StartAnimation("Size", Motion.Vector2Spring(_c, size, Motion.IslandSpring));
        _props.StartAnimation("Radius", Motion.ScalarSpring(_c, (float)radius, Motion.IslandSpring));

        // Shadow follows the surface: in with the growth, out slightly ahead of the shrink.
        var shadowAnimation = _c.CreateScalarKeyFrameAnimation();
        shadowAnimation.InsertKeyFrame(1f, shadow, Motion.EaseOut(_c));
        shadowAnimation.Duration = TimeSpan.FromMilliseconds(elevated ? 320 : 140);
        _props.StartAnimation("Shadow", shadowAnimation);
        batch.End();
        batch.Completed += (_, _) =>
        {
            if (generation == _generation)
            {
                completed?.Invoke();
            }
        };
    }

    private void Bind(CompositionRoundedRectangleGeometry geometry)
    {
        var offset = _c.CreateExpressionAnimation("p.Offset");
        offset.SetReferenceParameter("p", _props);
        geometry.StartAnimation("Offset", offset);

        var size = _c.CreateExpressionAnimation("p.Size");
        size.SetReferenceParameter("p", _props);
        geometry.StartAnimation("Size", size);

        // Radius never exceeds half the shortest side, so the shape stays a true pill mid-morph.
        var radius = _c.CreateExpressionAnimation("Vector2(Min(p.Radius, Min(p.Size.X, p.Size.Y) / 2), Min(p.Radius, Min(p.Size.X, p.Size.Y) / 2))");
        radius.SetReferenceParameter("p", _props);
        geometry.StartAnimation("CornerRadius", radius);
    }

    private void BindShadow(SpriteVisual shadow)
    {
        var inset = ShadowInset.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var offset = _c.CreateExpressionAnimation($"Vector3(p.Offset.X - {inset}, p.Offset.Y - {inset}, 0)");
        offset.SetReferenceParameter("p", _props);
        shadow.StartAnimation("Offset", offset);

        var size = _c.CreateExpressionAnimation($"Vector2(p.Size.X + 2 * {inset}, p.Size.Y + 2 * {inset})");
        size.SetReferenceParameter("p", _props);
        shadow.StartAnimation("Size", size);

        var opacity = _c.CreateExpressionAnimation("p.Shadow");
        opacity.SetReferenceParameter("p", _props);
        shadow.StartAnimation("Opacity", opacity);
    }
}
