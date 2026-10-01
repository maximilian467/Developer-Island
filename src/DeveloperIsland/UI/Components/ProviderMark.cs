using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Path = Microsoft.UI.Xaml.Shapes.Path;

namespace DeveloperIsland.UI.Components;

/// <summary>
/// Neutral identifier marks for the AI tools. The official Claude and OpenAI logos are trademarks
/// that third-party apps may not bundle without permission, so the island uses simple geometric
/// marks in its own language instead: an open "C" ring for Claude Code and a hexagon ring for
/// Codex. Both are stroked outlines on a 16 × 16 grid with the same weight, so they read as one
/// family and stay legible at 12 px. (The earlier four-point spark looked too much like Gemini.)
/// </summary>
public sealed class ProviderMark : UserControl
{
    // Open ring, opening to the right (a "C"): 280° of a circle with round caps.
    private const string CPath = "M12.3,4.4 A5.6,5.6 0 1 0 12.3,11.6";

    // Hexagon ring (pointy-top), stroked.
    private const string HexPath = "M8,1.6 L13.6,4.8 L13.6,11.2 L8,14.4 L2.4,11.2 L2.4,4.8 Z";

    public static readonly DependencyProperty KindProperty = DependencyProperty.Register(
        nameof(Kind), typeof(string), typeof(ProviderMark), new PropertyMetadata("Claude", (d, _) => ((ProviderMark)d).Render()));

    public ProviderMark()
    {
        IsTabStop = false;
        RegisterPropertyChangedCallback(ForegroundProperty, (_, _) => Render());
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
            Data = (Geometry)XamlBindingHelper.ConvertValue(typeof(Geometry), isCodex ? HexPath : CPath),
            Stretch = Stretch.None,
            Stroke = brush,
            StrokeThickness = isCodex ? 1.6 : 1.8,
            StrokeLineJoin = PenLineJoin.Round,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
        };

        Content = new Viewbox { Child = new Grid { Width = 16, Height = 16, Children = { path } } };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAccessibilityView(this, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
    }
}
