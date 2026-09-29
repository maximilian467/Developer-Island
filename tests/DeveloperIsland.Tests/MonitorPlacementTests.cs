using DeveloperIsland.Core.Placement;

namespace DeveloperIsland.Tests;

public class MonitorPlacementTests
{
    // Primary 1920x1080 at 100% with a bottom taskbar; secondary 2560x1440 at 150% to the right.
    private static readonly MonitorInfo Primary = new(@"\\.\DISPLAY1", new PixelRect(0, 0, 1920, 1080), new PixelRect(0, 0, 1920, 1032), 1.0, true);
    private static readonly MonitorInfo Secondary = new(@"\\.\DISPLAY2", new PixelRect(1920, 0, 2560, 1440), new PixelRect(1920, 0, 2560, 1400), 1.5, false);
    private static readonly MonitorInfo[] Both = [Secondary, Primary];

    [Fact]
    public void Saved_monitor_is_used_when_present()
    {
        var placement = IslandPlacement.Resolve(Both, @"\\.\DISPLAY2", IslandAnchor.BottomLeft, 12, -8);

        Assert.Same(Secondary, placement.Monitor);
        Assert.Equal(IslandAnchor.BottomLeft, placement.Anchor);
        Assert.Equal((12.0, -8.0), (placement.OffsetX, placement.OffsetY));
        Assert.False(placement.IsFallback);
    }

    [Fact]
    public void Missing_monitor_falls_back_to_primary_top_center()
    {
        var placement = IslandPlacement.Resolve([Primary], @"\\.\DISPLAY7", IslandAnchor.BottomRight, 300, 40);

        Assert.Same(Primary, placement.Monitor);
        Assert.Equal(IslandAnchor.TopCenter, placement.Anchor);
        Assert.Equal((0.0, 0.0), (placement.OffsetX, placement.OffsetY));
        Assert.True(placement.IsFallback);
    }

    [Fact]
    public void No_saved_monitor_means_primary()
    {
        Assert.Same(Primary, IslandPlacement.Resolve(Both, null, IslandAnchor.TopCenter, 0, 0).Monitor);
    }

    [Fact]
    public void Monitor_names_match_case_insensitively()
    {
        Assert.Same(Secondary, IslandPlacement.Resolve(Both, @"\\.\display2", IslandAnchor.TopCenter, 0, 0).Monitor);
    }

    [Fact]
    public void Top_center_window_is_centered_under_the_top_edge()
    {
        // 500x400 DIP window with a 40 DIP margin: the capsule's top sits 10 DIP below the work area.
        var rect = IslandPlacement.WindowRect(Primary, IslandAnchor.TopCenter, 0, 0, 500, 400, 40);

        Assert.Equal(new PixelRect(710, -30, 500, 400), rect);
    }

    [Fact]
    public void Placement_scales_with_monitor_dpi()
    {
        var rect = IslandPlacement.WindowRect(Secondary, IslandAnchor.BottomRight, 0, 0, 500, 400, 40);

        // Anchor: right edge minus 15 px, bottom of work area minus 15 px; window is 750x600 px.
        Assert.Equal(750, rect.Width);
        Assert.Equal(600, rect.Height);
        Assert.Equal(1920 + 2560 - 15 - (500 - 40) * 1.5, rect.X);
        Assert.Equal(1400 - 15 - (400 - 40) * 1.5, rect.Y);
    }

    [Fact]
    public void Stale_offsets_cannot_push_the_island_off_screen()
    {
        var rect = IslandPlacement.WindowRect(Primary, IslandAnchor.TopCenter, 5000, 5000, 500, 400, 40);
        var anchorX = rect.X + 250;
        var anchorY = rect.Y + 40;

        Assert.InRange(anchorX, 0, 1920);
        Assert.InRange(anchorY, 0, 1080);
    }

    [Fact]
    public void Dropping_near_the_top_center_snaps_back_to_the_preset()
    {
        // Capsule 200x36 dragged a few pixels off center.
        var drop = IslandPlacement.FromDrop(Both, new PixelRect(868, 16, 200, 36));

        Assert.Same(Primary, drop.Monitor);
        Assert.Equal(IslandAnchor.TopCenter, drop.Anchor);
        Assert.Equal((0.0, 0.0), (drop.OffsetX, drop.OffsetY));
    }

    [Fact]
    public void Dropping_in_the_lower_right_picks_that_anchor_and_keeps_the_offset()
    {
        var drop = IslandPlacement.FromDrop(Both, new PixelRect(1500, 800, 200, 36));

        Assert.Equal(IslandAnchor.BottomRight, drop.Anchor);
        Assert.Equal(1700 - (1920 - 10), drop.OffsetX);
        Assert.Equal(836 - (1032 - 10), drop.OffsetY);
    }

    [Fact]
    public void Dropping_on_another_monitor_moves_the_island_there()
    {
        var drop = IslandPlacement.FromDrop(Both, new PixelRect(3000, 15, 300, 54));

        Assert.Same(Secondary, drop.Monitor);
        Assert.Equal(IslandAnchor.TopCenter, drop.Anchor);
        Assert.Equal(0, drop.OffsetY);
    }

    [Fact]
    public void Anchor_components_round_trip()
    {
        foreach (var anchor in Enum.GetValues<IslandAnchor>())
        {
            Assert.Equal(anchor, IslandAnchorExtensions.Compose(anchor.Horizontal(), anchor.Vertical()));
        }
    }
}
