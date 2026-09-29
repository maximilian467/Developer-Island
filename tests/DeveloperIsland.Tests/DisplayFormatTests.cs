using System.Globalization;
using DeveloperIsland.Core.Formatting;

namespace DeveloperIsland.Tests;

public class DisplayFormatTests
{
    private static readonly CultureInfo En = CultureInfo.GetCultureInfo("en-US");
    private static readonly CultureInfo De = CultureInfo.GetCultureInfo("de-DE");

    [Theory]
    [InlineData(0, "0")]
    [InlineData(940, "940")]
    [InlineData(12_400, "12.4k")]
    [InlineData(620_000, "620k")]
    [InlineData(1_820_000, "1.82M")]
    [InlineData(26_828_374, "26.8M")]
    [InlineData(2_300_000_000, "2.3B")]
    public void Tokens_are_compact(long value, string expected)
    {
        Assert.Equal(expected, DisplayFormat.Tokens(value, En));
    }

    [Fact]
    public void Tokens_follow_the_culture_decimal_separator()
    {
        Assert.Equal("1,82M", DisplayFormat.Tokens(1_820_000, De));
    }

    [Fact]
    public void Euro_is_shown_with_two_decimals_in_the_user_format()
    {
        Assert.Equal("€18.42", DisplayFormat.Euro(18.4213m, En));
        Assert.Equal("18,42 €", DisplayFormat.Euro(18.4213m, De));
    }

    [Theory]
    [InlineData(25 * 60, "25:00")]
    [InlineData(42 * 60 + 13, "42:13")]
    [InlineData(0.4, "0:01")]
    [InlineData(0, "0:00")]
    [InlineData(3909, "1:05:09")]
    public void Clock_rounds_up_for_countdowns(double seconds, string expected)
    {
        Assert.Equal(expected, DisplayFormat.Clock(TimeSpan.FromSeconds(seconds)));
    }

    [Fact]
    public void Media_time_rounds_down()
    {
        Assert.Equal("1:42", DisplayFormat.MediaTime(TimeSpan.FromSeconds(102.9)));
        Assert.Equal("3:51", DisplayFormat.MediaTime(TimeSpan.FromSeconds(231)));
    }

    [Theory]
    [InlineData(45, "45 min")]
    [InlineData(75, "1 h 15 min")]
    [InlineData(120, "2 h")]
    public void Durations_are_readable(int minutes, string expected)
    {
        Assert.Equal(expected, DisplayFormat.Duration(TimeSpan.FromMinutes(minutes)));
    }

    [Theory]
    [InlineData("claude-opus-5-5", "Opus 5.5")]
    [InlineData("claude-sonnet-4-5-20250929", "Sonnet 4.5")]
    [InlineData("claude-haiku-4-5", "Haiku 4.5")]
    [InlineData("gpt-5-codex", "gpt-5-codex")]
    [InlineData(null, "")]
    public void Model_labels_are_short(string? model, string expected)
    {
        Assert.Equal(expected, DisplayFormat.ModelLabel(model));
    }

    [Fact]
    public void No_dash_characters_other_than_hyphen_are_used()
    {
        var samples = new[]
        {
            DisplayFormat.Duration(TimeSpan.FromMinutes(75)),
            DisplayFormat.LimitWindow(300),
            DisplayFormat.ShortDay(new DateOnly(2026, 9, 24), En),
            DisplayFormat.Euro(-3m, En),
        };

        Assert.All(samples, s => Assert.DoesNotContain(s, c => c is '–' or '—'));
    }

    [Fact]
    public void Limit_window_is_described_in_hours()
    {
        Assert.Equal("5-hour limit", DisplayFormat.LimitWindow(300));
    }
}
