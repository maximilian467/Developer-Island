namespace DeveloperIsland.Core.Placement;

/// <summary>
/// Drag math for the island window. The grabbed point is kept in DIPs, so it stays under the cursor
/// when the window crosses into a monitor with another scale and Windows resizes it.
/// </summary>
public static class IslandDrag
{
    /// <summary>Where the cursor holds the window, in DIPs from its top-left corner.</summary>
    public static (double X, double Y) GrabPoint(int cursorX, int cursorY, int windowX, int windowY, double scale) =>
        ((cursorX - windowX) / scale, (cursorY - windowY) / scale);

    /// <summary>The window's top-left corner, in physical pixels, that puts the grab point under the cursor.</summary>
    public static (int X, int Y) WindowPosition(int cursorX, int cursorY, (double X, double Y) grab, double scale) =>
        ((int)Math.Round(cursorX - grab.X * scale), (int)Math.Round(cursorY - grab.Y * scale));
}
