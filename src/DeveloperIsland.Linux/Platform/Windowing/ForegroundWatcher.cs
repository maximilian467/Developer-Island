using Avalonia.Threading;
using DeveloperIsland.Core.Diagnostics;
using DeveloperIsland.Core.Island;
using DeveloperIsland.Core.Placement;

namespace DeveloperIsland.Platform.Windowing;

/// <summary>
/// Smart Auto-Hide's view of the focused window: its app, whether it is maximized and whether it is on
/// the island's monitor. The island's own windows (the island, Settings) never count: while one of
/// them has focus, the previous verdict stands. Changes are delivered on the UI thread.
/// </summary>
internal sealed class ForegroundWatcher : IDisposable
{
    private readonly IActiveWindowSource _source;
    private readonly Func<MonitorInfo?> _islandMonitor;
    private readonly Action<ForegroundInfo?> _changed;
    private ForegroundInfo? _current;
    private string? _lastApp;

    public ForegroundWatcher(IActiveWindowSource source, Func<MonitorInfo?> islandMonitor, Action<ForegroundInfo?> changed)
    {
        _source = source;
        _islandMonitor = islandMonitor;
        _changed = changed;
        _source.Changed += OnSourceChanged;
        Refresh();
    }

    public ForegroundInfo? Current => _current;

    /// <summary>Another app came to the front, whether or not the auto-hide verdict changes.</summary>
    public event Action? Switched;

    public void Refresh()
    {
        var info = Probe();
        _current = info;
        _changed(info);
    }

    /// <summary>What is in front right now; our own windows (or none) keep the last result.</summary>
    public ForegroundInfo? Probe()
    {
        var window = _source.Read();
        if (window is null || window.Pid == Environment.ProcessId)
        {
            return _current;
        }

        return Describe(window);
    }

    public void Dispose() => _source.Changed -= OnSourceChanged;

    private void OnSourceChanged() => Dispatcher.UIThread.Post(() =>
    {
        try
        {
            var window = _source.Read();
            if (window is null || window.Pid == Environment.ProcessId)
            {
                return;
            }

            if (window.App != _lastApp)
            {
                _lastApp = window.App;
                Switched?.Invoke();
            }

            var info = Describe(window);
            if (info != _current)
            {
                _current = info;
                _changed(info);
            }
        }
        catch (Exception ex)
        {
            Log.Debug("window", "Foreground evaluation failed", new { error = ex.GetType().Name });
        }
    });

    private ForegroundInfo Describe(ActiveWindow window)
    {
        var monitor = _islandMonitor();
        var onIslandMonitor = monitor is null || window.Bounds.Width <= 0
            || monitor.Bounds.Contains(window.Bounds.X + window.Bounds.Width / 2, window.Bounds.Y + window.Bounds.Height / 2);
        return new ForegroundInfo(window.App, window.IsMaximized || window.IsFullscreen, onIslandMonitor);
    }
}

/// <summary>
/// Steps the island aside while a fullscreen app (video, game, presentation) is focused on the
/// island's monitor. Delivered on the UI thread, once per change.
/// </summary>
internal sealed class FullscreenWatcher : IDisposable
{
    private readonly IActiveWindowSource _source;
    private readonly Func<MonitorInfo?> _islandMonitor;
    private readonly Action<bool> _changed;
    private bool? _last;

    public FullscreenWatcher(IActiveWindowSource source, Func<MonitorInfo?> islandMonitor, Action<bool> changed)
    {
        _source = source;
        _islandMonitor = islandMonitor;
        _changed = changed;
        _source.Changed += Evaluate;
    }

    public void Dispose() => _source.Changed -= Evaluate;

    private void Evaluate() => Dispatcher.UIThread.Post(() =>
    {
        var window = _source.Read();
        if (window is not null && window.Pid == Environment.ProcessId)
        {
            return; // the island itself took focus
        }

        var monitor = _islandMonitor();
        var fullscreen = window is { IsFullscreen: true }
            && (monitor is null || monitor.Bounds.Contains(window.Bounds.X + window.Bounds.Width / 2, window.Bounds.Y + window.Bounds.Height / 2));
        if (fullscreen != _last)
        {
            _last = fullscreen;
            _changed(fullscreen);
        }
    });
}
