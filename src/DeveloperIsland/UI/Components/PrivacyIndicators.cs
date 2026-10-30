using System.ComponentModel;
using DeveloperIsland.Core.Dictation;
using DeveloperIsland.UI.Animations;
using DeveloperIsland.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace DeveloperIsland.UI.Components;

/// <summary>
/// Small vector marks for a microphone (orange) and a camera (green) in use, the colors people know
/// from phone status bars. Collapsed while nothing is in use; fades in when a device starts. With the
/// WhisperFlow integration on, a short label follows the microphone: "WhisperFlow" while it records,
/// a small spinner while it transcribes, then the result for a few seconds.
/// </summary>
public sealed class PrivacyIndicators : UserControl
{
    public static readonly DependencyProperty SourceProperty = DependencyProperty.Register(
        nameof(Source), typeof(PrivacyViewModel), typeof(PrivacyIndicators), new PropertyMetadata(null, (d, e) => ((PrivacyIndicators)d).Attach(e.OldValue as PrivacyViewModel)));

    // Segoe Fluent Icons: Microphone and Video (vector glyphs, drawn in the status colors).
    private readonly FontIcon _microphone = Icon("", "StatusWarningColor");
    private readonly FontIcon _camera = Icon("", "StatusGoodColor");
    // WhisperFlow: a spinner while it transcribes, then CheckMark, MicOff or Error for its result.
    private readonly ProgressRing _transcribing = new()
    {
        IsActive = false,
        MinWidth = 0,
        MinHeight = 0,
        Foreground = new SolidColorBrush((Windows.UI.Color)Application.Current.Resources["AccentColor"]),
        VerticalAlignment = VerticalAlignment.Center,
        Visibility = Visibility.Collapsed,
    };

    private readonly FontIcon _done = Icon("", "StatusGoodColor");
    private readonly FontIcon _noSpeech = Icon("", "TextSecondaryColor");
    private readonly FontIcon _failed = Icon("", "StatusCriticalColor");
    private readonly TextBlock _dictation = new()
    {
        FontFamily = (FontFamily)Application.Current.Resources["TextFont"],
        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        CharacterSpacing = -8,
        Foreground = new SolidColorBrush((Windows.UI.Color)Application.Current.Resources["TextSecondaryColor"]),
        TextWrapping = TextWrapping.NoWrap,
        VerticalAlignment = VerticalAlignment.Center,
        Visibility = Visibility.Collapsed,
    };

    private bool _shown;

    public PrivacyIndicators()
    {
        IsTabStop = false;
        Visibility = Visibility.Collapsed;
        Content = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 5,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { _microphone, _transcribing, _done, _noSpeech, _failed, _dictation, _camera },
        };
        RegisterPropertyChangedCallback(FontSizeProperty, (_, _) =>
        {
            _microphone.FontSize = _camera.FontSize = _done.FontSize = _noSpeech.FontSize = _failed.FontSize = FontSize;
            _transcribing.Width = _transcribing.Height = FontSize;
            _dictation.FontSize = FontSize;
        });
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

    private static Visibility Show(bool visible) => visible ? Visibility.Visible : Visibility.Collapsed;

    private void Update()
    {
        var source = Source;
        var active = source?.IsActive ?? false;
        var dictation = source?.Dictation ?? DictationPhase.Idle;
        _microphone.Visibility = Show(source?.Microphone ?? false);
        _camera.Visibility = Show(source?.Camera ?? false);
        _transcribing.Visibility = Show(dictation == DictationPhase.Transcribing);
        _transcribing.IsActive = dictation == DictationPhase.Transcribing;
        _done.Visibility = Show(dictation == DictationPhase.Done);
        _noSpeech.Visibility = Show(dictation == DictationPhase.NoSpeech);
        _failed.Visibility = Show(dictation == DictationPhase.Failed);
        _dictation.Text = source?.DictationLabel ?? string.Empty;
        _dictation.Visibility = Show(dictation != DictationPhase.Idle);
        Visibility = Show(active);
        var description = source?.Description ?? string.Empty;
        AutomationProperties.SetName(this, description);
        ToolTipService.SetToolTip(this, active ? description : null);

        if (active && !_shown)
        {
            Motion.FadeIn(this, delay: TimeSpan.Zero, fromScale: 0.8f);
        }

        _shown = active;
    }
}
