using System.Runtime.InteropServices.WindowsRuntime;
using DeveloperIsland.Core.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace DeveloperIsland.UI.Components;

/// <summary>Development support: renders a XAML element to a PNG (<c>--demo --snapshot=folder</c>).</summary>
internal static class SnapshotWriter
{
    /// <param name="crop">Optional crop in DIPs, relative to the element.</param>
    public static async Task SaveAsync(UIElement element, string path, Rect? crop = null)
    {
        var bitmap = new RenderTargetBitmap();
        await bitmap.RenderAsync(element);
        var pixels = await bitmap.GetPixelsAsync();
        var scale = bitmap.PixelWidth / Math.Max(1, element.ActualSize.X);

        var bounds = new BitmapBounds { X = 0, Y = 0, Width = (uint)bitmap.PixelWidth, Height = (uint)bitmap.PixelHeight };
        if (crop is { } c)
        {
            uint Clamp(double v, int max) => (uint)Math.Clamp(v, 0, max);
            var left = Clamp(c.X * scale, bitmap.PixelWidth);
            var top = Clamp(c.Y * scale, bitmap.PixelHeight);
            var right = Clamp(c.Right * scale, bitmap.PixelWidth);
            var bottom = Clamp(c.Bottom * scale, bitmap.PixelHeight);
            bounds = new BitmapBounds { X = left, Y = top, Width = Math.Max(1, right - left), Height = Math.Max(1, bottom - top) };
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var stream = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, (uint)bitmap.PixelWidth, (uint)bitmap.PixelHeight, 96, 96, pixels.ToArray());
        encoder.BitmapTransform.Bounds = bounds;
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
