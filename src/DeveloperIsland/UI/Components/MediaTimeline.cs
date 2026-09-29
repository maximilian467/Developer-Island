using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.System;

namespace DeveloperIsland.UI.Components;

/// <summary>
/// A 4 DIP media progress line with a 16 DIP tall hit area. When the player supports seeking,
/// the line accepts click/drag and Left/Right keys (5 s steps); the knob appears on hover or focus.
/// </summary>
public sealed class MediaTimeline : UserControl
{
    private readonly Grid _root = new() { Height = 16, Background = new SolidColorBrush(Colors.Transparent) };
    private readonly Rectangle _track;
    private readonly Rectangle _fill;
    private readonly ScaleTransform _fillScale = new() { ScaleX = 0 };
    private readonly TranslateTransform _knobOffset = new();
    private readonly Ellipse _knob;
    private bool _dragging;
    private bool _hovered;

    public MediaTimeline()
    {
        _track = new Rectangle { Height = 4, RadiusX = 2, RadiusY = 2, Fill = (Brush)Application.Current.Resources["ProgressTrackBrush"], VerticalAlignment = VerticalAlignment.Center };
        _fill = new Rectangle
        {
            Height = 4,
            RadiusX = 2,
            RadiusY = 2,
            Fill = (Brush)Application.Current.Resources["TextPrimaryBrush"],
            VerticalAlignment = VerticalAlignment.Center,
            RenderTransform = _fillScale,
            RenderTransformOrigin = new Windows.Foundation.Point(0, 0.5),
        };
        _knob = new Ellipse
        {
            Width = 12,
            Height = 12,
            Fill = (Brush)Application.Current.Resources["TextPrimaryBrush"],
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            RenderTransform = _knobOffset,
            Opacity = 0,
            IsHitTestVisible = false,
        };
        _root.Children.Add(_track);
        _root.Children.Add(_fill);
        _root.Children.Add(_knob);
        Content = _root;
        UseSystemFocusVisuals = true;
        CornerRadius = new CornerRadius(8);
        FocusVisualMargin = new Thickness(-6, -2, -6, -2);
        AutomationProperties.SetName(this, "Playback position");

        PointerEntered += (_, _) => { _hovered = true; UpdateKnob(); };
        PointerExited += (_, _) => { _hovered = false; UpdateKnob(); };
        GotFocus += (_, _) => UpdateKnob();
        LostFocus += (_, _) => UpdateKnob();
        PointerPressed += OnPointerPressed;
        PointerMoved += OnPointerMoved;
        PointerReleased += OnPointerReleased;
        PointerCaptureLost += (_, _) => _dragging = false;
        SizeChanged += (_, _) => Apply();
    }

    public static readonly DependencyProperty ProgressProperty = DependencyProperty.Register(
        nameof(Progress), typeof(double), typeof(MediaTimeline), new PropertyMetadata(0.0, (d, _) => ((MediaTimeline)d).Apply()));

    public static readonly DependencyProperty CanSeekProperty = DependencyProperty.Register(
        nameof(CanSeek), typeof(bool), typeof(MediaTimeline), new PropertyMetadata(false, (d, _) => ((MediaTimeline)d).OnCanSeekChanged()));

    public double Progress
    {
        get => (double)GetValue(ProgressProperty);
        set => SetValue(ProgressProperty, value);
    }

    public bool CanSeek
    {
        get => (bool)GetValue(CanSeekProperty);
        set => SetValue(CanSeekProperty, value);
    }

    /// <summary>Requested seek position as a fraction of the duration.</summary>
    public event Action<double>? SeekRequested;

    /// <summary>Duration used for keyboard steps.</summary>
    public TimeSpan Duration { get; set; }

    protected override AutomationPeer OnCreateAutomationPeer() => new FrameworkElementAutomationPeer(this);

    protected override void OnKeyDown(KeyRoutedEventArgs e)
    {
        if (!CanSeek || Duration <= TimeSpan.Zero || e.Key is not (VirtualKey.Left or VirtualKey.Right))
        {
            base.OnKeyDown(e);
            return;
        }

        var step = TimeSpan.FromSeconds(5) / Duration;
        SeekRequested?.Invoke(Math.Clamp(Progress + (e.Key == VirtualKey.Right ? step : -step), 0, 1));
        e.Handled = true;
    }

    private void OnCanSeekChanged()
    {
        IsTabStop = CanSeek;
        UpdateKnob();
    }

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!CanSeek)
        {
            return;
        }

        _dragging = CapturePointer(e.Pointer);
        Preview(e);
        e.Handled = true;
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_dragging)
        {
            Preview(e);
        }
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging)
        {
            return;
        }

        _dragging = false;
        ReleasePointerCapture(e.Pointer);
        SeekRequested?.Invoke(FractionAt(e));
        e.Handled = true;
    }

    private void Preview(PointerRoutedEventArgs e)
    {
        var fraction = FractionAt(e);
        _fillScale.ScaleX = fraction;
        _knobOffset.X = fraction * ActualWidth - 6;
    }

    private double FractionAt(PointerRoutedEventArgs e) =>
        ActualWidth <= 0 ? 0 : Math.Clamp(e.GetCurrentPoint(this).Position.X / ActualWidth, 0, 1);

    private void Apply()
    {
        if (_dragging)
        {
            return;
        }

        var p = Math.Clamp(Progress, 0, 1);
        _fillScale.ScaleX = p;
        _knobOffset.X = p * ActualWidth - 6;
        AutomationProperties.SetItemStatus(this, $"{Math.Round(p * 100)} percent");
    }

    private void UpdateKnob()
    {
        _knob.Opacity = CanSeek && (_hovered || FocusState != FocusState.Unfocused) ? 1 : 0;
    }
}
