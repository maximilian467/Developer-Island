namespace DeveloperIsland.Core.Modules;

/// <summary>What a hover peek shows.</summary>
public enum PeekContent
{
    /// <summary>The featured module (a favorite, or the module opened last).</summary>
    Featured,
    Focus,
    Calendar,
    SystemAlert,
    Music,
    Usage,
    Info,
}

/// <summary>Live facts the content choice depends on (no UI types).</summary>
public sealed record IslandFacts(
    bool FocusActive,
    bool EventSoon,
    bool SystemAlert,
    bool MusicPlaying,
    bool MusicHasTrack,
    bool UsageAvailable);

/// <summary>
/// Which module content the island shows. Kept apart from the display state (Hidden, Compact,
/// Medium, Expanded, Dragging; see IslandStateMachine): favorites and the module opened last
/// decide <i>what</i> is shown, never <i>whether</i> the island is open.
/// <list type="bullet">
/// <item>Hover and opening show the featured module: the favorite (or the rotating favorites), else
/// the module opened last. Claude is never a silent default.</item>
/// <item>Only with neither does the classic priority apply (focus, next event, system alert,
/// music, usage).</item>
/// <item>The module opened last changes only when the user picks a tab, not when the island
/// opens on its own choice.</item>
/// </list>
/// </summary>
public static class IslandContent
{
    public static PeekContent ChoosePeek(ModuleId? featured, IslandFacts facts)
    {
        if (featured is not null)
        {
            return PeekContent.Featured;
        }

        if (facts.FocusActive) return PeekContent.Focus;
        if (facts.EventSoon) return PeekContent.Calendar;
        if (facts.SystemAlert) return PeekContent.SystemAlert;
        if (facts.MusicPlaying) return PeekContent.Music;
        if (facts.UsageAvailable) return PeekContent.Usage;
        if (facts.MusicHasTrack) return PeekContent.Music;
        return PeekContent.Info;
    }

    /// <summary>
    /// The module whose tab opens when the island expands: an explicit request (New Task) first, then
    /// the event the user just saw, then the featured module, then a running focus session; null
    /// keeps the current tab.
    /// </summary>
    public static ModuleId? ChooseTabOnOpen(ModuleId? requested, ModuleId? justAnnounced, ModuleId? featured, bool focusActive) =>
        requested ?? justAnnounced ?? featured ?? (focusActive ? ModuleId.Focus : null);
}
