namespace DeveloperIsland.Core.Modules;

/// <summary>Every module of the island, in the default order.</summary>
public enum ModuleId
{
    Claude,
    Codex,
    Music,
    Git,
    GitHub,
    Focus,
    Calendar,
    Tasks,
    System,
}

/// <summary>
/// The lifecycle every module reports, so each panel can show the same four honest states: working,
/// turned off, not available on this machine (with what would make it available), or failed.
/// </summary>
public enum ModuleState
{
    /// <summary>Turned off in Settings. Nothing runs.</summary>
    Disabled,

    /// <summary>Enabled, first result not in yet.</summary>
    Loading,

    /// <summary>Enabled and showing data.</summary>
    Ready,

    /// <summary>Enabled and working, but there is nothing to show yet (no repository, no calendar).</summary>
    Empty,

    /// <summary>A prerequisite is missing (tool not installed, not signed in).</summary>
    Unavailable,

    /// <summary>Reading failed; the rest of the island keeps working.</summary>
    Error,
}

/// <summary>A module's state plus a short, user-facing explanation for the non-ready states.</summary>
public sealed record ModuleStatus(ModuleState State, string? Message = null)
{
    public static readonly ModuleStatus Disabled = new(ModuleState.Disabled);
    public static readonly ModuleStatus Loading = new(ModuleState.Loading);
    public static readonly ModuleStatus Ready = new(ModuleState.Ready);

    public bool IsReady => State == ModuleState.Ready;
}

public static class ModuleCatalog
{
    public static readonly IReadOnlyList<ModuleId> DefaultOrder = Enum.GetValues<ModuleId>();

    public static string DisplayName(ModuleId id) => id switch
    {
        ModuleId.GitHub => "GitHub",
        ModuleId.Claude => "Claude Code",
        _ => id.ToString(),
    };

    /// <summary>
    /// Saved order first (unknown and duplicate names dropped), then any module the saved list does not
    /// know yet, in default order. New modules therefore appear without resetting the user's order.
    /// </summary>
    public static List<ModuleId> NormalizeOrder(IEnumerable<string>? saved)
    {
        var order = new List<ModuleId>();
        foreach (var name in saved ?? [])
        {
            if (Enum.TryParse<ModuleId>(name?.Trim(), ignoreCase: true, out var id) && Enum.IsDefined(id) && !order.Contains(id))
            {
                order.Add(id);
            }
        }

        foreach (var id in DefaultOrder)
        {
            if (!order.Contains(id))
            {
                order.Add(id);
            }
        }

        return order;
    }

    /// <summary>Moves a module one step up (-1) or down (+1); the ends are sticky.</summary>
    public static List<ModuleId> Move(IReadOnlyList<ModuleId> order, ModuleId id, int delta)
    {
        var list = order.ToList();
        var from = list.IndexOf(id);
        var to = Math.Clamp(from + delta, 0, list.Count - 1);
        if (from < 0 || from == to)
        {
            return list;
        }

        list.RemoveAt(from);
        list.Insert(to, id);
        return list;
    }
}
