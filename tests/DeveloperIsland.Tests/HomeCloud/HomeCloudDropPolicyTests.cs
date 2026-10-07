using DeveloperIsland.Core.HomeCloud;
using DeveloperIsland.Core.Island;

namespace DeveloperIsland.Tests.HomeCloud;

public class HomeCloudDropPolicyTests
{
    [Theory]
    [InlineData(IslandMode.Compact)]
    [InlineData(IslandMode.Activity)]
    [InlineData(IslandMode.Expanded)]
    [InlineData(IslandMode.Retracted)]
    [InlineData(IslandMode.Hidden)]
    public void Files_open_the_zone_from_any_mode(IslandMode mode)
    {
        Assert.True(HomeCloudDropPolicy.AcceptsDrag(enabled: true, hasFiles: true, mode, DropPhase.None));
        Assert.False(HomeCloudDropPolicy.AcceptsDrop(enabled: true, hasFiles: true, mode, DropPhase.None));
    }

    [Fact]
    public void The_open_zone_offers_a_copy()
    {
        Assert.True(HomeCloudDropPolicy.AcceptsDrag(true, true, IslandMode.Drop, DropPhase.Hover));
        Assert.True(HomeCloudDropPolicy.AcceptsDrop(true, true, IslandMode.Drop, DropPhase.Hover));
    }

    [Fact]
    public void Text_links_and_other_data_are_refused()
    {
        Assert.False(HomeCloudDropPolicy.AcceptsDrag(true, hasFiles: false, IslandMode.Compact, DropPhase.None));
        Assert.False(HomeCloudDropPolicy.AcceptsDrop(true, hasFiles: false, IslandMode.Drop, DropPhase.Hover));
    }

    [Fact]
    public void A_running_upload_refuses_a_second_drag()
    {
        Assert.False(HomeCloudDropPolicy.AcceptsDrag(true, true, IslandMode.Drop, DropPhase.Uploading));
        Assert.False(HomeCloudDropPolicy.AcceptsDrop(true, true, IslandMode.Drop, DropPhase.Uploading));
    }

    [Fact]
    public void A_shown_result_starts_over_on_a_new_drag()
    {
        Assert.True(HomeCloudDropPolicy.AcceptsDrag(true, true, IslandMode.Drop, DropPhase.Succeeded));
        Assert.True(HomeCloudDropPolicy.AcceptsDrag(true, true, IslandMode.Drop, DropPhase.Failed));
        Assert.False(HomeCloudDropPolicy.AcceptsDrop(true, true, IslandMode.Drop, DropPhase.Failed));
    }

    [Theory]
    [InlineData(IslandMode.Compact, DropPhase.None)]
    [InlineData(IslandMode.Drop, DropPhase.Hover)]
    [InlineData(IslandMode.Hidden, DropPhase.None)]
    public void Home_cloud_off_accepts_nothing(IslandMode mode, DropPhase phase)
    {
        Assert.False(HomeCloudDropPolicy.AcceptsDrag(enabled: false, hasFiles: true, mode, phase));
        Assert.False(HomeCloudDropPolicy.AcceptsDrop(enabled: false, hasFiles: true, mode, phase));
    }
}
