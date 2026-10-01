using System.Globalization;
using DeveloperIsland.Core.Formatting;
using DeveloperIsland.Core.Modules;
using DeveloperIsland.Core.SystemInfo;

namespace DeveloperIsland.ViewModels;

/// <summary>CPU, memory and battery. Speaks up in the compact island only for a real problem.</summary>
public sealed class SystemViewModel : ModuleViewModel
{
    private SystemSample? _sample;
    private SystemAlert _alert;

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

    /// <summary>The featured system line: "CPU 23% · RAM 61%".</summary>
    public string CompactSummary => _sample is null ? string.Empty : $"CPU {CpuFigure} · RAM {MemoryFigure}";

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
