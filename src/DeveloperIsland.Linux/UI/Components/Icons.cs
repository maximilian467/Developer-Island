using System.Collections.Concurrent;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using DeveloperIsland.Core.Modules;

namespace DeveloperIsland.UI.Components;

/// <summary>Parsed geometries, shared by every icon that draws the same path.</summary>
internal static class Geometries
{
    private static readonly ConcurrentDictionary<string, Geometry> Cache = new();

    public static Geometry Get(string data) => Cache.GetOrAdd(data, Geometry.Parse);
}

/// <summary>
/// A glyph icon: the shared view models name icons by their Segoe Fluent Icons code point; this draws
/// the matching open-licensed vector (<see cref="GlyphPaths"/>) at <see cref="Size"/> DIPs in the
/// inherited foreground, like a FontIcon would.
/// </summary>
public sealed class GlyphIcon : Control
{
    public static readonly StyledProperty<string?> GlyphProperty = AvaloniaProperty.Register<GlyphIcon, string?>(nameof(Glyph));

    public static readonly StyledProperty<double> SizeProperty = AvaloniaProperty.Register<GlyphIcon, double>(nameof(Size), 14);

    public static readonly StyledProperty<IBrush?> ForegroundProperty = TextElement.ForegroundProperty.AddOwner<GlyphIcon>();

    static GlyphIcon()
    {
        AffectsRender<GlyphIcon>(GlyphProperty, ForegroundProperty, SizeProperty);
        AffectsMeasure<GlyphIcon>(SizeProperty);
        IsHitTestVisibleProperty.OverrideDefaultValue<GlyphIcon>(false);
    }

    public string? Glyph
    {
        get => GetValue(GlyphProperty);
        set => SetValue(GlyphProperty, value);
    }

    public double Size
    {
        get => GetValue(SizeProperty);
        set => SetValue(SizeProperty, value);
    }

    public IBrush? Foreground
    {
        get => GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize) => new(Size, Size);

    public override void Render(DrawingContext context)
    {
        if (GlyphPaths.For(Glyph) is not { } data)
        {
            return;
        }

        var scale = Size / 24.0;
        var x = (Bounds.Width - Size) / 2;
        var y = (Bounds.Height - Size) / 2;
        using (context.PushTransform(Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation(x, y)))
        {
            context.DrawGeometry(Foreground ?? Brushes.White, null, Geometries.Get(data));
        }
    }
}

/// <summary>
/// The mark of an AI tool: the Claude mark for Claude Code and the OpenAI mark for Codex (see
/// <see cref="BrandMarks"/>), drawn monochrome in the foreground color on a 24-unit grid scaled to the
/// control's size.
/// </summary>
public sealed class ProviderMark : Control
{
    public static readonly StyledProperty<string> KindProperty = AvaloniaProperty.Register<ProviderMark, string>(nameof(Kind), "Claude");

    public static readonly StyledProperty<IBrush?> ForegroundProperty = TextElement.ForegroundProperty.AddOwner<ProviderMark>();

    static ProviderMark()
    {
        AffectsRender<ProviderMark>(KindProperty, ForegroundProperty);
        IsHitTestVisibleProperty.OverrideDefaultValue<ProviderMark>(false);
    }

    /// <summary>"Claude" or "Codex".</summary>
    public string Kind
    {
        get => GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }

    public IBrush? Foreground
    {
        get => GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        var isCodex = string.Equals(Kind, "Codex", StringComparison.OrdinalIgnoreCase);
        var size = Math.Min(Bounds.Width, Bounds.Height);
        if (size <= 0)
        {
            return;
        }

        var scale = size / 24.0;
        using (context.PushTransform(Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation((Bounds.Width - size) / 2, (Bounds.Height - size) / 2)))
        {
            context.DrawGeometry(Foreground ?? Brushes.White, null, Geometries.Get(isCodex ? BrandMarks.OpenAI : BrandMarks.Claude));
        }
    }
}

/// <summary>
/// The icon of a module, 16 DIP. Glyphs where one fits; Git, GitHub and Awake get simple drawn marks
/// (a branch, a pull request and a coffee cup) on the same 16-unit grid and stroke weight; Claude and
/// Codex use their provider marks. Foreground follows the inherited foreground, so tabs can dim icons.
/// </summary>
public sealed class ModuleIcon : Control
{
    public static readonly StyledProperty<ModuleId> ModuleProperty = AvaloniaProperty.Register<ModuleIcon, ModuleId>(nameof(Module), ModuleId.Claude);

    public static readonly StyledProperty<IBrush?> ForegroundProperty = TextElement.ForegroundProperty.AddOwner<ModuleIcon>();

    private static readonly string[] GitStrokes =
    [
        "M4.5,5.2 L4.5,10.8",
        "M11.5,5.2 C11.5,8.6 4.5,7.6 4.5,10.4",
        "M6.3,3.4 A1.8,1.8 0 1 1 2.7,3.4 A1.8,1.8 0 1 1 6.3,3.4 Z",
        "M6.3,12.6 A1.8,1.8 0 1 1 2.7,12.6 A1.8,1.8 0 1 1 6.3,12.6 Z",
        "M13.3,3.4 A1.8,1.8 0 1 1 9.7,3.4 A1.8,1.8 0 1 1 13.3,3.4 Z",
    ];

    private static readonly string[] AwakeStrokes =
    [
        "M2.6,6.6 L11,6.6 L11,10.2 C11,12.3 9.5,13.8 7.4,13.8 L6.2,13.8 C4.1,13.8 2.6,12.3 2.6,10.2 Z",
        "M11,7.8 L12.1,7.8 C13.3,7.8 14.1,8.6 14.1,9.6 C14.1,10.6 13.3,11.4 12.1,11.4 L11,11.4",
        "M5.4,1.8 C4.5,2.7 6.3,3.5 5.4,4.4",
        "M8.4,1.8 C7.5,2.7 9.3,3.5 8.4,4.4",
    ];

    private static readonly string[] GitHubStrokes =
    [
        "M4.5,5.2 L4.5,10.8",
        "M6.3,3.4 A1.8,1.8 0 1 1 2.7,3.4 A1.8,1.8 0 1 1 6.3,3.4 Z",
        "M6.3,12.6 A1.8,1.8 0 1 1 2.7,12.6 A1.8,1.8 0 1 1 6.3,12.6 Z",
        "M13.3,12.6 A1.8,1.8 0 1 1 9.7,12.6 A1.8,1.8 0 1 1 13.3,12.6 Z",
        "M11.5,10.8 L11.5,6.4 C11.5,4.6 10.6,3.4 8.6,3.4 L7.4,3.4",
        "M8.9,1.9 L7.4,3.4 L8.9,4.9",
    ];

    static ModuleIcon()
    {
        AffectsRender<ModuleIcon>(ModuleProperty, ForegroundProperty);
        IsHitTestVisibleProperty.OverrideDefaultValue<ModuleIcon>(false);
    }

    public ModuleIcon()
    {
        Width = 16;
        Height = 16;
    }

    public ModuleId Module
    {
        get => GetValue(ModuleProperty);
        set => SetValue(ModuleProperty, value);
    }

    public IBrush? Foreground
    {
        get => GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    /// <summary>The glyph for modules that have one, or null for drawn icons.</summary>
    public static string? Glyph(ModuleId module) => module switch
    {
        ModuleId.Music => "",
        ModuleId.Focus => "",
        ModuleId.Calendar => "",
        ModuleId.Tasks => "",
        ModuleId.System => "",
        _ => null,
    };

    public override void Render(DrawingContext context)
    {
        var brush = Foreground ?? Brushes.White;
        var ox = (Bounds.Width - 16) / 2;
        var oy = (Bounds.Height - 16) / 2;
        if (Module is ModuleId.Claude or ModuleId.Codex)
        {
            // 14 DIP mark centered in the 16 DIP slot.
            var s = 14 / 24.0;
            using (context.PushTransform(Matrix.CreateScale(s, s) * Matrix.CreateTranslation(ox + 1, oy + 1)))
            {
                context.DrawGeometry(brush, null, Geometries.Get(Module == ModuleId.Codex ? BrandMarks.OpenAI : BrandMarks.Claude));
            }

            return;
        }

        if (Glyph(Module) is { } glyph && GlyphPaths.For(glyph) is { } data)
        {
            // 15 DIP glyph, as on Windows (FontSize 15).
            var s = 15 / 24.0;
            using (context.PushTransform(Matrix.CreateScale(s, s) * Matrix.CreateTranslation(ox + 0.5, oy + 0.5)))
            {
                context.DrawGeometry(brush, null, Geometries.Get(data));
            }

            return;
        }

        var strokes = Module switch
        {
            ModuleId.Git => GitStrokes,
            ModuleId.Awake => AwakeStrokes,
            _ => GitHubStrokes,
        };
        var pen = new Pen(brush, 1.4, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
        using (context.PushTransform(Matrix.CreateTranslation(ox, oy)))
        {
            foreach (var stroke in strokes)
            {
                context.DrawGeometry(null, pen, Geometries.Get(stroke));
            }
        }
    }
}
