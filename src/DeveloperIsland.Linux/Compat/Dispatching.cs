// The WinUI dispatcher surface the shared code uses (TryEnqueue, CreateTimer), on Avalonia's
// UI-thread dispatcher. Kept in the WinUI namespace on purpose: the shared view models and module
// host compile unchanged on both platforms.
using Avalonia.Threading;

namespace Microsoft.UI.Dispatching;

public enum DispatcherQueuePriority
{
    Low = -10,
    Normal = 0,
    High = 10,
}

public delegate void DispatcherQueueTimerTick(DispatcherQueueTimer sender, object args);

/// <summary>Posts work to the UI thread.</summary>
public sealed class DispatcherQueue
{
    private static readonly DispatcherQueue Ui = new();

    private DispatcherQueue()
    {
    }

    /// <summary>The UI thread's queue (Avalonia has one UI thread).</summary>
    public static DispatcherQueue GetForCurrentThread() => Ui;

    public bool HasThreadAccess => Dispatcher.UIThread.CheckAccess();

    public bool TryEnqueue(Action callback) => TryEnqueue(DispatcherQueuePriority.Normal, callback);

    public bool TryEnqueue(DispatcherQueuePriority priority, Action callback)
    {
        var p = priority switch
        {
            DispatcherQueuePriority.Low => DispatcherPriority.Background,
            DispatcherQueuePriority.High => DispatcherPriority.Send,
            _ => DispatcherPriority.Normal,
        };
        Dispatcher.UIThread.Post(callback, p);
        return true;
    }

    public DispatcherQueueTimer CreateTimer() => new();
}

/// <summary>A UI-thread timer, repeating by default like WinUI's.</summary>
public sealed class DispatcherQueueTimer
{
    private readonly DispatcherTimer _timer = new(DispatcherPriority.Normal);

    internal DispatcherQueueTimer()
    {
        _timer.Tick += (_, _) =>
        {
            if (!IsRepeating)
            {
                _timer.Stop();
            }

            Tick?.Invoke(this, EventArgs.Empty);
        };
    }

    public event DispatcherQueueTimerTick? Tick;

    public TimeSpan Interval
    {
        get => _timer.Interval;
        set => _timer.Interval = value < TimeSpan.FromMilliseconds(1) ? TimeSpan.FromMilliseconds(1) : value;
    }

    public bool IsRepeating { get; set; } = true;

    public bool IsRunning => _timer.IsEnabled;

    public void Start()
    {
        _timer.Stop();
        _timer.Start();
    }

    public void Stop() => _timer.Stop();
}
