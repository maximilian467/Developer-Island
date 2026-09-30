using System.Runtime.InteropServices.WindowsRuntime;
using DeveloperIsland.Core.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace DeveloperIsland.UI.Island;

/// <summary>
/// Development support: renders the island's current state to a PNG without touching the desktop
/// (<c>--demo --snapshot=folder</c>). RenderTargetBitmap only sees XAML, so a XAML stand-in for the
/// composition capsule (fill and hairline, no shadow) is drawn behind the content while capturing.
/// </summary>
public sealed partial class IslandWindow
{
    private Border? _snapshotSurface;

    /// <summary>In snapshot mode the island never activates and ignores focus changes.</summary>
    public bool SnapshotMode { get; set; }

    public async Task SaveSnapshotAsync(string path, double padding = 24)
    {
        if (_current is null)
        {
            return;
        }

        var rect = BoundsOf(_current);
        var resources = Application.Current.Resources;
        _snapshotSurface ??= new Border
        {
            Background = (Brush)resources["IslandFillBrush"],
            BorderBrush = (Brush)resources["IslandHairlineBrush"],
            BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            IsHitTestVisible = false,
        };
        if (_snapshotSurface.Parent is null)
        {
            Root.Children.Insert(0, _snapshotSurface);
        }

        _snapshotSurface.Margin = new Thickness(rect.X, rect.Y, 0, 0);
        _snapshotSurface.Width = rect.Width;
        _snapshotSurface.Height = rect.Height;
        _snapshotSurface.CornerRadius = new CornerRadius(ReferenceEquals(_current, Expanded) ? ExpandedRadius : rect.Height / 2);
        Root.UpdateLayout();

        var bitmap = new RenderTargetBitmap();
        await bitmap.RenderAsync(Root);
        var pixels = await bitmap.GetPixelsAsync();
        var scale = bitmap.PixelWidth / Math.Max(1, Root.ActualWidth);

        uint Clamp(double v, int max) => (uint)Math.Clamp(v, 0, max);
        var left = Clamp((rect.X - padding) * scale, bitmap.PixelWidth);
        // The notch sits half above the screen edge; crop there to show only what is visible.
        var top = Clamp((ReferenceEquals(_current, Notch) ? ScreenEdgeY : rect.Y - padding) * scale, bitmap.PixelHeight);
        var right = Clamp((rect.Right + padding) * scale, bitmap.PixelWidth);
        var bottom = Clamp((rect.Bottom + padding) * scale, bitmap.PixelHeight);

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var stream = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, (uint)bitmap.PixelWidth, (uint)bitmap.PixelHeight, 96, 96, pixels.ToArray());
        encoder.BitmapTransform.Bounds = new BitmapBounds { X = left, Y = top, Width = right - left, Height = bottom - top };
        await encoder.FlushAsync();

        var bytes = new byte[stream.Size];
        stream.Seek(0);
        using (var reader = new DataReader(stream))
        {
            await reader.LoadAsync((uint)stream.Size);
            reader.ReadBytes(bytes);
        }

        await File.WriteAllBytesAsync(path, bytes);
        Log.Info("snapshot", "Saved", new { file = Path.GetFileName(path) });
    }
}
