using DeveloperIsland.Core.Placement;

namespace DeveloperIsland.Tests;

public class IslandDragTests
{
    [Fact]
    public void Grab_point_is_measured_in_dips()
    {
        var grab = IslandDrag.GrabPoint(1300, 90, 1000, 0, 1.5);

        Assert.Equal((200.0, 60.0), grab);
    }

    [Fact]
    public void Window_follows_the_cursor_at_the_same_scale()
    {
        var grab = IslandDrag.GrabPoint(960, 30, 710, -30, 1.0);

        Assert.Equal((960, -50), IslandDrag.WindowPosition(1210, 10, grab, 1.0));
    }

    [Fact]
    public void Grabbed_point_stays_under_the_cursor_after_a_scale_change()
    {
        // Grabbed at 100 %; the window crosses into a 150 % monitor and Windows makes it 1.5 times larger.
        var grab = IslandDrag.GrabPoint(960, 30, 710, -30, 1.0);
        var (x, y) = IslandDrag.WindowPosition(2500, 40, grab, 1.5);

        // The grab point, scaled to the new size, lands exactly on the cursor.
        Assert.Equal(2500, x + grab.X * 1.5);
        Assert.Equal(40, y + grab.Y * 1.5);
    }
}
