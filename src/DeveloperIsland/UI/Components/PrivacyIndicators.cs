using System.ComponentModel;
using DeveloperIsland.UI.Animations;
using DeveloperIsland.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace DeveloperIsland.UI.Components;

/// <summary>
/// Small vector marks for a microphone (orange) and a camera (green) in use, the colors people know
/// from phone status bars. Collapsed while nothing is in use; fades in when a device starts.
/// </summary>
public sealed class PrivacyIndicators : UserControl
{
    public static readonly DependencyProperty SourceProperty = DependencyProperty.Register(
        nameof(Source), typeof(PrivacyViewModel), typeof(PrivacyIndicators), new PropertyMetadata(null, (d, e) => ((PrivacyIndicators)d).Attach(e.OldValue as PrivacyViewModel)));

    // Segoe Fluent Icons: Microphone and Video (vector glyphs, drawn in the status colors).
    private readonly FontIcon _microphone = Icon("", "StatusWarningColor");
    private readonly FontIcon _camera = Icon("", "StatusGoodColor");
    private bool _shown;

    public PrivacyIndicators()
    {
        IsTabStop = false;
        Visibility = Visibility.Collapsed;
        Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5, VerticalAlignment = VerticalAlignment.Center, Children = { _microphone, _camera } };
        RegisterPropertyChangedCallback(FontSizeProperty, (_, _) => _microphone.FontSize = _camera.FontSize = FontSize);
        FontSize = 12;
    }

    public PrivacyViewModel? Source
    {
        get => (PrivacyViewModel?)GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    private static FontIcon Icon(string glyph, string colorKey) => new()
    {
        Glyph = glyph,
        FontFamily = (FontFamily)Application.Current.Resources["IconFont"],
        Foreground = new SolidColorBrush((Windows.UI.Color)Application.Current.Resources[colorKey]),
        VerticalAlignment = VerticalAlignment.Center,
    };

    private void Attach(PrivacyViewModel? old)
    {
        if (old is not null)
        {
            old.PropertyChanged -= OnChanged;
        }

        if (Source is { } source)
        {
            source.PropertyChanged += OnChanged;
        }

        Update();
    }

    private void OnChanged(object? sender, PropertyChangedEventArgs e) => Update();

    private void Update()
    {
        var state = Source?.State ?? default;
        _microphone.Visibility = state.Microphone ? Visibility.Visible : Visibility.Collapsed;
        _camera.Visibility = state.Camera ? Visibility.Visible : Visibility.Collapsed;
        Visibility = state.IsActive ? Visibility.Visible : Visibility.Collapsed;
        AutomationProperties.SetName(this, state.Description);
        ToolTipService.SetToolTip(this, state.IsActive ? state.Description : null);

        if (state.IsActive && !_shown)
        {
            Motion.FadeIn(this, delay: TimeSpan.Zero, fromScale: 0.8f);
        }

        _shown = state.IsActive;
    }
}
