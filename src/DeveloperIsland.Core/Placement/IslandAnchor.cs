namespace DeveloperIsland.Core.Placement;

/// <summary>Where the island rests on its monitor. The island grows away from this point.</summary>
public enum IslandAnchor
{
    TopLeft,
    TopCenter,
    TopRight,
    BottomLeft,
    BottomCenter,
    BottomRight,
}

public enum HorizontalEdge
{
    Left,
    Center,
    Right,
}

public enum VerticalEdge
{
    Top,
    Bottom,
}

public static class IslandAnchorExtensions
{
    public static HorizontalEdge Horizontal(this IslandAnchor anchor) => anchor switch
    {
        IslandAnchor.TopLeft or IslandAnchor.BottomLeft => HorizontalEdge.Left,
        IslandAnchor.TopRight or IslandAnchor.BottomRight => HorizontalEdge.Right,
        _ => HorizontalEdge.Center,
    };

    public static VerticalEdge Vertical(this IslandAnchor anchor) =>
        anchor is IslandAnchor.BottomLeft or IslandAnchor.BottomCenter or IslandAnchor.BottomRight ? VerticalEdge.Bottom : VerticalEdge.Top;

    public static IslandAnchor Compose(HorizontalEdge h, VerticalEdge v) => (h, v) switch
    {
        (HorizontalEdge.Left, VerticalEdge.Top) => IslandAnchor.TopLeft,
        (HorizontalEdge.Center, VerticalEdge.Top) => IslandAnchor.TopCenter,
        (HorizontalEdge.Right, VerticalEdge.Top) => IslandAnchor.TopRight,
        (HorizontalEdge.Left, VerticalEdge.Bottom) => IslandAnchor.BottomLeft,
        (HorizontalEdge.Center, VerticalEdge.Bottom) => IslandAnchor.BottomCenter,
        _ => IslandAnchor.BottomRight,
    };

    public static string DisplayName(this IslandAnchor anchor) => anchor switch
    {
        IslandAnchor.TopLeft => "Top Left",
        IslandAnchor.TopCenter => "Top Center",
        IslandAnchor.TopRight => "Top Right",
        IslandAnchor.BottomLeft => "Bottom Left",
        IslandAnchor.BottomCenter => "Bottom Center",
        _ => "Bottom Right",
    };
}
