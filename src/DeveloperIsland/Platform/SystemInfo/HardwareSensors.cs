using System.Runtime.InteropServices;
using DeveloperIsland.Core.Diagnostics;
using DeveloperIsland.Core.SystemInfo;
using LibreHardwareMonitor.Hardware;

namespace DeveloperIsland.Platform.SystemInfo;

/// <summary>
/// GPU load, temperatures and fans through LibreHardwareMonitor (an established open-source hardware
/// monitoring library) plus Windows' ACPI thermal zones (performance counters). Read-only: nothing
/// here writes to hardware.
/// <list type="bullet">
/// <item>Opening takes a few seconds, so it happens on a background thread; until then only CPU,
/// memory and battery are reported.</item>
/// <item>The CPU group is kept only if it actually delivers temperatures (it needs a driver and
/// administrator rights; without them its sensors stay empty and its update costs ~9 ms).</item>
/// <item>GPU load is read every second (well under a millisecond); temperatures and fans every five
/// seconds.</item>
/// <item>Anything the device does not expose simply never appears in a sample.</item>
/// </list>
/// </summary>
internal sealed class HardwareSensors : IDisposable
{
    private static readonly TimeSpan SlowInterval = TimeSpan.FromSeconds(5);

    private readonly object _gate = new();
    private Computer? _computer;
    private ThermalZoneCounter? _zones;
    private DateTimeOffset _lastSlow = DateTimeOffset.MinValue;
    private IReadOnlyList<TemperatureReading> _temperatures = [];
    private IReadOnlyList<FanReading> _fans = [];
    private bool _disposed;

    /// <summary>Opens the sensors in the background.</summary>
    public void Open()
    {
        _ = Task.Run(() =>
        {
            try
            {
                var computer = new Computer
                {
                    IsCpuEnabled = true,
                    IsGpuEnabled = true,
                    IsStorageEnabled = true,
                    IsMotherboardEnabled = true,
                    IsBatteryEnabled = true,
                };
                computer.Open();
                foreach (var hardware in computer.Hardware)
                {
                    hardware.Update();
                }

                // CPU temperatures need a kernel driver; without one the group is all cost and no data.
                var cpuTemps = computer.Hardware.Where(h => h.HardwareType == HardwareType.Cpu)
                    .SelectMany(h => h.Sensors).Any(s => s.SensorType == SensorType.Temperature && s.Value is > 0);
                if (!cpuTemps)
                {
                    computer.IsCpuEnabled = false;
                }

                // Groups that deliver nothing on this device (no board sensors on most laptops, storage
                // and battery temperatures without admin rights) are switched off to save work and memory.
                bool Useful(HardwareType type) => computer.Hardware.Where(h => h.HardwareType == type)
                    .SelectMany(h => h.Sensors.Concat(h.SubHardware.SelectMany(sub => { sub.Update(); return sub.Sensors; })))
                    .Any(s => s.SensorType is SensorType.Temperature or SensorType.Fan && s.Value is > 0);
                if (!Useful(HardwareType.Storage)) computer.IsStorageEnabled = false;
                if (!Useful(HardwareType.Motherboard)) computer.IsMotherboardEnabled = false;
                if (!Useful(HardwareType.Battery)) computer.IsBatteryEnabled = false;

                var zones = ThermalZoneCounter.TryCreate();
                lock (_gate)
                {
                    if (_disposed)
                    {
                        computer.Close();
                        zones?.Dispose();
                        return;
                    }

                    _computer = computer;
                    _zones = zones;
                }

                Log.Info("system", "Hardware sensors ready", new
                {
                    gpus = computer.Hardware.Count(h => h.HardwareType is HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel),
                    cpuTemperatures = cpuTemps,
                    storage = computer.IsStorageEnabled,
                    board = computer.IsMotherboardEnabled,
                    thermalZones = zones is not null,
                });
            }
            catch (Exception ex)
            {
                Log.Warn("system", "Hardware sensors unavailable; CPU, memory and battery only", new { error = ex.GetType().Name });
            }
        });
    }

    /// <summary>Adds GPU load, temperatures and fans to a basic sample.</summary>
    public SystemSample Enrich(SystemSample sample)
    {
        if (!Monitor.TryEnter(_gate))
        {
            return sample; // a slow read is still running; skip this second
        }

        try
        {
            if (_computer is null || _disposed)
            {
                return sample;
            }

            double? gpu = null;
            foreach (var hardware in _computer.Hardware.Where(IsGpu))
            {
                hardware.Update();
                gpu = Max(gpu, GpuLoad(hardware));
            }

            var now = DateTimeOffset.UtcNow;
            if (now - _lastSlow >= SlowInterval)
            {
                _lastSlow = now;
                ReadSlow();
            }

            return sample with { GpuPercent = gpu, Temperatures = _temperatures, Fans = _fans };
        }
        catch (Exception ex)
        {
            Log.Debug("system", "Sensor read failed", new { error = ex.GetType().Name });
            return sample;
        }
        finally
        {
            Monitor.Exit(_gate);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            try
            {
                _computer?.Close();
            }
            catch (Exception ex)
            {
                Log.Debug("system", "Sensors did not close cleanly", new { error = ex.GetType().Name });
            }

            _computer = null;
            _zones?.Dispose();
            _zones = null;
        }
    }

    private void ReadSlow()
    {
        var temperatures = new List<TemperatureReading>();
        var fans = new List<FanReading>();
        foreach (var hardware in _computer!.Hardware)
        {
            if (!IsGpu(hardware))
            {
                hardware.Update(); // GPUs were updated this second already
            }

            Collect(hardware, KindOf(hardware.HardwareType), temperatures, fans);
            foreach (var sub in hardware.SubHardware)
            {
                sub.Update();
                Collect(sub, KindOf(hardware.HardwareType), temperatures, fans);
            }
        }

        if (_zones?.Read() is { } zones)
        {
            temperatures.AddRange(zones.Select((c, i) => new TemperatureReading(TemperatureKind.ThermalZone, $"Thermal zone {i + 1}", c)));
        }

        _temperatures = temperatures;
        _fans = fans;
    }

    private static void Collect(IHardware hardware, TemperatureKind? kind, List<TemperatureReading> temperatures, List<FanReading> fans)
    {
        foreach (var sensor in hardware.Sensors)
        {
            if (sensor.Value is not { } value || !float.IsFinite(value))
            {
                continue;
            }

            if (sensor.SensorType == SensorType.Temperature && kind is { } k && IsPlausible(value) && IsSummary(k, sensor.Name))
            {
                temperatures.Add(new TemperatureReading(k, Name(k, hardware, sensor), value));
            }
            else if (sensor.SensorType == SensorType.Fan && value > 0)
            {
                fans.Add(new FanReading(sensor.Name, value));
            }
        }
    }

    /// <summary>One reading per part, not every core: package or max for CPUs, core for GPUs.</summary>
    private static bool IsSummary(TemperatureKind kind, string name) => kind switch
    {
        TemperatureKind.Cpu => name is "CPU Package" or "Core Max" or "Core (Tctl/Tdie)" or "Core (Tctl)",
        TemperatureKind.Gpu => name is "GPU Core" or "GPU Hot Spot",
        _ => true,
    };

    private static string Name(TemperatureKind kind, IHardware hardware, ISensor sensor) => kind switch
    {
        TemperatureKind.Cpu => "CPU",
        TemperatureKind.Gpu => sensor.Name == "GPU Hot Spot" ? "GPU hot spot" : "GPU",
        TemperatureKind.Storage => hardware.Name,
        _ => sensor.Name,
    };

    private static bool IsPlausible(float celsius) => celsius is > 1 and < 125;

    private static bool IsGpu(IHardware hardware) => hardware.HardwareType is HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel;

    private static TemperatureKind? KindOf(HardwareType type) => type switch
    {
        HardwareType.Cpu => TemperatureKind.Cpu,
        HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel => TemperatureKind.Gpu,
        HardwareType.Storage => TemperatureKind.Storage,
        HardwareType.Motherboard or HardwareType.SuperIO or HardwareType.EmbeddedController => TemperatureKind.Motherboard,
        HardwareType.Battery => TemperatureKind.Battery,
        _ => null,
    };

    /// <summary>Discrete GPUs report "GPU Core"; integrated ones the 3D engine load.</summary>
    private static double? GpuLoad(IHardware gpu)
    {
        var core = gpu.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Load && s.Name == "GPU Core")?.Value;
        var d3d = gpu.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Load && s.Name == "D3D 3D")?.Value;
        var value = core ?? d3d;
        return value is { } v && float.IsFinite(v) ? Math.Clamp(v, 0, 100) : null;
    }

    private static double? Max(double? a, double? b) => a is null ? b : b is null ? a : Math.Max(a.Value, b.Value);
}

/// <summary>ACPI thermal zones from the "Thermal Zone Information" performance counters (no admin rights needed).</summary>
internal sealed class ThermalZoneCounter : IDisposable
{
    private const uint PDH_FMT_DOUBLE = 0x200;
    private const uint PDH_MORE_DATA = 0x800007D2;

    private IntPtr _query;
    private readonly IntPtr _counter;

    private ThermalZoneCounter(IntPtr query, IntPtr counter)
    {
        _query = query;
        _counter = counter;
    }

    public static ThermalZoneCounter? TryCreate()
    {
        if (PdhOpenQuery(null, IntPtr.Zero, out var query) != 0)
        {
            return null;
        }

        if (PdhAddEnglishCounter(query, @"\Thermal Zone Information(*)\High Precision Temperature", IntPtr.Zero, out var counter) != 0)
        {
            PdhCloseQuery(query);
            return null;
        }

        var zones = new ThermalZoneCounter(query, counter);
        return zones.Read() is { Count: > 0 } ? zones : Dispose(zones);

        static ThermalZoneCounter? Dispose(ThermalZoneCounter z)
        {
            z.Dispose();
            return null;
        }
    }

    /// <summary>Plausible zone temperatures in °C (the counter reports tenths of a kelvin).</summary>
    public IReadOnlyList<double>? Read()
    {
        if (_query == IntPtr.Zero || PdhCollectQueryData(_query) != 0)
        {
            return null;
        }

        uint size = 0;
        if (PdhGetFormattedCounterArray(_counter, PDH_FMT_DOUBLE, ref size, out _, IntPtr.Zero) != PDH_MORE_DATA)
        {
            return null;
        }

        var buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            if (PdhGetFormattedCounterArray(_counter, PDH_FMT_DOUBLE, ref size, out var count, buffer) != 0)
            {
                return null;
            }

            // PDH_FMT_COUNTERVALUE_ITEM_W: name pointer, then { CStatus (4), padding (4), double }.
            var itemSize = IntPtr.Size + 16;
            var result = new List<double>();
            for (var i = 0; i < count; i++)
            {
                var tenthsKelvin = Marshal.PtrToStructure<double>(buffer + i * itemSize + IntPtr.Size + 8);
                var celsius = tenthsKelvin / 10 - 273.15;
                if (celsius is > 1 and < 125)
                {
                    result.Add(Math.Round(celsius, 1));
                }
            }

            return result;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    public void Dispose()
    {
        if (_query != IntPtr.Zero)
        {
            PdhCloseQuery(_query);
            _query = IntPtr.Zero;
        }
    }

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhOpenQuery(string? source, IntPtr user, out IntPtr query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhAddEnglishCounter(IntPtr query, string path, IntPtr user, out IntPtr counter);

    [DllImport("pdh.dll")]
    private static extern uint PdhCollectQueryData(IntPtr query);

    [DllImport("pdh.dll")]
    private static extern uint PdhCloseQuery(IntPtr query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhGetFormattedCounterArray(IntPtr counter, uint format, ref uint bufferSize, out uint count, IntPtr buffer);
}
