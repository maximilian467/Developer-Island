namespace DeveloperIsland.Core.Awake;

/// <summary>How an awake session ends: at a set time, or when the user ends it.</summary>
public enum AwakeMode
{
    Timed,
    Indefinite,
}

/// <summary>Why a session ended.</summary>
public enum AwakeEndReason
{
    /// <summary>The timer ran out.</summary>
    Completed,

    /// <summary>The user ended it.</summary>
    Stopped,

    /// <summary>On battery below the floor in Settings.</summary>
    Battery,

    /// <summary>The app quit (Quit always ends the session).</summary>
    Quit,

    /// <summary>The module was turned off in Settings.</summary>
    Disabled,
}

/// <summary>What closing the lid does during the session.</summary>
public enum AwakeLid
{
    /// <summary>Closing the lid keeps the PC running (set by the session, or already so before it).</summary>
    Protected,

    /// <summary>The lid setting could not be read or changed: closing the lid still sleeps the PC.</summary>
    Failed,
}

/// <summary>Immutable view of the awake session for the UI.</summary>
public sealed record AwakeSnapshot(AwakeMode? Mode, TimeSpan Planned, TimeSpan Remaining, TimeSpan Elapsed, DateTimeOffset? StartedAt, AwakeLid Lid)
{
    public static readonly AwakeSnapshot Idle = new(null, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, null, AwakeLid.Protected);

    public bool IsActive => Mode is not null;

    public bool IsIndefinite => Mode == AwakeMode.Indefinite;

    public double Progress => Mode != AwakeMode.Timed || Planned <= TimeSpan.Zero ? 0 : Math.Clamp((Planned - Remaining) / Planned, 0, 1);
}

/// <summary>
/// The lid-close action as it was before the session changed it, in the power scheme it was changed
/// in. Values follow LIDACTION: 0 do nothing, 1 sleep, 2 hibernate, 3 shut down.
/// </summary>
public sealed record LidChange(Guid Scheme, uint OriginalAc, uint OriginalDc);

/// <summary>
/// The recovery journal: written before the lid setting is changed, deleted only after it was put
/// back. It outlives a crash, so the next start (or the scheduled backstop) can undo the change.
/// </summary>
/// <param name="EndsAt">When a timed session ends; null for an indefinite one.</param>
/// <param name="Lid">The lid change to undo; null when the session changed nothing.</param>
public sealed record AwakeJournal(AwakeMode Mode, DateTimeOffset StartedAt, TimeSpan Planned, DateTimeOffset? EndsAt, LidChange? Lid);

/// <summary>Windows power control, behind an interface so the session logic is testable.</summary>
public interface IPowerControl
{
    /// <summary>Holds (true) or releases (false) the power request that keeps the system out of idle sleep.</summary>
    bool SetKeepAwake(bool on);

    /// <summary>The active power scheme, or null when it cannot be read.</summary>
    Guid? ActiveScheme();

    /// <summary>The lid-close action of a scheme on mains (AC) and battery (DC), or null when unreadable.</summary>
    (uint Ac, uint Dc)? ReadLidAction(Guid scheme);

    /// <summary>Writes the lid-close action of a scheme and applies it. False when Windows refused.</summary>
    bool WriteLidAction(Guid scheme, uint ac, uint dc);
}

/// <summary>
/// Undoes a timed session's lid change at its end time if the app is no longer running to do it
/// (a scheduled task). Scheduling replaces an earlier schedule.
/// </summary>
public interface IAwakeBackstop
{
    void Schedule(DateTimeOffset endsAt);

    void Cancel();
}

/// <summary>What the app does with a journal it finds at start.</summary>
public enum AwakeStartupAction
{
    /// <summary>No journal: nothing to do.</summary>
    None,

    /// <summary>The session is still running (indefinite, or its end time is ahead): continue it.</summary>
    Resume,

    /// <summary>The session ended while the app was not running: undo its lid change.</summary>
    Restore,
}

/// <summary>The pure rules of the awake module.</summary>
public static class AwakeRules
{
    /// <summary>LIDACTION "do nothing".</summary>
    public const uint LidDoNothing = 0;

    public static readonly TimeSpan MinimumDuration = TimeSpan.FromMinutes(1);

    /// <summary>A timed session is never longer than this, also after extending it.</summary>
    public static readonly TimeSpan MaximumDuration = TimeSpan.FromDays(7);

    /// <summary>
    /// End the session on battery below the floor. A floor of 0 turns the rule off; on mains power,
    /// or with no battery (a desktop, an unreadable level), it never applies.
    /// </summary>
    public static bool ShouldEndForBattery(int? percent, bool charging, int floor) =>
        floor > 0 && !charging && percent is { } p && p < floor;

    public static AwakeStartupAction OnStartup(AwakeJournal? journal, DateTimeOffset now) => journal switch
    {
        null => AwakeStartupAction.None,
        { Mode: AwakeMode.Indefinite } => AwakeStartupAction.Resume,
        { EndsAt: { } endsAt } when endsAt > now => AwakeStartupAction.Resume,
        _ => AwakeStartupAction.Restore,
    };

    /// <summary>
    /// The values to write back at the end. Each power source gets its original value only while it
    /// still reads "do nothing": a value the user changed during the session is theirs and stays.
    /// Null when nothing needs writing.
    /// </summary>
    public static (uint Ac, uint Dc)? Restoration(LidChange change, (uint Ac, uint Dc) current)
    {
        var ac = current.Ac == LidDoNothing ? change.OriginalAc : current.Ac;
        var dc = current.Dc == LidDoNothing ? change.OriginalDc : current.Dc;
        return (ac, dc) == current ? null : (ac, dc);
    }

    /// <summary>The end of a session started or extended now, within the limits.</summary>
    public static TimeSpan ClampDuration(TimeSpan duration) =>
        duration < MinimumDuration ? MinimumDuration : duration > MaximumDuration ? MaximumDuration : duration;
}
