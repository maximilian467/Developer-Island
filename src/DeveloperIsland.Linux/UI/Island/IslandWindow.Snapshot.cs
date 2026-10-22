using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using DeveloperIsland.Core.Diagnostics;

namespace DeveloperIsland.UI.Island;

/// <summary>
/// Development support: renders the island's current state to a PNG without touching the desktop
/// (<c>--demo --snapshot=folder</c>), cropped around the capsule.
/// </summary>
public sealed partial class IslandWindow
{
    public async Task SaveSnapshotAsync(string path, double padding = 24)
    {
        if (_current is null)
        {
            return;
        }

        await Task.Yield();
        Root.UpdateLayout();
        var scale = 2.0;
        var size = new PixelSize((int)(WindowWidth * scale), (int)(WindowHeight * scale));
        using var bitmap = new RenderTargetBitmap(size, new Vector(96 * scale, 96 * scale));
        bitmap.Render(Root);

        var rect = _morph.Target;
        var top = ReferenceEquals(_current, Notch) ? ScreenEdgeY : rect.Y - padding;
        var crop = new Rect(rect.X - padding, top, rect.Width + 2 * padding, rect.Bottom + padding - top)
            .Intersect(new Rect(0, 0, WindowWidth, WindowHeight));
        var pixels = new PixelRect((int)(crop.X * scale), (int)(crop.Y * scale), (int)(crop.Width * scale), (int)(crop.Height * scale));

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var cropped = new WriteableBitmap(pixels.Size, bitmap.Dpi, Avalonia.Platform.PixelFormat.Bgra8888, Avalonia.Platform.AlphaFormat.Premul);
        using (var buffer = cropped.Lock())
        {
            bitmap.CopyPixels(pixels, buffer.Address, buffer.RowBytes * pixels.Height, buffer.RowBytes);
        }

        cropped.Save(path);
        Log.Info("snapshot", "Saved", new { file = Path.GetFileName(path) });
    }
}
