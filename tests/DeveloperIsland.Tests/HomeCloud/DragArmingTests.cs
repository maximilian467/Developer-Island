using DeveloperIsland.Core.HomeCloud;
using DeveloperIsland.Core.Island;
using DeveloperIsland.Core.Placement;

namespace DeveloperIsland.Tests.HomeCloud;

public class DragArmPolicyTests
{
    [Theory]
    [InlineData(IslandMode.Retracted, true)]
    [InlineData(IslandMode.Hidden, true)]
    [InlineData(IslandMode.Compact, false)]
    [InlineData(IslandMode.Activity, false)]
    [InlineData(IslandMode.Expanded, false)]
    [InlineData(IslandMode.Drop, false)]
    public void The_hook_runs_only_for_the_notch_or_a_hidden_island(IslandMode mode, bool expected)
    {
        Assert.Equal(expected, HomeCloudDropPolicy.ShouldArm(enabled: true, mode, topAnchored: true));
    }

    [Theory]
    [InlineData(IslandMode.Retracted)]
    [InlineData(IslandMode.Hidden)]
    public void Home_cloud_off_never_hooks(IslandMode mode)
    {
        Assert.False(HomeCloudDropPolicy.ShouldArm(enabled: false, mode, topAnchored: true));
    }

    [Fact]
    public void An_island_at_the_bottom_never_hooks()
    {
        Assert.False(HomeCloudDropPolicy.ShouldArm(enabled: true, IslandMode.Hidden, topAnchored: false));
    }

    [Fact]
    public void The_arm_zone_follows_the_monitor_scale()
    {
        Assert.Equal(new PixelRect(810, 0, 300, 60), HomeCloudArmZone.OnScreen(960, 0, 1.0));
        Assert.Equal(new PixelRect(1695, -1440, 450, 90), HomeCloudArmZone.OnScreen(1920, -1440, 1.5));
    }
}

public class DragArmDetectorTests
{
    // Zone: x 800..1100, y 0..60; drag distance 4 px.
    private static DragArmDetector Create() => new(4, 4) { Zone = new PixelRect(800, 0, 300, 60) };

    private static readonly Func<bool> Showing = () => true;

    [Fact]
    public void A_drag_from_below_into_the_zone_arms_and_release_disarms()
    {
        var d = Create();
        Assert.False(d.ButtonDown(900, 500));
        Assert.False(d.Moved(900, 300, Showing));
        Assert.True(d.Moved(950, 30, Showing));
        Assert.True(d.IsArmed);

        Assert.True(d.ButtonUp());
        Assert.False(d.IsArmed);
    }

    [Fact]
    public void Moving_without_a_press_does_nothing()
    {
        var d = Create();
        Assert.False(d.Moved(950, 30, Showing));
        Assert.False(d.IsArmed);
    }

    [Fact]
    public void A_jitter_below_the_drag_distance_is_a_click()
    {
        var d = Create();
        d.ButtonDown(950, 62);
        Assert.False(d.Moved(952, 59, Showing)); // into the zone, but only 3 px
        Assert.False(d.IsArmed);
    }

    [Fact]
    public void A_press_inside_the_zone_never_arms()
    {
        // Tabs, title bars and text at the top edge: dragging them is not a file drag.
        var d = Create();
        d.ButtonDown(900, 20);
        Assert.False(d.Moved(1000, 40, Showing));
        Assert.False(d.Moved(900, 400, Showing));
        Assert.False(d.Moved(900, 20, Showing));
        Assert.False(d.IsArmed);
    }

    [Fact]
    public void A_hidden_cursor_never_arms()
    {
        var d = Create();
        d.ButtonDown(900, 500);
        Assert.False(d.Moved(950, 30, () => false));
        Assert.False(d.IsArmed);
    }

    [Fact]
    public void Leaving_the_zone_disarms_and_coming_back_rearms()
    {
        var d = Create();
        d.ButtonDown(900, 500);
        d.Moved(950, 30, Showing);

        Assert.True(d.Moved(1300, 30, Showing));
        Assert.False(d.IsArmed);
        Assert.True(d.Moved(1000, 10, Showing));
        Assert.True(d.IsArmed);
    }

    [Fact]
    public void A_new_press_starts_over()
    {
        var d = Create();
        d.ButtonDown(900, 500);
        d.Moved(950, 30, Showing);

        Assert.True(d.ButtonDown(950, 30)); // the release was missed; the press inside disarms
        Assert.False(d.Moved(980, 50, Showing));
        Assert.False(d.IsArmed);
    }

    [Fact]
    public void Moving_the_zone_applies_to_the_next_move()
    {
        var d = Create();
        d.ButtonDown(100, 500);
        Assert.False(d.Moved(100, 30, Showing));

        d.Zone = new PixelRect(0, 0, 300, 60);
        Assert.True(d.Moved(101, 30, Showing));
    }
}
