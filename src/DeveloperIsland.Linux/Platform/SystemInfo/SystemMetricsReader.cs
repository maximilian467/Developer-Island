using System.Globalization;
using DeveloperIsland.Core.SystemInfo;

namespace DeveloperIsland.Platform.SystemInfo;

/// <summary>
/// Reads CPU, memory and battery from the kernel's own files: <c>/proc/stat</c>, <c>/proc/meminfo</c>
/// and <c>/sys/class/power_supply</c>. No processes are started. CPU load is the share of non-idle
/// time between two consecutive readings, as on Windows.
/// </summary>
internal sealed class SystemMetricsReader
{
    private ulong _lastIdle;
    private ulong _lastTotal;

    public SystemSample? Read()
    {
        if (ReadCpuTimes() is not { } times)
        {
            return null;
        }

        var cpu = 0.0;
        if (_lastTotal != 0 && times.Total > _lastTotal)
        {
            var total = times.Total - _lastTotal;
            var idle = times.Idle - _lastIdle;
            cpu = Math.Clamp(100.0 * (total - Math.Min(idle, total)) / total, 0, 100);
        }

        _lastIdle = times.Idle;
        _lastTotal = times.Total;

        if (ReadMemory() is not { } memory)
        {
            return null;
        }

        var (battery, charging) = ReadBattery();
        return new SystemSample(cpu, memory.Total - memory.Available, memory.Total, battery, charging);
    }

    /// <summary>The battery level (null without a battery) and whether the machine runs on mains power.</summary>
    public static (int? Percent, bool OnMains) ReadBattery()
    {
        int? percent = null;
        var onMains = false;
        var sawMains = false;
        try
        {
            foreach (var dir in Directory.EnumerateDirectories("/sys/class/power_supply"))
            {
                var type = ReadText(Path.Combine(dir, "type"));
                if (type == "Battery" && ReadText(Path.Combine(dir, "scope")) != "Device")
                {
                    // "scope = Device" marks a mouse or headset battery, not the system's.
                    if (int.TryParse(ReadText(Path.Combine(dir, "capacity")), NumberStyles.Integer, CultureInfo.InvariantCulture, out var capacity) && capacity is >= 0 and <= 100)
                    {
                        percent = percent is null ? capacity : Math.Min(percent.Value, capacity);
                    }
                }
                else if (type is "Mains" or "USB" or "USB_C" or "USB_PD")
                {
                    sawMains = true;
                    onMains |= ReadText(Path.Combine(dir, "online")) == "1";
                }
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        // Without a reported adapter, a machine without a battery runs on mains.
        return (percent, onMains || (!sawMains && percent is null));
    }

    private static (ulong Idle, ulong Total)? ReadCpuTimes()
    {
        try
        {
            using var reader = new StreamReader("/proc/stat");
            var line = reader.ReadLine();
            if (line is null || !line.StartsWith("cpu ", StringComparison.Ordinal))
            {
                return null;
            }

            // user nice system idle iowait irq softirq steal (guest times are already in user/nice).
            var fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1).Take(8)
                .Select(f => ulong.TryParse(f, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0).ToArray();
            if (fields.Length < 4)
            {
                return null;
            }

            var idle = fields[3] + (fields.Length > 4 ? fields[4] : 0);
            var total = 0UL;
            foreach (var f in fields)
            {
                total += f;
            }

            return (idle, total);
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static (ulong Total, ulong Available)? ReadMemory()
    {
        try
        {
            ulong total = 0, available = 0;
            foreach (var line in File.ReadLines("/proc/meminfo"))
            {
                if (line.StartsWith("MemTotal:", StringComparison.Ordinal))
                {
                    total = Kilobytes(line);
                }
                else if (line.StartsWith("MemAvailable:", StringComparison.Ordinal))
                {
                    available = Kilobytes(line);
                }

                if (total != 0 && available != 0)
                {
                    break;
                }
            }

            return total == 0 ? null : (total, Math.Min(available, total));
        }
        catch (IOException)
        {
            return null;
        }

        static ulong Kilobytes(string line)
        {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return parts.Length >= 2 && ulong.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var kb) ? kb * 1024 : 0;
        }
    }

    internal static string? ReadText(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path).Trim() : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
