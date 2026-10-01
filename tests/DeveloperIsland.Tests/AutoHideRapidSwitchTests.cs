using DeveloperIsland.Core.Island;
using DeveloperIsland.Core.Placement;
using DeveloperIsland.Core.Settings;
using Microsoft.Extensions.Time.Testing;

namespace DeveloperIsland.Tests;

/// <summary>
/// Fast app switching: the newest foreground app always decides, stepping aside never waits, and
/// nothing a module does brings the island out of the notch while an auto-hide app is in front.
/// </summary>
public class AutoHideRapidSwitchTests
{
    private static readonly AppSettings Settings = new() { SmartHideProcesses = ["chrome"] };

    private readonly FakeTimeProvider _time = new();
    private readonly IslandStateMachine _island;
    private readonly AutoHideCoordinator _autoHide;
    private readonly List<RestMode> _applied = [];
    private ForegroundInfo? _actual;

    public AutoHideRapidSwitchTests()
    {
        _island = new IslandStateMachine(_time) { PointerProbe = () => false };
        _autoHide = new AutoHideCoordinator(
            _time,
            a => a(),
            () => _actual,
            f => SmartHidePolicy.Decide(Settings, IslandAnchor.TopCenter, 0, f),
            rest =>
            {
                _applied.Add(rest);
                _island.SetRest(rest);
            });
    }

    private static ForegroundInfo App(string process) => new(process, IsMaximized: true, IsOnIslandMonitor: true);

    /// <summary>The app comes to the front: Windows reports it (the event) and it is what is in front.</summary>
    private void Switch(string process, int afterMs = 30)
    {
        _time.Advance(TimeSpan.FromMilliseconds(afterMs));
        _actual = App(process);
        _autoHide.ForegroundChanged(_actual);
    }

    [Fact]
    public void Chrome_retracts_at_once_without_a_debounce()
    {
        Switch("chrome", afterMs: 0);

        Assert.Equal(IslandMode.Retracted, _island.Mode);
    }

    [Fact]
    public void Chrome_vs_code_chrome_in_quick_succession_ends_retracted_and_never_flashes_compact()
    {
        Switch("chrome");
        Switch("code");
        Switch("chrome");
        _time.Advance(TimeSpan.FromSeconds(1));

        Assert.Equal(IslandMode.Retracted, _island.Mode);
        Assert.DoesNotContain(RestMode.Compact, _applied);
    }

    [Fact]
    public void A_long_chain_of_switches_ends_with_the_last_app()
    {
        Switch("chrome");
        Switch("explorer");
        Switch("chrome");
        Switch("spotify");
        Switch("chrome");
        _time.Advance(TimeSpan.FromSeconds(1));

        Assert.Equal(IslandMode.Retracted, _island.Mode);
        Assert.DoesNotContain(RestMode.Compact, _applied);
    }

    [Fact]
    public void A_late_event_for_an_app_that_already_lost_the_foreground_is_ignored()
    {
        Switch("chrome");

        // A queued "VS Code" event arrives, but Chrome is already in front again.
        _autoHide.ForegroundChanged(App("code"));
        _actual = App("chrome");
        _time.Advance(AutoHideCoordinator.ReturnSettle + TimeSpan.FromMilliseconds(10));

        Assert.Equal(IslandMode.Retracted, _island.Mode);
    }

    [Fact]
    public void Leaving_chrome_returns_to_compact_after_a_short_settle()
    {
        Switch("chrome");
        Switch("code");
        Assert.Equal(IslandMode.Retracted, _island.Mode);

        _time.Advance(AutoHideCoordinator.ReturnSettle + TimeSpan.FromMilliseconds(10));

        Assert.Equal(IslandMode.Compact, _island.Mode);
    }

    [Fact]
    public void An_older_pending_return_never_overrides_a_newer_decision()
    {
        Switch("code", afterMs: 0);
        Switch("chrome", afterMs: 20); // within the settle of the VS Code return
        var version = _autoHide.Version;
        _time.Advance(TimeSpan.FromSeconds(1));

        Assert.Equal(IslandMode.Retracted, _island.Mode);
        Assert.Equal(version, _autoHide.Version);
        Assert.False(_autoHide.IsReturnPending);
    }

    [Fact]
    public void Module_updates_never_bring_the_island_out_of_the_notch()
    {
        Switch("chrome");

        // Music track change, Claude usage refresh, system alert, focus and calendar announcements.
        Assert.False(_island.ShowEvent());
        Assert.False(_island.ShowEvent(TimeSpan.FromSeconds(3)));
        Assert.False(_island.ShowEvent(TimeSpan.FromSeconds(5)));
        _island.PointerExited();
        _time.Advance(TimeSpan.FromSeconds(10));

        Assert.Equal(IslandMode.Retracted, _island.Mode);
    }

    [Fact]
    public void A_hover_or_a_peek_in_progress_cannot_keep_the_island_out_when_chrome_comes_to_the_front()
    {
        _island.PointerEntered();
        Switch("chrome", afterMs: 100); // before the hover dwell ends
        _time.Advance(TimeSpan.FromSeconds(1));

        Assert.Equal(IslandMode.Retracted, _island.Mode);
    }

    [Fact]
    public void A_deliberate_hover_on_the_notch_still_opens_the_peek()
    {
        var island = new IslandStateMachine(_time) { PointerProbe = () => true };
        island.SetRest(RestMode.Retracted);

        island.PointerEntered();
        _time.Advance(IslandStateMachine.NotchDwell + TimeSpan.FromMilliseconds(10));

        Assert.Equal(IslandMode.Activity, island.Mode);
    }

    [Fact]
    public void Reconciliation_catches_a_foreground_event_windows_never_delivered()
    {
        Switch("code");
        _time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(IslandMode.Compact, _island.Mode);

        // Chrome came to the front, but no event arrived.
        _actual = App("chrome");
        _autoHide.Reconcile(_island.Rest);

        Assert.Equal(IslandMode.Retracted, _island.Mode);
    }

    [Fact]
    public void Reconciliation_does_nothing_when_everything_matches()
    {
        Switch("chrome");
        var version = _autoHide.Version;

        _autoHide.Reconcile(_island.Rest);

        Assert.Equal(version, _autoHide.Version);
        Assert.Single(_applied);
    }

    [Fact]
    public void A_rule_change_applies_at_once()
    {
        _actual = App("chrome");

        _autoHide.Reevaluate();

        Assert.Equal(IslandMode.Retracted, _island.Mode);
    }
}
