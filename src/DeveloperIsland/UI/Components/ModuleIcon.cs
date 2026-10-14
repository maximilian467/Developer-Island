using DeveloperIsland.Core.Modules;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Path = Microsoft.UI.Xaml.Shapes.Path;

namespace DeveloperIsland.UI.Components;

/// <summary>
/// The icon of a module, 16 DIP. Fluent glyphs where one fits; Git, GitHub and Awake have none, so
/// they get simple drawn marks (a branch, a pull request and a coffee cup) on the same 16-unit grid
/// and stroke weight.
/// Foreground follows the control's Foreground, so tabs can dim unselected icons.
/// </summary>
public sealed class ModuleIcon : UserControl
{
    public static readonly DependencyProperty ModuleProperty =
        DependencyProperty.Register(nameof(Module), typeof(ModuleId), typeof(ModuleIcon), new PropertyMetadata(ModuleId.Claude, (d, _) => ((ModuleIcon)d).Build()));

    public ModuleIcon()
    {
        Width = 16;
        Height = 16;
        IsTabStop = false;
        Build();
    }

    public ModuleId Module
    {
        get => (ModuleId)GetValue(ModuleProperty);
        set => SetValue(ModuleProperty, value);
    }

    /// <summary>Segoe Fluent Icons code point, or null for drawn icons.</summary>
    public static string? Glyph(ModuleId module) => module switch
    {
        ModuleId.Music => "",
        ModuleId.Focus => "",
        ModuleId.Calendar => "",
        ModuleId.Tasks => "",
        ModuleId.System => "",
        _ => null,
    };

    private void Build()
    {
        // The AI tools use their provider marks (see ProviderMark).
        if (Module is ModuleId.Claude or ModuleId.Codex)
        {
            var mark = new ProviderMark { Kind = Module == ModuleId.Claude ? "Claude" : "Codex", Width = 14, Height = 14 };
            mark.SetBinding(ForegroundProperty, new Microsoft.UI.Xaml.Data.Binding { Source = this, Path = new PropertyPath(nameof(Foreground)) });
            Content = mark;
            return;
        }

        if (Glyph(Module) is { } glyph)
        {
            var icon = new FontIcon
            {
                Glyph = glyph,
                FontSize = 15,
                FontFamily = (FontFamily)Application.Current.Resources["IconFont"],
            };
            icon.SetBinding(IconElement.ForegroundProperty, new Microsoft.UI.Xaml.Data.Binding { Source = this, Path = new PropertyPath(nameof(Foreground)) });
            Content = icon;
            return;
        }

        var canvas = new Canvas { Width = 16, Height = 16 };
        void Stroke(string data)
        {
            var path = new Path
            {
                Data = (Geometry)Microsoft.UI.Xaml.Markup.XamlBindingHelper.ConvertValue(typeof(Geometry), data),
                StrokeThickness = 1.4,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                StrokeLineJoin = PenLineJoin.Round,
            };
            path.SetBinding(Shape.StrokeProperty, new Microsoft.UI.Xaml.Data.Binding { Source = this, Path = new PropertyPath(nameof(Foreground)) });
            canvas.Children.Add(path);
        }

        if (Module == ModuleId.Git)
        {
            // Branch: a trunk with a side branch merging back in.
            Stroke("M4.5,5.2 L4.5,10.8");
            Stroke("M11.5,5.2 C11.5,8.6 4.5,7.6 4.5,10.4");
            Stroke("M4.5,1.6 A1.8,1.8 0 1 1 4.49,1.6 Z");
            Stroke("M4.5,10.8 A1.8,1.8 0 1 1 4.49,10.8 Z");
            Stroke("M11.5,1.6 A1.8,1.8 0 1 1 11.49,1.6 Z");
        }
        else if (Module == ModuleId.Awake)
        {
            // Coffee cup: a rounded cup with a handle and two wisps of steam.
            Stroke("M2.6,6.6 L11,6.6 L11,10.2 C11,12.3 9.5,13.8 7.4,13.8 L6.2,13.8 C4.1,13.8 2.6,12.3 2.6,10.2 Z");
            Stroke("M11,7.8 L12.1,7.8 C13.3,7.8 14.1,8.6 14.1,9.6 C14.1,10.6 13.3,11.4 12.1,11.4 L11,11.4");
            Stroke("M5.4,1.8 C4.5,2.7 6.3,3.5 5.4,4.4");
            Stroke("M8.4,1.8 C7.5,2.7 9.3,3.5 8.4,4.4");
        }
        else
        {
            // Pull request: a branch with an arrow back to the base.
            Stroke("M4.5,5.2 L4.5,10.8");
            Stroke("M4.5,1.6 A1.8,1.8 0 1 1 4.49,1.6 Z");
            Stroke("M4.5,10.8 A1.8,1.8 0 1 1 4.49,10.8 Z");
            Stroke("M11.5,10.8 A1.8,1.8 0 1 1 11.49,10.8 Z");
            Stroke("M11.5,10.8 L11.5,6.4 C11.5,4.6 10.6,3.4 8.6,3.4 L7.4,3.4");
            Stroke("M8.9,1.9 L7.4,3.4 L8.9,4.9");
        }

        Content = new Viewbox { Width = 16, Height = 16, Stretch = Stretch.None, Child = canvas };
    }
}
