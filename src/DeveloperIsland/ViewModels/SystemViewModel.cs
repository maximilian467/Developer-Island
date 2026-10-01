using System.Globalization;
using DeveloperIsland.Core.Formatting;
using DeveloperIsland.Core.Modules;
using DeveloperIsland.Core.SystemInfo;

namespace DeveloperIsland.ViewModels;

/// <summary>A quiet detail line under the System tiles (a sensor or a fan).</summary>
public sealed record SensorRow(string Label, string Value, bool IsWarning, SystemLevel Level = SystemLevel.Good);

/// <summary>
/// CPU, memory, GPU, temperatures, fans and battery: only what this device exposes, each with its
/// last 60 seconds. Speaks up in the compact island only for a real problem (or when featured).
/// </summary>
public sealed class SystemViewModel : ModuleViewModel
{
    /// <summary>CPU and GPU temperatures from here on are shown with a subtle warning.</summary>
    public const double HotCelsius = 90;

    private SystemSample? _sample;
    private SystemAlert _alert;
    private IReadOnlyList<SystemSample> _history = [];
    private HardwareCapabilities _capabilities = HardwareCapabilities.None;

    public SystemViewModel()
        : base(ModuleId.System)
    {
    }

    public string CpuFigure => $"{Math.Round(_sample?.CpuPercent ?? 0).ToString(CultureInfo.CurrentCulture)}%";

    public double CpuFraction => Math.Clamp((_sample?.CpuPercent ?? 0) / 100, 0, 1);

    public string MemoryFigure => $"{Math.Round(_sample?.MemoryPercent ?? 0).ToString(CultureInfo.CurrentCulture)}%";

    public string MemoryDetail => _sample is null ? string.Empty
        : $"{DisplayFormat.Gigabytes(_sample.MemoryUsedBytes)} of {DisplayFormat.Gigabytes(_sample.MemoryTotalBytes)}{DisplayFormat.Nbsp}GB";

    public double MemoryFraction => Math.Clamp((_sample?.MemoryPercent ?? 0) / 100, 0, 1);

    public bool HasBattery => _sample?.HasBattery == true;

    public string BatteryFigure => _sample?.BatteryPercent is { } b ? $"{b.ToString(CultureInfo.CurrentCulture)}%" : string.Empty;

    public string BatteryDetail => _sample is null ? string.Empty : _sample.IsCharging ? "charging" : "on battery";

    public double BatteryFraction => Math.Clamp((_sample?.BatteryPercent ?? 0) / 100.0, 0, 1);

    public bool CpuHigh => _alert == SystemAlert.CpuHigh;

    public bool BatteryLow => _alert == SystemAlert.BatteryLow;

    public bool HasCompact => IsReady && _alert != SystemAlert.None;

    public string CompactText => _alert switch
    {
        SystemAlert.BatteryLow => $"Battery {BatteryFigure}",
        SystemAlert.CpuHigh => $"CPU {CpuFigure}",
        _ => string.Empty,
    };

    public string CompactGlyph => _alert == SystemAlert.BatteryLow ? "" : "";

    /// <summary>
    /// The featured system line, from values this device has: "CPU 38% · 54°" with a CPU
    /// temperature, else "CPU 31% · GPU 22%" with a GPU, else "CPU 23% · RAM 61%".
    /// </summary>
    public string CompactSummary => _sample is null ? string.Empty
        : _sample.Hottest(TemperatureKind.Cpu) is { } cpuTemp ? $"CPU {CpuFigure} · {Math.Round(cpuTemp):0}°"
        : _sample.GpuPercent is not null ? $"CPU {CpuFigure} · GPU {GpuFigure}"
        : $"CPU {CpuFigure} · RAM {MemoryFigure}";

    /// <summary>Hover peek line: what is not in the title (GPU, temperature, memory size), only if it exists.</summary>
    public string PeekLine
    {
        get
        {
            if (_sample is null)
            {
                return StateTitle;
            }

            var parts = new List<string>();
            if (_sample.GpuPercent is not null && !CompactSummary.Contains("GPU", StringComparison.Ordinal)) parts.Add($"GPU {GpuFigure}");
            if (_sample.Hottest(TemperatureKind.Cpu) is null && _sample.Hottest(TemperatureKind.ThermalZone) is { } zone) parts.Add($"{Math.Round(zone):0}{DisplayFormat.Nbsp}°C");
            parts.Add(MemoryDetail);
            if (HasBattery) parts.Add($"Battery {BatteryFigure}");
            return string.Join(" · ", parts);
        }
    }

    // History and optional hardware -------------------------------------------------------------------

    public HardwareCapabilities Capabilities => _capabilities;

    public IReadOnlyList<double> CpuHistory => _history.Select(h => h.CpuPercent).ToList();

    public IReadOnlyList<double> MemoryHistory => _history.Select(h => h.MemoryPercent).ToList();

    public IReadOnlyList<double> GpuHistory => _history.Select(h => h.GpuPercent ?? 0).ToList();

    public bool HasGpu => _capabilities.HasGpu;

    public string GpuFigure => $"{Math.Round(_sample?.GpuPercent ?? 0).ToString(CultureInfo.CurrentCulture)}%";

    public double GpuFraction => Math.Clamp((_sample?.GpuPercent ?? 0) / 100, 0, 1);

    private double? CpuTemperature => _sample?.Hottest(TemperatureKind.Cpu);

    private double? GpuTemperature => _sample?.Temperatures.Where(t => t.Kind == TemperatureKind.Gpu && t.Name == "GPU").Select(t => (double?)t.Celsius).FirstOrDefault()
        ?? _sample?.Hottest(TemperatureKind.Gpu);

    /// <summary>Under the CPU figure: its temperature when the device reports one, else what the figure covers.</summary>
    public string CpuCaption => CpuTemperature is { } t
        ? CpuHigh ? $"{Celsius(t)} · busy for a while" : Celsius(t)
        : CpuHigh ? "busy for a while" : "all cores";

    public string GpuCaption => GpuTemperature is { } t ? Celsius(t) : "graphics load";

    /// <summary>A CPU or GPU at or above 90 °C: the caption turns red; nothing else changes color.</summary>
    public bool CpuHot => CpuTemperature >= HotCelsius;

    public bool GpuHot => GpuTemperature >= HotCelsius;

    /// <summary>Load levels over the last few seconds: they color the graph line and the bar.</summary>
    public SystemLevel CpuLevel => SystemLevels.RecentLoad(CpuHistory, SystemLevels.Load);

    public SystemLevel MemoryLevel => SystemLevels.RecentLoad(MemoryHistory, SystemLevels.Memory);

    public SystemLevel GpuLevel => SystemLevels.RecentLoad(GpuHistory, SystemLevels.Load);

    /// <summary>Temperature levels: they color only the temperature text.</summary>
    public SystemLevel CpuTemperatureLevel => CpuTemperature is { } t ? SystemLevels.Temperature(TemperatureKind.Cpu, t) : SystemLevel.Good;

    public SystemLevel GpuTemperatureLevel => GpuTemperature is { } t ? SystemLevels.Temperature(TemperatureKind.Gpu, t) : SystemLevel.Good;

    private static SensorRow TemperatureRow(TemperatureReading t)
    {
        var level = SystemLevels.Temperature(t.Kind, t.Celsius);
        return new SensorRow(t.Name, Celsius(t.Celsius), level != SystemLevel.Good, level);
    }

    /// <summary>Storage, board, battery and thermal-zone temperatures, and fans: only those that exist.</summary>
    public IReadOnlyList<SensorRow> SensorRows
    {
        get
        {
            if (_sample is null)
            {
                return [];
            }

            var rows = new List<SensorRow>();
            foreach (var t in _sample.Temperatures.Where(t => t.Kind is TemperatureKind.Storage or TemperatureKind.Motherboard or TemperatureKind.Battery or TemperatureKind.ThermalZone))
            {
                rows.Add(TemperatureRow(t));
            }

            if (GpuTemperature is null && _sample.Temperatures.FirstOrDefault(t => t.Kind == TemperatureKind.Gpu) is { } gpuOnly)
            {
                rows.Add(TemperatureRow(gpuOnly));
            }

            foreach (var fan in _sample.Fans)
            {
                // Monitoring only: manual control is offered only on hardware with a crash-safe fallback.
                rows.Add(new SensorRow(fan.Name, $"{fan.Rpm.ToString("N0", CultureInfo.CurrentCulture)}{DisplayFormat.Nbsp}RPM · Auto", false));
            }

            return rows;
        }
    }

    public bool HasSensorRows => SensorRows.Count > 0;

    private static string Celsius(double value) => $"{Math.Round(value):0}{DisplayFormat.Nbsp}°C";

    /// <summary>A new sample with its history and what the device exposes.</summary>
    public void Update(ModuleStatus status, SystemSample? sample, SystemAlert alert, IReadOnlyList<SystemSample> history, HardwareCapabilities capabilities)
    {
        _history = history;
        _capabilities = capabilities;
        Update(status, sample, alert);
    }

    public string AccessibleSummary => _sample is null ? StateTitle
        : $"CPU {CpuFigure}, memory {MemoryFigure}{(HasBattery ? $", battery {BatteryFigure} {BatteryDetail}" : string.Empty)}";

    public void Update(ModuleStatus status, SystemSample? sample, SystemAlert alert)
    {
        _sample = sample;
        _alert = alert;
        SetStatus(status);
        OnAllPropertiesChanged();
    }
}
