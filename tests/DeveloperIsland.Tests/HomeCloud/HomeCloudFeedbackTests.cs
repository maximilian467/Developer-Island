using System.Globalization;
using DeveloperIsland.Core.HomeCloud;

namespace DeveloperIsland.Tests.HomeCloud;

public class HomeCloudFeedbackTests
{
    private static readonly CultureInfo En = CultureInfo.GetCultureInfo("en-US");

    [Fact]
    public void Progress_reads_file_and_percent()
    {
        Assert.Equal("3/5 · 42 %", HomeCloudFeedback.ProgressText(new HomeCloudProgress(420, 1000, 3, 5)));
        Assert.Equal(100, HomeCloudFeedback.Percent(new HomeCloudProgress(1000, 1000, 5, 5)));
        Assert.Equal(0, HomeCloudFeedback.Percent(new HomeCloudProgress(0, 1000, 1, 5)));
    }

    [Fact]
    public void Empty_files_count_by_file()
    {
        Assert.Equal(50, HomeCloudFeedback.Percent(new HomeCloudProgress(0, 0, 1, 2)));
        Assert.Equal(100, HomeCloudFeedback.Percent(new HomeCloudProgress(0, 0, 0, 0)));
    }

    [Fact]
    public void Success_names_count_and_size_only()
    {
        var (title, detail) = HomeCloudFeedback.Result(HomeCloudUploadResult.Succeeded(3, 13_000_000, TimeSpan.FromSeconds(2)), En);
        Assert.Equal("Saved to Home Cloud", title);
        Assert.Equal("3 files · 12 MB", detail);

        Assert.Equal("1 file · 512 B", HomeCloudFeedback.Result(HomeCloudUploadResult.Succeeded(1, 512, TimeSpan.Zero), En).Detail);
    }

    [Theory]
    [InlineData("Target not reachable", "Home Cloud not reachable")]
    [InlineData("Target folder not found", "Home Cloud not reachable")]
    [InlineData("Access denied", "Could not save to Home Cloud")]
    [InlineData("Disk full", "Could not save to Home Cloud")]
    public void Failures_show_a_title_and_the_short_reason(string reason, string title)
    {
        var result = HomeCloudFeedback.Result(HomeCloudUploadResult.Failed(reason), En);
        Assert.Equal(title, result.Title);
        Assert.Equal(reason, result.Detail);
    }

    [Fact]
    public void An_unreachable_server_before_the_drop_keeps_a_useful_detail()
    {
        Assert.Equal(("Home Cloud not reachable", "Target folder not found"), HomeCloudFeedback.Unreachable("Target folder not found"));
        Assert.Equal("Check the connection to your server", HomeCloudFeedback.Unreachable("Home Cloud not reachable").Detail);
    }

    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(1023, "1023 B")]
    [InlineData(48 * 1024, "48 KB")]
    [InlineData(5 * 1024 * 1024 + 512 * 1024, "5.5 MB")]
    [InlineData(1288490189L, "1.2 GB")]
    public void Sizes_use_binary_units(long bytes, string expected)
    {
        Assert.Equal(expected, HomeCloudFeedback.Size(bytes, En));
    }
}
