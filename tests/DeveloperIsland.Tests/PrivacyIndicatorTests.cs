using DeveloperIsland.Core.Island;
using DeveloperIsland.Core.Placement;
using DeveloperIsland.Core.Privacy;
using DeveloperIsland.Core.Settings;
using Microsoft.Extensions.Time.Testing;

namespace DeveloperIsland.Tests;

/// <summary>Camera and microphone marks: a global state, visible in every island state, never opening the island.</summary>
public class PrivacyIndicatorTests
{
    private static readonly PrivacyState Microphone = new(Microphone: true, Camera: false);
    private static readonly PrivacyState Camera = new(Microphone: false, Camera: true);

    private static readonly AppSettings HideSettings = new()
    {
        SmartHideProcesses = ["chrome"],
        SmartHideBehavior = SmartHideBehavior.Hide,
    };

    private static readonly ForegroundInfo Chrome = new("chrome", IsMaximized: true, IsOnIslandMonitor: true);

    [Fact]
    public void An_app_that_started_and_has_not_stopped_is_using_the_device()
    {
        Assert.True(ConsentStore.InUse(new ConsentUsage(133_000_000_000_000_000, 0)));
        Assert.False(ConsentStore.InUse(new ConsentUsage(133_000_000_000_000_000, 133_000_000_100_000_000)));
        Assert.False(ConsentStore.InUse(new ConsentUsage(0, 0)));
        Assert.True(ConsentStore.AnyInUse([new(5, 9), new(7, 0)]));
        Assert.False(ConsentStore.AnyInUse([]));
    }

    [Fact]
    public void Microphone_on_while_auto_hidden_keeps_the_notch_with_its_mark_and_does_not_open()
    {
        var island = new IslandStateMachine(new FakeTimeProvider());

        // Chrome in front with "hide": the island is gone.
        island.SetRest(SmartHidePolicy.Decide(HideSettings, IslandAnchor.TopCenter, 0, Chrome));
        Assert.Equal(IslandMode.Hidden, island.Mode);
        Assert.False(PrivacyIndicator.IsVisible(island.Mode, Microphone));

        // The microphone starts: the host re-applies the rule with privacy active.
        island.SetRest(SmartHidePolicy.Decide(HideSettings, IslandAnchor.TopCenter, 0, Chrome, privacyActive: true));
        Assert.Equal(IslandMode.Retracted, island.Mode);
        Assert.True(PrivacyIndicator.IsVisible(island.Mode, Microphone));

        // It stops: hidden again.
        island.SetRest(SmartHidePolicy.Decide(HideSettings, IslandAnchor.TopCenter, 0, Chrome, privacyActive: false));
        Assert.Equal(IslandMode.Hidden, island.Mode);
    }

    [Fact]
    public void Microphone_on_while_retracted_stays_retracted()
    {
        var settings = new AppSettings { SmartHideProcesses = ["chrome"], SmartHideBehavior = SmartHideBehavior.Retract };
        var island = new IslandStateMachine(new FakeTimeProvider());
        island.SetRest(SmartHidePolicy.Decide(settings, IslandAnchor.TopCenter, 0, Chrome, privacyActive: true));

        Assert.Equal(IslandMode.Retracted, island.Mode);
        Assert.True(PrivacyIndicator.IsVisible(island.Mode, Microphone));
    }

    [Theory]
    [InlineData(IslandMode.Compact)]
    [InlineData(IslandMode.Activity)]
    [InlineData(IslandMode.Expanded)]
    [InlineData(IslandMode.Retracted)]
    public void Camera_on_shows_in_every_visible_state(IslandMode mode)
    {
        Assert.True(PrivacyIndicator.IsVisible(mode, Camera));
        Assert.False(PrivacyIndicator.IsVisible(mode, PrivacyState.None));
    }

    [Fact]
    public void Camera_on_while_expanded_keeps_the_island_open_and_the_mark_visible()
    {
        var island = new IslandStateMachine(new FakeTimeProvider());
        island.Activate();
        island.SetRest(SmartHidePolicy.Decide(HideSettings, IslandAnchor.TopCenter, 0, new ForegroundInfo("notepad", true, true), privacyActive: true));

        Assert.Equal(IslandMode.Expanded, island.Mode);
        Assert.True(PrivacyIndicator.IsVisible(island.Mode, Camera));
    }

    [Fact]
    public void A_hide_the_user_asked_for_wins()
    {
        var island = new IslandStateMachine(new FakeTimeProvider());
        island.Hide();

        Assert.False(PrivacyIndicator.IsVisible(island.Mode, Microphone));
    }

    [Fact]
    public void The_description_names_what_is_in_use()
    {
        Assert.Equal("Microphone in use", Microphone.Description);
        Assert.Equal("Camera in use", Camera.Description);
        Assert.Equal("Microphone and camera in use", new PrivacyState(true, true).Description);
        Assert.False(PrivacyState.None.IsActive);
    }
}
