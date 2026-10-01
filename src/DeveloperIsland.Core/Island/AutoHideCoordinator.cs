namespace DeveloperIsland.Core.Island;

/// <summary>
/// Turns foreground changes into the island's resting mode, deterministically under fast switching:
/// <list type="bullet">
/// <item><b>Latest event wins.</b> Every change gets a new version; a pending decision from an older
/// version is dropped, so "Chrome, VS Code, Chrome" ends retracted, never compact.</item>
/// <item><b>Stepping aside is immediate.</b> An auto-hide app in front retracts (or hides) the island
/// at once, with no debounce.</item>
/// <item><b>Coming back waits briefly</b> (<see cref="ReturnSettle"/>), so a quick pass through
/// another window (Alt+Tab, the task switcher) does not flash the capsule, and is checked against
/// the window that is actually in front before it is applied.</item>
/// </list>
/// Timer callbacks are posted through <c>post</c>, like the state machine's, to stay on one thread.
/// </summary>
public sealed class AutoHideCoordinator : IDisposable
{
    /// <summary>How long a return to the capsule waits for a newer foreground change.</summary>
    public static readonly TimeSpan ReturnSettle = TimeSpan.FromMilliseconds(80);

    private readonly TimeProvider _time;
    private readonly Action<Action> _post;
    private readonly Func<ForegroundInfo?> _actualForeground;
    private readonly Func<ForegroundInfo?, RestMode> _decide;
    private readonly Action<RestMode> _apply;
    private ITimer? _timer;

    /// <param name="actualForeground">Reads what is in front right now (verification before a return).</param>
    /// <param name="decide">The auto-hide rule for a foreground app (settings, anchor, privacy).</param>
    /// <param name="apply">Sets the resting mode (the state machine applies its priorities).</param>
    public AutoHideCoordinator(TimeProvider time, Action<Action> post, Func<ForegroundInfo?> actualForeground, Func<ForegroundInfo?, RestMode> decide, Action<RestMode> apply)
    {
        _time = time;
        _post = post;
        _actualForeground = actualForeground;
        _decide = decide;
        _apply = apply;
    }

    /// <summary>Increases with every foreground change; only the newest may apply a decision.</summary>
    public long Version { get; private set; }

    /// <summary>A return to the capsule is waiting for <see cref="ReturnSettle"/>.</summary>
    public bool IsReturnPending => _timer is not null;

    /// <summary>The foreground app changed (from the event hook or the reconciliation check).</summary>
    public void ForegroundChanged(ForegroundInfo? foreground)
    {
        var version = ++Version;
        CancelTimer();
        var rest = _decide(foreground);
        if (rest != RestMode.Compact)
        {
            _apply(rest);
            return;
        }

        _timer = _time.CreateTimer(_ => _post(() =>
        {
            if (version != Version)
            {
                return; // overtaken by a newer foreground change
            }

            CancelTimer();
            _apply(_decide(_actualForeground()));
        }), null, ReturnSettle, Timeout.InfiniteTimeSpan);
    }

    /// <summary>The rule itself changed (settings, placement, camera or microphone): decide again now.</summary>
    public void Reevaluate()
    {
        Version++;
        CancelTimer();
        _apply(_decide(_actualForeground()));
    }

    /// <summary>
    /// The cheap safety net: if the window in front no longer matches what was last applied (an
    /// event Windows never delivered), treat it as a change. Does nothing while a return is pending.
    /// </summary>
    public void Reconcile(RestMode applied)
    {
        if (_timer is null && _decide(_actualForeground()) != applied)
        {
            ForegroundChanged(_actualForeground());
        }
    }

    public void Dispose() => CancelTimer();

    private void CancelTimer()
    {
        _timer?.Dispose();
        _timer = null;
    }
}
