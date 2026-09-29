namespace DeveloperIsland.Core.Island;

public enum IslandMode
{
    Hidden,
    Compact,

    /// <summary>The "medium" state: a transient live activity or a hover peek.</summary>
    Activity,
    Expanded,
}

public enum ActivitySource
{
    None,
    Hover,
    Event,
}

/// <summary>
/// Decides which state the island is in. Pure logic with injectable time; all callbacks are
/// posted through <paramref name="post"/> so the UI can keep everything on its thread.
/// <list type="bullet">
/// <item>Hovering the compact island for a moment opens a peek (Activity). Leaving closes it.</item>
/// <item>Events (song change, focus done, usage events) open an Activity for a few seconds.</item>
/// <item>While the pointer is over an Activity it never auto-closes.</item>
/// <item>Click expands. Esc or clicking elsewhere collapses.</item>
/// </list>
/// </summary>
public sealed class IslandStateMachine : IDisposable
{
    public static readonly TimeSpan HoverDwell = TimeSpan.FromMilliseconds(280);
    public static readonly TimeSpan HoverLinger = TimeSpan.FromMilliseconds(450);
    public static readonly TimeSpan DefaultEventDuration = TimeSpan.FromSeconds(4.5);
    public static readonly TimeSpan MinimumAfterHover = TimeSpan.FromSeconds(1.2);

    private readonly TimeProvider _time;
    private readonly Action<Action> _post;
    private ITimer? _timer;
    private long _generation;
    private DateTimeOffset _eventEndsAt;

    public IslandStateMachine(TimeProvider? time = null, Action<Action>? post = null)
    {
        _time = time ?? TimeProvider.System;
        _post = post ?? (a => a());
    }

    /// <summary>Old mode, new mode.</summary>
    public event Action<IslandMode, IslandMode>? ModeChanged;

    public IslandMode Mode { get; private set; } = IslandMode.Compact;

    public ActivitySource ActivitySource { get; private set; }

    public bool IsPointerOver { get; private set; }

    public void PointerEntered()
    {
        IsPointerOver = true;
        switch (Mode)
        {
            case IslandMode.Compact:
                Schedule(HoverDwell, () =>
                {
                    if (IsPointerOver && Mode == IslandMode.Compact)
                    {
                        SetMode(IslandMode.Activity, ActivitySource.Hover);
                    }
                });
                break;
            case IslandMode.Activity:
                CancelTimer();
                break;
        }
    }

    public void PointerExited()
    {
        IsPointerOver = false;
        switch (Mode)
        {
            case IslandMode.Compact:
                CancelTimer();
                break;
            case IslandMode.Activity when ActivitySource == ActivitySource.Hover:
                Schedule(HoverLinger, CollapseIfIdle);
                break;
            case IslandMode.Activity:
                var remaining = _eventEndsAt - _time.GetUtcNow();
                Schedule(remaining > MinimumAfterHover ? remaining : MinimumAfterHover, CollapseIfIdle);
                break;
        }
    }

    /// <summary>A click on the capsule.</summary>
    public void Activate()
    {
        if (Mode is IslandMode.Compact or IslandMode.Activity)
        {
            CancelTimer();
            SetMode(IslandMode.Expanded, ActivitySource.None);
        }
    }

    /// <summary>Esc, clicking elsewhere, or an explicit close.</summary>
    public void Dismiss()
    {
        if (Mode is IslandMode.Expanded or IslandMode.Activity)
        {
            CancelTimer();
            SetMode(IslandMode.Compact, ActivitySource.None);
        }
    }

    /// <summary>Opens a transient activity unless the user is looking at the expanded island.</summary>
    public bool ShowEvent(TimeSpan? duration = null)
    {
        if (Mode is IslandMode.Expanded or IslandMode.Hidden)
        {
            return false;
        }

        var length = duration ?? DefaultEventDuration;
        _eventEndsAt = _time.GetUtcNow() + length;
        SetMode(IslandMode.Activity, ActivitySource.Event, force: true);
        if (!IsPointerOver)
        {
            Schedule(length, CollapseIfIdle);
        }
        else
        {
            CancelTimer();
        }

        return true;
    }

    public void Hide()
    {
        CancelTimer();
        SetMode(IslandMode.Hidden, ActivitySource.None);
    }

    public void Show()
    {
        if (Mode == IslandMode.Hidden)
        {
            SetMode(IslandMode.Compact, ActivitySource.None);
        }
    }

    public void Dispose() => CancelTimer();

    private void CollapseIfIdle()
    {
        if (Mode == IslandMode.Activity && !IsPointerOver)
        {
            SetMode(IslandMode.Compact, ActivitySource.None);
        }
    }

    private void SetMode(IslandMode mode, ActivitySource source, bool force = false)
    {
        var old = Mode;
        if (old == mode && ActivitySource == source && !force)
        {
            return;
        }

        Mode = mode;
        ActivitySource = source;
        ModeChanged?.Invoke(old, mode);
    }

    private void Schedule(TimeSpan due, Action action)
    {
        CancelTimer();
        var generation = ++_generation;
        _timer = _time.CreateTimer(
            _ => _post(() =>
            {
                if (generation == _generation)
                {
                    action();
                }
            }),
            null,
            due < TimeSpan.Zero ? TimeSpan.Zero : due,
            Timeout.InfiniteTimeSpan);
    }

    private void CancelTimer()
    {
        _generation++;
        _timer?.Dispose();
        _timer = null;
    }
}
