using DeveloperIsland.Core.Diagnostics;
using DeveloperIsland.Core.HomeCloud;
using DeveloperIsland.Core.Island;
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

            // The uploader's first steps touch the share synchronously; never on the UI thread.
            result = await Task.Run(() => _uploader.UploadAsync(paths, target, null, token));
        }

        Log.Info("homecloud", "Drop handled", new { items = paths.Count, status = result.Status.ToString(), files = result.FileCount, bytes = result.Bytes, ms = (long)result.Duration.TotalMilliseconds });
        _state.FinishDrop(result.Status == HomeCloudUploadStatus.Succeeded);
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
            _state.FinishDrop(succeeded: false);
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
