using System.Globalization;
using System.Runtime.InteropServices;
using DeveloperIsland.Core.Diagnostics;
using DeveloperIsland.Core.SystemInfo;

namespace DeveloperIsland.Platform.SystemInfo;

/// <summary>
/// GPU load, temperatures and fans on Linux, read-only and without root:
/// <list type="bullet">
/// <item>Temperatures and fans from the kernel's hwmon drivers (<c>/sys/class/hwmon</c>): coretemp and
/// k10temp for the CPU, amdgpu/nouveau/radeon for GPUs, nvme and drivetemp for drives, Super I/O and
/// laptop drivers (nct6775, it87, thinkpad, dell_smm, asus...) for the board and fans, and ACPI
/// thermal zones.</item>
/// <item>GPU load from amdgpu's <c>gpu_busy_percent</c>, and from NVIDIA's management library (NVML,
/// installed with the proprietary driver) for NVIDIA cards, which also gives their temperature.</item>
/// </list>
/// Anything the device does not expose never appears in a sample, as on Windows. GPU load is read every
/// second; temperatures and fans every five seconds.
/// </summary>
internal sealed partial class HardwareSensors : IDisposable
{
    private static readonly TimeSpan SlowInterval = TimeSpan.FromSeconds(5);

    private readonly object _gate = new();
    private List<HwmonChip> _chips = [];
    private List<string> _amdBusyFiles = [];
    private Nvml? _nvml;
    private DateTimeOffset _lastSlow = DateTimeOffset.MinValue;
    private IReadOnlyList<TemperatureReading> _temperatures = [];
    private IReadOnlyList<FanReading> _fans = [];
    private bool _opened;
    private bool _disposed;

    public void Open()
    {
        _ = Task.Run(() =>
        {
            try
            {
                var chips = HwmonChip.Discover();
                var busy = Directory.Exists("/sys/class/drm")
                    ? Directory.EnumerateDirectories("/sys/class/drm", "card*")
                        .Where(d => !Path.GetFileName(d).Contains('-'))
                        .Select(d => Path.Combine(d, "device", "gpu_busy_percent"))
                        .Where(File.Exists).ToList()
                    : [];
                var nvml = Nvml.TryOpen();
                lock (_gate)
                {
                    if (_disposed)
                    {
                        nvml?.Dispose();
                        return;
                    }

                    _chips = chips;
                    _amdBusyFiles = busy;
                    _nvml = nvml;
                    _opened = true;
                }

                Log.Info("system", "Hardware sensors ready", new
                {
                    chips = chips.Select(c => c.Driver).ToArray(),
                    amdGpus = busy.Count,
                    nvidiaGpus = nvml?.Count ?? 0,
                });
            }
            catch (Exception ex)
            {
                Log.Warn("system", "Hardware sensors unavailable; CPU, memory and battery only", new { error = ex.GetType().Name });
            }
        });
    }

    public SystemSample Enrich(SystemSample sample)
    {
        if (!Monitor.TryEnter(_gate))
        {
            return sample;
        }

        try
        {
            if (!_opened || _disposed)
            {
                return sample;
            }

            double? gpu = null;
            foreach (var file in _amdBusyFiles)
            {
                if (double.TryParse(SystemMetricsReader.ReadText(file), NumberStyles.Float, CultureInfo.InvariantCulture, out var load))
                {
                    gpu = Max(gpu, Math.Clamp(load, 0, 100));
                }
            }

            if (_nvml?.MaxUtilization() is { } nv)
            {
                gpu = Max(gpu, nv);
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
            _nvml?.Dispose();
            _nvml = null;
        }
    }

    private void ReadSlow()
    {
        var temperatures = new List<TemperatureReading>();
        var fans = new List<FanReading>();
        var zone = 0;
        foreach (var chip in _chips)
        {
            chip.Read(temperatures, fans, ref zone);
        }

        if (_nvml is not null)
        {
            foreach (var celsius in _nvml.Temperatures())
            {
                temperatures.Add(new TemperatureReading(TemperatureKind.Gpu, "GPU", celsius));
            }
        }

        _temperatures = temperatures;
        _fans = fans;
    }

    private static double? Max(double? a, double? b) => a is null ? b : b is null ? a : Math.Max(a.Value, b.Value);

    /// <summary>One hwmon device and the inputs worth showing from it.</summary>
    private sealed class HwmonChip
    {
        private readonly string _dir;
        private readonly TemperatureKind? _kind;
        private readonly string _displayName;

        private HwmonChip(string dir, string driver, TemperatureKind? kind, string displayName)
        {
            _dir = dir;
            Driver = driver;
            _kind = kind;
            _displayName = displayName;
        }

        public string Driver { get; }

        public static List<HwmonChip> Discover()
        {
            var chips = new List<HwmonChip>();
            if (!Directory.Exists("/sys/class/hwmon"))
            {
                return chips;
            }

            foreach (var dir in Directory.EnumerateDirectories("/sys/class/hwmon").Order(StringComparer.Ordinal))
            {
                var driver = SystemMetricsReader.ReadText(Path.Combine(dir, "name")) ?? string.Empty;
                var kind = KindOf(driver);
                var name = kind == TemperatureKind.Storage ? StorageName(dir, driver) : driver;
                chips.Add(new HwmonChip(dir, driver, kind, name));
            }

            return chips;
        }

        public void Read(List<TemperatureReading> temperatures, List<FanReading> fans, ref int zone)
        {
            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(_dir, "*_input").Order(StringComparer.Ordinal).ToList();
            }
            catch (IOException)
            {
                return;
            }

            foreach (var input in files)
            {
                var file = Path.GetFileName(input);
                var stem = file[..^"_input".Length];
                var label = SystemMetricsReader.ReadText(Path.Combine(_dir, stem + "_label"));
                if (!double.TryParse(SystemMetricsReader.ReadText(input), NumberStyles.Float, CultureInfo.InvariantCulture, out var raw))
                {
                    continue;
                }

                if (stem.StartsWith("temp", StringComparison.Ordinal) && _kind is { } kind)
                {
                    var celsius = raw / 1000.0;
                    if (celsius is > 1 and < 125 && IsSummary(kind, label, stem))
                    {
                        var name = kind switch
                        {
                            TemperatureKind.Cpu => "CPU",
                            TemperatureKind.Gpu => label is "junction" ? "GPU hot spot" : "GPU",
                            TemperatureKind.Storage => _displayName,
                            TemperatureKind.ThermalZone => $"Thermal zone {++zone}",
                            _ => label ?? _displayName,
                        };
                        temperatures.Add(new TemperatureReading(kind, name, Math.Round(celsius, 1)));
                    }
                }
                else if (stem.StartsWith("fan", StringComparison.Ordinal) && raw > 0)
                {
                    fans.Add(new FanReading(label ?? FanName(stem), raw));
                }
            }
        }

        /// <summary>One reading per part, not every core: package or die for CPUs, edge and junction for GPUs.</summary>
        private static bool IsSummary(TemperatureKind kind, string? label, string stem) => kind switch
        {
            TemperatureKind.Cpu => label is null ? stem == "temp1" : label.StartsWith("Package id", StringComparison.Ordinal) || label is "Tdie" or "Tctl" or "Tccd1",
            TemperatureKind.Gpu => label is null or "edge" or "junction" or "GPU core",
            TemperatureKind.Storage => label is null or "Composite" || stem == "temp1",
            _ => true,
        };

        private static TemperatureKind? KindOf(string driver) => driver switch
        {
            "coretemp" or "k10temp" or "zenpower" or "cpu_thermal" or "x86_pkg_temp" => TemperatureKind.Cpu,
            "amdgpu" or "radeon" or "nouveau" or "i915" or "xe" => TemperatureKind.Gpu,
            "nvme" or "drivetemp" => TemperatureKind.Storage,
            "acpitz" => TemperatureKind.ThermalZone,
            "BAT0" or "BAT1" => TemperatureKind.Battery,
            var d when d.StartsWith("nct", StringComparison.Ordinal) || d.StartsWith("it87", StringComparison.Ordinal) || d.StartsWith("it86", StringComparison.Ordinal)
                || d is "thinkpad" or "dell_smm" or "asus" or "asus_wmi_sensors" or "asusec" or "gigabyte_wmi" or "hp" or "applesmc" => TemperatureKind.Motherboard,
            _ => null, // unknown hardware is never guessed; its fans still count
        };

        private static string StorageName(string dir, string driver)
        {
            var model = SystemMetricsReader.ReadText(Path.Combine(dir, "device", "model"));
            return string.IsNullOrWhiteSpace(model) ? (driver == "nvme" ? "NVMe drive" : "Drive") : model!;
        }

        private static string FanName(string stem) => "Fan " + stem["fan".Length..];
    }

    /// <summary>NVIDIA's management library (libnvidia-ml.so.1), present with the proprietary driver.</summary>
    private sealed partial class Nvml : IDisposable
    {
        private const string Lib = "libnvidia-ml.so.1";
        private readonly IntPtr[] _devices;

        private Nvml(IntPtr[] devices)
        {
            _devices = devices;
        }

        public int Count => _devices.Length;

        public static Nvml? TryOpen()
        {
            try
            {
                if (nvmlInit_v2() != 0)
                {
                    return null;
                }

                if (nvmlDeviceGetCount_v2(out var count) != 0 || count == 0)
                {
                    nvmlShutdown();
                    return null;
                }

                var devices = new List<IntPtr>();
                for (uint i = 0; i < count; i++)
                {
                    if (nvmlDeviceGetHandleByIndex_v2(i, out var device) == 0)
                    {
                        devices.Add(device);
                    }
                }

                return new Nvml([.. devices]);
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
            {
                return null;
            }
        }

        public double? MaxUtilization()
        {
            double? max = null;
            foreach (var device in _devices)
            {
                if (nvmlDeviceGetUtilizationRates(device, out var rates) == 0)
                {
                    max = Math.Max(max ?? 0, Math.Clamp(rates.Gpu, 0, 100));
                }
            }

            return max;
        }

        public IEnumerable<double> Temperatures()
        {
            foreach (var device in _devices)
            {
                if (nvmlDeviceGetTemperature(device, 0, out var celsius) == 0 && celsius is > 1 and < 125)
                {
                    yield return celsius;
                }
            }
        }

        public void Dispose() => nvmlShutdown();

        [StructLayout(LayoutKind.Sequential)]
        private struct Utilization
        {
            public uint Gpu;
            public uint Memory;
        }

        [LibraryImport(Lib)] private static partial int nvmlInit_v2();
        [LibraryImport(Lib)] private static partial int nvmlShutdown();
        [LibraryImport(Lib)] private static partial int nvmlDeviceGetCount_v2(out uint count);
        [LibraryImport(Lib)] private static partial int nvmlDeviceGetHandleByIndex_v2(uint index, out IntPtr device);
        [LibraryImport(Lib)] private static partial int nvmlDeviceGetUtilizationRates(IntPtr device, out Utilization utilization);
        [LibraryImport(Lib)] private static partial int nvmlDeviceGetTemperature(IntPtr device, int sensor, out uint celsius);
    }
}
