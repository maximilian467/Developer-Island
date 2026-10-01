using System.Globalization;
using DeveloperIsland.Core.SystemInfo;
using DeveloperIsland.Platform.SystemInfo;

namespace DeveloperIsland;

/// <summary>
/// <c>DeveloperIsland.exe --system-report</c>: prints what the System module can read on this device
/// (which tiles, temperatures and fans would appear) and exits. No UI, no settings, nothing written.
/// Useful to check hardware support without starting the island.
/// </summary>
internal static class SystemReport
{
    public const string Argument = "--system-report";

    public static bool IsRequested(string[] args) => args.Contains(Argument, StringComparer.Ordinal);

    public static int Run()
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        var reader = new SystemMetricsReader();
        using var sensors = new HardwareSensors();
        sensors.Open();
        reader.Read(); // the first CPU reading only primes the counters

        // Opening the sensor library takes a few seconds; then take three one-second samples.
        var samples = new List<SystemSample>();
        var started = DateTime.UtcNow;
        while (DateTime.UtcNow - started < TimeSpan.FromSeconds(12) && samples.Count < 3)
        {
            Thread.Sleep(1000);
            if (reader.Read() is { } basic)
            {
                var sample = sensors.Enrich(basic);
                if (sample.HasGpu || sample.Temperatures.Count > 0 || DateTime.UtcNow - started > TimeSpan.FromSeconds(8))
                {
                    samples.Add(sample);
                }
            }
        }

        var c = CultureInfo.InvariantCulture;
        var caps = HardwareCapabilities.From(samples);
        var last = samples.LastOrDefault();
        Console.WriteLine("Developer Island system report");
        Console.WriteLine($"CPU      {(last is null ? "-" : last.CpuPercent.ToString("0", c) + " %")}");
        Console.WriteLine($"Memory   {(last is null ? "-" : last.MemoryPercent.ToString("0", c) + " %")}");
        Console.WriteLine($"GPU      {(caps.HasGpu ? last?.GpuPercent?.ToString("0", c) + " %" : "not exposed (tile hidden)")}");
        Console.WriteLine($"Battery  {(caps.HasBattery ? last?.BatteryPercent + " %" + (last!.IsCharging ? ", charging" : string.Empty) : "none (tile hidden)")}");
        Console.WriteLine("Temperatures:");
        foreach (var t in last?.Temperatures ?? [])
        {
            Console.WriteLine($"  {t.Kind,-12} {t.Name,-28} {t.Celsius.ToString("0.0", c)} °C");
        }

        if (last?.Temperatures.Count is null or 0)
        {
            Console.WriteLine("  none exposed (no temperature rows)");
        }

        Console.WriteLine("Fans:");
        foreach (var f in last?.Fans ?? [])
        {
            Console.WriteLine($"  {f.Name,-28} {f.Rpm.ToString("0", c)} RPM");
        }

        if (last?.Fans.Count is null or 0)
        {
            Console.WriteLine("  none exposed (no fan rows)");
        }

        Console.WriteLine("Manual fan control: not offered (no crash-safe fallback to firmware control)");
        return 0;
    }
}
