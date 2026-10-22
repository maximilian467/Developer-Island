using System.ComponentModel;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using Avalonia.Media;
using DeveloperIsland.Core.Formatting;
using DeveloperIsland.Core.SystemInfo;
using DeveloperIsland.UI.Animations;
using DeveloperIsland.ViewModels;

namespace DeveloperIsland.UI.Components;

/// <summary>
/// A compact label whose width does not jitter while its value ticks: an invisible copy of the
/// widest form (<see cref="CompactWidth.Reserve"/>) holds the slot, the real text sits on top with
/// tabular figures. Long text still ends in an ellipsis at MaxWidth.
/// </summary>
public sealed class StableText : UserControl
{
    public static readonly StyledProperty<string?> TextProperty = AvaloniaProperty.Register<StableText, string?>(nameof(Text), string.Empty);

    public static readonly StyledProperty<bool> AlignRightProperty = AvaloniaProperty.Register<StableText, bool>(nameof(AlignRight));

    private static readonly FontFeatureCollection Tabular = [FontFeature.Parse("tnum")];
    private readonly TextBlock _reserve = new() { Opacity = 0 };
    private readonly TextBlock _text = new();

    public StableText()
    {
        foreach (var block in new[] { _reserve, _text })
        {
            block.Classes.Add("label");
            block.FontFeatures = Tabular;
        }

        _text.Bind(TextBlock.ForegroundProperty, this.GetObservable(ForegroundProperty));
        Content = new Panel { Children = { _reserve, _text } };
        VerticalAlignment = VerticalAlignment.Center;
        Update();
    }

    public string? Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    /// <summary>Right-aligns the text in its slot (trailing values).</summary>
    public bool AlignRight
    {
        get => GetValue(AlignRightProperty);
        set => SetValue(AlignRightProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == TextProperty)
        {
            Update();
        }
        else if (change.Property == AlignRightProperty)
        {
            _text.HorizontalAlignment = AlignRight ? HorizontalAlignment.Right : HorizontalAlignment.Left;
        }
    }

    private void Update()
    {
        _text.Text = Text ?? string.Empty;
        _reserve.Text = CompactWidth.Reserve(_text.Text, CultureInfo.CurrentCulture.NumberFormat.NumberDecimalSeparator);
    }
}

/// <summary>The three hardware level colors from the theme, as colors and shared brushes.</summary>
public static class StatusColors
{
    private static readonly Dictionary<SystemLevel, SolidColorBrush> Brushes = [];

    public static Color Color(SystemLevel level) => Resource<Color>(level switch
    {
        SystemLevel.Critical => "StatusCriticalColor",
        SystemLevel.Warning => "StatusWarningColor",
        _ => "StatusGoodColor",
    });

    public static SolidColorBrush Brush(SystemLevel level)
    {
        if (!Brushes.TryGetValue(level, out var brush))
        {
            Brushes[level] = brush = new SolidColorBrush(Color(level));
        }

        return brush;
    }

    /// <summary>Text keeps its normal color while good and takes the level color only as a warning.</summary>
    public static IBrush Text(SystemLevel level, string normalKey) =>
        level == SystemLevel.Good ? Resource<IBrush>(normalKey) : Brush(level);

    public static T Resource<T>(string key) =>
        Application.Current!.TryGetResource(key, null, out var value) && value is T typed ? typed : throw new KeyNotFoundException(key);
}

/// <summary>
/// The notch's quiet form of the privacy marks: a 4 DIP orange dot for the microphone and a green
/// one for the camera, side by side. No icons, no text, and the notch keeps its size.
/// </summary>
public sealed class PrivacyDots : UserControl
{
    public const double DotSize = 4;

    public static readonly StyledProperty<PrivacyViewModel?> SourceProperty = AvaloniaProperty.Register<PrivacyDots, PrivacyViewModel?>(nameof(Source));

    private readonly Avalonia.Controls.Shapes.Ellipse _microphone = Dot("StatusWarningColor");
    private readonly Avalonia.Controls.Shapes.Ellipse _camera = Dot("StatusGoodColor");
    private bool _shown;

    public PrivacyDots()
    {
        IsHitTestVisible = false;
        IsVisible = false;
        Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = DotSize, Children = { _microphone, _camera } };
    }

    public PrivacyViewModel? Source
    {
        get => GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == SourceProperty)
        {
            if (change.OldValue is PrivacyViewModel old)
            {
                old.PropertyChanged -= OnChanged;
            }

            if (change.NewValue is PrivacyViewModel source)
            {
                source.PropertyChanged += OnChanged;
            }

            Update();
        }
    }

    private static Avalonia.Controls.Shapes.Ellipse Dot(string colorKey) => new()
    {
        Width = DotSize,
        Height = DotSize,
        Fill = new SolidColorBrush(StatusColors.Resource<Color>(colorKey)),
    };

    private void OnChanged(object? sender, PropertyChangedEventArgs e) => Update();

    private void Update()
    {
        var state = Source?.State ?? default;
        _microphone.IsVisible = state.Microphone;
        _camera.IsVisible = state.Camera;
        IsVisible = state.IsActive;
        if (state.IsActive && !_shown)
        {
            Motion.FadeIn(this, delay: TimeSpan.Zero);
        }

        _shown = state.IsActive;
    }
}

/// <summary>
/// Small vector marks for a microphone (orange) and a camera (green) in use, the colors people know
/// from phone status bars. Collapsed while nothing is in use; fades in when a device starts.
/// </summary>
public sealed class PrivacyIndicators : UserControl
{
    public static readonly StyledProperty<PrivacyViewModel?> SourceProperty = AvaloniaProperty.Register<PrivacyIndicators, PrivacyViewModel?>(nameof(Source));

    public static readonly StyledProperty<double> IconSizeProperty = AvaloniaProperty.Register<PrivacyIndicators, double>(nameof(IconSize), 12);

    private readonly GlyphIcon _microphone = Icon("", "StatusWarningColor");
    private readonly GlyphIcon _camera = Icon("", "StatusGoodColor");
    private bool _shown;

    public PrivacyIndicators()
    {
        IsVisible = false;
        VerticalAlignment = VerticalAlignment.Center;
        Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5, VerticalAlignment = VerticalAlignment.Center, Children = { _microphone, _camera } };
    }

    public PrivacyViewModel? Source
    {
        get => GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    public double IconSize
    {
        get => GetValue(IconSizeProperty);
        set => SetValue(IconSizeProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == SourceProperty)
        {
            if (change.OldValue is PrivacyViewModel old)
            {
                old.PropertyChanged -= OnChanged;
            }

            if (change.NewValue is PrivacyViewModel source)
            {
                source.PropertyChanged += OnChanged;
            }

            Update();
        }
        else if (change.Property == IconSizeProperty)
        {
            _microphone.Size = _camera.Size = IconSize;
        }
    }

    private static GlyphIcon Icon(string glyph, string colorKey) => new()
    {
        Glyph = glyph,
        Size = 12,
        Foreground = new SolidColorBrush(StatusColors.Resource<Color>(colorKey)),
        VerticalAlignment = VerticalAlignment.Center,
    };

    private void OnChanged(object? sender, PropertyChangedEventArgs e) => Update();

    private void Update()
    {
        var state = Source?.State ?? default;
        _microphone.IsVisible = state.Microphone;
        _camera.IsVisible = state.Camera;
        IsVisible = state.IsActive;
        ToolTip.SetTip(this, state.IsActive ? state.Description : null);
        if (state.IsActive && !_shown)
        {
            Motion.FadeIn(this, delay: TimeSpan.Zero, fromScale: 0.8);
        }

        _shown = state.IsActive;
    }
}

/// <summary>A sensor row's value color: its level color, or the normal text color while good.</summary>
public sealed class LevelToTextBrush : Avalonia.Data.Converters.IValueConverter
{
    public static readonly LevelToTextBrush Primary = new("TextPrimaryBrush");

    private readonly string _normalKey;

    private LevelToTextBrush(string normalKey) => _normalKey = normalKey;

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is SystemLevel level ? StatusColors.Text(level, _normalKey) : StatusColors.Resource<IBrush>(_normalKey);

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
