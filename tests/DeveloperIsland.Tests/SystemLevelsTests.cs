using DeveloperIsland.Core.SystemInfo;
using Xunit;

namespace DeveloperIsland.Tests;

public class SystemLevelsTests
{
    [Theory]
    [InlineData(9, SystemLevel.Good)]
    [InlineData(59.9, SystemLevel.Good)]
    [InlineData(60, SystemLevel.Warning)]
    [InlineData(84, SystemLevel.Warning)]
    [InlineData(85, SystemLevel.Critical)]
    [InlineData(100, SystemLevel.Critical)]
    public void Load_levels(double percent, SystemLevel expected) => Assert.Equal(expected, SystemLevels.Load(percent));

    [Theory]
    [InlineData(60, SystemLevel.Good)]
    [InlineData(75, SystemLevel.Warning)]
    [InlineData(90, SystemLevel.Critical)]
    public void Memory_has_its_own_thresholds(double percent, SystemLevel expected) => Assert.Equal(expected, SystemLevels.Memory(percent));

    [Fact]
    public void Each_temperature_kind_has_its_own_thresholds()
    {
        // 70 °C is normal for a CPU but hot for an SSD and alarming for a battery.
        Assert.Equal(SystemLevel.Good, SystemLevels.Temperature(TemperatureKind.Cpu, 70));
        Assert.Equal(SystemLevel.Critical, SystemLevels.Temperature(TemperatureKind.Storage, 70));
        Assert.Equal(SystemLevel.Critical, SystemLevels.Temperature(TemperatureKind.Battery, 70));
        Assert.Equal(SystemLevel.Warning, SystemLevels.Temperature(TemperatureKind.Cpu, 88));
        Assert.Equal(SystemLevel.Critical, SystemLevels.Temperature(TemperatureKind.Gpu, 92));
    }

    [Fact]
    public void A_single_spike_does_not_change_the_load_level()
    {
        Assert.Equal(SystemLevel.Good, SystemLevels.RecentLoad([10, 12, 9, 11, 100], SystemLevels.Load));
        Assert.Equal(SystemLevel.Critical, SystemLevels.RecentLoad([95, 97, 99, 100, 96], SystemLevels.Load));
        Assert.Equal(SystemLevel.Good, SystemLevels.RecentLoad([], SystemLevels.Load));
    }
}
