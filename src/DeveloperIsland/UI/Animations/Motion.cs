using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using Windows.UI.ViewManagement;

namespace DeveloperIsland.UI.Animations;

/// <summary>A critically-damped-ish spring: damping ratio and natural period.</summary>
internal readonly record struct Spring(float DampingRatio, TimeSpan Period);

/// <summary>
/// Shared motion vocabulary (see docs/design/DESIGN-PRINCIPLES.md, section 5). Everything runs on the
/// compositor thread; nothing uses a per-frame UI-thread loop. When Windows "Animation effects"
/// is off, <see cref="IsEnabled"/> is false and callers switch to instant changes plus short fades.
/// </summary>
internal static class Motion
{
    /// <summary>The island shape morph: settles without a visible bounce.</summary>
    public static readonly Spring IslandSpring = new(0.84f, TimeSpan.FromMilliseconds(80));

    /// <summary>Small UI elements (tab indicator).</summary>
    public static readonly Spring SnappySpring = new(0.9f, TimeSpan.FromMilliseconds(45));

    public static readonly TimeSpan FadeOutDuration = TimeSpan.FromMilliseconds(110);
    public static readonly TimeSpan FadeInDuration = TimeSpan.FromMilliseconds(240);
    public static readonly TimeSpan FadeInDelay = TimeSpan.FromMilliseconds(70);

    private static readonly UISettings Settings = new();
    private static CompositionEasingFunction? s_easeOut;
    private static CompositionEasingFunction? s_easeIn;

    public static bool IsEnabled
    {
        get
        {
            try
            {
                return Settings.AnimationsEnabled;
            }
            catch
            {
                return true;
            }
        }
    }

    /// <summary>Ease-out (0.16, 1, 0.3, 1): fast start, long soft landing.</summary>
    public static CompositionEasingFunction EaseOut(Compositor c) =>
        s_easeOut ??= c.CreateCubicBezierEasingFunction(new Vector2(0.16f, 1f), new Vector2(0.3f, 1f));

    public static CompositionEasingFunction EaseIn(Compositor c) =>
        s_easeIn ??= c.CreateCubicBezierEasingFunction(new Vector2(0.4f, 0f), new Vector2(1f, 1f));

    public static void SpringTranslation(Visual visual, Vector3 target, Spring spring)
    {
        var animation = visual.Compositor.CreateSpringVector3Animation();
        animation.FinalValue = target;
        animation.DampingRatio = spring.DampingRatio;
        animation.Period = spring.Period;
        visual.StartAnimation("Translation", animation);
    }

    public static ScalarNaturalMotionAnimation ScalarSpring(Compositor c, float target, Spring spring)
    {
        var animation = c.CreateSpringScalarAnimation();
        animation.FinalValue = target;
        animation.DampingRatio = spring.DampingRatio;
        animation.Period = spring.Period;
        return animation;
    }

    public static Vector2NaturalMotionAnimation Vector2Spring(Compositor c, Vector2 target, Spring spring)
    {
        var animation = c.CreateSpringVector2Animation();
        animation.FinalValue = target;
        animation.DampingRatio = spring.DampingRatio;
        animation.Period = spring.Period;
        return animation;
    }

    /// <summary>Fades an element in from a slight offset (content arriving after a morph or tab switch).</summary>
    public static void FadeIn(UIElement element, TimeSpan? delay = null, float fromY = 0, float fromScale = 1f, Vector2? origin = null)
    {
        var visual = ElementCompositionPreview.GetElementVisual(element);
        var c = visual.Compositor;
        ElementCompositionPreview.SetIsTranslationEnabled(element, true);

        if (!IsEnabled)
        {
            visual.StopAnimation("Opacity");
            visual.StopAnimation("Scale");
            visual.StopAnimation("Translation");
            visual.Scale = Vector3.One;
            visual.Properties.InsertVector3("Translation", Vector3.Zero);
            var quick = c.CreateScalarKeyFrameAnimation();
            quick.InsertKeyFrame(0f, 0f);
            quick.InsertKeyFrame(1f, 1f);
            quick.Duration = TimeSpan.FromMilliseconds(100);
            visual.StartAnimation("Opacity", quick);
            return;
        }

        if (origin is { } o)
        {
            visual.CenterPoint = new Vector3(o, 0);
        }

        var opacity = c.CreateScalarKeyFrameAnimation();
        opacity.InsertKeyFrame(0f, 0f);
        opacity.InsertKeyFrame(1f, 1f, EaseOut(c));
        opacity.Duration = FadeInDuration;
        opacity.DelayTime = delay ?? TimeSpan.Zero;
        opacity.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;
        visual.StartAnimation("Opacity", opacity);

        if (fromY != 0)
        {
            var translation = c.CreateVector3KeyFrameAnimation();
            translation.InsertKeyFrame(0f, new Vector3(0, fromY, 0));
            translation.InsertKeyFrame(1f, Vector3.Zero, EaseOut(c));
            translation.Duration = FadeInDuration + TimeSpan.FromMilliseconds(80);
            translation.DelayTime = delay ?? TimeSpan.Zero;
            translation.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;
            visual.StartAnimation("Translation", translation);
        }

        if (fromScale != 1f)
        {
            var scale = c.CreateVector3KeyFrameAnimation();
            scale.InsertKeyFrame(0f, new Vector3(fromScale, fromScale, 1));
            scale.InsertKeyFrame(1f, Vector3.One, EaseOut(c));
            scale.Duration = FadeInDuration + TimeSpan.FromMilliseconds(80);
            scale.DelayTime = delay ?? TimeSpan.Zero;
            scale.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;
            visual.StartAnimation("Scale", scale);
        }
    }

    /// <summary>Fades an element out, continuing from its current opacity, then runs <paramref name="completed"/>.</summary>
    public static void FadeOut(UIElement element, Action? completed, float toScale = 1f, Vector2? origin = null)
    {
        var visual = ElementCompositionPreview.GetElementVisual(element);
        var c = visual.Compositor;
        var batch = c.CreateScopedBatch(CompositionBatchTypes.Animation);

        var opacity = c.CreateScalarKeyFrameAnimation();
        opacity.InsertKeyFrame(1f, 0f, EaseIn(c));
        opacity.Duration = IsEnabled ? FadeOutDuration : TimeSpan.FromMilliseconds(80);
        visual.StartAnimation("Opacity", opacity);

        if (IsEnabled && toScale != 1f)
        {
            if (origin is { } o)
            {
                visual.CenterPoint = new Vector3(o, 0);
            }

            var scale = c.CreateVector3KeyFrameAnimation();
            scale.InsertKeyFrame(1f, new Vector3(toScale, toScale, 1), EaseIn(c));
            scale.Duration = FadeOutDuration;
            visual.StartAnimation("Scale", scale);
        }

        batch.End();
        batch.Completed += (_, _) => completed?.Invoke();
    }
}
