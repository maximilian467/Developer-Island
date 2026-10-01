using DeveloperIsland.Core.Island;
using DeveloperIsland.Core.Modules;
using DeveloperIsland.Core.Placement;
using DeveloperIsland.Core.Settings;
using Microsoft.Extensions.Time.Testing;

namespace DeveloperIsland.Tests;

/// <summary>Auto-hide is one central rule (foreground app + rule + island state), whatever module is showing.</summary>
public class AutoHideAcrossModulesTests
{
    private readonly FakeTimeProvider _time = new();
    private bool? _pointerOver;

    private static readonly AppSettings Settings = new()
    {
        SmartHideProcesses = ["chrome", "code"],
        AutoHideApps =
        [
            new AutoHideApp { Process = "chrome", Name = "Google Chrome" },
            new AutoHideApp { Process = "code", Name = "Visual Studio Code" },
        ],
    };

    private static RestMode RestFor(string process) =>
        SmartHidePolicy.Decide(Settings, IslandAnchor.TopCenter, 0, new ForegroundInfo(process, IsMaximized: true, IsOnIslandMonitor: true));

    /// <summary>How each module typically leaves the island when the user switches apps.</summary>
    private IslandStateMachine IslandShowing(string module)
    {
        var island = new IslandStateMachine(_time) { PointerProbe = () => _pointerOver };
        switch (module)
        {
            case "Claude": // resting compact, Claude featured
                break;
            case "Codex": // an AI session activity is open
                island.ShowEvent();
                break;
            case "Music": // a song change, and the pointer rests on it (on its way to the tabs)
                _pointerOver = true;
                island.ShowEvent();
                island.PointerEntered();
                break;
            case "Focus": // focus just started
                island.ShowEvent(TimeSpan.FromSeconds(3));
                break;
            case "System": // the wide System capsule caught a hover: the peek is open under the pointer
                _pointerOver = true;
                island.PointerEntered();
                _time.Advance(IslandStateMachine.HoverDwell + TimeSpan.FromMilliseconds(10));
                Assert.Equal(IslandMode.Activity, island.Mode);
                break;
            case "Calendar": // "Robotics in 25 min" announcement
                island.ShowEvent(TimeSpan.FromSeconds(5));
                break;
            case "Expanded": // the user had the island open
                island.Activate();
                break;
        }

        return island;
    }

    [Theory]
    [InlineData("Claude", "chrome")]
    [InlineData("Codex", "chrome")]
    [InlineData("Music", "chrome")]
    [InlineData("Focus", "chrome")]
    [InlineData("System", "chrome")]
    [InlineData("Calendar", "chrome")]
    [InlineData("Expanded", "chrome")]
    [InlineData("Claude", "code")]
    [InlineData("Music", "code")]
    [InlineData("System", "code")]
    public void A_configured_app_in_front_retracts_the_island_at_once(string module, string app)
    {
        var island = IslandShowing(module);

        island.SetRest(RestFor(app));

        Assert.Equal(IslandMode.Retracted, island.Mode);
        _time.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal(IslandMode.Retracted, island.Mode); // no timer brings it back
    }

    [Theory]
    [InlineData("Claude")]
    [InlineData("Music")]
    [InlineData("System")]
    [InlineData("Calendar")]
    public void A_normal_app_keeps_normal_behavior(string module)
    {
        var island = IslandShowing(module);
        var before = island.Mode;

        island.SetRest(RestFor("explorer"));

        Assert.Equal(before, island.Mode);
    }

    [Fact]
    public void Events_never_pop_out_of_the_notch()
    {
        var island = IslandShowing("Claude");
        island.SetRest(RestFor("chrome"));

        Assert.False(island.ShowEvent()); // a song change, focus, an AI session
        Assert.Equal(IslandMode.Retracted, island.Mode);
    }

    [Fact]
    public void A_drag_finishes_before_the_island_retracts()
    {
        var island = IslandShowing("Claude");
        island.BeginDrag();
        island.SetRest(RestFor("chrome"));
        Assert.Equal(IslandMode.Compact, island.Mode);

        island.EndDrag();
        Assert.Equal(IslandMode.Retracted, island.Mode);
    }
}

public class IslandContentTests
{
    private static readonly IslandFacts Busy = new(FocusActive: true, EventSoon: true, SystemAlert: false, MusicPlaying: true, MusicHasTrack: true, UsageAvailable: true);
    private static readonly IslandFacts Quiet = new(false, false, false, false, false, UsageAvailable: true);

    [Theory]
    [InlineData(ModuleId.Music)]
    [InlineData(ModuleId.System)]
    [InlineData(ModuleId.Focus)]
    [InlineData(ModuleId.Calendar)]
    public void Hover_shows_the_featured_module_not_Claude(ModuleId featured)
    {
        Assert.Equal(PeekContent.Featured, IslandContent.ChoosePeek(featured, Quiet));
        Assert.Equal(PeekContent.Featured, IslandContent.ChoosePeek(featured, Busy));
    }

    [Fact]
    public void Without_favorite_or_last_module_the_classic_priority_applies()
    {
        Assert.Equal(PeekContent.Focus, IslandContent.ChoosePeek(null, Busy));
        Assert.Equal(PeekContent.Usage, IslandContent.ChoosePeek(null, Quiet));
        Assert.Equal(PeekContent.Info, IslandContent.ChoosePeek(null, Quiet with { UsageAvailable = false }));
    }

    [Fact]
    public void Opening_shows_the_featured_module_unless_something_was_just_announced_or_requested()
    {
        Assert.Equal(ModuleId.System, IslandContent.ChooseTabOnOpen(null, null, ModuleId.System, focusActive: true));
        Assert.Equal(ModuleId.Music, IslandContent.ChooseTabOnOpen(null, ModuleId.Music, ModuleId.System, false));
        Assert.Equal(ModuleId.Tasks, IslandContent.ChooseTabOnOpen(ModuleId.Tasks, ModuleId.Music, ModuleId.System, false));
        Assert.Equal(ModuleId.Focus, IslandContent.ChooseTabOnOpen(null, null, null, focusActive: true));
        Assert.Null(IslandContent.ChooseTabOnOpen(null, null, null, false));
    }

    [Fact]
    public void The_last_opened_module_is_featured_when_there_is_no_favorite()
    {
        // Music was opened last; hover and the compact island must stay on Music.
        Assert.Equal(ModuleId.Music, CompactSelector.Select([], ModuleId.Music, _ => true, turn: 3));
        Assert.Equal(PeekContent.Featured, IslandContent.ChoosePeek(ModuleId.Music, Quiet));
    }
}
