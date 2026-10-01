using System.ComponentModel;
using DeveloperIsland.UI.Animations;
using DeveloperIsland.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;

namespace DeveloperIsland.UI.Components;

/// <summary>
/// The notch's quiet form of the privacy marks: a 4 DIP orange dot for the microphone and a green
/// one for the camera, side by side. No icons, no text, and the notch keeps its size.
/// </summary>
public sealed class PrivacyDots : UserControl
{
    public const double DotSize = 4;

    public static readonly DependencyProperty SourceProperty = DependencyProperty.Register(
        nameof(Source), typeof(PrivacyViewModel), typeof(PrivacyDots), new PropertyMetadata(null, (d, e) => ((PrivacyDots)d).Attach(e.OldValue as PrivacyViewModel)));

    private readonly Ellipse _microphone = Dot("StatusWarningColor");
    private readonly Ellipse _camera = Dot("StatusGoodColor");
    private bool _shown;

    public PrivacyDots()
    {
        IsTabStop = false;
        IsHitTestVisible = false;
        Visibility = Visibility.Collapsed;
        Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = DotSize, Children = { _microphone, _camera } };
    }

    public PrivacyViewModel? Source
    {
        get => (PrivacyViewModel?)GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    private static Ellipse Dot(string colorKey) => new()
    {
        Width = DotSize,
        Height = DotSize,
        Fill = new SolidColorBrush((Windows.UI.Color)Application.Current.Resources[colorKey]),
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

        if (state.IsActive && !_shown)
        {
            Motion.FadeIn(this, delay: TimeSpan.Zero);
        }

        _shown = state.IsActive;
    }
}
