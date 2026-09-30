namespace DeveloperIsland.Core.Island;

public enum IslandMode
{
    Hidden,

    /// <summary>Retracted into the top screen edge as a small notch (Smart Browser Notch).</summary>
    Retracted,
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

/// <summary>Where the island rests when nothing is happening.</summary>
public enum RestMode
{
    Compact,
    Retracted,
    Hidden,
}

/// <summary>
/// Decides which state the island is in. Pure logic with injectable time; all callbacks are
/// posted through <paramref name="post"/> so the UI can keep everything on its thread.
/// <list type="bullet">
/// <item><b>Rest</b>: Compact normally; Retracted or Hidden while Smart Auto-Hide applies (<see cref="SetRest"/>).
/// Every collapse returns to the current rest mode.</item>
/// <item><b>Hover intent</b>: the pointer must stay on the capsule for <see cref="HoverDwell"/>
/// (<see cref="NotchDwell"/> on the notch) before a peek opens; leaving cancels immediately.</item>
/// <item><b>Events</b> (song change, focus, usage) open an Activity for a few seconds, never while
/// expanded, retracted or hidden. A hovered Activity does not auto-close.</item>
/// <item><b>Click</b> expands immediately; Esc or clicking elsewhere collapses.</item>
/// <item><b>Forced hide</b> (tray, fullscreen app) overrides everything until <see cref="Show"/>.</item>
/// </list>
/// Pointer enter/exit must describe the <i>visible</i> capsule (see <see cref="HoverTracker"/>).
/// </summary>
public sealed class IslandStateMachine : IDisposable
{
    public static readonly TimeSpan HoverDwell = TimeSpan.FromMilliseconds(280);
    public static readonly TimeSpan NotchDwell = TimeSpan.FromMilliseconds(400);
    public static readonly TimeSpan HoverLinger = TimeSpan.FromMilliseconds(450);
    public static readonly TimeSpan DefaultEventDuration = TimeSpan.FromSeconds(4.5);
    public static readonly TimeSpan MinimumAfterHover = TimeSpan.FromSeconds(1.2);

    private readonly TimeProvider _time;
    private readonly Action<Action> _post;
    private ITimer? _timer;
    private long _generation;
    private DateTimeOffset _eventEndsAt;
    private RestMode _rest = RestMode.Compact;
    private bool _forcedHidden;

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

    public RestMode Rest => _rest;

    public bool IsForcedHidden => _forcedHidden;

    /// <summary>The mode the island returns to when idle.</summary>
    public IslandMode RestingMode => _forcedHidden
        ? IslandMode.Hidden
        : _rest switch
        {
            RestMode.Retracted => IslandMode.Retracted,
            RestMode.Hidden => IslandMode.Hidden,
            _ => IslandMode.Compact,
        };

    public bool IsResting => Mode == RestingMode;

    /// <summary>Changes the rest mode. A resting island moves at once; an open one keeps what the user is looking at.</summary>
    public void SetRest(RestMode rest)
    {
        if (_rest == rest)
        {
            return;
        }

        var wasResting = Mode is IslandMode.Compact or IslandMode.Retracted or IslandMode.Hidden && !_forcedHidden;
        _rest = rest;
        if (wasResting)
        {
            CancelTimer();
            SetMode(RestingMode, ActivitySource.None);
        }
    }

    public void PointerEntered()
    {
        IsPointerOver = true;
        switch (Mode)
        {
            case IslandMode.Compact:
            case IslandMode.Retracted:
                var from = Mode;
                Schedule(from == IslandMode.Retracted ? NotchDwell : HoverDwell, () =>
                {
                    if (IsPointerOver && Mode == from)
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
            case IslandMode.Retracted:
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

    /// <summary>A click on the capsule or notch.</summary>
    public void Activate()
    {
        if (Mode is IslandMode.Compact or IslandMode.Retracted or IslandMode.Activity)
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
            SetMode(RestingMode, ActivitySource.None);
        }
    }

    /// <summary>Keyboard shortcut: open from any resting or hidden state, close when expanded.</summary>
    public void Toggle()
    {
        if (Mode == IslandMode.Expanded)
        {
            Dismiss();
            return;
        }

        _forcedHidden = false;
        CancelTimer();
        SetMode(IslandMode.Expanded, ActivitySource.None);
    }

    /// <summary>Opens a transient activity unless the island is expanded, retracted or hidden.</summary>
    public bool ShowEvent(TimeSpan? duration = null)
    {
        if (Mode is IslandMode.Expanded or IslandMode.Hidden or IslandMode.Retracted)
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

    /// <summary>Forced hide (tray, fullscreen app).</summary>
    public void Hide()
    {
        CancelTimer();
        _forcedHidden = true;
        SetMode(IslandMode.Hidden, ActivitySource.None);
    }

    public void Show()
    {
        if (!_forcedHidden && Mode != IslandMode.Hidden)
        {
            return;
        }

        _forcedHidden = false;
        if (Mode == IslandMode.Hidden)
        {
            SetMode(RestingMode, ActivitySource.None);
        }
    }

    public void Dispose() => CancelTimer();

    private void CollapseIfIdle()
    {
        if (Mode == IslandMode.Activity && !IsPointerOver)
        {
            SetMode(RestingMode, ActivitySource.None);
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
