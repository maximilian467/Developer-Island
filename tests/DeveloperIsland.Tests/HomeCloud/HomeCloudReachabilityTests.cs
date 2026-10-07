using System.Diagnostics;
using DeveloperIsland.Core.HomeCloud;

namespace DeveloperIsland.Tests.HomeCloud;

public class HomeCloudReachabilityTests
{
    [Fact]
    public async Task An_existing_folder_is_reachable()
    {
        var result = await HomeCloudReachability.CheckAsync(TestData.TempDirectory());

        Assert.True(result.Reachable);
        Assert.Null(result.Reason);
    }

    [Fact]
    public async Task A_missing_folder_is_not_reachable()
    {
        var result = await HomeCloudReachability.CheckAsync(Path.Combine(TestData.TempDirectory(), "missing"));

        Assert.False(result.Reachable);
        Assert.False(string.IsNullOrEmpty(result.Reason));
    }

    [Fact]
    public async Task A_blocking_probe_times_out_quickly()
    {
        using var release = new ManualResetEventSlim();
        var watch = Stopwatch.StartNew();

        var result = await HomeCloudReachability.CheckAsync(
            @"\\nowhere\share",
            _ => { release.Wait(TimeSpan.FromSeconds(30)); return true; },
            TimeSpan.FromMilliseconds(200));
        watch.Stop();
        release.Set();

        Assert.False(result.Reachable);
        Assert.Equal("Home Cloud not reachable", result.Reason);
        Assert.InRange(watch.ElapsedMilliseconds, 150, 3000);
    }

    [Fact]
    public async Task A_throwing_probe_is_not_reachable()
    {
        var result = await HomeCloudReachability.CheckAsync("x", _ => throw new IOException("denied"));

        Assert.False(result.Reachable);
        Assert.Equal("Home Cloud not reachable", result.Reason);
    }
}
