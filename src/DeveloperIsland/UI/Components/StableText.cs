using System.Globalization;
using DeveloperIsland.Core.Formatting;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;

namespace DeveloperIsland.UI.Components;

/// <summary>
/// A compact label whose width does not jitter while its value ticks: an invisible copy of the
/// widest form (<see cref="CompactWidth.Reserve"/>) holds the slot, the real text sits on top with
/// tabular figures. Long text still ends in an ellipsis at <see cref="FrameworkElement.MaxWidth"/>.
/// </summary>
public sealed class StableText : UserControl
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(StableText), new PropertyMetadata(string.Empty, (d, _) => ((StableText)d).Update()));

    private readonly TextBlock _reserve = new() { Opacity = 0 };
    private readonly TextBlock _text = new();

    public StableText()
    {
        IsTabStop = false;
        var style = (Style)Application.Current.Resources["LabelTextStyle"];
        foreach (var block in new[] { _reserve, _text })
        {
            block.Style = style;
            Typography.SetNumeralAlignment(block, FontNumeralAlignment.Tabular);
        }

        AutomationProperties.SetAccessibilityView(_reserve, AccessibilityView.Raw);
        Content = new Grid { Children = { _reserve, _text } };
        RegisterPropertyChangedCallback(ForegroundProperty, (_, _) => _text.Foreground = Foreground);
    }

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    /// <summary>Right-aligns the text in its slot (trailing values).</summary>
    public bool AlignRight
    {
        get => _text.HorizontalAlignment == HorizontalAlignment.Right;
        set => _text.HorizontalAlignment = value ? HorizontalAlignment.Right : HorizontalAlignment.Left;
    }

    private void Update()
    {
        _text.Text = Text ?? string.Empty;
        _reserve.Text = CompactWidth.Reserve(_text.Text, CultureInfo.CurrentCulture.NumberFormat.NumberDecimalSeparator);
        AutomationProperties.SetName(this, _text.Text);
    }
}
