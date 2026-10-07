using DeveloperIsland.Core.Diagnostics;
using DeveloperIsland.Core.HomeCloud;
using DeveloperIsland.Core.Island;
using DeveloperIsland.Core.Placement;
using DeveloperIsland.Platform.Windowing;
using DeveloperIsland.ViewModels;
using Microsoft.UI.Xaml;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.Storage;
using static DeveloperIsland.Platform.Native.NativeMethods;

namespace DeveloperIsland.UI.Island;

/// <summary>
/// Home Cloud: files dragged onto the island turn it into a drop zone (<see cref="IslandMode.Drop"/>);
/// dropped files are copied to the configured folder. With Home Cloud off nothing here is attached:
/// no drop handlers, no AllowDrop, no region change.
/// </summary>
public sealed partial class IslandWindow
{
    private readonly HomeCloudUploader _uploader = new(new FileSystemSink());
    private readonly CancellationTokenSource _dropCancel = new();
    private bool _homeCloudEnabled;
    private string _homeCloudTarget = "";
    private (RoutedEvent Event, DragEventHandler Handler)[]? _dragHandlers;
    private CancellationTokenSource? _reachabilityCheck;

    /// <summary>What the drop zone shows.</summary>
    internal DropViewModel DropFeedback { get; } = new();

    private void InitializeDropTarget()
    {
        DropZone.ViewModel = DropFeedback;
        _state.DropPhaseChanged += OnDropPhaseChanged;

        // After OnModeChanged: a mode change first settles the view, then the hook follows.
        _state.ModeChanged += (_, _) => UpdateDropArming();
    }

    // Arming (notch and hidden) -----------------------------------------------------------------------
    //
    // A notch or a hidden island has no drop target on screen. While Home Cloud is on, a mouse hook
    // (DragArming) watches for a drag that heads for the top edge above the island; then the window
    // region (and, when hidden, the window, without activation and fully transparent) covers the arm
    // zone, so Windows delivers DragEnter and the island opens as a drop zone. Releasing the button
    // or leaving the zone puts region and visibility back.

    private DragArming? _arming;
    private bool _armed;

    /// <summary>The arm zone in window DIPs (empty when it does not apply), kept current with placement.</summary>
    private Rect _armRegion = Rect.Empty;

    /// <summary>Installs or removes the hook to match the mode, settings and placement.</summary>
    private void UpdateDropArming()
    {
        var topAnchored = _anchor.Vertical() == VerticalEdge.Top;
        var zone = _homeCloudEnabled && topAnchored ? ArmZoneOnScreen() : null;
        _armRegion = zone is { } z ? ToWindowRegion(z) : Rect.Empty;
        var wanted = !_exiting && zone is not null && !_armRegion.IsEmpty
            && HomeCloudDropPolicy.ShouldArm(_homeCloudEnabled, _state.Mode, topAnchored);

        if (wanted)
        {
            if (_arming is null)
            {
                _arming = new DragArming(zone!.Value, armed => DispatcherQueue.TryEnqueue(() => OnArmChanged(armed)));
                Log.Info("homecloud", "Drag arming hook installed", new { mode = _state.Mode.ToString(), hooked = _arming.IsInstalled });
            }
            else
            {
                _arming.SetZone(zone!.Value);
            }

            if (_armed)
            {
                // Notch to hidden (or back) in the middle of an armed drag.
                ShowArmed();
            }

            return;
        }

        if (_arming is not null)
        {
            _arming.Dispose();
            _arming = null;
            Log.Info("homecloud", "Drag arming hook removed", new { mode = _state.Mode.ToString() });
        }

        Disarm();
    }

    /// <summary>On the UI thread, posted by the hook thread once per change.</summary>
    private void OnArmChanged(bool armed)
    {
        if (_arming is null || armed == _armed || (armed && _state.Mode is not (IslandMode.Retracted or IslandMode.Hidden)))
        {
            return;
        }

        if (!armed)
        {
            Disarm();
            return;
        }

        _armed = true;
        Log.Debug("homecloud", "Drop target armed", new { mode = _state.Mode.ToString() });
        ShowArmed();
    }

    private void ShowArmed()
    {
        if (_state.Mode == IslandMode.Hidden && !_visible)
        {
            // Nothing may show while only armed: the window is there for Windows' drop routing alone.
            Root.Opacity = 0;
            AppWindow.Show(activateWindow: false);
            if (_alwaysOnTop)
            {
                IslandWindowChrome.RaiseAboveTopmost(_hwnd);
            }
        }
        else
        {
            Root.Opacity = 1;
        }

        ApplyWindowRegion(EffectiveRegion(), force: false);
    }

    private void Disarm()
    {
        if (!_armed)
        {
            return;
        }

        _armed = false;
        Log.Debug("homecloud", "Drop target disarmed", new { mode = _state.Mode.ToString() });
        Root.Opacity = 1;
        if (_state.Mode == IslandMode.Hidden && !_visible)
        {
            AppWindow.Hide();
        }

        ApplyWindowRegion(EffectiveRegion(), force: true);
    }

    /// <summary>The region Windows hit-tests: the view's, plus the arm zone while armed.</summary>
    private Rect EffectiveRegion()
    {
        if (!_armed || _armRegion.IsEmpty)
        {
            return _regionRect;
        }

        return _state.Mode == IslandMode.Hidden ? _armRegion : Union(_regionRect, _armRegion);
    }

    /// <summary>The arm zone on the island's monitor, centered over where the drop zone opens.</summary>
    private PixelRect? ArmZoneOnScreen()
    {
        if (!GetWindowRect(_hwnd, out var window))
        {
            return null;
        }

        var monitor = MonitorFromWindow(_hwnd, MONITOR_DEFAULTTONEAREST);
        var info = new MONITORINFOEX { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFOEX>() };
        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info))
        {
            return null;
        }

        var centerX = _anchor.Horizontal() switch
        {
            HorizontalEdge.Left => EdgeMargin + DropView.ZoneWidth / 2,
            HorizontalEdge.Right => WindowWidth - EdgeMargin - DropView.ZoneWidth / 2,
            _ => WindowWidth / 2,
        };
        return HomeCloudArmZone.OnScreen(window.Left + centerX * _scale, info.rcMonitor.Top, _scale);
    }

    private Rect ToWindowRegion(PixelRect zone)
    {
        if (!GetWindowRect(_hwnd, out var window))
        {
            return Rect.Empty;
        }

        var r = Clamp(new Rect((zone.X - window.Left) / _scale, (zone.Y - window.Top) / _scale, zone.Width / _scale, zone.Height / _scale));
        return r.Width < 1 || r.Height < 1 ? Rect.Empty : r;
    }

    /// <summary>
    /// Every way into the zone (a real drag, the debug path, a snapshot) passes here: a new hover asks
    /// once whether the server can be reached, so the zone can say so before the drop.
    /// </summary>
    private void OnDropPhaseChanged(DropPhase phase)
    {
        _reachabilityCheck?.Cancel();
        _reachabilityCheck = null;
        switch (phase)
        {
            case DropPhase.Hover:
                DropFeedback.BeginHover();
                if (!SnapshotMode)
                {
                    _ = CheckReachabilityAsync();
                }

                break;
            case DropPhase.Uploading:
                DropFeedback.BeginUpload();
                break;
        }
    }

    private async Task CheckReachabilityAsync()
    {
        var check = new CancellationTokenSource();
        _reachabilityCheck = check;
        try
        {
            var result = await HomeCloudReachability.CheckAsync(_homeCloudTarget, TimeSpan.FromSeconds(2), check.Token);
            if (ReferenceEquals(_reachabilityCheck, check) && _state.DropPhase == DropPhase.Hover)
            {
                DropFeedback.SetReachability(result);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Log.Debug("homecloud", "Reachability check failed", new { error = ex.GetType().Name });
        }
    }

    /// <summary>Shows a finished upload in the zone, which then closes after a moment.</summary>
    internal void ShowDropResult(HomeCloudUploadResult result)
    {
        DropFeedback.Finish(result);
        _state.FinishDrop(result.Status == HomeCloudUploadStatus.Succeeded);
    }

    /// <summary>Applies the Home Cloud settings; switching off removes every drop hook at once.</summary>
    public void ConfigureHomeCloud(bool enabled, string targetPath, HomeCloudDropSide side)
    {
        _homeCloudTarget = targetPath;
        DropZone.SetSide(side);
        if (enabled == _homeCloudEnabled)
        {
            return;
        }

        _homeCloudEnabled = enabled;
        _state.SetDropEnabled(enabled);
        Root.AllowDrop = enabled;
        _dragHandlers ??=
        [
            (UIElement.DragEnterEvent, new DragEventHandler(OnFileDragEnter)),
            (UIElement.DragOverEvent, new DragEventHandler(OnFileDragOver)),
            (UIElement.DragLeaveEvent, new DragEventHandler(OnFileDragLeave)),
            (UIElement.DropEvent, new DragEventHandler(OnFileDrop)),
        ];
        foreach (var (routedEvent, handler) in _dragHandlers)
        {
            if (enabled)
            {
                // Handled events too: a text box in the expanded island must not swallow a file drag.
                Root.AddHandler(routedEvent, handler, true);
            }
            else
            {
                Root.RemoveHandler(routedEvent, handler);
            }
        }

        Log.Info("homecloud", enabled ? "Drop target enabled" : "Drop target disabled");
        UpdateDropArming();
    }

    /// <summary>
    /// The drop pipeline after the files are known (a real drop, or the <c>--homecloud-upload</c>
    /// debug path): upload in the background with progress in the zone, then show the result.
    /// </summary>
    public async Task<HomeCloudUploadResult> UploadDroppedAsync(IReadOnlyList<string> paths)
    {
        if (!_state.BeginDropUpload())
        {
            return HomeCloudUploadResult.Failed("Home Cloud is off");
        }

        HomeCloudUploadResult result;
        if (paths.Count == 0)
        {
            result = HomeCloudUploadResult.Failed("Nothing to copy");
        }
        else
        {
            var target = _homeCloudTarget;
            var token = _dropCancel.Token;

            // Created here, so reports arrive on the UI thread.
            var progress = new Progress<HomeCloudProgress>(DropFeedback.Report);

            // The uploader's first steps touch the share synchronously; never on the UI thread.
            result = await Task.Run(() => _uploader.UploadAsync(paths, target, progress, token));
        }

        Log.Info("homecloud", "Drop handled", new { items = paths.Count, status = result.Status.ToString(), files = result.FileCount, bytes = result.Bytes, ms = (long)result.Duration.TotalMilliseconds });
        ShowDropResult(result);
        return result;
    }

    private void StopDropTarget() => _dropCancel.Cancel();

    private bool HasFiles(DragEventArgs e) => e.DataView.Contains(StandardDataFormats.StorageItems);

    private void OnFileDragEnter(object sender, DragEventArgs e)
    {
        var hasFiles = HasFiles(e);
        if (HomeCloudDropPolicy.AcceptsDrag(_homeCloudEnabled, hasFiles, _state.Mode, _state.DropPhase))
        {
            _state.FileDragEntered();
        }

        OfferCopy(e, hasFiles);
    }

    private void OnFileDragOver(object sender, DragEventArgs e) => OfferCopy(e, HasFiles(e));

    private void OfferCopy(DragEventArgs e, bool hasFiles)
    {
        if (HomeCloudDropPolicy.AcceptsDrop(_homeCloudEnabled, hasFiles, _state.Mode, _state.DropPhase))
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
            e.DragUIOverride.Caption = "Save to Home Cloud";
            e.DragUIOverride.IsGlyphVisible = true;
        }
        else
        {
            e.AcceptedOperation = DataPackageOperation.None;
        }

        e.Handled = true;
    }

    /// <summary>
    /// A leave also arrives when the drag only moves between elements inside the window: decide on
    /// the real cursor once the move has settled.
    /// </summary>
    private void OnFileDragLeave(object sender, DragEventArgs e)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (ProbeDrop() != true)
            {
                _state.FileDragLeft();
            }
        });
    }

    private async void OnFileDrop(object sender, DragEventArgs e)
    {
        if (!HomeCloudDropPolicy.AcceptsDrop(_homeCloudEnabled, HasFiles(e), _state.Mode, _state.DropPhase))
        {
            return;
        }

        e.AcceptedOperation = DataPackageOperation.Copy;
        e.Handled = true;
        var paths = new List<string>();
        var deferral = e.GetDeferral();
        try
        {
            foreach (var item in await e.DataView.GetStorageItemsAsync())
            {
                // Virtual items (inside a zip, from a phone) have no path and cannot be copied.
                if (item is IStorageItem { Path: { Length: > 0 } path })
                {
                    paths.Add(path);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn("homecloud", "Dropped items could not be read", new { error = ex.GetType().Name });
        }
        finally
        {
            deferral.Complete();
        }

        try
        {
            await UploadDroppedAsync(paths);
        }
        catch (Exception ex)
        {
            Log.Error("homecloud", "Drop failed", ex);
            ShowDropResult(HomeCloudUploadResult.Failed("Copy failed"));
        }
    }

    /// <summary>
    /// For the drop zone's re-check: a drag is still over the zone while the primary button is held
    /// and the cursor is inside the window region. Null in snapshot mode (no real pointer).
    /// </summary>
    private bool? ProbeDrop()
    {
        if (SnapshotMode || !GetCursorPos(out var cursor) || !GetWindowRect(_hwnd, out var window))
        {
            return null;
        }

        var inside = _regionRect.Contains(new Point((cursor.X - window.Left) / _scale, (cursor.Y - window.Top) / _scale));
        return inside && IsPrimaryButtonDown();
    }
}
