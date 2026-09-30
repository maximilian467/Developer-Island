using DeveloperIsland.Core.Island;
using Microsoft.Extensions.Time.Testing;

namespace DeveloperIsland.Tests;

public class IslandStateMachineTests
{
    private readonly FakeTimeProvider _time = new();

    private IslandStateMachine Create() => new(_time);

    [Fact]
    public void Starts_compact()
    {
        Assert.Equal(IslandMode.Compact, Create().Mode);
    }

    [Fact]
    public void Hover_opens_a_peek_after_a_short_dwell()
    {
        var island = Create();
        island.PointerEntered();

        _time.Advance(IslandStateMachine.HoverDwell / 2);
        Assert.Equal(IslandMode.Compact, island.Mode);

        _time.Advance(IslandStateMachine.HoverDwell);
        Assert.Equal(IslandMode.Activity, island.Mode);
        Assert.Equal(ActivitySource.Hover, island.ActivitySource);
    }

    [Fact]
    public void Passing_over_quickly_does_not_expand()
    {
        var island = Create();
        island.PointerEntered();
        _time.Advance(TimeSpan.FromMilliseconds(100));
        island.PointerExited();
        _time.Advance(TimeSpan.FromSeconds(2));

        Assert.Equal(IslandMode.Compact, island.Mode);
    }

    [Fact]
    public void Leaving_a_peek_collapses_it()
    {
        var island = Create();
        island.PointerEntered();
        _time.Advance(TimeSpan.FromSeconds(1));
        island.PointerExited();
        _time.Advance(IslandStateMachine.HoverLinger + TimeSpan.FromMilliseconds(10));

        Assert.Equal(IslandMode.Compact, island.Mode);
    }

    [Fact]
    public void Events_show_briefly_then_collapse()
    {
        var island = Create();
        Assert.True(island.ShowEvent(TimeSpan.FromSeconds(4)));
        Assert.Equal(IslandMode.Activity, island.Mode);

        _time.Advance(TimeSpan.FromSeconds(4.1));
        Assert.Equal(IslandMode.Compact, island.Mode);
    }

    [Fact]
    public void Hover_keeps_an_event_open()
    {
        var island = Create();
        island.ShowEvent(TimeSpan.FromSeconds(4));
        island.PointerEntered();

        _time.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal(IslandMode.Activity, island.Mode);

        island.PointerExited();
        _time.Advance(IslandStateMachine.MinimumAfterHover + TimeSpan.FromMilliseconds(10));
        Assert.Equal(IslandMode.Compact, island.Mode);
    }

    [Fact]
    public void Events_do_not_interrupt_the_expanded_island()
    {
        var island = Create();
        island.Activate();

        Assert.False(island.ShowEvent());
        Assert.Equal(IslandMode.Expanded, island.Mode);
    }

    [Fact]
    public void A_new_event_restarts_the_timeout()
    {
        var island = Create();
        island.ShowEvent(TimeSpan.FromSeconds(4));
        _time.Advance(TimeSpan.FromSeconds(3));
        var changes = 0;
        island.ModeChanged += (_, _) => changes++;

        island.ShowEvent(TimeSpan.FromSeconds(4));
        _time.Advance(TimeSpan.FromSeconds(3));

        Assert.Equal(IslandMode.Activity, island.Mode);
        Assert.Equal(1, changes); // content refresh notification, same mode
    }

    [Fact]
    public void Click_expands_and_dismiss_collapses()
    {
        var island = Create();
        island.Activate();
        Assert.Equal(IslandMode.Expanded, island.Mode);

        island.Dismiss();
        Assert.Equal(IslandMode.Compact, island.Mode);
    }

    [Fact]
    public void Hidden_island_ignores_events_until_shown()
    {
        var island = Create();
        island.Hide();

        Assert.False(island.ShowEvent());
        Assert.Equal(IslandMode.Hidden, island.Mode);

        island.Show();
        Assert.Equal(IslandMode.Compact, island.Mode);
    }
}

public class IslandRestModeTests
{
    private readonly FakeTimeProvider _time = new();

    private IslandStateMachine Create() => new(_time);

    [Fact]
    public void Retracting_moves_a_resting_island_into_the_notch()
    {
        var island = Create();
        island.SetRest(RestMode.Retracted);

        Assert.Equal(IslandMode.Retracted, island.Mode);
    }

    [Fact]
    public void An_open_island_is_not_yanked_away_when_rest_changes()
    {
        var island = Create();
        island.Activate();
        island.SetRest(RestMode.Retracted);

        Assert.Equal(IslandMode.Expanded, island.Mode);

        island.Dismiss();
        Assert.Equal(IslandMode.Retracted, island.Mode);
    }

    [Fact]
    public void Passing_over_the_notch_does_nothing()
    {
        var island = Create();
        island.SetRest(RestMode.Retracted);
        island.PointerEntered();
        _time.Advance(IslandStateMachine.NotchDwell - TimeSpan.FromMilliseconds(100));
        island.PointerExited();
        _time.Advance(TimeSpan.FromSeconds(2));

        Assert.Equal(IslandMode.Retracted, island.Mode);
    }

    [Fact]
    public void Dwelling_on_the_notch_opens_a_peek_that_retracts_again_on_leave()
    {
        var island = Create();
        island.SetRest(RestMode.Retracted);
        island.PointerEntered();
        _time.Advance(IslandStateMachine.NotchDwell + TimeSpan.FromMilliseconds(10));
        Assert.Equal(IslandMode.Activity, island.Mode);

        island.PointerExited();
        _time.Advance(IslandStateMachine.HoverLinger + TimeSpan.FromMilliseconds(10));
        Assert.Equal(IslandMode.Retracted, island.Mode);
    }

    [Fact]
    public void Clicking_the_notch_opens_immediately()
    {
        var island = Create();
        island.SetRest(RestMode.Retracted);
        island.Activate();

        Assert.Equal(IslandMode.Expanded, island.Mode);
    }

    [Fact]
    public void Events_do_not_pop_out_of_the_notch()
    {
        var island = Create();
        island.SetRest(RestMode.Retracted);

        Assert.False(island.ShowEvent());
        Assert.Equal(IslandMode.Retracted, island.Mode);
    }

    [Fact]
    public void Leaving_the_browser_restores_the_compact_island()
    {
        var island = Create();
        island.SetRest(RestMode.Retracted);
        island.SetRest(RestMode.Compact);

        Assert.Equal(IslandMode.Compact, island.Mode);
    }

    [Fact]
    public void Toggle_opens_from_hidden_and_closes_when_expanded()
    {
        var island = Create();
        island.Hide();

        island.Toggle();
        Assert.Equal(IslandMode.Expanded, island.Mode);

        island.Toggle();
        Assert.Equal(IslandMode.Compact, island.Mode);
    }

    [Fact]
    public void Forced_hide_wins_over_rest_changes_until_shown()
    {
        var island = Create();
        island.Hide();
        island.SetRest(RestMode.Retracted);
        Assert.Equal(IslandMode.Hidden, island.Mode);

        island.Show();
        Assert.Equal(IslandMode.Retracted, island.Mode);
    }
}
