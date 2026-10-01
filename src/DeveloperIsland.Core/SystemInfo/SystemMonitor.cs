using DeveloperIsland.Core.Diagnostics;
using DeveloperIsland.Core.Modules;

namespace DeveloperIsland.Core.SystemInfo;

/// <summary>Which component a temperature belongs to. Unknown hardware is never guessed.</summary>
public enum TemperatureKind
{
    Cpu,
    Gpu,
    Storage,
    Motherboard,
    Battery,

    /// <summary>An ACPI thermal zone: a real sensor, but Windows does not say what it measures.</summary>
    ThermalZone,
}

public sealed record TemperatureReading(TemperatureKind Kind, string Name, double Celsius);

public sealed record FanReading(string Name, double Rpm);

/// <summary>
/// One reading of the machine. Battery values are null on desktops; GPU, temperatures and fans are
/// absent when the device or its driver does not expose them (they are hidden, never shown as
/// "unavailable").
/// </summary>
public sealed record SystemSample(double CpuPercent, ulong MemoryUsedBytes, ulong MemoryTotalBytes, int? BatteryPercent, bool IsCharging)
{
    public double MemoryPercent => MemoryTotalBytes == 0 ? 0 : 100.0 * MemoryUsedBytes / MemoryTotalBytes;

    public bool HasBattery => BatteryPercent is not null;

    /// <summary>3D engine or GPU core load, 0-100; null without a readable GPU.</summary>
    public double? GpuPercent { get; init; }

    public IReadOnlyList<TemperatureReading> Temperatures { get; init; } = [];

    public IReadOnlyList<FanReading> Fans { get; init; } = [];

    public bool HasGpu => GpuPercent is not null;

    /// <summary>The hottest reading of a kind, or null when the device has none.</summary>
    public double? Hottest(TemperatureKind kind) =>
        Temperatures.Where(t => t.Kind == kind).Select(t => (double?)t.Celsius).Max();
}

public enum SystemAlert
{
    None,
    CpuHigh,
    BatteryLow,
}

/// <summary>
/// When the System module may speak up in the compact island: a battery that is low and not charging,
/// or a CPU that stays busy for 15 seconds (a spike never counts). Everything else waits in the panel.
/// </summary>
public static class SystemAlertPolicy
{
    public const int BatteryLowPercent = 20;
    public const double CpuHighPercent = 85;

    /// <summary>15 seconds of samples at <see cref="SystemMonitor.SampleInterval"/>.</summary>
    public const int CpuHighSamples = 15;

    public static SystemAlert Evaluate(IReadOnlyList<SystemSample> recent)
    {
        if (recent.Count == 0)
        {
            return SystemAlert.None;
        }

        var last = recent[^1];
        if (last.BatteryPercent is { } battery && battery <= BatteryLowPercent && !last.IsCharging)
        {
            return SystemAlert.BatteryLow;
        }

        return recent.Count >= CpuHighSamples && recent.TakeLast(CpuHighSamples).All(s => s.CpuPercent >= CpuHighPercent)
            ? SystemAlert.CpuHigh
            : SystemAlert.None;
    }
}

/// <summary>What this device exposes, derived from the readings themselves.</summary>
public sealed record HardwareCapabilities(bool HasGpu, bool HasBattery, IReadOnlyList<TemperatureKind> Temperatures, IReadOnlyList<string> Fans)
{
    public static readonly HardwareCapabilities None = new(false, false, [], []);

    public bool HasFans => Fans.Count > 0;

    public bool Has(TemperatureKind kind) => Temperatures.Contains(kind);

    /// <summary>A component counts once any recent reading had it (a sensor that blinks out briefly does not reshuffle the layout).</summary>
    public static HardwareCapabilities From(IEnumerable<SystemSample> samples)
    {
        var list = samples.ToList();
        return new HardwareCapabilities(
            list.Any(s => s.HasGpu),
            list.Any(s => s.HasBattery),
            list.SelectMany(s => s.Temperatures).Select(t => t.Kind).Distinct().Order().ToList(),
            list.SelectMany(s => s.Fans).Select(f => f.Name).Distinct().ToList());
    }
}

/// <summary>
/// Samples CPU, memory, GPU, temperatures, fans and battery once a second while the System module is
/// enabled, and keeps the last 60 samples for the panel's graphs. Nothing runs while disabled.
/// Sampling never touches the UI: the host decides when the UI needs to hear about it.
/// </summary>
public sealed class SystemMonitor : IDisposable
{
    public static readonly TimeSpan SampleInterval = TimeSpan.FromSeconds(1);
    public const int HistoryLength = 60;

    // Kept for callers that distinguish background and visible sampling; both are 1 Hz now.
    public static readonly TimeSpan BackgroundInterval = SampleInterval;
    public static readonly TimeSpan VisibleInterval = SampleInterval;

    private readonly Func<SystemSample?> _sample;
    private readonly TimeProvider _time;
    private readonly object _gate = new();
    private readonly Queue<SystemSample> _recent = new();
    private ITimer? _timer;
    private bool _enabled;
    private bool _visible;
    private bool _failed;

    /// <param name="sample">Reads the machine; returns null when a reading is not possible.</param>
    public SystemMonitor(Func<SystemSample?> sample, TimeProvider? time = null)
    {
        _sample = sample;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Raised on a background thread after each sample.</summary>
    public event Action? Changed;

    public ModuleStatus Status
    {
        get
        {
            lock (_gate)
            {
                return !_enabled ? ModuleStatus.Disabled
                    : _failed ? new ModuleStatus(ModuleState.Error, "System readings are not available on this device.")
                    : _recent.Count == 0 ? ModuleStatus.Loading
                    : ModuleStatus.Ready;
            }
        }
    }

    public SystemSample? Latest
    {
        get
        {
            lock (_gate)
            {
                return _recent.Count > 0 ? _recent.Last() : null;
            }
        }
    }

    /// <summary>Up to the last 60 samples, oldest first (one per second).</summary>
    public IReadOnlyList<SystemSample> History
    {
        get
        {
            lock (_gate)
            {
                return _recent.ToList();
            }
        }
    }

    public HardwareCapabilities Capabilities
    {
        get
        {
            lock (_gate)
            {
                return HardwareCapabilities.From(_recent);
            }
        }
    }

    public SystemAlert Alert
    {
        get
        {
            lock (_gate)
            {
                return _enabled ? SystemAlertPolicy.Evaluate(_recent.ToList()) : SystemAlert.None;
            }
        }
    }

    public bool IsVisible
    {
        get
        {
            lock (_gate)
            {
                return _visible;
            }
        }
    }

    public void Configure(bool enabled)
    {
        lock (_gate)
        {
            if (enabled == _enabled)
            {
                return;
            }

            _enabled = enabled;
            if (!enabled)
            {
                _recent.Clear();
                _failed = false;
            }

            Reschedule();
        }

        Changed?.Invoke();
    }

    /// <summary>The System panel is (or is no longer) on screen. Sampling stays at 1 Hz either way.</summary>
    public void SetVisible(bool visible)
    {
        lock (_gate)
        {
            _visible = visible;
        }
    }

    /// <summary>Takes one sample now. Public for tests.</summary>
    public void Tick()
    {
        SystemSample? sample;
        try
        {
            sample = _sample();
        }
        catch (Exception ex)
        {
            Log.Debug("system", "Sample failed", new { error = ex.GetType().Name });
            sample = null;
        }

        lock (_gate)
        {
            if (!_enabled)
            {
                return;
            }

            _failed = sample is null && _recent.Count == 0;
            if (sample is not null)
            {
                _recent.Enqueue(sample);
                while (_recent.Count > HistoryLength)
                {
                    _recent.Dequeue();
                }
            }
        }

        Changed?.Invoke();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _enabled = false;
            _timer?.Dispose();
            _timer = null;
        }
    }

    private void Reschedule()
    {
        _timer?.Dispose();
        _timer = null;
        if (_enabled)
        {
            _timer = _time.CreateTimer(_ => Tick(), null, TimeSpan.Zero, SampleInterval);
        }
    }
}

/// <summary>
/// Whether manual fan control may be offered, and the fail-safe state when it is. Manual control is
/// only allowed when the hardware interface is documented or established, the app runs with the
/// rights it needs, and the fans are guaranteed to return to firmware control when the app crashes
/// or is killed (a watchdog in the controller or the OEM interface). Without that guarantee the
/// island offers monitoring only.
/// </summary>
public sealed record FanControlCapability(bool HasControllableFans, bool RevertsToFirmwareOnCrash, bool HasRights, double MinimumPercent)
{
    public static readonly FanControlCapability None = new(false, false, false, 0);

    public bool IsManualAllowed => HasControllableFans && RevertsToFirmwareOnCrash && HasRights;
}

public enum FanMode
{
    Auto,
    Manual,
}

/// <summary>
/// The fan control state machine: starts in Auto, returns to Auto on exit, on any error and on a
/// lost sensor, never goes below the firmware's minimum, never switches a fan off.
/// </summary>
public sealed class FanSafetyState
{
    private readonly FanControlCapability _capability;

    public FanSafetyState(FanControlCapability capability)
    {
        _capability = capability;
    }

    public FanMode Mode { get; private set; } = FanMode.Auto;

    /// <summary>The requested duty in manual mode, clamped to the safe range.</summary>
    public double ManualPercent { get; private set; }

    /// <summary>Asks for manual control at a duty. Refused (stays Auto) unless manual control is allowed.</summary>
    public bool RequestManual(double percent)
    {
        if (!_capability.IsManualAllowed || !double.IsFinite(percent))
        {
            Mode = FanMode.Auto;
            return false;
        }

        // Never off, never below the firmware minimum.
        var floor = Math.Max(_capability.MinimumPercent, 1);
        ManualPercent = Math.Clamp(percent, floor, 100);
        Mode = FanMode.Manual;
        return true;
    }

    public void RequestAuto() => Mode = FanMode.Auto;

    /// <summary>Any control or sensor failure, or the app exiting: back to firmware control.</summary>
    public void OnFailure() => Mode = FanMode.Auto;

    public void OnSensorLost() => Mode = FanMode.Auto;

    public void OnExit() => Mode = FanMode.Auto;
}
