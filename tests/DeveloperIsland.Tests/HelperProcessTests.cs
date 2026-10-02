using System.Diagnostics;
using DeveloperIsland.Core.Diagnostics;

namespace DeveloperIsland.Tests;

public class HelperProcessTests
{
    [Fact]
    public void A_helper_tool_joins_the_job_that_ends_it_with_the_app()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var helper = Process.Start(new ProcessStartInfo("cmd.exe", "/c ping -n 30 127.0.0.1 >nul") { UseShellExecute = false, CreateNoWindow = true })!;
        try
        {
            Assert.False(HelperProcessJob.Contains(helper));
            Assert.True(HelperProcessJob.Adopt(helper));
            Assert.True(HelperProcessJob.Contains(helper));
        }
        finally
        {
            helper.Kill(entireProcessTree: true);
        }
    }

    [Fact]
    public async Task Tools_run_through_the_runner_still_return_their_output()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var result = await ProcessRunner.RunAsync("cmd.exe", ["/c", "echo island"]);

        Assert.True(result.Succeeded);
        Assert.Equal("island", result.Output.Trim());
    }
}
