using System.Numerics;
using DeveloperIsland.Core.Diagnostics;
using DeveloperIsland.Core.Island;
using DeveloperIsland.Core.Placement;
using DeveloperIsland.Platform.Windowing;
using DeveloperIsland.UI.Animations;
using DeveloperIsland.ViewModels;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Windows.Foundation;
using VirtualKey = Windows.System.VirtualKey;
using Windows.UI;
using static DeveloperIsland.Platform.Native.NativeMethods;

namespace DeveloperIsland.UI.Island;

/// <summary>
/// The island. A fixed-size, transparent, click-through window whose visible capsule is drawn and
/// morphed on the compositor (<see cref="IslandMorph"/>). The window never resizes during motion:
/// only the capsule animates, and the hit-test region follows it at animation boundaries.
/// </summary>
public sealed partial class IslandWindow : Window
{
    /// <summary>Room around the capsule for growth and the expanded shadow, in DIPs.</summary>
    public const double EdgeMargin = 40;

    /// <summary>Largest capsule the window must hold, in DIPs.</summary>
    public const double MaxCapsuleWidth = 420;
    public const double MaxCapsuleHeight = 520;

    public static double WindowWidth => MaxCapsuleWidth + 2 * EdgeMargin;

    public static double WindowHeight => MaxCapsuleHeight + 2 * EdgeMargin;

    private const double ExpandedRadius = 28;

    /// <summary>The notch is drawn 10 DIP tall and cut flat by the screen edge; 5 DIP remain visible.</summary>
    private const double NotchHeight = 10;

    /// <summary>Window y of the screen edge above a top-anchored island.</summary>
    private static double ScreenEdgeY => EdgeMargin - IslandPlacement.EdgeGap;
    private const double ShadowBleed = 40;

    private readonly IntPtr _hwnd;
    private readonly IslandViewModel _viewModel;
    private readonly IslandStateMachine _state;
    private readonly IslandMorph _morph;
    private readonly DispatcherQueueTimer _mediaTicker;
    private IslandAnchor _anchor = IslandAnchor.TopCenter;
    private FrameworkElement? _current;
    private Rect _regionRect;
    private (int L, int T, int R, int B) _appliedRegion;
    private double _scale = 1;
    private bool _alwaysOnTop = true;
    private bool _visible;
    private bool _loaded;
    private IntPtr _previousForeground;
    private readonly HoverTracker _hover = new();
    private bool _restoreFocusOnCollapse;
    private bool _keyboardInitiated;
    private bool _focusTaskCapture;

    // Drag state (physical pixels).
    private bool _pointerDown;
    private bool _dragging;
    private POINT _dragStartCursor;
    private RECT _dragStartWindow;

    public IslandWindow(IslandViewModel viewModel, IslandStateMachine state, bool alwaysOnTop)
    {
        InitializeComponent();
        _viewModel = viewModel;
        _state = state;
        _alwaysOnTop = alwaysOnTop;
        SystemBackdrop = new TransparentBackdrop();
        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        IslandWindowChrome.Apply(_hwnd, AppWindow, alwaysOnTop);

        // Nothing is visible until the first layout sets the real region; this avoids a black
        // frame on the first composition pass.
        IslandWindowChrome.SetRegion(_hwnd, 0, 0, 0, 0);

        Compact.ViewModel = viewModel;
        Activity.ViewModel = viewModel.Activity;
        Expanded.ViewModel = viewModel;
        Expanded.SettingsRequested += () => SettingsRequested?.Invoke();
        Expanded.VisiblePanelChanged += tab => VisiblePanelChanged?.Invoke(tab);

        var resources = Application.Current.Resources;
        _morph = new IslandMorph(SurfaceHost, ContentHost, (Color)resources["IslandFillColor"], (Color)resources["IslandHairlineColor"]);

        _mediaTicker = DispatcherQueue.CreateTimer();
        _mediaTicker.Interval = TimeSpan.FromSeconds(1);
        _mediaTicker.Tick += (_, _) => _viewModel.Music.Tick();

        Root.SizeChanged += (_, e) => _morph.SetCanvasSize(e.NewSize);
        Root.Loaded += OnLoaded;
        Root.PointerEntered += OnPointerEntered;
        Root.PointerExited += OnPointerExited;
        Root.PointerPressed += OnPointerPressed;
        Root.PointerMoved += OnPointerMoved;
        Root.PointerReleased += OnPointerReleased;
        Root.PointerCaptureLost += (_, _) => EndDrag(commit: _dragging);
        Root.KeyDown += OnKeyDown;
        Activated += OnWindowActivated;

        Compact.SizeChanged += OnViewSizeChanged;
        Activity.SizeChanged += OnViewSizeChanged;
        Expanded.SizeChanged += OnViewSizeChanged;

        _state.ModeChanged += OnModeChanged;
        _viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(IslandViewModel.SelectedTab))
            {
                UpdateMediaTicker();
            }
        };
        _viewModel.Music.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is "" or null)
            {
                UpdateMediaTicker();
            }
        };
    }

    public event Action? SettingsRequested;

    /// <summary>The module panel on screen changed; null when the island is not expanded.</summary>
    public event Action<ViewModels.IslandTab?>? VisiblePanelChanged;

    /// <summary>The user dropped the capsule; argument is its rectangle in screen pixels.</summary>
    public event Action<PixelRect>? DragCompleted;

    /// <summary>DPI changed (for example after moving to another monitor): placement must be recomputed.</summary>
    public event Action? PlacementInvalidated;

    public IntPtr Handle => _hwnd;

    public bool IsIslandVisible => _visible;

    /// <summary>Positions the window for a monitor and anchor. Content alignment follows the anchor.</summary>
    public void Place(MonitorInfo monitor, IslandAnchor anchor, double offsetX, double offsetY)
    {
        var anchorChanged = anchor != _anchor;
        _anchor = anchor;
        ApplyAnchorLayout();

        var rect = IslandPlacement.WindowRect(monitor, anchor, offsetX, offsetY, WindowWidth, WindowHeight, EdgeMargin);
        if (SnapshotMode)
        {
            // Rendered offscreen only: never visible on any monitor.
            rect = rect with { X = -32000, Y = -32000 };
        }

        IslandWindowChrome.Move(_hwnd, rect.X, rect.Y, rect.Width, rect.Height, _alwaysOnTop && !SnapshotMode);
        if (_loaded && anchorChanged)
        {
            Root.UpdateLayout();
            RefreshShape(animate: false);
        }
    }

    public void SetAlwaysOnTop(bool value)
    {
        _alwaysOnTop = value;
        IslandWindowChrome.SetAlwaysOnTop(_hwnd, AppWindow, value);
    }

    public void ShowIsland()
    {
        if (_visible)
        {
            return;
        }

        _visible = true;
        AppWindow.Show(activateWindow: false);
        if (_alwaysOnTop)
        {
            IslandWindowChrome.SetAlwaysOnTop(_hwnd, AppWindow, true);
        }
    }

    public void HideIsland()
    {
        if (!_visible)
        {
            return;
        }

        _visible = false;
        AppWindow.Hide();
    }

    /// <summary>Global shortcut: opens from any state (keyboard focus inside), closes when expanded.</summary>
    public void ToggleFromShortcut()
    {
        _keyboardInitiated = _state.Mode != IslandMode.Expanded;
        _state.Toggle();
    }

    /// <summary>Expands from outside the island (tray click); keyboard focus lands inside.</summary>
    /// <summary>Quick capture: expand on Tasks with the cursor in the input.</summary>
    public void ExpandToTaskCapture()
    {
        _focusTaskCapture = true;
        ExpandFromShortcut();
    }

    public void ExpandFromShortcut()
    {
        _keyboardInitiated = true;
        _state.Show();
        _state.Activate();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _loaded = true;
        _scale = Root.XamlRoot?.RasterizationScale ?? 1;
        Root.XamlRoot!.Changed += (_, _) =>
        {
            var scale = Root.XamlRoot.RasterizationScale;
            if (Math.Abs(scale - _scale) > 0.001)
            {
                _scale = scale;
                PlacementInvalidated?.Invoke();
                ApplyRegion(_regionRect, force: true);
            }
        };

        ApplyAnchorLayout();

        // Events may have changed the mode before the first layout; start in whatever is current.
        var mode = _state.Mode == IslandMode.Hidden ? IslandMode.Compact : _state.Mode;
        TransitionTo(ViewFor(mode), animate: false);

        // Placing the window on a monitor with a different DPI lets Windows rescale it before the
        // first layout; place it once more at the final scale.
        PlacementInvalidated?.Invoke();
    }

    // State changes ----------------------------------------------------------------------------

    private void OnModeChanged(IslandMode oldMode, IslandMode newMode)
    {
        Log.Debug("island", "Mode changed", new { from = oldMode.ToString(), to = newMode.ToString(), source = _state.ActivitySource.ToString() });
        try
        {
            switch (newMode)
            {
                case IslandMode.Hidden:
                    if (oldMode == IslandMode.Expanded)
                    {
                        Expanded.OnHidden();
                        ReturnFocus();
                    }

                    HideIsland();
                    UpdateMediaTicker();
                    return;
                case IslandMode.Activity when _state.ActivitySource == ActivitySource.Hover:
                    _viewModel.PreparePeek();
                    break;
                case IslandMode.Expanded:
                    _viewModel.SelectTabForExpand();
                    Expanded.PrepareForShow();
                    break;
            }

            if (oldMode == IslandMode.Hidden)
            {
                ShowIsland();
            }

            if (!_loaded)
            {
                // The first layout (OnLoaded) applies the current mode.
                return;
            }

            TransitionTo(ViewFor(newMode), animate: true);

            if (newMode == IslandMode.Expanded)
            {
                ActivateForKeyboard();
            }
            else if (oldMode == IslandMode.Expanded)
            {
                Expanded.OnHidden();
                ReturnFocus();
            }

            UpdateMediaTicker();
        }
        catch (Exception ex)
        {
            Log.Error("window", "Island state change failed", ex, new { from = oldMode.ToString(), to = newMode.ToString() });
        }
    }

    private FrameworkElement ViewFor(IslandMode mode) => mode switch
    {
        IslandMode.Retracted => Notch,
        IslandMode.Activity => Activity,
        IslandMode.Expanded => Expanded,
        _ => Compact,
    };

    private void TransitionTo(FrameworkElement target, bool animate)
    {
        var previous = _current;
        target.Visibility = Visibility.Visible;
        Root.UpdateLayout();
        _current = target;

        var rect = BoundsOf(target);
        var elevated = ReferenceEquals(target, Expanded);
        var radius = elevated ? ExpandedRadius : rect.Height / 2;
        var finalRegion = RegionFor(target, rect, elevated);

        // While morphing, the region covers both shapes; it shrinks to the final shape once settled.
        ApplyRegion(_regionRect.IsEmpty ? finalRegion : Union(_regionRect, finalRegion));
        _morph.MorphTo(rect, radius, elevated, animate, () => OnMorphSettled(finalRegion));

        if (previous is not null && !ReferenceEquals(previous, target))
        {
            var fadingOut = previous;
            Motion.FadeOut(fadingOut, () =>
            {
                if (!ReferenceEquals(_current, fadingOut))
                {
                    fadingOut.Visibility = Visibility.Collapsed;
                }
            }, toScale: 0.97f, origin: AnchorPointIn(fadingOut));

            var fromY = _anchor.Vertical() == VerticalEdge.Top ? -6f : 6f;
            Motion.FadeIn(target, Motion.FadeInDelay, fromY, fromScale: 0.97f, origin: AnchorPointIn(target));
        }
    }

    /// <summary>Content of the current view changed size (text, tab switch): follow it.</summary>
    private void OnViewSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (ReferenceEquals(sender, _current) && !_dragging && _loaded)
        {
            RefreshShape(animate: true);
        }
    }

    private void RefreshShape(bool animate)
    {
        if (_current is null)
        {
            return;
        }

        var rect = BoundsOf(_current);
        if (rect.Width <= 0 || rect.Height <= 0 || rect == _morph.Target)
        {
            return;
        }

        var elevated = ReferenceEquals(_current, Expanded);
        var finalRegion = RegionFor(_current, rect, elevated);
        ApplyRegion(Union(_regionRect, finalRegion));
        _morph.MorphTo(rect, elevated ? ExpandedRadius : rect.Height / 2, elevated, animate, () => OnMorphSettled(finalRegion));
    }

    /// <summary>
    /// The view's layout rectangle in window coordinates. Uses the arrange result (ActualOffset), not
    /// TransformToVisual: the latter includes the fade-in's composition scale and translation, so a
    /// resize during a fade would otherwise lock the capsule onto a 97% rectangle.
    /// </summary>
    private Rect BoundsOf(FrameworkElement view)
    {
        var offset = view.ActualOffset + ContentHost.ActualOffset;
        return new Rect(offset.X, offset.Y, view.ActualWidth, view.ActualHeight);
    }

    /// <summary>The point the view grows from (for scale origins), relative to the view.</summary>
    private Vector2 AnchorPointIn(FrameworkElement view)
    {
        var w = (float)view.ActualWidth;
        var h = (float)view.ActualHeight;
        var x = _anchor.Horizontal() switch
        {
            HorizontalEdge.Left => 0f,
            HorizontalEdge.Right => w,
            _ => w / 2,
        };
        return new Vector2(x, _anchor.Vertical() == VerticalEdge.Top ? 0f : h);
    }

    private void ApplyAnchorLayout()
    {
        var h = _anchor.Horizontal() switch
        {
            HorizontalEdge.Left => HorizontalAlignment.Left,
            HorizontalEdge.Right => HorizontalAlignment.Right,
            _ => HorizontalAlignment.Center,
        };
        var top = _anchor.Vertical() == VerticalEdge.Top;
        var margin = new Thickness(
            h == HorizontalAlignment.Left ? EdgeMargin : 0,
            top ? EdgeMargin : 0,
            h == HorizontalAlignment.Right ? EdgeMargin : 0,
            top ? 0 : EdgeMargin);

        foreach (var view in new FrameworkElement[] { Compact, Activity, Expanded })
        {
            view.HorizontalAlignment = h;
            view.VerticalAlignment = top ? VerticalAlignment.Top : VerticalAlignment.Bottom;
            view.Margin = margin;
        }

        // The notch always hugs the top edge above the island's center (Smart Auto-Hide is top-center only).
        Notch.HorizontalAlignment = HorizontalAlignment.Center;
        Notch.VerticalAlignment = VerticalAlignment.Top;
        Notch.Margin = new Thickness(0, ScreenEdgeY - NotchHeight / 2, 0, 0);
    }

    // Region --------------------------------------------------------------------------------------

    private static Rect RegionFor(FrameworkElement view, Rect capsule, bool elevated) =>
        view.Name == "Notch"
            // Only the part below the screen edge, plus 2 DIP so the edge-hugging tab is easy to hit.
            ? Clamp(new Rect(capsule.X - 1, ScreenEdgeY, capsule.Width + 2, Math.Max(1, capsule.Bottom - ScreenEdgeY + 2)))
            : RegionFor(capsule, elevated);

    private static Rect RegionFor(Rect capsule, bool elevated)
    {
        var bleed = elevated ? ShadowBleed : 1;
        var r = new Rect(capsule.X - bleed, capsule.Y - bleed, capsule.Width + 2 * bleed, capsule.Height + 2 * bleed);
        return Clamp(r);
    }

    private static Rect Union(Rect a, Rect b)
    {
        if (a.IsEmpty)
        {
            return b;
        }

        var left = Math.Min(a.Left, b.Left);
        var top = Math.Min(a.Top, b.Top);
        return new Rect(left, top, Math.Max(a.Right, b.Right) - left, Math.Max(a.Bottom, b.Bottom) - top);
    }

    /// <summary>
    /// Keeps the region a few DIPs inside the window: Windows 11 draws its active-window border on
    /// the outermost pixels, even with DWMWA_BORDER_COLOR set to none.
    /// </summary>
    private static Rect Clamp(Rect r)
    {
        var left = Math.Max(4, r.Left);
        var top = Math.Max(1, r.Top);
        var right = Math.Min(WindowWidth - 4, r.Right);
        var bottom = Math.Min(WindowHeight - 1, r.Bottom);
        return new Rect(left, top, Math.Max(0, right - left), Math.Max(0, bottom - top));
    }

    private void ApplyRegion(Rect dip, bool force = false)
    {
        _regionRect = dip;
        var s = _scale;
        var px = ((int)Math.Floor(dip.Left * s), (int)Math.Floor(dip.Top * s), (int)Math.Ceiling(dip.Right * s), (int)Math.Ceiling(dip.Bottom * s));
        if (!force && px == _appliedRegion)
        {
            return;
        }

        _appliedRegion = px;
        IslandWindowChrome.SetRegion(_hwnd, px.Item1, px.Item2, px.Item3, px.Item4);
    }

    // Input -----------------------------------------------------------------------------------------
    //
    // Hover and clicks are judged against the visible capsule (the morph target), never against the
    // window region: the region is larger while a morph runs and around the expanded shadow.

    private CapsuleBounds Capsule => new(_morph.Target.X, _morph.Target.Y, _morph.Target.Width, _morph.Target.Height);

    private void OnMorphSettled(Rect finalRegion)
    {
        ApplyRegion(finalRegion);
        RevalidatePointer();
    }

    /// <summary>The OS reports no leave when a shrinking region slides out from under a still pointer.</summary>
    private void RevalidatePointer()
    {
        if (_dragging || !GetCursorPos(out var cursor) || !GetWindowRect(_hwnd, out var window))
        {
            return;
        }

        ApplyHover(_hover.Revalidate((cursor.X - window.Left) / _scale, (cursor.Y - window.Top) / _scale, Capsule));
    }

    private void ApplyHover(HoverChange change)
    {
        if (change == HoverChange.Entered)
        {
            _state.PointerEntered();
        }
        else if (change == HoverChange.Exited)
        {
            _state.PointerExited();
        }
    }

    private void OnPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging && !_pointerDown)
        {
            var p = e.GetCurrentPoint(Root).Position;
            ApplyHover(_hover.PointerAt(p.X, p.Y, Capsule));
        }
    }

    private void OnPointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging && !_pointerDown)
        {
            ApplyHover(_hover.PointerLeftWindow());
        }
    }

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(Root);
        if (!Capsule.Contains(point.Position.X, point.Position.Y))
        {
            // A press in the transparent margin is a click outside the island, never on it.
            if (_state.Mode == IslandMode.Expanded)
            {
                _restoreFocusOnCollapse = false;
                _state.Dismiss();
            }

            e.Handled = true;
            return;
        }

        if (_state.Mode is not (IslandMode.Compact or IslandMode.Activity or IslandMode.Retracted))
        {
            return;
        }

        if (!point.Properties.IsLeftButtonPressed)
        {
            return;
        }

        _pointerDown = true;
        _dragging = false;
        GetCursorPos(out _dragStartCursor);
        GetWindowRect(_hwnd, out _dragStartWindow);
        Root.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_pointerDown)
        {
            var p = e.GetCurrentPoint(Root).Position;
            ApplyHover(_hover.PointerAt(p.X, p.Y, Capsule));
            return;
        }

        GetCursorPos(out var cursor);
        var dx = cursor.X - _dragStartCursor.X;
        var dy = cursor.Y - _dragStartCursor.Y;
        if (!_dragging && dx * dx + dy * dy > Math.Pow(5 * _scale, 2))
        {
            Log.Debug("island", "Drag started", new { dx, dy, sx = _dragStartCursor.X, sy = _dragStartCursor.Y, cx = cursor.X, cy = cursor.Y });
            _dragging = true;
            Root.SetCursor(InputSystemCursor.Create(InputSystemCursorShape.SizeAll));
        }

        if (_dragging)
        {
            SetWindowPos(_hwnd, IntPtr.Zero, _dragStartWindow.Left + dx, _dragStartWindow.Top + dy, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
        }
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_pointerDown)
        {
            return;
        }

        // Read the drag state first: releasing capture raises PointerCaptureLost synchronously,
        // which would end the drag and make this release look like a click.
        var wasDragging = _dragging;
        EndDrag(commit: wasDragging);
        Root.ReleasePointerCapture(e.Pointer);
        if (!wasDragging)
        {
            _keyboardInitiated = false;
            _state.Activate();
        }

        e.Handled = true;
    }

    private void EndDrag(bool commit)
    {
        if (!_pointerDown)
        {
            return;
        }

        _pointerDown = false;
        if (!_dragging)
        {
            return;
        }

        _dragging = false;
        Root.SetCursor(null);
        if (commit)
        {
            GetWindowRect(_hwnd, out var window);
            var t = _morph.Target;
            var capsule = new PixelRect(
                window.Left + (int)Math.Round(t.X * _scale),
                window.Top + (int)Math.Round(t.Y * _scale),
                (int)Math.Round(t.Width * _scale),
                (int)Math.Round(t.Height * _scale));
            DragCompleted?.Invoke(capsule);
        }
    }

    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape && _state.Mode == IslandMode.Expanded)
        {
            _restoreFocusOnCollapse = true;
            _state.Dismiss();
            e.Handled = true;
        }
    }

    private void OnWindowActivated(object sender, WindowActivatedEventArgs args)
    {
        if (SnapshotMode)
        {
            return;
        }

        IslandWindowChrome.SuppressBorder(_hwnd);

        // Clicking anywhere else closes the expanded island, like a popover.
        if (args.WindowActivationState == WindowActivationState.Deactivated && _state.Mode == IslandMode.Expanded)
        {
            _restoreFocusOnCollapse = false;
            _state.Dismiss();
        }
    }

    private void ActivateForKeyboard()
    {
        if (SnapshotMode)
        {
            return;
        }

        var foreground = GetForegroundWindow();
        if (foreground != _hwnd)
        {
            _previousForeground = foreground;
        }

        SetForegroundWindow(_hwnd);
        IslandWindowChrome.SuppressBorder(_hwnd);
        var keyboard = _keyboardInitiated;
        var capture = _focusTaskCapture;
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            if (_state.Mode != IslandMode.Expanded)
            {
                return;
            }

            if (capture)
            {
                Expanded.FocusTaskCapture();
            }
            else
            {
                // Keyboard-initiated expansion shows the focus ring; a click does not.
                Expanded.FocusFirst(keyboard ? FocusState.Keyboard : FocusState.Programmatic);
            }
        });
        _keyboardInitiated = false;
        _focusTaskCapture = false;
    }

    private void ReturnFocus()
    {
        if (_restoreFocusOnCollapse && _previousForeground != IntPtr.Zero && IsWindow(_previousForeground))
        {
            SetForegroundWindow(_previousForeground);
        }

        _restoreFocusOnCollapse = false;
        _previousForeground = IntPtr.Zero;
    }

    private void UpdateMediaTicker()
    {
        var needed = _state.Mode == IslandMode.Expanded && _viewModel.SelectedTab == IslandTab.Music && _viewModel.Music.IsEnabled && _viewModel.Music.IsPlaying;
        if (needed && !_mediaTicker.IsRunning)
        {
            _viewModel.Music.Tick();
            _mediaTicker.Start();
        }
        else if (!needed && _mediaTicker.IsRunning)
        {
            _mediaTicker.Stop();
        }
    }
}
