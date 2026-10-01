namespace DeveloperIsland.Core.SystemInfo;

/// <summary>How a reading looks: calm, worth a glance, or worth acting on.</summary>
public enum SystemLevel
{
    Good,
    Warning,
    Critical,
}

/// <summary>
/// Thresholds per metric. Load levels use the last few seconds, not one sample, so a short spike
/// does not make a graph flicker between colors.
/// </summary>
public static class SystemLevels
{
    /// <summary>Samples (one per second) a load level is averaged over.</summary>
    public const int LoadWindow = 5;

    public static SystemLevel Of(double value, double warning, double critical) =>
        value >= critical ? SystemLevel.Critical : value >= warning ? SystemLevel.Warning : SystemLevel.Good;

    /// <summary>CPU or GPU load: busy from 60 %, saturated from 85 %.</summary>
    public static SystemLevel Load(double percent) => Of(percent, 60, 85);

    /// <summary>Memory in use: tight from 75 %, close to paging from 90 %.</summary>
    public static SystemLevel Memory(double percent) => Of(percent, 75, 90);

    /// <summary>The load level of the recent history (the mean of the last <see cref="LoadWindow"/> samples).</summary>
    public static SystemLevel RecentLoad(IReadOnlyList<double> history, Func<double, SystemLevel> level)
    {
        if (history.Count == 0)
        {
            return SystemLevel.Good;
        }

        var recent = history.Skip(Math.Max(0, history.Count - LoadWindow)).ToList();
        return level(recent.Average());
    }

    /// <summary>
    /// Temperatures by what they measure: laptop CPUs run hot by design (throttling near 100 °C),
    /// SSDs and batteries much cooler.
    /// </summary>
    public static SystemLevel Temperature(TemperatureKind kind, double celsius) => kind switch
    {
        TemperatureKind.Cpu => Of(celsius, 85, 95),
        TemperatureKind.Gpu => Of(celsius, 80, 90),
        TemperatureKind.Storage => Of(celsius, 60, 70),
        TemperatureKind.Battery => Of(celsius, 45, 55),
        TemperatureKind.Motherboard => Of(celsius, 65, 80),
        _ => Of(celsius, 80, 95),
    };
}
