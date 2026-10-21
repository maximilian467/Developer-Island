using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Platform;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Threading;
using DeveloperIsland.Core.Diagnostics;
using DeveloperIsland.Core.Island;
using DeveloperIsland.Core.Placement;
using DeveloperIsland.Platform.Monitors;
using DeveloperIsland.Platform.X11;
using DeveloperIsland.UI.Animations;
using DeveloperIsland.UI.Components;
using DeveloperIsland.ViewModels;
using Microsoft.UI.Dispatching;
using PixelRect = DeveloperIsland.Core.Placement.PixelRect;

namespace DeveloperIsland.UI.Island;

/// <summary>
/// The island. A fixed-size, transparent window whose visible capsule is drawn and morphed inside it
/// (<see cref="IslandMorph"/>). The window never resizes during motion: only the capsule animates,
/// and the window's X11 input shape follows it at animation boundaries, so everything outside the
/// capsule is click-through.
/// </summary>
public sealed partial class IslandWindow : Window
{
    /// <summary>Room around the capsule for growth and the expanded shadow, in DIPs.</summary>
    public const double EdgeMargin = 40;

    /// <summary>Largest capsule the window must hold, in DIPs.</summary>
    public const double MaxCapsuleWidth = 420;
    public const double MaxCapsuleHeight = 700;

    public static double WindowWidth => MaxCapsuleWidth + 2 * EdgeMargin;

    public static double WindowHeight => MaxCapsuleHeight + 2 * EdgeMargin;

    private const double ExpandedRadius = 28;

    /// <summary>The notch is drawn 10 DIP tall and cut flat by the edge; 5 DIP remain visible.</summary>
    private const double NotchHeight = 10;

    /// <summary>Window y of the screen edge (the top of the work area) above a top-anchored island.</summary>
    private static double ScreenEdgeY => EdgeMargin - IslandPlacement.EdgeGap;
    private const double ShadowBleed = 40;

    private readonly IslandViewModel _viewModel;
    private readonly IslandStateMachine _state;
    private readonly IslandMorph _morph;
    private readonly DispatcherQueueTimer _mediaTicker;
    private readonly HoverTracker _hover = new();
    private readonly XDisplay? _x;
    private IntPtr _xid;
    private IslandAnchor _anchor = IslandAnchor.TopCenter;
    private Control? _current;
    private Rect _regionRect;
    private (int L, int T, int R, int B)? _appliedRegion;
    private bool _alwaysOnTop = true;
    private bool _visible;
    private bool _loaded;
    private bool _exiting;
    private IntPtr _previousActive;
    private bool _restoreFocusOnCollapse;
    private bool _keyboardInitiated;
    private bool _focusTaskCapture;

    // Drag state (physical pixels).
    private bool _pointerDown;

    /// <summary>When the capsule last changed shape (ms, monotonic): a crossing right after is the shape moving, not the user.</summary>
    private long _shapeChangedAt;

    /// <summary>Last pointer position seen by the hover poll, in screen pixels.</summary>
    private (int X, int Y)? _polledPointer;

    private readonly DispatcherQueueTimer _hoverPoll = DispatcherQueue.GetForCurrentThread().CreateTimer();

    private const long ShapeSettleMs = 250;

    private bool ShapeIsSettled => Environment.TickCount64 - _shapeChangedAt > ShapeSettleMs;
    private bool _dragging;
    private bool _placementDeferred;
    private (int X, int Y) _dragStartCursor;
    private (double X, double Y) _dragGrab;
    private readonly DispatcherQueueTimer _dragTicker;

    public IslandWindow()
        : this(null!, null!, true)
    {
        // For the XAML previewer only.
    }

    public IslandWindow(IslandViewModel viewModel, IslandStateMachine state, bool alwaysOnTop)
    {
        InitializeComponent();
        _viewModel = viewModel;
        _state = state;
        _alwaysOnTop = alwaysOnTop;
        Width = WindowWidth;
        Height = WindowHeight;
        Topmost = alwaysOnTop;

        // A utility window: no taskbar entry, not in Alt+Tab, never maximized or tiled by the window manager.
        X11Properties.SetNetWmWindowType(this, X11NetWmWindowType.Utility);

        var surface = new CapsuleSurface(StatusColors.Resource<Color>("IslandFillColor"), StatusColors.Resource<Color>("IslandHairlineColor"));
        SurfaceHost.Children.Add(surface);
        SurfaceHost.SizeChanged += (_, e) => { surface.Width = e.NewSize.Width; surface.Height = e.NewSize.Height; };
        _morph = new IslandMorph(surface, ContentHost, this);
        _x = XDisplay.Open();

        if (viewModel is null)
        {
            _mediaTicker = DispatcherQueue.GetForCurrentThread().CreateTimer();
            _dragTicker = DispatcherQueue.GetForCurrentThread().CreateTimer();
            return;
        }

        Compact.ViewModel = viewModel;
        Activity.ViewModel = viewModel.Activity;
        Expanded.ViewModel = viewModel;
        Expanded.SettingsRequested += () => SettingsRequested?.Invoke();
        Expanded.VisiblePanelChanged += tab => VisiblePanelChanged?.Invoke(tab);
        NotchPrivacy.Source = viewModel.Privacy;

        _mediaTicker = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _mediaTicker.Interval = TimeSpan.FromSeconds(1);
        _mediaTicker.Tick += (_, _) => _viewModel.Music.Tick();

        // Runs only during a drag: ends it when the button is up even if the release was not delivered.
        _dragTicker = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _dragTicker.Interval = TimeSpan.FromMilliseconds(15);
        _dragTicker.Tick += (_, _) => OnDragTick();

        _hoverPoll.Interval = TimeSpan.FromMilliseconds(100);
        _hoverPoll.Tick += (_, _) => OnHoverPoll();

        Opened += OnOpened;
        Root.PointerEntered += OnPointerEntered;
        Root.PointerExited += OnPointerExited;
        Root.PointerPressed += OnPointerPressed;
        Root.PointerMoved += OnPointerMoved;
        Root.PointerReleased += OnPointerReleased;
        Root.PointerCaptureLost += OnPointerCaptureLost;
        KeyDown += OnKeyDown;
        Deactivated += OnDeactivated;
        Closing += OnClosing;
        ScalingChanged += (_, _) =>
        {
            PlacementInvalidated?.Invoke();
            ApplyRegion(_regionRect, force: true);
        };

        foreach (var view in new Control[] { Compact, Activity, Expanded, DropZone })
        {
            view.SizeChanged += OnViewSizeChanged;
        }

        _state.ModeChanged += OnModeChanged;
        _state.PointerProbe = ProbePointer;
        _state.DropProbe = ProbeDrop;
        InitializeDropTarget();
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
    public event Action<IslandTab?>? VisiblePanelChanged;

    /// <summary>The user dropped the capsule; argument is its rectangle in screen pixels.</summary>
    public event Action<PixelRect>? DragCompleted;

    /// <summary>Scale changed (for example after moving to another monitor): placement must be recomputed.</summary>
    public event Action? PlacementInvalidated;

    public bool IsIslandVisible => _visible;

    /// <summary>In snapshot mode the island never shows on screen and never takes focus.</summary>
    public bool SnapshotMode { get; set; }

    private double Scale => RenderScaling > 0 ? RenderScaling : 1;

    /// <summary>The monitor that shows the capsule: the one under its anchor point.</summary>
    public MonitorInfo? IslandMonitor
    {
        get
        {
            var (ax, ay) = IslandPlacement.AnchorInWindow(_anchor, WindowWidth, WindowHeight, EdgeMargin);
            return MonitorService.FromPoint(Position.X + (int)Math.Round(ax * Scale), Position.Y + (int)Math.Round(ay * Scale));
        }
    }

    /// <summary>Positions the window for a monitor and anchor. Content alignment follows the anchor.</summary>
    public void Place(MonitorInfo monitor, IslandAnchor anchor, double offsetX, double offsetY)
    {
        if (_dragging)
        {
            // The drop decides where the island goes.
            _placementDeferred = true;
            return;
        }

        var anchorChanged = anchor != _anchor;
        _anchor = anchor;
        ApplyAnchorLayout();

        var rect = IslandPlacement.WindowRect(monitor, anchor, offsetX, offsetY, WindowWidth, WindowHeight, EdgeMargin, Scale);
        Log.Debug("window", "Placed", new { device = monitor.DeviceName, monitorScale = monitor.Scale, scale = Scale, anchor = anchor.ToString(), rect.X, rect.Y });
        if (SnapshotMode)
        {
            rect = rect with { X = -32000, Y = -32000 };
        }

        Position = new PixelPoint(rect.X, rect.Y);
        if (_x is not null && _xid != IntPtr.Zero)
        {
            // Window managers may ignore a move request for a window they consider placed; ask directly too.
            _x.Move(_xid, rect.X, rect.Y);
            _x.Flush();
        }

        if (_loaded && anchorChanged)
        {
            Root.UpdateLayout();
            RefreshShape(animate: false);
        }

        UpdateDropArming();
    }

    public void SetAlwaysOnTop(bool value)
    {
        _alwaysOnTop = value;
        Topmost = value;
    }

    /// <summary>Another app came to the front: stay above other always-on-top windows. Never activates the island.</summary>
    public void KeepOnTop()
    {
        if (_visible && _alwaysOnTop && !SnapshotMode && !_exiting && _x is not null && _xid != IntPtr.Zero)
        {
            _x.Raise(_xid);
            _x.Flush();
        }
    }

    /// <summary>The app is quitting: let the window close.</summary>
    public void PrepareForExit()
    {
        _exiting = true;
        _mediaTicker.Stop();
        StopDropTarget();
        UpdateDropArming();
    }

    public void ShowIsland()
    {
        if (_visible)
        {
            return;
        }

        _visible = true;
        if (!SnapshotMode)
        {
            Show();
            ApplyX11State();
        }
        else
        {
            // Rendered offscreen only.
            Show();
        }

        UpdateHoverPoll();
    }

    public void HideIsland()
    {
        if (!_visible)
        {
            return;
        }

        _visible = false;
        Hide();
    }

    /// <summary>Global shortcut: opens from any state (keyboard focus inside), closes when expanded.</summary>
    public void ToggleFromShortcut()
    {
        _keyboardInitiated = _state.Mode != IslandMode.Expanded;
        _state.Toggle();
    }

    /// <summary>Quick capture: expand on Tasks with the cursor in the input.</summary>
    public void ExpandToTaskCapture()
    {
        _focusTaskCapture = true;
        ExpandFromShortcut();
    }

    /// <summary>Expands from outside the island (tray, shortcut); keyboard focus lands inside.</summary>
    public void ExpandFromShortcut()
    {
        _keyboardInitiated = true;
        _state.Show();
        _state.Activate();
    }

    private void OnOpened(object? sender, EventArgs e)
    {
        _xid = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        ApplyX11State();
        if (_loaded)
        {
            ApplyRegion(_regionRect, force: true);
            return;
        }

        _loaded = true;
        ApplyAnchorLayout();
        UpdateHoverPoll();

        // Nothing is clickable until the first layout sets the real region.
        ApplyWindowRegion(default, force: true);
        Dispatcher.UIThread.Post(() =>
        {
            var mode = _state.Mode == IslandMode.Hidden ? IslandMode.Compact : _state.Mode;
            TransitionTo(ViewFor(mode), animate: false);
            PlacementInvalidated?.Invoke();
        }, DispatcherPriority.Loaded);
    }

    /// <summary>Sticky on every workspace, above other windows, out of taskbars and pagers.</summary>
    private void ApplyX11State()
    {
        if (_x is null || _xid == IntPtr.Zero || SnapshotMode)
        {
            return;
        }

        _x.ChangeState(_xid, true, "_NET_WM_STATE_SKIP_TASKBAR", "_NET_WM_STATE_SKIP_PAGER");
        _x.ChangeState(_xid, true, "_NET_WM_STATE_STICKY");
        _x.SetAllDesktops(_xid);
        if (_alwaysOnTop)
        {
            _x.ChangeState(_xid, true, "_NET_WM_STATE_ABOVE");
        }

        _x.Flush();
    }

    // State changes ------------------------------------------------------------------------------

    private void OnModeChanged(IslandMode oldMode, IslandMode newMode)
    {
        Log.Debug("island", "Mode changed", new { from = oldMode.ToString(), to = newMode.ToString(), source = _state.ActivitySource.ToString() });
        var timing = System.Diagnostics.Stopwatch.StartNew();
        var marks = new List<string>();
        void Mark(string what) => marks.Add($"{what}={timing.ElapsedMilliseconds}");
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
                    UpdateHoverPoll();
                    return;
                case IslandMode.Activity when _state.ActivitySource == ActivitySource.Hover:
                    _viewModel.PreparePeek();
                    break;
                case IslandMode.Expanded:
                    _viewModel.SelectTabForExpand();
                    Mark("select");
                    Expanded.PrepareForShow();
                    Mark("prepare");
                    break;
            }

            if (oldMode == IslandMode.Hidden)
            {
                ShowIsland();
            }

            if (!_loaded)
            {
                return;
            }

            TransitionTo(ViewFor(newMode), animate: true);
            Mark("transition");
            UpdateHoverPoll();
            if (newMode == IslandMode.Expanded)
            {
                ActivateForKeyboard();
                Mark("activate");
            }
            else if (oldMode == IslandMode.Expanded)
            {
                Expanded.OnHidden();
                ReturnFocus();
            }

            UpdateMediaTicker();
            if (timing.ElapsedMilliseconds > 30)
            {
                Log.Info("render", "Slow state change", new { to = newMode.ToString(), ms = timing.ElapsedMilliseconds, steps = string.Join(" ", marks) });
            }
        }
        catch (Exception ex)
        {
            Log.Error("window", "Island state change failed", ex, new { from = oldMode.ToString(), to = newMode.ToString() });
        }
    }

    private Control ViewFor(IslandMode mode) => mode switch
    {
        IslandMode.Retracted => Notch,
        IslandMode.Activity => Activity,
        IslandMode.Expanded => Expanded,
        IslandMode.Drop => DropZone,
        _ => Compact,
    };

    private void TransitionTo(Control target, bool animate)
    {
        var previous = _current;
        target.IsVisible = true;
        Root.UpdateLayout();
        _current = target;

        var rect = BoundsOf(target);
        var elevated = ReferenceEquals(target, Expanded);
        var radius = elevated ? ExpandedRadius : rect.Height / 2;
        var finalRegion = RegionFor(target, rect, elevated);

        _shapeChangedAt = Environment.TickCount64;

        // While morphing, the region covers both shapes; it shrinks to the final shape once settled.
        ApplyRegion(_regionRect.Width <= 0 ? finalRegion : Union(_regionRect, finalRegion));
        _morph.MorphTo(rect, radius, elevated, animate, () => OnMorphSettled(finalRegion));

        if (previous is not null && !ReferenceEquals(previous, target))
        {
            var fadingOut = previous;
            Motion.FadeOut(fadingOut, () =>
            {
                if (!ReferenceEquals(_current, fadingOut))
                {
                    fadingOut.IsVisible = false;
                }
            }, toScale: 0.97, origin: AnchorPointIn(fadingOut));

            var fromY = _anchor.Vertical() == VerticalEdge.Top ? -6 : 6;
            Motion.FadeIn(target, Motion.FadeInDelay, fromY, fromScale: 0.97, origin: AnchorPointIn(target));
        }
    }

    /// <summary>Content of the current view changed size (text, tab switch): follow it.</summary>
    private void OnViewSizeChanged(object? sender, SizeChangedEventArgs e)
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
        _shapeChangedAt = Environment.TickCount64;
        ApplyRegion(Union(_regionRect, finalRegion));
        _morph.MorphTo(rect, elevated ? ExpandedRadius : rect.Height / 2, elevated, animate, () => OnMorphSettled(finalRegion));
    }

    /// <summary>The view's layout rectangle in window coordinates (not including running fade transforms).</summary>
    private Rect BoundsOf(Control view)
    {
        var b = view.Bounds;
        var host = ContentHost.Bounds;
        return new Rect(host.X + b.X, host.Y + b.Y, b.Width, b.Height);
    }

    /// <summary>The point the view grows from (for scale origins), relative to the view.</summary>
    private Point AnchorPointIn(Control view)
    {
        var w = view.Bounds.Width;
        var h = view.Bounds.Height;
        var x = _anchor.Horizontal() switch
        {
            HorizontalEdge.Left => 0,
            HorizontalEdge.Right => w,
            _ => w / 2,
        };
        return new Point(x, _anchor.Vertical() == VerticalEdge.Top ? 0 : h);
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

        foreach (var view in new Control[] { Compact, Activity, Expanded, DropZone })
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

    // Region ---------------------------------------------------------------------------------------

    private Rect RegionFor(Control view, Rect capsule, bool elevated) => view.Name switch
    {
        // Only the part below the edge, plus 2 DIP so the edge-hugging tab is easy to hit.
        "Notch" => Clamp(new Rect(capsule.X - 1, ScreenEdgeY, capsule.Width + 2, Math.Max(1, capsule.Bottom - ScreenEdgeY + 2))),

        // The drop zone also covers the arm zone above it, so a drag that armed it stays inside.
        "DropZone" => Union(RegionFor(capsule, elevated), _armRegion),
        _ => RegionFor(capsule, elevated),
    };

    /// <summary>
    /// The input region around a capsule. The expanded shadow is not included (unlike Windows, the
    /// shadow needs no hit-testing here): a click on it goes to the window below and closes the island
    /// through focus loss.
    /// </summary>
    private static Rect RegionFor(Rect capsule, bool elevated) =>
        Clamp(new Rect(capsule.X - 1, capsule.Y - 1, capsule.Width + 2, capsule.Height + 2));

    private static Rect Union(Rect a, Rect b)
    {
        if (a.Width <= 0 || a.Height <= 0)
        {
            return b;
        }

        if (b.Width <= 0 || b.Height <= 0)
        {
            return a;
        }

        return a.Union(b);
    }

    private static Rect Clamp(Rect r)
    {
        var left = Math.Max(0, r.Left);
        var top = Math.Max(0, r.Top);
        var right = Math.Min(WindowWidth, r.Right);
        var bottom = Math.Min(WindowHeight, r.Bottom);
        return new Rect(left, top, Math.Max(0, right - left), Math.Max(0, bottom - top));
    }

    private void ApplyRegion(Rect dip, bool force = false)
    {
        _regionRect = dip;
        ApplyWindowRegion(EffectiveRegion(), force);
    }

    private void ApplyWindowRegion(Rect dip, bool force)
    {
        var s = Scale;
        var px = ((int)Math.Floor(dip.Left * s), (int)Math.Floor(dip.Top * s), (int)Math.Ceiling(dip.Right * s), (int)Math.Ceiling(dip.Bottom * s));
        if (!force && px == _appliedRegion)
        {
            return;
        }

        _appliedRegion = px;
        if (_x is not null && _xid != IntPtr.Zero && !SnapshotMode)
        {
            _x.SetInputRegion(_xid, px.Item1, px.Item2, px.Item3, px.Item4);
        }
    }

    // Input -----------------------------------------------------------------------------------------
    //
    // Hover and clicks are judged against the visible capsule (the morph target), never against the
    // input region: the region is larger while a morph runs.

    private CapsuleBounds Capsule => new(_morph.Target.X, _morph.Target.Y, _morph.Target.Width, _morph.Target.Height);

    private void OnMorphSettled(Rect finalRegion)
    {
        _shapeChangedAt = Environment.TickCount64;
        ApplyRegion(finalRegion);
        RevalidatePointer();
    }

    /// <summary>The pointer relative to the window in DIPs, from the X server; null when unknown.</summary>
    private (double X, double Y, bool Button)? PointerInWindow()
    {
        if (_x?.Pointer() is not { } p)
        {
            return null;
        }

        return ((p.X - Position.X) / Scale, (p.Y - Position.Y) / Scale, p.Button1);
    }

    /// <summary>Over the visible capsule or not, for the state machine's timers. Null while dragging.</summary>
    private bool? ProbePointer()
    {
        if (_dragging)
        {
            return null;
        }

        if (!IsIslandVisible || PointerInWindow() is not { } p)
        {
            return false;
        }

        return Capsule.Contains(p.X, p.Y);
    }

    /// <summary>The X server reports no leave when a shrinking region slides out from under a still pointer.</summary>
    private void RevalidatePointer()
    {
        if (_dragging || PointerInWindow() is not { } p)
        {
            return;
        }

        ApplyHover(_hover.Revalidate(p.X, p.Y, Capsule));
    }

    private void ApplyHover(HoverChange change, [System.Runtime.CompilerServices.CallerMemberName] string via = "")
    {
        if (change != HoverChange.None)
        {
            Log.Debug("island", "Hover", new { change = change.ToString(), via, mode = _state.Mode.ToString() });
        }

        if (change == HoverChange.Entered)
        {
            _state.PointerEntered();
        }
        else if (change == HoverChange.Exited)
        {
            _state.PointerExited();
        }
    }

    private void OnPointerEntered(object? sender, PointerEventArgs e)
    {
        if (!_dragging && !_pointerDown)
        {
            var p = e.GetPosition(Root);
            ApplyHover(ShapeIsSettled ? _hover.PointerArrived(p.X, p.Y, Capsule) : _hover.PointerAt(p.X, p.Y, Capsule));
        }
    }

    /// <summary>
    /// Safety net for hover: X servers (XWayland in particular) do not always deliver a motion event
    /// after the crossing into a shaped window, so a pointer that moved in and stopped would never
    /// count as hovering. Every 100 ms the pointer position is read from the server; movement into
    /// or out of the capsule is fed to the hover tracker like a motion event.
    /// </summary>
    private void OnHoverPoll()
    {
        if (_dragging || _pointerDown || _x?.Pointer() is not { } cursor)
        {
            return;
        }

        var previous = _polledPointer;
        _polledPointer = (cursor.X, cursor.Y);
        if (previous is not { } last || (last.X == cursor.X && last.Y == cursor.Y))
        {
            return; // no movement, no intent
        }

        var x = (cursor.X - Position.X) / Scale;
        var y = (cursor.Y - Position.Y) / Scale;
        var inside = Capsule.Contains(x, y);
        if (inside != _hover.IsInside && (ShapeIsSettled || !inside))
        {
            ApplyHover(_hover.PointerArrived(x, y, Capsule));
        }
    }

    private void UpdateHoverPoll()
    {
        var wanted = IsIslandVisible && _state.Mode is IslandMode.Compact or IslandMode.Retracted or IslandMode.Activity;
        if (wanted && !_hoverPoll.IsRunning)
        {
            _polledPointer = null;
            _hoverPoll.Start();
        }
        else if (!wanted && _hoverPoll.IsRunning)
        {
            _hoverPoll.Stop();
        }
    }

    private void OnPointerExited(object? sender, PointerEventArgs e)
    {
        if (!_dragging && !_pointerDown)
        {
            ApplyHover(_hover.PointerLeftWindow());
        }
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var point = e.GetCurrentPoint(Root);
        if (!Capsule.Contains(point.Position.X, point.Position.Y))
        {
            // A press beside the capsule is a click outside the island, never on it.
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

        // A capsule that just grew under a resting mouse (an event, a peek) is not a target yet. Once
        // the shape has been still for a moment, a press on it is plainly meant for it.
        if (e.Pointer.Type == PointerType.Mouse && !_hover.IsInside && ShapeIsSettled)
        {
            ApplyHover(_hover.PointerArrived(point.Position.X, point.Position.Y, Capsule));
        }

        if (e.Pointer.Type == PointerType.Mouse && !_hover.IsInside)
        {
            Log.Debug("island", "Unarmed click ignored");
            e.Handled = true;
            return;
        }

        if (!point.Properties.IsLeftButtonPressed)
        {
            return;
        }

        _pointerDown = true;
        _dragging = false;
        if (_x?.Pointer() is { } cursor)
        {
            _dragStartCursor = (cursor.X, cursor.Y);
            _dragGrab = IslandDrag.GrabPoint(cursor.X, cursor.Y, Position.X, Position.Y, Scale);
        }

        e.Pointer.Capture(Root);
        e.Handled = true;
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_pointerDown)
        {
            var p = e.GetPosition(Root);
            ApplyHover(_hover.PointerAt(p.X, p.Y, Capsule));
            return;
        }

        if (_x?.Pointer() is not { } cursor)
        {
            return;
        }

        var dx = cursor.X - _dragStartCursor.X;
        var dy = cursor.Y - _dragStartCursor.Y;
        if (!_dragging && dx * dx + dy * dy > Math.Pow(5 * Scale, 2))
        {
            _dragging = true;
            _state.BeginDrag();
            Cursor = new Cursor(StandardCursorType.SizeAll);
            _dragTicker.Start();
        }

        if (_dragging)
        {
            MoveDrag(cursor.X, cursor.Y);
        }
    }

    /// <summary>Keeps the grabbed point under the cursor.</summary>
    private void MoveDrag(int cursorX, int cursorY)
    {
        var (x, y) = IslandDrag.WindowPosition(cursorX, cursorY, _dragGrab, Scale);
        Position = new PixelPoint(x, y);
    }

    private void OnDragTick()
    {
        if (!_dragging)
        {
            _dragTicker.Stop();
            return;
        }

        if (_x?.Pointer() is not { } cursor)
        {
            return;
        }

        if (!cursor.Button1)
        {
            // The release was not delivered: drop where the cursor let go.
            EndDrag(commit: true);
            return;
        }

        MoveDrag(cursor.X, cursor.Y);
    }

    private void OnPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        if (_dragging && _x?.Pointer() is { Button1: true })
        {
            return; // the drag ticker carries on
        }

        EndDrag(commit: _dragging);
    }

    private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_pointerDown)
        {
            return;
        }

        var wasDragging = _dragging;
        EndDrag(commit: wasDragging);
        e.Pointer.Capture(null);
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
        _dragTicker.Stop();
        _state.EndDrag();
        Cursor = Cursor.Default;
        var placementDeferred = _placementDeferred;
        _placementDeferred = false;
        if (commit)
        {
            var t = _morph.Target;
            var s = Scale;
            var capsule = new PixelRect(
                Position.X + (int)Math.Round(t.X * s),
                Position.Y + (int)Math.Round(t.Y * s),
                (int)Math.Round(t.Width * s),
                (int)Math.Round(t.Height * s));
            DragCompleted?.Invoke(capsule);
        }
        else if (placementDeferred)
        {
            PlacementInvalidated?.Invoke();
        }
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && _state.Mode == IslandMode.Expanded)
        {
            _restoreFocusOnCollapse = true;
            _state.Dismiss();
            e.Handled = true;
        }
    }

    /// <summary>Clicking anywhere else closes the expanded island, like a popover.</summary>
    private void OnDeactivated(object? sender, EventArgs e)
    {
        if (SnapshotMode)
        {
            return;
        }

        if (_state.Mode == IslandMode.Expanded)
        {
            // A context menu or combo box popup of the island itself takes focus briefly: not "outside".
            Dispatcher.UIThread.Post(() =>
            {
                if (_state.Mode == IslandMode.Expanded && !IsActive && !HasOpenPopup())
                {
                    _restoreFocusOnCollapse = false;
                    _state.Dismiss();
                }
            }, DispatcherPriority.Background);
        }
    }

    private bool HasOpenPopup() =>
        Expanded.GetLogicalDescendants().OfType<ComboBox>().Any(c => c.IsDropDownOpen)
        || Expanded.GetLogicalDescendants().OfType<Control>().Any(c => c.ContextMenu?.IsOpen == true);

    /// <summary>The island lives as long as the app: a close request closes the island like Esc instead.</summary>
    private void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_exiting)
        {
            return;
        }

        e.Cancel = true;
        if (_state.Mode == IslandMode.Expanded)
        {
            _restoreFocusOnCollapse = true;
            _state.Dismiss();
        }
    }

    private void ActivateForKeyboard()
    {
        if (SnapshotMode)
        {
            return;
        }

        var active = _x?.ActiveWindow() ?? IntPtr.Zero;
        if (active != _xid)
        {
            _previousActive = active;
        }

        Activate();
        if (_x is not null && _xid != IntPtr.Zero)
        {
            _x.Activate(_xid);
        }

        var keyboard = _keyboardInitiated;
        var capture = _focusTaskCapture;
        Dispatcher.UIThread.Post(() =>
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
                Expanded.FocusFirst(keyboard);
            }
        }, DispatcherPriority.Background);
        _keyboardInitiated = false;
        _focusTaskCapture = false;
    }

    private void ReturnFocus()
    {
        if (_restoreFocusOnCollapse && _previousActive != IntPtr.Zero && _x is not null)
        {
            _x.Activate(_previousActive);
        }

        _restoreFocusOnCollapse = false;
        _previousActive = IntPtr.Zero;
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
