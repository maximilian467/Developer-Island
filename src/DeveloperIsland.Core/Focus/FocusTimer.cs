using DeveloperIsland.Core.Storage;
using DeveloperIsland.Core.Usage;

namespace DeveloperIsland.Core.Focus;

public enum FocusState
{
    Idle,
    Running,
    Paused,
}

/// <summary>Immutable view of the timer for the UI.</summary>
public sealed record FocusSnapshot(FocusState State, TimeSpan Planned, TimeSpan Remaining, DateTimeOffset? StartedAt)
{
    public static readonly FocusSnapshot Idle = new(FocusState.Idle, TimeSpan.Zero, TimeSpan.Zero, null);

    public bool IsActive => State != FocusState.Idle;

    public TimeSpan Elapsed => Planned - Remaining;

    public double Progress => Planned <= TimeSpan.Zero ? 0 : Math.Clamp(Elapsed / Planned, 0, 1);
}

/// <summary>
/// Countdown focus timer. Ticks once per second only while running, so an idle timer costs nothing.
/// Uses <see cref="TimeProvider"/> for testability. Remaining time is derived from the end timestamp,
/// so ticks can be late without drifting.
/// </summary>
public sealed class FocusTimer : IDisposable
{
    /// <summary>Sessions shorter than this are not recorded when stopped early.</summary>
    public static readonly TimeSpan MinimumRecordedFocus = TimeSpan.FromMinutes(1);

    private readonly TimeProvider _time;
    private readonly TimeZoneInfo _zone;
    private readonly object _gate = new();
    private ITimer? _ticker;
    private FocusState _state = FocusState.Idle;
    private TimeSpan _planned;
    private TimeSpan _remainingAtPause;
    private DateTimeOffset _endsAt;
    private DateTimeOffset? _startedAt;

    public FocusTimer(TimeProvider? time = null, TimeZoneInfo? zone = null)
    {
        _time = time ?? TimeProvider.System;
        _zone = zone ?? TimeZoneInfo.Local;
    }

    /// <summary>Raised on start, pause, resume, stop, completion and every second while running.</summary>
    public event Action<FocusSnapshot>? Changed;

    /// <summary>Raised when a session ends, whether it completed or was stopped early.</summary>
    public event Action<FocusSessionRecord>? SessionEnded;

    public FocusSnapshot Snapshot
    {
        get
        {
            lock (_gate)
            {
                return CreateSnapshot();
            }
        }
    }

    public void Start(TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(duration));
        }

        FocusSessionRecord? replaced;
        lock (_gate)
        {
            replaced = _state != FocusState.Idle ? EndSession(completed: false) : null;
            _planned = duration;
            _startedAt = _time.GetUtcNow();
            _endsAt = _startedAt.Value + duration;
            _state = FocusState.Running;
            StartTicker();
        }

        if (replaced is not null)
        {
            SessionEnded?.Invoke(replaced);
        }

        RaiseChanged();
    }

    /// <summary>Continues a session that is already partly done (used by demo mode).</summary>
    public void Restore(TimeSpan planned, TimeSpan remaining)
    {
        lock (_gate)
        {
            StopTicker();
            _planned = planned;
            var now = _time.GetUtcNow();
            _startedAt = now - (planned - remaining);
            _endsAt = now + remaining;
            _state = FocusState.Running;
            StartTicker();
        }

        RaiseChanged();
    }

    public void Pause()
    {
        lock (_gate)
        {
            if (_state != FocusState.Running)
            {
                return;
            }

            _remainingAtPause = Clamp(_endsAt - _time.GetUtcNow());
            _state = FocusState.Paused;
            StopTicker();
        }

        RaiseChanged();
    }

    public void Resume()
    {
        lock (_gate)
        {
            if (_state != FocusState.Paused)
            {
                return;
            }

            _endsAt = _time.GetUtcNow() + _remainingAtPause;
            _state = FocusState.Running;
            StartTicker();
        }

        RaiseChanged();
    }

    public void TogglePause()
    {
        if (Snapshot.State == FocusState.Running)
        {
            Pause();
        }
        else
        {
            Resume();
        }
    }

    /// <summary>Ends the session early. Records it if at least a minute was focused.</summary>
    public void Stop()
    {
        FocusSessionRecord? record;
        lock (_gate)
        {
            if (_state == FocusState.Idle)
            {
                return;
            }

            record = EndSession(completed: false);
        }

        if (record is not null)
        {
            SessionEnded?.Invoke(record);
        }

        RaiseChanged();
    }

    /// <summary>Advances the timer. Called by the internal ticker; public for tests.</summary>
    public void Tick()
    {
        FocusSessionRecord? record = null;
        lock (_gate)
        {
            if (_state != FocusState.Running)
            {
                return;
            }

            if (_time.GetUtcNow() >= _endsAt)
            {
                record = EndSession(completed: true);
            }
        }

        if (record is not null)
        {
            SessionEnded?.Invoke(record);
        }

        RaiseChanged();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            StopTicker();
        }
    }

    private FocusSessionRecord? EndSession(bool completed)
    {
        var now = _time.GetUtcNow();
        var remaining = _state switch
        {
            FocusState.Running => Clamp(_endsAt - now),
            FocusState.Paused => _remainingAtPause,
            _ => TimeSpan.Zero,
        };
        if (completed)
        {
            remaining = TimeSpan.Zero;
        }

        var focused = _planned - remaining;
        var started = _startedAt ?? now;
        _state = FocusState.Idle;
        _startedAt = null;
        StopTicker();

        if (!completed && focused < MinimumRecordedFocus)
        {
            return null;
        }

        return new FocusSessionRecord(
            started,
            now,
            UsageAggregator.LocalDay(started, _zone),
            (int)_planned.TotalSeconds,
            (int)Math.Round(focused.TotalSeconds),
            completed);
    }

    private FocusSnapshot CreateSnapshot() => _state switch
    {
        FocusState.Running => new FocusSnapshot(FocusState.Running, _planned, Clamp(_endsAt - _time.GetUtcNow()), _startedAt),
        FocusState.Paused => new FocusSnapshot(FocusState.Paused, _planned, _remainingAtPause, _startedAt),
        _ => FocusSnapshot.Idle,
    };

    private void StartTicker()
    {
        StopTicker();

        // Align ticks to whole seconds of remaining time so the display changes exactly on the second.
        var remaining = _endsAt - _time.GetUtcNow();
        var firstDue = TimeSpan.FromTicks(remaining.Ticks % TimeSpan.TicksPerSecond);
        if (firstDue <= TimeSpan.Zero)
        {
            firstDue = TimeSpan.FromSeconds(1);
        }

        _ticker = _time.CreateTimer(_ => Tick(), null, firstDue + TimeSpan.FromMilliseconds(5), TimeSpan.FromSeconds(1));
    }

    private void StopTicker()
    {
        _ticker?.Dispose();
        _ticker = null;
    }

    private void RaiseChanged() => Changed?.Invoke(Snapshot);

    private static TimeSpan Clamp(TimeSpan value) => value < TimeSpan.Zero ? TimeSpan.Zero : value;
}
