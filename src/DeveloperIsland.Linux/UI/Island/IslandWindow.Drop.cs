using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Platform;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using DeveloperIsland.Core.Diagnostics;
using DeveloperIsland.Core.HomeCloud;
using DeveloperIsland.Core.Island;
using DeveloperIsland.Core.Placement;
using DeveloperIsland.ViewModels;
using PixelRect = DeveloperIsland.Core.Placement.PixelRect;

namespace DeveloperIsland.UI.Island;

/// <summary>
/// Home Cloud: files dragged onto the island turn it into a drop zone (<see cref="IslandMode.Drop"/>);
/// dropped files are copied to the configured folder (a mounted network share, an SMB/NFS mount, or
/// a GVFS path under /run/user/…/gvfs). With Home Cloud off nothing here is attached.
/// </summary>
public sealed partial class IslandWindow
{
    private readonly HomeCloudUploader _uploader = new(new FileSystemSink());
    private readonly CancellationTokenSource _dropCancel = new();
    private bool _homeCloudEnabled;
    private string _homeCloudTarget = "";
    private CancellationTokenSource? _reachabilityCheck;

    /// <summary>What the drop zone shows.</summary>
    internal DropViewModel DropFeedback { get; } = new();

    private void InitializeDropTarget()
    {
        DropZone.ViewModel = DropFeedback;
        _state.DropPhaseChanged += OnDropPhaseChanged;
        _state.ModeChanged += (_, _) => UpdateDropArming();
        AddHandler(DragDrop.DragEnterEvent, OnFileDragEnter, handledEventsToo: true);
        AddHandler(DragDrop.DragOverEvent, OnFileDragOver, handledEventsToo: true);
        AddHandler(DragDrop.DragLeaveEvent, OnFileDragLeave, handledEventsToo: true);
        AddHandler(DragDrop.DropEvent, OnFileDrop, handledEventsToo: true);
    }

    // Arming (notch and hidden) -----------------------------------------------------------------------
    //
    // A notch or a hidden island has no drop target under the pointer. While Home Cloud is on, a thin
    // invisible window (DropArmWindow) sits over the arm zone at the top edge and accepts drags
    // (XDND); a drag entering it opens the island as a drop zone, and the drop itself lands on the
    // island, whose input region then covers the arm zone.

    private DropArmWindow? _armWindow;
    private bool _armed;

    /// <summary>The arm zone in window DIPs (empty when it does not apply), kept current with placement.</summary>
    private Rect _armRegion;

    private void UpdateDropArming()
    {
        var topAnchored = _anchor.Vertical() == VerticalEdge.Top;
        var zone = _homeCloudEnabled && topAnchored && !SnapshotMode ? ArmZoneOnScreen() : null;
        _armRegion = zone is { } z ? ToWindowRegion(z) : default;
        var wanted = !_exiting && zone is not null && _armRegion.Width > 0
            && HomeCloudDropPolicy.ShouldArm(_homeCloudEnabled, _state.Mode, topAnchored);

        if (wanted)
        {
            if (_armWindow is null)
            {
                _armWindow = new DropArmWindow(() => OnArmChanged(true));
                Log.Info("homecloud", "Drag arming zone shown", new { mode = _state.Mode.ToString() });
            }

            _armWindow.ShowAt(zone!.Value);
            return;
        }

        if (_armWindow is not null)
        {
            _armWindow.Close();
            _armWindow = null;
            Log.Info("homecloud", "Drag arming zone removed", new { mode = _state.Mode.ToString() });
        }

        Disarm();
    }

    private void OnArmChanged(bool armed)
    {
        if (armed == _armed || (armed && _state.Mode is not (IslandMode.Retracted or IslandMode.Hidden)))
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

        // The drag is over the zone already: open the drop zone at once.
        if (_state.FileDragEntered())
        {
            ShowIsland();
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
        ApplyWindowRegion(EffectiveRegion(), force: true);
    }

    /// <summary>The region the X server hit-tests: the view's, plus the arm zone while armed.</summary>
    private Rect EffectiveRegion()
    {
        if (!_armed || _armRegion.Width <= 0)
        {
            return _regionRect;
        }

        return _state.Mode == IslandMode.Hidden ? _armRegion : Union(_regionRect, _armRegion);
    }

    /// <summary>The arm zone on the island's monitor, centered over where the drop zone opens.</summary>
    private PixelRect? ArmZoneOnScreen()
    {
        if (IslandMonitor is not { } monitor)
        {
            return null;
        }

        var centerX = _anchor.Horizontal() switch
        {
            HorizontalEdge.Left => EdgeMargin + DropView.ZoneWidth / 2,
            HorizontalEdge.Right => WindowWidth - EdgeMargin - DropView.ZoneWidth / 2,
            _ => WindowWidth / 2,
        };
        return HomeCloudArmZone.OnScreen(Position.X + centerX * Scale, monitor.WorkArea.Y, Scale);
    }

    private Rect ToWindowRegion(PixelRect zone)
    {
        var s = Scale;
        var r = Clamp(new Rect((zone.X - Position.X) / s, (zone.Y - Position.Y) / s, zone.Width / s, zone.Height / s));
        return r.Width < 1 || r.Height < 1 ? default : r;
    }

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

    /// <summary>Applies the Home Cloud settings; switching off removes every drop target at once.</summary>
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
        DragDrop.SetAllowDrop(Root, enabled);
        Log.Info("homecloud", enabled ? "Drop target enabled" : "Drop target disabled");
        UpdateDropArming();
    }

    /// <summary>The drop pipeline after the files are known: upload in the background with progress, then the result.</summary>
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
            var progress = new Progress<HomeCloudProgress>(DropFeedback.Report);
            result = await Task.Run(() => _uploader.UploadAsync(paths, target, progress, token));
        }

        Log.Info("homecloud", "Drop handled", new { items = paths.Count, status = result.Status.ToString(), files = result.FileCount, bytes = result.Bytes, ms = (long)result.Duration.TotalMilliseconds });
        ShowDropResult(result);
        return result;
    }

    private void StopDropTarget()
    {
        _dropCancel.Cancel();
        _armWindow?.Close();
        _armWindow = null;
    }

    private static bool HasFiles(DragEventArgs e) => e.DataTransfer.Contains(DataFormat.File);

    private void OnFileDragEnter(object? sender, DragEventArgs e)
    {
        var hasFiles = HasFiles(e);
        if (HomeCloudDropPolicy.AcceptsDrag(_homeCloudEnabled, hasFiles, _state.Mode, _state.DropPhase))
        {
            _state.FileDragEntered();
        }

        OfferCopy(e, hasFiles);
    }

    private void OnFileDragOver(object? sender, DragEventArgs e) => OfferCopy(e, HasFiles(e));

    private void OfferCopy(DragEventArgs e, bool hasFiles)
    {
        e.DragEffects = HomeCloudDropPolicy.AcceptsDrop(_homeCloudEnabled, hasFiles, _state.Mode, _state.DropPhase)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    /// <summary>A leave also arrives when the drag moves between elements: decide on the real cursor once settled.</summary>
    private void OnFileDragLeave(object? sender, DragEventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (ProbeDrop() != true)
            {
                _state.FileDragLeft();
                Disarm();
            }
        });
    }

    private async void OnFileDrop(object? sender, DragEventArgs e)
    {
        if (!HomeCloudDropPolicy.AcceptsDrop(_homeCloudEnabled, HasFiles(e), _state.Mode, _state.DropPhase))
        {
            return;
        }

        e.DragEffects = DragDropEffects.Copy;
        e.Handled = true;
        var paths = new List<string>();
        try
        {
            foreach (var item in e.DataTransfer.TryGetFiles() ?? [])
            {
                // Virtual items (inside an archive, from a phone over MTP) have no local path and cannot be copied.
                if (item.TryGetLocalPath() is { Length: > 0 } path)
                {
                    paths.Add(path);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn("homecloud", "Dropped items could not be read", new { error = ex.GetType().Name });
        }

        Disarm();
        try
        {
            await UploadDroppedAsync(paths);
        }
        catch (Exception ex)
        {
            Log.Error("homecloud", "Drop failed", data: new { error = ex.GetType().Name });
            ShowDropResult(HomeCloudUploadResult.Failed("Copy failed"));
        }
    }

    /// <summary>A drag is still over the zone while the primary button is held and the cursor is inside the region.</summary>
    private bool? ProbeDrop()
    {
        if (SnapshotMode || PointerInWindow() is not { } p)
        {
            return null;
        }

        return EffectiveRegion().Contains(new Point(p.X, p.Y)) && p.Button;
    }
}

/// <summary>
/// A thin, fully transparent window over the Home Cloud arm zone at the top edge, there only to
/// receive a file drag (XDND) while the island is a notch or hidden. It never takes focus and never
/// shows anything.
/// </summary>
internal sealed class DropArmWindow : Window
{
    private readonly Action _entered;

    public DropArmWindow(Action entered)
    {
        _entered = entered;
        SystemDecorations = SystemDecorations.None;
        Background = Avalonia.Media.Brushes.Transparent;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        ShowInTaskbar = false;
        ShowActivated = false;
        CanResize = false;
        Topmost = true;
        Focusable = false;
        Title = "Developer Island drop zone";
        X11Properties.SetNetWmWindowType(this, X11NetWmWindowType.Utility);
        var target = new Panel { Background = Avalonia.Media.Brushes.Transparent };
        DragDrop.SetAllowDrop(target, true);
        target.AddHandler(DragDrop.DragEnterEvent, (_, e) =>
        {
            e.DragEffects = e.DataTransfer.Contains(DataFormat.File) ? DragDropEffects.Copy : DragDropEffects.None;
            if (e.DataTransfer.Contains(DataFormat.File))
            {
                // Step aside so the island underneath receives the rest of the drag and the drop.
                Hide();
                _entered();
            }
        });
        Content = target;
    }

    public void ShowAt(PixelRect zone)
    {
        Position = new PixelPoint(zone.X, zone.Y);
        Width = zone.Width / (RenderScaling > 0 ? RenderScaling : 1);
        Height = zone.Height / (RenderScaling > 0 ? RenderScaling : 1);
        if (!IsVisible)
        {
            Show();
        }
    }
}
