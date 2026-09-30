using DeveloperIsland.Core.Diagnostics;
using DeveloperIsland.Core.Modules;

namespace DeveloperIsland.Core.SystemInfo;

/// <summary>One reading of the machine. Battery values are null on desktops.</summary>
public sealed record SystemSample(double CpuPercent, ulong MemoryUsedBytes, ulong MemoryTotalBytes, int? BatteryPercent, bool IsCharging)
{
    public double MemoryPercent => MemoryTotalBytes == 0 ? 0 : 100.0 * MemoryUsedBytes / MemoryTotalBytes;

    public bool HasBattery => BatteryPercent is not null;
}

public enum SystemAlert
{
    None,
    CpuHigh,
    BatteryLow,
}

/// <summary>
/// When the System module may speak up in the compact island: a battery that is low and not charging,
/// or a CPU that stays busy (a single spike never counts). Everything else waits in the panel.
/// </summary>
public static class SystemAlertPolicy
{
    public const int BatteryLowPercent = 20;
    public const double CpuHighPercent = 85;
    public const int CpuHighSamples = 3;

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

/// <summary>
/// Samples CPU, memory and battery. Nothing runs while disabled; every 5 s while enabled (enough to
/// notice sustained load or a low battery); every 2 s while the System panel is on screen.
/// </summary>
public sealed class SystemMonitor : IDisposable
{
    public static readonly TimeSpan BackgroundInterval = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan VisibleInterval = TimeSpan.FromSeconds(2);

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

    /// <summary>The System panel is (or is no longer) on screen.</summary>
    public void SetVisible(bool visible)
    {
        lock (_gate)
        {
            if (visible == _visible)
            {
                return;
            }

            _visible = visible;
            Reschedule();
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
                while (_recent.Count > 30)
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
            var interval = _visible ? VisibleInterval : BackgroundInterval;
            _timer = _time.CreateTimer(_ => Tick(), null, TimeSpan.Zero, interval);
        }
    }
}
