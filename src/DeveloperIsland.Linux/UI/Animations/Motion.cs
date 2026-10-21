using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using System.Diagnostics;

namespace DeveloperIsland.UI.Animations;

/// <summary>A damped spring: damping ratio and natural period (WinUI NaturalMotion parameters).</summary>
internal readonly record struct Spring(double DampingRatio, TimeSpan Period)
{
    /// <summary>
    /// Advances one value towards <paramref name="target"/> by <paramref name="dt"/> seconds, exactly
    /// (closed form), so the result does not depend on the frame rate.
    /// </summary>
    public (double X, double V) Step(double x, double v, double target, double dt)
    {
        var omega = 2 * Math.PI / Period.TotalSeconds;
        var zeta = DampingRatio;
        var d = x - target;
        if (zeta < 1)
        {
            var wd = omega * Math.Sqrt(1 - zeta * zeta);
            var decay = Math.Exp(-zeta * omega * dt);
            var c = Math.Cos(wd * dt);
            var s = Math.Sin(wd * dt);
            var b = (v + zeta * omega * d) / wd;
            var nd = decay * (d * c + b * s);
            var nv = decay * ((-zeta * omega) * (d * c + b * s) + (-d * wd * s + b * wd * c));
            return (target + nd, nv);
        }

        // Critically damped (or over): x(t) = (d + (v + w d) t) e^(-w t)
        var e = Math.Exp(-omega * dt);
        var k = v + omega * d;
        return (target + (d + k * dt) * e, (k - omega * (d + k * dt)) * e);
    }
}

/// <summary>
/// Shared motion vocabulary (see docs/design/DESIGN-PRINCIPLES.md, section 5). When the desktop asks
/// for reduced motion (GNOME's "Animations" switch off), <see cref="IsEnabled"/> is false and callers
/// switch to instant changes plus short fades.
/// </summary>
internal static class Motion
{
    /// <summary>The island shape morph: settles without a visible bounce.</summary>
    public static readonly Spring IslandSpring = new(0.84, TimeSpan.FromMilliseconds(80));

    /// <summary>Small UI elements (tab indicator).</summary>
    public static readonly Spring SnappySpring = new(0.9, TimeSpan.FromMilliseconds(45));

    public static readonly TimeSpan FadeOutDuration = TimeSpan.FromMilliseconds(110);
    public static readonly TimeSpan FadeInDuration = TimeSpan.FromMilliseconds(240);
    public static readonly TimeSpan FadeInDelay = TimeSpan.FromMilliseconds(70);

    /// <summary>Ease-out (0.16, 1, 0.3, 1): fast start, long soft landing.</summary>
    public static readonly Easing EaseOut = new SplineEasing(0.16, 1, 0.3, 1);

    public static readonly Easing EaseIn = new SplineEasing(0.4, 0, 1, 1);

    private static bool? s_enabled;

    public static bool IsEnabled => s_enabled ??= ReadEnabled();

    /// <summary>Fades an element in from a slight offset (content arriving after a morph or tab switch).</summary>
    public static void FadeIn(Visual element, TimeSpan? delay = null, double fromY = 0, double fromScale = 1, Point? origin = null)
    {
        var generation = Restart(element);
        var (scale, translate) = Transforms(element, origin);
        var enabled = IsEnabled;
        var moves = enabled && (fromY != 0 || fromScale != 1);
        var duration = enabled ? FadeInDuration : TimeSpan.FromMilliseconds(100);
        var wait = enabled ? delay ?? TimeSpan.Zero : TimeSpan.Zero;

        element.Opacity = 0;
        scale.ScaleX = scale.ScaleY = moves ? fromScale : 1;
        translate.Y = moves ? fromY : 0;
        Tweens.Start(element, generation, duration, wait, EaseOut, t => element.Opacity = t, null);
        if (moves)
        {
            Tweens.Start(element, generation, duration + TimeSpan.FromMilliseconds(80), wait, EaseOut, t =>
            {
                var s = fromScale + (1 - fromScale) * t;
                scale.ScaleX = scale.ScaleY = s;
                translate.Y = fromY * (1 - t);
            }, null);
        }
    }

    /// <summary>Fades an element out, then runs <paramref name="completed"/> (unless another animation took over).</summary>
    public static void FadeOut(Visual element, Action? completed, double toScale = 1, Point? origin = null)
    {
        var generation = Restart(element);
        var (scale, _) = Transforms(element, origin);
        var duration = IsEnabled ? FadeOutDuration : TimeSpan.FromMilliseconds(80);
        var fromOpacity = element.Opacity;
        var fromScale = scale.ScaleX;
        var scaling = IsEnabled && toScale != 1;
        Tweens.Start(element, generation, duration, TimeSpan.Zero, EaseIn, t =>
        {
            element.Opacity = fromOpacity * (1 - t);
            if (scaling)
            {
                scale.ScaleX = scale.ScaleY = fromScale + (toScale - fromScale) * t;
            }
        }, completed);
    }

    /// <summary>The element's scale and translate transforms (created once, reused by every fade).</summary>
    private static (ScaleTransform Scale, TranslateTransform Translate) Transforms(Visual element, Point? origin)
    {
        if (origin is { } o)
        {
            element.RenderTransformOrigin = new RelativePoint(o, RelativeUnit.Absolute);
        }

        if (element.RenderTransform is TransformGroup { Children: [ScaleTransform s, TranslateTransform t] })
        {
            return (s, t);
        }

        var scale = new ScaleTransform();
        var translate = new TranslateTransform();
        element.RenderTransform = new TransformGroup { Children = { scale, translate } };
        return (scale, translate);
    }

    /// <summary>A new fade replaces whatever fade the element was running.</summary>
    private static long Restart(Visual element)
    {
        var generation = Interlocked.Increment(ref s_generation);
        Generations.AddOrUpdate(element, generation);
        return generation;
    }

    internal static bool IsCurrent(Visual element, long generation) =>
        Generations.TryGetValue(element, out var current) && current == generation;

    private static readonly ConditionalWeakTable<Visual, object> GenerationBoxes = new();
    private static long s_generation;

    private static class Generations
    {
        public static void AddOrUpdate(Visual element, long generation) => GenerationBoxes.AddOrUpdate(element, generation);

        public static bool TryGetValue(Visual element, out long generation)
        {
            if (GenerationBoxes.TryGetValue(element, out var value) && value is long g)
            {
                generation = g;
                return true;
            }

            generation = 0;
            return false;
        }
    }

    /// <summary>
    /// A small tween engine on the UI thread: one timer at display rate, running only while a tween
    /// is in flight. Values are set as plain local values, so nothing lingers after an animation.
    /// </summary>
    private static class Tweens
    {
        private static readonly List<Tween> Active = [];
        private static readonly DispatcherTimer Timer = new(TimeSpan.FromMilliseconds(1000.0 / 60), DispatcherPriority.Render, (_, _) => Tick());
        private static readonly Stopwatch Clock = Stopwatch.StartNew();

        private sealed record Tween(Visual Owner, long Generation, TimeSpan Start, TimeSpan Duration, Easing Easing, Action<double> Apply, Action? Completed);

        public static void Start(Visual owner, long generation, TimeSpan duration, TimeSpan delay, Easing easing, Action<double> apply, Action? completed)
        {
            Active.Add(new Tween(owner, generation, Clock.Elapsed + delay, duration, easing, apply, completed));
            if (!Timer.IsEnabled)
            {
                Timer.Start();
            }
        }

        private static void Tick()
        {
            var now = Clock.Elapsed;
            for (var i = Active.Count - 1; i >= 0; i--)
            {
                var tween = Active[i];
                if (!IsCurrent(tween.Owner, tween.Generation))
                {
                    Active.RemoveAt(i); // replaced by a newer fade of the same element
                    continue;
                }

                if (now < tween.Start)
                {
                    continue;
                }

                var t = tween.Duration <= TimeSpan.Zero ? 1 : Math.Clamp((now - tween.Start) / tween.Duration, 0, 1);
                try
                {
                    tween.Apply(tween.Easing.Ease(t));
                }
                catch (Exception ex)
                {
                    Core.Diagnostics.Log.Debug("ui", "Tween failed", new { error = ex.GetType().Name });
                    t = 1;
                }

                if (t >= 1)
                {
                    Active.RemoveAt(i);
                    tween.Completed?.Invoke();
                }
            }

            if (Active.Count == 0)
            {
                Timer.Stop();
            }
        }
    }

    /// <summary>GNOME's org.gnome.desktop.interface enable-animations (true when unknown).</summary>
    private static bool ReadEnabled()
    {
        try
        {
            if (Environment.GetEnvironmentVariable("DI_REDUCED_MOTION") == "1")
            {
                return false;
            }

            var info = new System.Diagnostics.ProcessStartInfo("gsettings", "get org.gnome.desktop.interface enable-animations")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            using var process = System.Diagnostics.Process.Start(info);
            var output = process?.StandardOutput.ReadToEnd().Trim();
            process?.WaitForExit(1000);
            return output != "false";
        }
        catch
        {
            return true;
        }
    }
}
