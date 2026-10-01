using DeveloperIsland.Core.Island;

namespace DeveloperIsland.Tests;

public class HoverTrackerTests
{
    private static readonly CapsuleBounds Compact = new(150, 40, 200, 36);
    private static readonly CapsuleBounds Expanded = new(40, 40, 420, 300);

    [Fact]
    public void Moving_into_the_capsule_is_hover_intent()
    {
        var tracker = new HoverTracker();
        tracker.PointerAt(100, 20, Compact);

        Assert.Equal(HoverChange.Entered, tracker.PointerAt(200, 50, Compact));
        Assert.True(tracker.IsInside);
    }

    [Fact]
    public void A_capsule_growing_under_a_resting_pointer_is_not_hover_intent()
    {
        var tracker = new HoverTracker();
        // First report at a position outside the compact capsule.
        tracker.PointerAt(100, 200, Compact);

        // The capsule expands under the pointer; XAML reports the same position again.
        Assert.Equal(HoverChange.None, tracker.PointerAt(100, 200, Expanded));
        Assert.False(tracker.IsInside);

        // Only real movement counts.
        Assert.Equal(HoverChange.Entered, tracker.PointerAt(104, 205, Expanded));
    }

    [Fact]
    public void Pointer_in_the_shadow_margin_is_outside()
    {
        var tracker = new HoverTracker();
        tracker.PointerAt(0, 0, Expanded);

        Assert.Equal(HoverChange.None, tracker.PointerAt(30, 360, Expanded));
        Assert.False(Expanded.Contains(30, 360));
    }

    [Fact]
    public void Leaving_the_capsule_but_not_the_window_exits()
    {
        var tracker = new HoverTracker();
        tracker.PointerAt(100, 20, Compact);
        tracker.PointerAt(200, 50, Compact);

        Assert.Equal(HoverChange.Exited, tracker.PointerAt(200, 90, Compact));
    }

    [Fact]
    public void A_shrinking_capsule_exits_a_resting_pointer_on_revalidation()
    {
        var tracker = new HoverTracker();
        tracker.PointerAt(0, 0, Expanded);
        tracker.PointerAt(80, 250, Expanded);
        Assert.True(tracker.IsInside);

        // Collapse: the pointer did not move, the OS reports nothing.
        Assert.Equal(HoverChange.Exited, tracker.Revalidate(80, 250, Compact));
        Assert.False(tracker.IsInside);
    }

    [Fact]
    public void Revalidation_never_creates_hover_intent()
    {
        var tracker = new HoverTracker();
        tracker.PointerAt(80, 250, Compact);

        Assert.Equal(HoverChange.None, tracker.Revalidate(80, 250, Expanded));
        Assert.False(tracker.IsInside);
    }

    [Fact]
    public void Losing_the_window_exits_once()
    {
        var tracker = new HoverTracker();
        tracker.PointerAt(100, 20, Compact);
        tracker.PointerAt(200, 50, Compact);

        Assert.Equal(HoverChange.Exited, tracker.PointerLeftWindow());
        Assert.Equal(HoverChange.None, tracker.PointerLeftWindow());
    }
}

public class ClickArmingTests
{
    private static readonly CapsuleBounds Small = new(100, 10, 120, 36);
    private static readonly CapsuleBounds Grown = new(40, 10, 360, 56);

    [Fact]
    public void A_capsule_that_grows_under_a_resting_pointer_is_not_armed()
    {
        var tracker = new HoverTracker();
        tracker.PointerAt(50, 30, Small); // outside the small capsule
        tracker.PointerAt(50, 30, Grown); // same spot, now inside the grown one: no movement

        Assert.False(tracker.IsInside); // a press here is not a click on the island
    }

    [Fact]
    public void Moving_onto_the_capsule_arms_it()
    {
        var tracker = new HoverTracker();
        tracker.PointerAt(50, 30, Grown);
        tracker.PointerAt(52, 31, Grown);

        Assert.True(tracker.IsInside);
    }
}
