using DeveloperIsland.Core.Placement;

namespace DeveloperIsland.Core.HomeCloud;

/// <summary>
/// Decides from raw mouse input whether a drag is heading for the arm zone. A press, then movement
/// beyond the system drag distance, then the cursor inside the zone arms; leaving the zone or
/// releasing the button disarms. A press inside the zone never arms (tab strips, title bars and
/// text sit there; a file drag comes from elsewhere), and neither does a hidden cursor (games).
/// Not thread-safe: the caller serializes.
/// </summary>
public sealed class DragArmDetector(int dragWidth, int dragHeight)
{
    private bool _pressed;
    private bool _dragging;
    private bool _startedInZone;
    private int _startX;
    private int _startY;

    public PixelRect Zone { get; set; }

    public bool IsArmed { get; private set; }

    /// <summary>Returns whether <see cref="IsArmed"/> changed (likewise for the other inputs).</summary>
    public bool ButtonDown(int x, int y)
    {
        _pressed = true;
        _dragging = false;
        _startX = x;
        _startY = y;
        _startedInZone = Zone.Contains(x, y);
        return SetArmed(false);
    }

    public bool ButtonUp()
    {
        _pressed = false;
        _dragging = false;
        return SetArmed(false);
    }

    /// <param name="cursorShowing">Asked only before arming.</param>
    public bool Moved(int x, int y, Func<bool> cursorShowing)
    {
        if (!_pressed || _startedInZone)
        {
            return false;
        }

        if (!_dragging)
        {
            if (Math.Abs(x - _startX) <= dragWidth && Math.Abs(y - _startY) <= dragHeight)
            {
                return false;
            }

            _dragging = true;
        }

        var inZone = Zone.Contains(x, y);
        if (inZone == IsArmed || (inZone && !cursorShowing()))
        {
            return false;
        }

        return SetArmed(inZone);
    }

    private bool SetArmed(bool armed)
    {
        if (IsArmed == armed)
        {
            return false;
        }

        IsArmed = armed;
        return true;
    }
}
