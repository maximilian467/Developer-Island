using DeveloperIsland.Core.Modules;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Path = Microsoft.UI.Xaml.Shapes.Path;

namespace DeveloperIsland.UI.Components;

/// <summary>
/// The icon of a module, 16 DIP. Fluent glyphs where one fits; Git and GitHub have none, so they
/// get simple drawn marks (a branch, and a pull request) on the same 16-unit grid and stroke weight.
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
        ModuleId.Claude or ModuleId.Codex => "", // sparkle: AI usage
        ModuleId.Music => "",
        ModuleId.Focus => "",
        ModuleId.Calendar => "",
        ModuleId.Tasks => "",
        ModuleId.System => "",
        _ => null,
    };

    private void Build()
    {
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
