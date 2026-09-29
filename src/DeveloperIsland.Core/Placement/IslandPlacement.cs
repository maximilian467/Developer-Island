namespace DeveloperIsland.Core.Placement;

/// <summary>A rectangle in physical screen pixels.</summary>
public readonly record struct PixelRect(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;

    public int Bottom => Y + Height;

    public double CenterX => X + Width / 2.0;

    public double CenterY => Y + Height / 2.0;

    public bool Contains(double x, double y) => x >= X && x < Right && y >= Y && y < Bottom;
}

/// <summary>A display as reported by the OS.</summary>
public sealed record MonitorInfo(string DeviceName, PixelRect Bounds, PixelRect WorkArea, double Scale, bool IsPrimary);

/// <summary>Where the island ended up, and whether the saved monitor was unavailable.</summary>
public sealed record ResolvedPlacement(MonitorInfo Monitor, IslandAnchor Anchor, double OffsetX, double OffsetY, bool IsFallback);

/// <summary>Result of a drag: the new anchor and offset relative to it, in DIPs.</summary>
public sealed record DropPlacement(MonitorInfo Monitor, IslandAnchor Anchor, double OffsetX, double OffsetY);

/// <summary>
/// Pure placement math for the island window. Device-independent. It never special-cases hardware
/// vendors, only monitor geometry and DPI scale.
/// </summary>
public static class IslandPlacement
{
    /// <summary>Distance between the capsule and the work-area edge, in DIPs.</summary>
    public const double EdgeGap = 10;

    /// <summary>Offsets smaller than this (per axis) snap back to the preset anchor, in DIPs.</summary>
    public const double SnapDistance = 18;

    /// <summary>
    /// Picks the saved monitor by device name. If it is gone, falls back to the primary monitor at
    /// Top Center without offsets.
    /// </summary>
    public static ResolvedPlacement Resolve(IReadOnlyList<MonitorInfo> monitors, string? deviceName, IslandAnchor anchor, double offsetX, double offsetY)
    {
        if (monitors.Count == 0)
        {
            throw new ArgumentException("At least one monitor is required.", nameof(monitors));
        }

        var primary = monitors.FirstOrDefault(m => m.IsPrimary) ?? monitors[0];
        if (string.IsNullOrEmpty(deviceName))
        {
            return new ResolvedPlacement(primary, anchor, offsetX, offsetY, false);
        }

        var match = monitors.FirstOrDefault(m => string.Equals(m.DeviceName, deviceName, StringComparison.OrdinalIgnoreCase));
        return match is not null
            ? new ResolvedPlacement(match, anchor, offsetX, offsetY, false)
            : new ResolvedPlacement(primary, IslandAnchor.TopCenter, 0, 0, true);
    }

    /// <summary>The anchor point inside the island window, in DIPs.</summary>
    public static (double X, double Y) AnchorInWindow(IslandAnchor anchor, double windowWidth, double windowHeight, double margin)
    {
        var x = anchor.Horizontal() switch
        {
            HorizontalEdge.Left => margin,
            HorizontalEdge.Right => windowWidth - margin,
            _ => windowWidth / 2,
        };
        var y = anchor.Vertical() == VerticalEdge.Top ? margin : windowHeight - margin;
        return (x, y);
    }

    /// <summary>The preset anchor point on the monitor (no offset), in physical pixels.</summary>
    public static (double X, double Y) AnchorOnMonitor(MonitorInfo monitor, IslandAnchor anchor)
    {
        var wa = monitor.WorkArea;
        var gap = EdgeGap * monitor.Scale;
        var x = anchor.Horizontal() switch
        {
            HorizontalEdge.Left => wa.X + gap,
            HorizontalEdge.Right => wa.Right - gap,
            _ => wa.CenterX,
        };
        var y = anchor.Vertical() == VerticalEdge.Top ? wa.Y + gap : wa.Bottom - gap;
        return (x, y);
    }

    /// <summary>Window rectangle (physical pixels) for a window of the given DIP size.</summary>
    public static PixelRect WindowRect(MonitorInfo monitor, IslandAnchor anchor, double offsetX, double offsetY, double windowWidth, double windowHeight, double margin)
    {
        var s = monitor.Scale;
        var (ax, ay) = AnchorOnMonitor(monitor, anchor);
        ax += offsetX * s;
        ay += offsetY * s;

        // Keep the anchor point on the monitor so a stale offset can never hide the island.
        var b = monitor.Bounds;
        ax = Math.Clamp(ax, b.X + EdgeGap * s, b.Right - EdgeGap * s);
        ay = Math.Clamp(ay, b.Y, b.Bottom - EdgeGap * s);

        var (wx, wy) = AnchorInWindow(anchor, windowWidth, windowHeight, margin);
        return new PixelRect(
            (int)Math.Round(ax - wx * s),
            (int)Math.Round(ay - wy * s),
            (int)Math.Ceiling(windowWidth * s),
            (int)Math.Ceiling(windowHeight * s));
    }

    /// <summary>
    /// Converts where the user dropped the capsule into a monitor, anchor and offset. The anchor is
    /// chosen by position (thirds horizontally, halves vertically), so the island keeps growing away
    /// from the nearest edge. Small offsets snap to the preset.
    /// </summary>
    public static DropPlacement FromDrop(IReadOnlyList<MonitorInfo> monitors, PixelRect capsule)
    {
        var monitor = monitors.FirstOrDefault(m => m.Bounds.Contains(capsule.CenterX, capsule.CenterY))
            ?? monitors.MinBy(m => DistanceSquared(m.Bounds, capsule.CenterX, capsule.CenterY))
            ?? throw new ArgumentException("At least one monitor is required.", nameof(monitors));

        var wa = monitor.WorkArea;
        var relX = (capsule.CenterX - wa.X) / Math.Max(1, wa.Width);
        var relY = (capsule.CenterY - wa.Y) / Math.Max(1, wa.Height);
        var h = relX < 1.0 / 3 ? HorizontalEdge.Left : relX > 2.0 / 3 ? HorizontalEdge.Right : HorizontalEdge.Center;
        var v = relY < 0.5 ? VerticalEdge.Top : VerticalEdge.Bottom;
        var anchor = IslandAnchorExtensions.Compose(h, v);

        double px = h switch
        {
            HorizontalEdge.Left => capsule.X,
            HorizontalEdge.Right => capsule.Right,
            _ => capsule.CenterX,
        };
        double py = v == VerticalEdge.Top ? capsule.Y : capsule.Bottom;

        var (ax, ay) = AnchorOnMonitor(monitor, anchor);
        var ox = (px - ax) / monitor.Scale;
        var oy = (py - ay) / monitor.Scale;
        if (Math.Abs(ox) < SnapDistance)
        {
            ox = 0;
        }

        if (Math.Abs(oy) < SnapDistance)
        {
            oy = 0;
        }

        return new DropPlacement(monitor, anchor, Math.Round(ox, 1), Math.Round(oy, 1));
    }

    private static double DistanceSquared(PixelRect r, double x, double y)
    {
        var dx = Math.Max(Math.Max(r.X - x, 0), x - r.Right);
        var dy = Math.Max(Math.Max(r.Y - y, 0), y - r.Bottom);
        return dx * dx + dy * dy;
    }
}
