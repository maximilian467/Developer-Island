using DeveloperIsland.Core.Formatting;
using Xunit;

namespace DeveloperIsland.Tests;

/// <summary>Values that tick in the compact capsule must reserve one width, so the capsule does not jitter.</summary>
public class CompactWidthTests
{
    [Theory]
    [InlineData("CPU 9%", "CPU 10%", "CPU 100%")]
    [InlineData("GPU 0%", "GPU 42%", "GPU 100%")]
    [InlineData("RAM 7%", "RAM 75%", "RAM 100%")]
    [InlineData("CPU 9% · 52°", "CPU 100% · 100°", "CPU 45% · 8°")]
    [InlineData("1.12M · 72%", "999k · 5%", "12.4M · 100%")]
    [InlineData("1.12M tokens", "376k tokens", "86.7k tokens")]
    [InlineData("4:05", "24:59", "9:00")]
    [InlineData("in 5 min", "in 45 min", "in 12 min")]
    public void Values_of_one_kind_reserve_the_same_width(string a, string b, string c)
    {
        Assert.Equal(CompactWidth.Reserve(a), CompactWidth.Reserve(b));
        Assert.Equal(CompactWidth.Reserve(a), CompactWidth.Reserve(c));
    }

    [Fact]
    public void Reserves_are_the_widest_form()
    {
        Assert.Equal("CPU 000%", CompactWidth.Reserve("CPU 9%"));
        Assert.Equal("000.00M · 000%", CompactWidth.Reserve("376k · 7%"));
        Assert.Equal("000,00M", CompactWidth.Reserve("1,12M", ","));
        Assert.Equal("000,00M", CompactWidth.Reserve("376k", ","));
        Assert.Equal("00:00", CompactWidth.Reserve("4:05"));
    }

    [Fact]
    public void Text_without_numbers_is_its_own_reserve()
    {
        Assert.Equal("Nothing playing", CompactWidth.Reserve("Nothing playing"));
        Assert.Equal(string.Empty, CompactWidth.Reserve(string.Empty));
    }
}
