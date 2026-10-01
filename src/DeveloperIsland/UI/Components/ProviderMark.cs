using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Path = Microsoft.UI.Xaml.Shapes.Path;

namespace DeveloperIsland.UI.Components;

/// <summary>
/// The mark of an AI tool: the Claude mark for Claude Code and the OpenAI mark for Codex (see
/// <see cref="BrandMarks"/>), drawn monochrome in the island's foreground color like every other
/// icon, on a 24-unit grid scaled to the control's size.
/// </summary>
public sealed class ProviderMark : UserControl
{
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
            Data = (Geometry)XamlBindingHelper.ConvertValue(typeof(Geometry), isCodex ? BrandMarks.OpenAI : BrandMarks.Claude),
            Fill = brush,
            Stretch = Stretch.None,
        };

        Content = new Viewbox { Child = new Grid { Width = 24, Height = 24, Children = { path } } };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAccessibilityView(this, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
    }
}
