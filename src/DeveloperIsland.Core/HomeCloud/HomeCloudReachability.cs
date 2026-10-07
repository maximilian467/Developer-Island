using System.Diagnostics;
using DeveloperIsland.Core.Diagnostics;

namespace DeveloperIsland.Core.HomeCloud;

public sealed record HomeCloudReachabilityResult(bool Reachable, string? Reason);

/// <summary>On-demand check whether the target folder can be reached. No polling, no timer.</summary>
public static class HomeCloudReachability
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(2);

    public static Task<HomeCloudReachabilityResult> CheckAsync(string targetDirectory, TimeSpan? timeout = null, CancellationToken ct = default) =>
        CheckAsync(targetDirectory, Directory.Exists, timeout, ct);

    /// <summary>
    /// Directory.Exists on an unreachable UNC path can block for many seconds, so the probe runs on the
    /// thread pool and is abandoned when the timeout passes.
    /// </summary>
    internal static async Task<HomeCloudReachabilityResult> CheckAsync(
        string targetDirectory,
        Func<string, bool> probe,
        TimeSpan? timeout = null,
        CancellationToken ct = default)
    {
        var watch = Stopwatch.StartNew();
        HomeCloudReachabilityResult result;
        try
        {
            var exists = await Task.Run(() => probe(targetDirectory), ct).WaitAsync(timeout ?? DefaultTimeout, ct).ConfigureAwait(false);
            result = exists
                ? new HomeCloudReachabilityResult(true, null)
                : new HomeCloudReachabilityResult(false, "Target folder not found");
        }
        catch (TimeoutException)
        {
            result = new HomeCloudReachabilityResult(false, "Home Cloud not reachable");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            result = new HomeCloudReachabilityResult(false, "Home Cloud not reachable");
        }

        Log.Info("homecloud", "reachability checked", new { reachable = result.Reachable, ms = watch.ElapsedMilliseconds });
        return result;
    }
}
