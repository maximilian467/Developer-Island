using DeveloperIsland.Core.Diagnostics;

namespace DeveloperIsland.Core.Awake;

/// <summary>
/// Keeps the PC awake for a timed or an indefinite session, also with the lid closed:
/// <list type="number">
/// <item>A power request keeps Windows out of idle sleep. Windows drops it when the process ends.</item>
/// <item>The lid-close action of the active power scheme is set to "do nothing". This survives a
/// crash, so the original values go into the journal first and are put back at the end, at the next
/// start, or by the scheduled backstop. A value the user changed meanwhile is left alone.</item>
/// </list>
/// Ticks only while a session runs (every second when timed, every minute when indefinite), and
/// derives the remaining time from the end timestamp, so late ticks never drift.
/// </summary>
public sealed class AwakeController : IDisposable
{
    private readonly IPowerControl _power;
    private readonly IAwakeBackstop _backstop;
    private readonly AwakeJournalStore _store;
    private readonly TimeProvider _time;
    private readonly object _gate = new();
    private ITimer? _ticker;
    private AwakeJournal? _session;
    private AwakeLid _lid = AwakeLid.Protected;

    public AwakeController(IPowerControl power, IAwakeBackstop backstop, AwakeJournalStore store, TimeProvider? time = null)
    {
        _power = power;
        _backstop = backstop;
        _store = store;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Raised on start, extend, end and every tick.</summary>
    public event Action<AwakeSnapshot>? Changed;

    /// <summary>Raised once when a session ends.</summary>
    public event Action<AwakeEndReason>? Ended;

    public AwakeSnapshot Snapshot
    {
        get
        {
            lock (_gate)
            {
                return CreateSnapshot();
            }
        }
    }

    public void StartTimed(TimeSpan duration) => Start(AwakeMode.Timed, AwakeRules.ClampDuration(duration));

    public void StartIndefinite() => Start(AwakeMode.Indefinite, TimeSpan.Zero);

    /// <summary>Adds time to a timed session (from now, if it is somehow already due).</summary>
    public void Extend(TimeSpan extra)
    {
        lock (_gate)
        {
            if (_session is not { Mode: AwakeMode.Timed, EndsAt: { } endsAt } session || extra <= TimeSpan.Zero)
            {
                return;
            }

            var now = _time.GetUtcNow();
            var ceiling = now + AwakeRules.MaximumDuration;
            var next = Min(Max(endsAt, now) + extra, ceiling);
            _session = session with { EndsAt = next, Planned = session.Planned + (next - endsAt) };
            _store.Save(_session);
            _backstop.Schedule(next);
            StartTicker();
        }

        RaiseChanged();
    }

    public void Stop(AwakeEndReason reason = AwakeEndReason.Stopped)
    {
        lock (_gate)
        {
            if (!EndSession(reason))
            {
                return;
            }
        }

        Ended?.Invoke(reason);
        RaiseChanged();
    }

    /// <summary>A battery reading: ends the session on battery below the floor (0 turns this off).</summary>
    public void OnBattery(int? percent, bool charging, int floor)
    {
        if (Snapshot.IsActive && AwakeRules.ShouldEndForBattery(percent, charging, floor))
        {
            Log.Info("awake", "Battery below the floor; ending the session", new { percent, floor });
            Stop(AwakeEndReason.Battery);
        }
    }

    /// <summary>
    /// At start: continues a session that is still running, or undoes the lid change of one that
    /// ended while the app was not running (a crash, a restart, a shutdown).
    /// </summary>
    public AwakeStartupAction Recover()
    {
        var journal = _store.Load();
        var action = AwakeRules.OnStartup(journal, _time.GetUtcNow());
        if (journal is null)
        {
            return action;
        }

        if (action == AwakeStartupAction.Restore)
        {
            Finish(journal, _power, _store, _backstop);
            return action;
        }

        lock (_gate)
        {
            _session = journal;

            // The lid change outlived the restart; check it is still in place.
            var current = _power.ActiveScheme() is { } scheme ? _power.ReadLidAction(scheme) : null;
            _lid = current == (AwakeRules.LidDoNothing, AwakeRules.LidDoNothing) ? AwakeLid.Protected : AwakeLid.Failed;
            if (!_power.SetKeepAwake(true))
            {
                Log.Warn("awake", "Power request refused after restart");
            }

            if (journal.EndsAt is { } endsAt)
            {
                _backstop.Schedule(endsAt);
            }

            StartTicker();
        }

        Log.Info("awake", "Session resumed", new { mode = journal.Mode.ToString(), lid = _lid.ToString() });
        RaiseChanged();
        return action;
    }

    /// <summary>
    /// The scheduled backstop (<c>--awake-restore</c>, with the app not running): undoes the lid change
    /// of a session whose end has passed, and leaves a running session alone.
    /// </summary>
    public static bool RestoreIfEnded(AwakeJournalStore store, IPowerControl power, IAwakeBackstop backstop, DateTimeOffset now)
    {
        var journal = store.Load();
        if (AwakeRules.OnStartup(journal, now) != AwakeStartupAction.Restore || journal is null)
        {
            return false;
        }

        return Finish(journal, power, store, backstop);
    }

    /// <summary>Advances the session. Called by the internal ticker; public for tests.</summary>
    public void Tick()
    {
        bool completed;
        lock (_gate)
        {
            if (_session is null)
            {
                return;
            }

            completed = _session.EndsAt is { } endsAt && _time.GetUtcNow() >= endsAt && EndSession(AwakeEndReason.Completed);
        }

        if (completed)
        {
            Ended?.Invoke(AwakeEndReason.Completed);
        }

        RaiseChanged();
    }

    /// <summary>Stops ticking. Does not end the session: Quit ends it explicitly first.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            StopTicker();
        }
    }

    private void Start(AwakeMode mode, TimeSpan duration)
    {
        lock (_gate)
        {
            var now = _time.GetUtcNow();
            DateTimeOffset? endsAt = mode == AwakeMode.Timed ? now + duration : null;
            if (_session is { } running)
            {
                // Another preset while running: the lid change already made stays.
                _session = running with { Mode = mode, StartedAt = now, Planned = duration, EndsAt = endsAt };
                _store.Save(_session);
            }
            else
            {
                _session = new AwakeJournal(mode, now, duration, endsAt, null);
                _lid = ProtectLid();
                if (!_power.SetKeepAwake(true))
                {
                    Log.Warn("awake", "Power request refused; idle sleep is not held off");
                }
            }

            if (endsAt is { } end)
            {
                _backstop.Schedule(end);
            }
            else
            {
                _backstop.Cancel();
            }

            StartTicker();
            Log.Info("awake", "Session started", new { mode = mode.ToString(), minutes = (int)duration.TotalMinutes, lid = _lid.ToString() });
        }

        RaiseChanged();
    }

    /// <summary>Sets the lid action to "do nothing", journaling the original values first.</summary>
    private AwakeLid ProtectLid()
    {
        var scheme = _power.ActiveScheme();
        var current = scheme is { } s ? _power.ReadLidAction(s) : null;
        if (scheme is null || current is not { } values)
        {
            Log.Warn("awake", "Lid setting unreadable; closing the lid will still sleep");
            _store.Save(_session);
            return AwakeLid.Failed;
        }

        if (values == (AwakeRules.LidDoNothing, AwakeRules.LidDoNothing))
        {
            // Already "do nothing" by the user's own choice: nothing to change or undo.
            _store.Save(_session);
            return AwakeLid.Protected;
        }

        var change = new LidChange(scheme.Value, values.Ac, values.Dc);
        _session = _session! with { Lid = change };
        if (!_store.Save(_session))
        {
            // Without a journal a crash could leave the change behind: do not make it.
            _session = _session with { Lid = null };
            return AwakeLid.Failed;
        }

        if (_power.WriteLidAction(change.Scheme, AwakeRules.LidDoNothing, AwakeRules.LidDoNothing))
        {
            return AwakeLid.Protected;
        }

        Log.Warn("awake", "Lid setting refused; closing the lid will still sleep");
        Undo(_power, change);
        _session = _session with { Lid = null };
        _store.Save(_session);
        return AwakeLid.Failed;
    }

    /// <summary>Ends the running session under the lock. False when none was running.</summary>
    private bool EndSession(AwakeEndReason reason)
    {
        if (_session is not { } journal)
        {
            return false;
        }

        StopTicker();
        _session = null;
        _power.SetKeepAwake(false);
        if (!Finish(journal, _power, _store, _backstop))
        {
            // Keep the journal, now as an ended session, so the next start restores instead of resuming.
            var now = _time.GetUtcNow();
            _store.Save(journal with { Mode = AwakeMode.Timed, EndsAt = now });
            _backstop.Schedule(now + TimeSpan.FromMinutes(1));
        }

        Log.Info("awake", "Session ended", new { reason = reason.ToString() });
        return true;
    }

    /// <summary>Undoes a journal's lid change, then deletes the journal and the backstop.</summary>
    private static bool Finish(AwakeJournal journal, IPowerControl power, AwakeJournalStore store, IAwakeBackstop backstop)
    {
        if (journal.Lid is { } change && !Undo(power, change))
        {
            Log.Error("awake", "Lid setting could not be restored; kept in the journal");
            return false;
        }

        store.Save(null);
        backstop.Cancel();
        return true;
    }

    /// <summary>Puts the original lid values back where they still read "do nothing".</summary>
    private static bool Undo(IPowerControl power, LidChange change)
    {
        if (power.ReadLidAction(change.Scheme) is not { } current)
        {
            return false;
        }

        if (AwakeRules.Restoration(change, current) is not { } target)
        {
            if (current != (AwakeRules.LidDoNothing, AwakeRules.LidDoNothing) && current != (change.OriginalAc, change.OriginalDc))
            {
                Log.Info("awake", "Lid setting was changed during the session; left as it is");
            }

            return true;
        }

        return power.WriteLidAction(change.Scheme, target.Ac, target.Dc);
    }

    private AwakeSnapshot CreateSnapshot()
    {
        if (_session is not { } s)
        {
            return AwakeSnapshot.Idle;
        }

        var now = _time.GetUtcNow();
        var remaining = s.EndsAt is { } endsAt ? Clamp(endsAt - now) : TimeSpan.Zero;
        return new AwakeSnapshot(s.Mode, s.Planned, remaining, Clamp(now - s.StartedAt), s.StartedAt, _lid);
    }

    private void StartTicker()
    {
        StopTicker();
        if (_session is not { } s)
        {
            return;
        }

        var now = _time.GetUtcNow();
        TimeSpan firstDue;
        TimeSpan period;
        if (s.EndsAt is { } endsAt)
        {
            // Whole seconds of remaining time, so the countdown changes exactly on the second.
            period = TimeSpan.FromSeconds(1);
            firstDue = TimeSpan.FromTicks((endsAt - now).Ticks % TimeSpan.TicksPerSecond);
        }
        else
        {
            // Indefinite sessions show minutes: tick on whole minutes since the start.
            period = TimeSpan.FromMinutes(1);
            firstDue = period - TimeSpan.FromTicks((now - s.StartedAt).Ticks % TimeSpan.TicksPerMinute);
        }

        if (firstDue <= TimeSpan.Zero)
        {
            firstDue = period;
        }

        _ticker = _time.CreateTimer(_ => Tick(), null, firstDue + TimeSpan.FromMilliseconds(5), period);
    }

    private void StopTicker()
    {
        _ticker?.Dispose();
        _ticker = null;
    }

    private void RaiseChanged() => Changed?.Invoke(Snapshot);

    private static TimeSpan Clamp(TimeSpan value) => value < TimeSpan.Zero ? TimeSpan.Zero : value;

    private static DateTimeOffset Min(DateTimeOffset a, DateTimeOffset b) => a < b ? a : b;

    private static DateTimeOffset Max(DateTimeOffset a, DateTimeOffset b) => a > b ? a : b;
}
