namespace DeveloperIsland.Core.Island;

public enum IslandMode
{
    Hidden,

    /// <summary>Retracted into the top screen edge as a small notch (Smart Auto-Hide).</summary>
    Retracted,
    Compact,

    /// <summary>The "medium" state: a transient live activity or a hover peek.</summary>
    Activity,
    Expanded,

    /// <summary>Files are dragged over the island (Home Cloud): drop zone, upload progress, then the result.</summary>
    Drop,
}

/// <summary>What the drop zone shows while the island is in <see cref="IslandMode.Drop"/>.</summary>
public enum DropPhase
{
    None,
    Hover,
    Uploading,
    Succeeded,
    Failed,
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

/// <summary>The inputs the island reacts to (see the transition table on <see cref="IslandStateMachine"/>).</summary>
public enum IslandInput
{
    PointerEntered,
    PointerExited,
    HoverDelayElapsed,
    Clicked,
    ClickedOutside,
    ActiveAppChanged,
    ModuleEvent,
    DragStarted,
    DragEnded,
    FileDragEntered,
    FileDragLeft,

    /// <summary>The files were dropped: the upload starts.</summary>
    FileDropCompleted,
    DropSucceeded,
    DropFailed,
}

/// <summary>
/// Decides which state the island is in. Pure logic with injectable time; every timer callback is
/// posted through <c>post</c> so the UI keeps everything on one thread, and carries a generation
/// number so a timer that was overtaken by a newer input can never act.
/// <para>States: Hidden, Retracted (notch), Compact, Activity (medium), Expanded, Drop (Home Cloud);
/// Dragging is an orthogonal flag that freezes the current state while the user moves the island.</para>
/// <code>
/// input               Compact/Retracted        Activity (hover)        Activity (event)        Expanded
/// PointerEntered      start hover delay        keep open               keep open (pause end)   -
/// HoverDelayElapsed   open hover activity *    -                       -                       -
/// PointerExited       cancel delay             close after linger      close at event end      -
/// Clicked             expand                   expand                  expand                  -
/// ClickedOutside      -                        -                       -                       rest
/// ModuleEvent         open event activity      open event activity     restart event           ignored
/// ActiveAppChanged    move to new rest         (kept, rests after)     (kept, rests after)     (kept)
/// DragStarted/Ended   freeze timers / resume   freeze / resume         freeze / resume         -
/// FileDragEntered     drop (hover) **          drop (hover)            drop (hover)            drop (hover)
/// * only if the pointer is still over the capsule (re-checked through <see cref="PointerProbe"/>).
/// ** also from Hidden, including a forced hide; every drop input is ignored while Home Cloud is off.
///
/// input               Drop (hover)             Drop (uploading)        Drop (succeeded/failed)
/// FileDragEntered     -                        -                       back to hover
/// FileDragLeft        rest ***                 -                       -
/// FileDropCompleted   uploading                -                       uploading
/// DropSucceeded       -                        check mark, rest later  -
/// DropFailed          -                        error, rest later       -
/// ModuleEvent, Clicked, ClickedOutside, hover and island drags do nothing in Drop.
/// *** also when <see cref="DropProbe"/> reports the drag gone. Rest is the mode the island rests in
/// now: hidden again after Hidden, forced hidden again after a forced hide.
/// </code>
/// Timers never trust a remembered pointer flag alone: when one fires, <see cref="PointerProbe"/> is
/// asked where the pointer really is, so a missed pointer-exit can no longer keep the island open.
/// </summary>
public sealed class IslandStateMachine : IDisposable
{
    public static readonly TimeSpan HoverDwell = TimeSpan.FromMilliseconds(280);

    /// <summary>The notch sits where the pointer passes on its way to browser tabs: ask for a longer, deliberate dwell.</summary>
    public static readonly TimeSpan NotchDwell = TimeSpan.FromMilliseconds(450);

    /// <summary>How long the pointer rests on the capsule before it peeks (default <see cref="HoverDwell"/>).</summary>
    public TimeSpan HoverDelay { get; set; } = HoverDwell;

    /// <summary>How long the pointer rests on the notch before it opens (default <see cref="NotchDwell"/>).</summary>
    public TimeSpan NotchHoverDelay { get; set; } = NotchDwell;
    public static readonly TimeSpan HoverLinger = TimeSpan.FromMilliseconds(450);
    public static readonly TimeSpan DefaultEventDuration = TimeSpan.FromSeconds(4.5);
    public static readonly TimeSpan MinimumAfterHover = TimeSpan.FromSeconds(1.2);

    /// <summary>How often an open hover peek re-checks that the pointer is still there.</summary>
    public static readonly TimeSpan HoverRecheck = TimeSpan.FromSeconds(1);

    /// <summary>How long a finished upload shows its check mark.</summary>
    public static readonly TimeSpan DropSuccessLinger = TimeSpan.FromSeconds(1.5);

    /// <summary>How long a failed upload shows its error.</summary>
    public static readonly TimeSpan DropFailureLinger = TimeSpan.FromSeconds(4);

    /// <summary>How often the drop zone re-checks that a drag is still over it (a lost leave must not keep it open).</summary>
    public static readonly TimeSpan DropRecheck = TimeSpan.FromMilliseconds(400);

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

    /// <summary>The drop zone's phase changed (raised before <see cref="ModeChanged"/> on entry, after it on exit).</summary>
    public event Action<DropPhase>? DropPhaseChanged;

    public IslandMode Mode { get; private set; } = IslandMode.Compact;

    public ActivitySource ActivitySource { get; private set; }

    /// <summary>The last reported pointer state (from enter and exit).</summary>
    public bool IsPointerOver { get; private set; }

    /// <summary>The user is dragging the island: timers and events wait.</summary>
    public bool IsDragging { get; private set; }

    /// <summary>
    /// Where the pointer really is when a timer fires: true over the visible capsule, false
    /// elsewhere, null when unknown (then the last reported state is used).
    /// </summary>
    public Func<bool?>? PointerProbe { get; set; }

    /// <summary>
    /// Whether a file drag is still over the drop zone when its re-check fires: false leaves Drop,
    /// true or null (unknown) keeps it.
    /// </summary>
    public Func<bool?>? DropProbe { get; set; }

    /// <summary>Home Cloud is on: only then do the drop inputs do anything.</summary>
    public bool IsDropEnabled { get; private set; }

    public DropPhase DropPhase { get; private set; }

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

    /// <summary>Routes an input by name; the named methods below are equivalent.</summary>
    public void Handle(IslandInput input)
    {
        switch (input)
        {
            case IslandInput.PointerEntered: PointerEntered(); break;
            case IslandInput.PointerExited: PointerExited(); break;
            case IslandInput.Clicked: Activate(); break;
            case IslandInput.ClickedOutside: Dismiss(); break;
            case IslandInput.ModuleEvent: ShowEvent(); break;
            case IslandInput.DragStarted: BeginDrag(); break;
            case IslandInput.DragEnded: EndDrag(); break;
            case IslandInput.HoverDelayElapsed: OnHoverDelayElapsed(Mode); break;
            case IslandInput.ActiveAppChanged: break; // carries a rest mode: use SetRest
            case IslandInput.FileDragEntered: FileDragEntered(); break;
            case IslandInput.FileDragLeft: FileDragLeft(); break;
            case IslandInput.FileDropCompleted: BeginDropUpload(); break;
            case IslandInput.DropSucceeded: FinishDrop(succeeded: true); break;
            case IslandInput.DropFailed: FinishDrop(succeeded: false); break;
        }
    }

    /// <summary>
    /// ActiveAppChanged. Priority (highest first): Dragging, auto-hide, Expanded, Medium (hover or
    /// event), Compact. Retracting or hiding therefore closes an open activity or expanded island at
    /// once, whatever module it shows; returning to Compact moves only a resting island (an open one
    /// keeps what the user is looking at). During a drag the change waits for the drag to end.
    /// </summary>
    public void SetRest(RestMode rest)
    {
        if (_rest == rest)
        {
            return;
        }

        _rest = rest;
        if (IsDragging || _forcedHidden)
        {
            return;
        }

        var resting = Mode is IslandMode.Compact or IslandMode.Retracted or IslandMode.Hidden;
        var stepsAside = rest is RestMode.Retracted or RestMode.Hidden;
        if (resting || (stepsAside && Mode is IslandMode.Activity or IslandMode.Expanded))
        {
            CancelTimer();
            SetMode(RestingMode, ActivitySource.None);
        }
    }

    public void PointerEntered()
    {
        IsPointerOver = true;
        if (IsDragging)
        {
            return;
        }

        switch (Mode)
        {
            case IslandMode.Compact:
            case IslandMode.Retracted:
                var from = Mode;
                Schedule(from == IslandMode.Retracted ? NotchHoverDelay : HoverDelay, () => OnHoverDelayElapsed(from));
                break;
            case IslandMode.Activity:
                // Hovering keeps it open; the exit (or the next check) decides when it closes.
                Schedule(HoverRecheck, CollapseIfIdle);
                break;
        }
    }

    public void PointerExited()
    {
        IsPointerOver = false;
        if (IsDragging)
        {
            return;
        }

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

    /// <summary>Clicked: a click on the capsule or notch expands it.</summary>
    public void Activate()
    {
        if (!IsDragging && Mode is IslandMode.Compact or IslandMode.Retracted or IslandMode.Activity)
        {
            CancelTimer();
            SetMode(IslandMode.Expanded, ActivitySource.None);
        }
    }

    /// <summary>ClickedOutside: Esc, clicking elsewhere, or an explicit close.</summary>
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
        IsDragging = false;
        CancelTimer();
        SetMode(IslandMode.Expanded, ActivitySource.None);
    }

    /// <summary>ModuleEvent: opens a transient activity unless the island is expanded, retracted, hidden, a drop zone or being dragged.</summary>
    public bool ShowEvent(TimeSpan? duration = null)
    {
        if (IsDragging || Mode is IslandMode.Expanded or IslandMode.Hidden or IslandMode.Retracted or IslandMode.Drop)
        {
            return false;
        }

        var length = duration ?? DefaultEventDuration;
        _eventEndsAt = _time.GetUtcNow() + length;
        SetMode(IslandMode.Activity, ActivitySource.Event, force: true);

        // Always schedule the end; if the pointer is over the activity then, it is extended instead.
        Schedule(length, CollapseIfIdle);
        return true;
    }

    /// <summary>DragStarted: freeze timers so nothing opens or closes under the moving island.</summary>
    public void BeginDrag()
    {
        IsDragging = true;
        CancelTimer();
    }

    /// <summary>DragEnded: resume; an open activity closes once the pointer has left.</summary>
    public void EndDrag()
    {
        if (!IsDragging)
        {
            return;
        }

        IsDragging = false;
        if (Mode is IslandMode.Compact or IslandMode.Retracted or IslandMode.Hidden && !_forcedHidden && Mode != RestingMode)
        {
            // The foreground app changed during the drag.
            SetMode(RestingMode, ActivitySource.None);
        }
        else if (Mode == IslandMode.Activity)
        {
            Schedule(MinimumAfterHover, CollapseIfIdle);
        }
    }

    /// <summary>Forced hide (tray, fullscreen app).</summary>
    public void Hide()
    {
        CancelTimer();
        _forcedHidden = true;
        IsDragging = false;
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

    /// <summary>Home Cloud switched on or off. Switching off closes an open drop zone at once.</summary>
    public void SetDropEnabled(bool enabled)
    {
        IsDropEnabled = enabled;
        if (!enabled && Mode == IslandMode.Drop)
        {
            LeaveDrop();
        }
    }

    /// <summary>FileDragEntered: files are dragged over the island. Works from every mode, hidden included.</summary>
    public bool FileDragEntered()
    {
        if (!IsDropEnabled || (Mode == IslandMode.Drop && DropPhase == DropPhase.Uploading))
        {
            return false;
        }

        if (Mode == IslandMode.Drop && DropPhase == DropPhase.Hover)
        {
            return true;
        }

        EnterDrop(DropPhase.Hover);
        Schedule(DropRecheck, RecheckDropHover);
        return true;
    }

    /// <summary>FileDragLeft: the drag went away without a drop. Only a waiting drop zone closes.</summary>
    public void FileDragLeft()
    {
        if (Mode == IslandMode.Drop && DropPhase == DropPhase.Hover)
        {
            LeaveDrop();
        }
    }

    /// <summary>FileDropCompleted: the upload starts; the island stays in Drop until it has finished.</summary>
    public bool BeginDropUpload()
    {
        if (!IsDropEnabled || (Mode == IslandMode.Drop && DropPhase == DropPhase.Uploading))
        {
            return false;
        }

        EnterDrop(DropPhase.Uploading);
        return true;
    }

    /// <summary>DropSucceeded / DropFailed: show the result for a moment, then rest.</summary>
    public void FinishDrop(bool succeeded)
    {
        if (Mode != IslandMode.Drop || DropPhase != DropPhase.Uploading)
        {
            return;
        }

        SetDropPhase(succeeded ? DropPhase.Succeeded : DropPhase.Failed);
        Schedule(succeeded ? DropSuccessLinger : DropFailureLinger, LeaveDrop);
    }

    public void Dispose() => CancelTimer();

    private void EnterDrop(DropPhase phase)
    {
        CancelTimer();
        SetDropPhase(phase);
        SetMode(IslandMode.Drop, ActivitySource.None);
    }

    private void LeaveDrop()
    {
        if (Mode == IslandMode.Drop)
        {
            CancelTimer();
            SetMode(RestingMode, ActivitySource.None);
        }
    }

    private void RecheckDropHover()
    {
        if (Mode != IslandMode.Drop || DropPhase != DropPhase.Hover)
        {
            return;
        }

        if (DropProbe?.Invoke() == false)
        {
            LeaveDrop();
            return;
        }

        Schedule(DropRecheck, RecheckDropHover);
    }

    private void SetDropPhase(DropPhase phase)
    {
        if (DropPhase != phase)
        {
            DropPhase = phase;
            DropPhaseChanged?.Invoke(phase);
        }
    }

    /// <summary>The pointer state, corrected by a fresh probe when one is available.</summary>
    private bool PointerIsOver()
    {
        if (PointerProbe?.Invoke() is { } actual)
        {
            IsPointerOver = actual;
        }

        return IsPointerOver;
    }

    private void OnHoverDelayElapsed(IslandMode from)
    {
        if (!IsDragging && Mode == from && Mode is IslandMode.Compact or IslandMode.Retracted && PointerIsOver())
        {
            SetMode(IslandMode.Activity, ActivitySource.Hover);

            // Keep checking while the peek is open: a lost pointer-exit must not keep it open.
            Schedule(HoverRecheck, CollapseIfIdle);
        }
    }

    private void CollapseIfIdle()
    {
        if (Mode != IslandMode.Activity || IsDragging)
        {
            return;
        }

        if (PointerIsOver())
        {
            Schedule(ActivitySource == ActivitySource.Hover ? HoverRecheck : MinimumAfterHover, CollapseIfIdle);
            return;
        }

        SetMode(RestingMode, ActivitySource.None);
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
        if (mode != IslandMode.Drop)
        {
            // Leaving the drop zone (also through Hide or Toggle) ends its phase.
            SetDropPhase(DropPhase.None);
        }
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
