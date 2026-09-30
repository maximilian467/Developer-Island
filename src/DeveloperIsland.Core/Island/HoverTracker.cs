namespace DeveloperIsland.Core.Island;

public enum HoverChange
{
    None,
    Entered,
    Exited,
}

/// <summary>An axis-aligned rectangle in window DIPs.</summary>
public readonly record struct CapsuleBounds(double X, double Y, double Width, double Height)
{
    public bool Contains(double x, double y) => Width > 0 && x >= X && x < X + Width && y >= Y && y < Y + Height;
}

/// <summary>
/// Decides whether the pointer is over the <b>visible</b> capsule, independent of the window's
/// hit-test region (which is larger while a morph runs and around the expanded shadow).
/// <list type="bullet">
/// <item>Entering requires real pointer movement: a capsule that grows under a resting pointer is not hover intent.</item>
/// <item>Leaving is detected from movement, from the window losing the pointer, and from
/// re-validation after the capsule changed shape (the OS does not report a leave when a shrinking
/// region moves out from under a still pointer).</item>
/// </list>
/// </summary>
public sealed class HoverTracker
{
    private double _lastX = double.NaN;
    private double _lastY = double.NaN;

    public bool IsInside { get; private set; }

    /// <summary>Pointer moved (or entered the window) at the given position.</summary>
    public HoverChange PointerAt(double x, double y, CapsuleBounds capsule)
    {
        var moved = !double.IsNaN(_lastX) && (Math.Abs(x - _lastX) > 0.5 || Math.Abs(y - _lastY) > 0.5);
        _lastX = x;
        _lastY = y;
        var inside = capsule.Contains(x, y);

        if (inside && !IsInside && moved)
        {
            IsInside = true;
            return HoverChange.Entered;
        }

        if (!inside && IsInside)
        {
            IsInside = false;
            return HoverChange.Exited;
        }

        return HoverChange.None;
    }

    /// <summary>The window no longer receives the pointer.</summary>
    public HoverChange PointerLeftWindow()
    {
        _lastX = double.NaN;
        _lastY = double.NaN;
        if (!IsInside)
        {
            return HoverChange.None;
        }

        IsInside = false;
        return HoverChange.Exited;
    }

    /// <summary>
    /// Re-checks a resting pointer after the capsule changed shape. Never produces Entered (no
    /// movement means no intent), only Exited when the capsule moved away from the pointer.
    /// </summary>
    public HoverChange Revalidate(double x, double y, CapsuleBounds capsule)
    {
        if (IsInside && !capsule.Contains(x, y))
        {
            IsInside = false;
            return HoverChange.Exited;
        }

        return HoverChange.None;
    }
}
