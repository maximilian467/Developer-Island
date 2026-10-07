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

/// <summary>Home Cloud drop zone: reachable from every mode, holds while uploading, returns to rest.</summary>
public class IslandDropModeTests
{
    private readonly FakeTimeProvider _time = new();

    public enum Start
    {
        Compact,
        Retracted,
        RestHidden,
        ForcedHidden,
        HoverPeek,
        Event,
        Expanded,
    }

    private IslandStateMachine Create(Start start, bool dropEnabled = true)
    {
        var island = new IslandStateMachine(_time) { PointerProbe = () => true };
        island.SetDropEnabled(dropEnabled);
        switch (start)
        {
            case Start.Retracted: island.SetRest(RestMode.Retracted); break;
            case Start.RestHidden: island.SetRest(RestMode.Hidden); break;
            case Start.ForcedHidden: island.Hide(); break;
            case Start.HoverPeek:
                island.PointerEntered();
                _time.Advance(IslandStateMachine.HoverDwell + TimeSpan.FromMilliseconds(10));
                break;
            case Start.Event: island.ShowEvent(TimeSpan.FromSeconds(30)); break;
            case Start.Expanded: island.Activate(); break;
        }

        return island;
    }

    [Theory]
    [InlineData(Start.Compact)]
    [InlineData(Start.Retracted)]
    [InlineData(Start.RestHidden)]
    [InlineData(Start.ForcedHidden)]
    [InlineData(Start.HoverPeek)]
    [InlineData(Start.Event)]
    [InlineData(Start.Expanded)]
    public void A_file_drag_opens_the_drop_zone_from_every_mode(Start start)
    {
        var island = Create(start);

        Assert.True(island.FileDragEntered());
        Assert.Equal(IslandMode.Drop, island.Mode);
        Assert.Equal(DropPhase.Hover, island.DropPhase);
    }

    [Theory]
    [InlineData(Start.Compact, IslandMode.Compact)]
    [InlineData(Start.Retracted, IslandMode.Retracted)]
    [InlineData(Start.RestHidden, IslandMode.Hidden)]
    [InlineData(Start.ForcedHidden, IslandMode.Hidden)]
    [InlineData(Start.HoverPeek, IslandMode.Compact)]
    [InlineData(Start.Event, IslandMode.Compact)]
    [InlineData(Start.Expanded, IslandMode.Compact)]
    public void Leaving_without_a_drop_returns_to_rest(Start start, IslandMode expected)
    {
        var island = Create(start);
        island.FileDragEntered();

        island.Handle(IslandInput.FileDragLeft);

        Assert.Equal(expected, island.Mode);
        Assert.Equal(DropPhase.None, island.DropPhase);
        Assert.Equal(start == Start.ForcedHidden, island.IsForcedHidden);
    }

    [Fact]
    public void A_running_upload_holds_the_drop_zone()
    {
        var island = Create(Start.Retracted);
        island.FileDragEntered();
        Assert.True(island.BeginDropUpload());

        island.FileDragLeft();
        Assert.False(island.ShowEvent());
        island.Activate();
        island.Dismiss();
        _time.Advance(TimeSpan.FromMinutes(5));

        Assert.Equal(IslandMode.Drop, island.Mode);
        Assert.Equal(DropPhase.Uploading, island.DropPhase);
        Assert.False(island.FileDragEntered()); // a second drag waits for the upload
    }

    [Fact]
    public void Success_shows_the_check_mark_briefly_then_rests()
    {
        var island = Create(Start.Retracted);
        island.FileDragEntered();
        island.Handle(IslandInput.FileDropCompleted);
        island.Handle(IslandInput.DropSucceeded);
        Assert.Equal(DropPhase.Succeeded, island.DropPhase);

        _time.Advance(IslandStateMachine.DropSuccessLinger - TimeSpan.FromMilliseconds(100));
        Assert.Equal(IslandMode.Drop, island.Mode);

        _time.Advance(TimeSpan.FromMilliseconds(200));
        Assert.Equal(IslandMode.Retracted, island.Mode);
    }

    [Fact]
    public void A_failure_stays_longer_than_a_success()
    {
        var island = Create(Start.Compact);
        island.FileDragEntered();
        island.BeginDropUpload();
        island.Handle(IslandInput.DropFailed);
        Assert.Equal(DropPhase.Failed, island.DropPhase);
        Assert.True(IslandStateMachine.DropFailureLinger > IslandStateMachine.DropSuccessLinger);

        _time.Advance(IslandStateMachine.DropFailureLinger - TimeSpan.FromMilliseconds(100));
        Assert.Equal(IslandMode.Drop, island.Mode);

        _time.Advance(TimeSpan.FromMilliseconds(200));
        Assert.Equal(IslandMode.Compact, island.Mode);
    }

    [Fact]
    public void A_forced_hide_ends_hidden_again_after_the_upload()
    {
        var island = Create(Start.ForcedHidden);
        island.FileDragEntered();
        island.BeginDropUpload();
        island.FinishDrop(succeeded: true);
        _time.Advance(IslandStateMachine.DropSuccessLinger + TimeSpan.FromMilliseconds(10));

        Assert.Equal(IslandMode.Hidden, island.Mode);
        Assert.True(island.IsForcedHidden);
        island.Show();
        Assert.Equal(IslandMode.Compact, island.Mode);
    }

    [Fact]
    public void A_rest_change_during_the_drop_applies_afterwards()
    {
        var island = Create(Start.Compact);
        island.FileDragEntered();
        island.SetRest(RestMode.Retracted);
        Assert.Equal(IslandMode.Drop, island.Mode);

        island.FileDragLeft();
        Assert.Equal(IslandMode.Retracted, island.Mode);
    }

    [Fact]
    public void A_new_drag_over_the_result_starts_over()
    {
        var island = Create(Start.Compact);
        island.FileDragEntered();
        island.BeginDropUpload();
        island.FinishDrop(succeeded: false);

        Assert.True(island.FileDragEntered());
        Assert.Equal(DropPhase.Hover, island.DropPhase);
        _time.Advance(IslandStateMachine.DropFailureLinger + TimeSpan.FromSeconds(1));
        Assert.Equal(IslandMode.Drop, island.Mode); // the result timer no longer applies
    }

    [Fact]
    public void A_lost_drag_leave_closes_the_zone_at_the_next_check()
    {
        var dragOver = true;
        var island = Create(Start.Retracted);
        island.DropProbe = () => dragOver;
        island.FileDragEntered();

        _time.Advance(IslandStateMachine.DropRecheck * 3);
        Assert.Equal(IslandMode.Drop, island.Mode);

        dragOver = false;
        _time.Advance(IslandStateMachine.DropRecheck + TimeSpan.FromMilliseconds(10));
        Assert.Equal(IslandMode.Retracted, island.Mode);
    }

    [Fact]
    public void Phase_and_mode_changes_are_reported()
    {
        var island = Create(Start.Compact);
        var events = new List<string>();
        island.ModeChanged += (from, to) => events.Add($"{from}->{to}");
        island.DropPhaseChanged += phase => events.Add(phase.ToString());

        island.FileDragEntered();
        island.BeginDropUpload();
        island.FinishDrop(succeeded: true);
        _time.Advance(IslandStateMachine.DropSuccessLinger + TimeSpan.FromMilliseconds(10));

        Assert.Equal(["Hover", "Compact->Drop", "Uploading", "Succeeded", "Drop->Compact", "None"], events);
    }

    [Theory]
    [InlineData(Start.Compact)]
    [InlineData(Start.Retracted)]
    [InlineData(Start.ForcedHidden)]
    [InlineData(Start.Expanded)]
    public void With_home_cloud_off_drop_inputs_do_nothing(Start start)
    {
        var island = Create(start, dropEnabled: false);
        var mode = island.Mode;

        Assert.False(island.FileDragEntered());
        Assert.False(island.BeginDropUpload());
        island.Handle(IslandInput.FileDragLeft);
        island.Handle(IslandInput.DropSucceeded);

        Assert.Equal(mode, island.Mode);
        Assert.Equal(DropPhase.None, island.DropPhase);
    }

    [Fact]
    public void Drop_inputs_are_off_until_enabled()
    {
        var island = new IslandStateMachine(_time);
        Assert.False(island.IsDropEnabled);
        Assert.False(island.FileDragEntered());
        Assert.Equal(IslandMode.Compact, island.Mode);
    }

    [Fact]
    public void Switching_home_cloud_off_closes_an_open_drop_zone()
    {
        var island = Create(Start.Retracted);
        island.FileDragEntered();
        island.BeginDropUpload();

        island.SetDropEnabled(false);

        Assert.Equal(IslandMode.Retracted, island.Mode);
        Assert.Equal(DropPhase.None, island.DropPhase);
        island.FinishDrop(succeeded: true); // the upload ending later changes nothing
        Assert.Equal(IslandMode.Retracted, island.Mode);
    }
}
