using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Path = Microsoft.UI.Xaml.Shapes.Path;

namespace DeveloperIsland.UI.Components;

/// <summary>
/// Neutral identifier marks for the AI tools. The official Claude and OpenAI logos are trademarks
/// that third-party apps may not bundle without permission, so the island uses simple geometric
/// marks in its own language instead: a four-point spark for Claude Code and a hexagon for Codex.
/// Both are drawn on a 16 × 16 grid with the same visual weight.
/// </summary>
public sealed class ProviderMark : UserControl
{
    // Concave four-point spark, slightly tapered so it reads at 12 px.
    private const string SparkPath =
        "M8,0.8 C8.55,5.1 10.9,7.45 15.2,8 C10.9,8.55 8.55,10.9 8,15.2 C7.45,10.9 5.1,8.55 0.8,8 C5.1,7.45 7.45,5.1 8,0.8 Z";

    // Hexagon ring (pointy-top), stroked.
    private const string HexPath = "M8,1.6 L13.6,4.8 L13.6,11.2 L8,14.4 L2.4,11.2 L2.4,4.8 Z";

    public static readonly DependencyProperty KindProperty = DependencyProperty.Register(
        nameof(Kind), typeof(string), typeof(ProviderMark), new PropertyMetadata("Claude", (d, _) => ((ProviderMark)d).Render()));

    public ProviderMark()
    {
        IsTabStop = false;
        Render();
    }

    /// <summary>"Claude" or "Codex".</summary>
    public string Kind
    {
        get => (string)GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }

    private void Render()
    {
        var brush = Foreground ?? (Brush)Application.Current.Resources["TextPrimaryBrush"];
        var isCodex = string.Equals(Kind, "Codex", StringComparison.OrdinalIgnoreCase);
        var path = new Path
        {
            Data = (Geometry)XamlBindingHelper.ConvertValue(typeof(Geometry), isCodex ? HexPath : SparkPath),
            Stretch = Stretch.None,
        };

        if (isCodex)
        {
            path.Stroke = brush;
            path.StrokeThickness = 1.6;
            path.StrokeLineJoin = PenLineJoin.Round;
        }
        else
        {
            path.Fill = brush;
        }

        Content = new Viewbox { Child = new Grid { Width = 16, Height = 16, Children = { path } } };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAccessibilityView(this, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
    }
}
