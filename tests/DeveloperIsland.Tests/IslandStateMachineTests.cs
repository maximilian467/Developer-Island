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
    public void An_auto_hide_app_in_front_retracts_even_an_expanded_island()
    {
        // Priority: auto-hide outranks Expanded and Medium.
        var island = Create();
        island.Activate();
        island.SetRest(RestMode.Retracted);

        Assert.Equal(IslandMode.Retracted, island.Mode);
    }

    [Fact]
    public void Returning_to_a_normal_app_keeps_an_open_island_open()
    {
        var island = Create();
        island.SetRest(RestMode.Retracted);
        island.Activate();               // opened from the notch
        island.SetRest(RestMode.Compact); // the auto-hide app went away

        Assert.Equal(IslandMode.Expanded, island.Mode);
        island.Dismiss();
        Assert.Equal(IslandMode.Compact, island.Mode);
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

/// <summary>Inputs that used to leave the island open or open it without intent.</summary>
public class IslandInputTests
{
    private readonly FakeTimeProvider _time = new();
    private bool? _pointerReallyOver;

    private IslandStateMachine Create() => new(_time) { PointerProbe = () => _pointerReallyOver };

    [Fact]
    public void A_lost_pointer_exit_no_longer_keeps_an_event_open()
    {
        var island = Create();
        island.PointerEntered();          // reported...
        _pointerReallyOver = false;       // ...but the pointer has gone; the exit never arrived
        island.ShowEvent(TimeSpan.FromSeconds(3));

        _time.Advance(TimeSpan.FromSeconds(3.1));

        Assert.Equal(IslandMode.Compact, island.Mode);
        Assert.False(island.IsPointerOver);
    }

    [Fact]
    public void A_hovered_event_stays_open_and_closes_once_the_pointer_leaves()
    {
        var island = Create();
        _pointerReallyOver = true;
        island.ShowEvent(TimeSpan.FromSeconds(2));

        _time.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal(IslandMode.Activity, island.Mode);

        _pointerReallyOver = false;
        _time.Advance(IslandStateMachine.MinimumAfterHover + TimeSpan.FromMilliseconds(50));
        Assert.Equal(IslandMode.Compact, island.Mode);
    }

    [Fact]
    public void A_hover_peek_closes_when_the_exit_was_lost()
    {
        var island = Create();
        _pointerReallyOver = true;
        island.PointerEntered();
        _time.Advance(IslandStateMachine.HoverDwell + TimeSpan.FromMilliseconds(10));
        Assert.Equal(IslandMode.Activity, island.Mode);

        _pointerReallyOver = false; // no PointerExited
        _time.Advance(IslandStateMachine.HoverRecheck + TimeSpan.FromMilliseconds(10));

        Assert.Equal(IslandMode.Compact, island.Mode);
    }

    [Fact]
    public void Hover_delay_does_not_open_when_the_pointer_is_already_gone()
    {
        var island = Create();
        _pointerReallyOver = true;
        island.PointerEntered();
        _pointerReallyOver = false;

        _time.Advance(IslandStateMachine.HoverDwell + TimeSpan.FromMilliseconds(10));

        Assert.Equal(IslandMode.Compact, island.Mode);
    }

    [Fact]
    public void Dragging_freezes_timers_events_and_clicks()
    {
        var island = Create();
        _pointerReallyOver = true;
        island.ShowEvent(TimeSpan.FromSeconds(1));
        island.Handle(IslandInput.DragStarted);

        Assert.False(island.ShowEvent());
        island.Handle(IslandInput.Clicked);
        _time.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal(IslandMode.Activity, island.Mode);
        Assert.True(island.IsDragging);

        _pointerReallyOver = false;
        island.Handle(IslandInput.DragEnded);
        _time.Advance(IslandStateMachine.MinimumAfterHover + TimeSpan.FromMilliseconds(10));
        Assert.Equal(IslandMode.Compact, island.Mode);
    }

    [Fact]
    public void Hovering_during_a_drag_does_not_start_a_peek()
    {
        var island = Create();
        _pointerReallyOver = true;
        island.BeginDrag();
        island.PointerEntered();
        _time.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(IslandMode.Compact, island.Mode);
    }

    [Fact]
    public void Inputs_by_name_follow_the_transition_table()
    {
        var island = Create();
        island.Handle(IslandInput.Clicked);
        Assert.Equal(IslandMode.Expanded, island.Mode);

        island.Handle(IslandInput.ModuleEvent); // ignored while expanded
        Assert.Equal(IslandMode.Expanded, island.Mode);

        island.Handle(IslandInput.ClickedOutside);
        Assert.Equal(IslandMode.Compact, island.Mode);

        island.Handle(IslandInput.ModuleEvent);
        Assert.Equal(IslandMode.Activity, island.Mode);
        Assert.Equal(ActivitySource.Event, island.ActivitySource);
    }

    [Fact]
    public void The_notch_asks_for_a_deliberate_dwell()
    {
        Assert.InRange(IslandStateMachine.NotchDwell.TotalMilliseconds, 350, 600);
        Assert.True(IslandStateMachine.NotchDwell > IslandStateMachine.HoverDwell);
    }

    [Fact]
    public void A_stale_expiry_timer_cannot_close_a_newer_event()
    {
        var island = Create();
        _pointerReallyOver = false;
        island.ShowEvent(TimeSpan.FromSeconds(2));
        _time.Advance(TimeSpan.FromSeconds(1.5));
        island.ShowEvent(TimeSpan.FromSeconds(2)); // restarts

        _time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(IslandMode.Activity, island.Mode);

        _time.Advance(TimeSpan.FromSeconds(1.1));
        Assert.Equal(IslandMode.Compact, island.Mode);
    }
}
