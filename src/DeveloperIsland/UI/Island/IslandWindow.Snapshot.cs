using DeveloperIsland.UI.Components;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

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

        // The notch sits half above the screen edge; crop there to show only what is visible.
        var top = ReferenceEquals(_current, Notch) ? ScreenEdgeY : rect.Y - padding;
        var crop = new Rect(rect.X - padding, top, rect.Width + 2 * padding, rect.Bottom + padding - top);
        await SnapshotWriter.SaveAsync(Root, path, crop);
    }
}
