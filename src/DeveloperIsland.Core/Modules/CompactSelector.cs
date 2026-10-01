namespace DeveloperIsland.Core.Modules;

/// <summary>
/// Decides which module the compact island features:
/// <list type="bullet">
/// <item>One favorite: that one.</item>
/// <item>Several favorites: they take turns every <see cref="RotationInterval"/>; favorites with
/// nothing to say right now (no track playing, no session today) are skipped, unless none has
/// anything to say.</item>
/// <item>No favorite: the module opened last.</item>
/// <item>Neither: null, and the island keeps its classic summary (focus, AI usage, music).</item>
/// </list>
/// Disabled modules never appear: callers pass only enabled ones.
/// </summary>
public static class CompactSelector
{
    public static readonly TimeSpan RotationInterval = TimeSpan.FromSeconds(7);

    /// <param name="favorites">Enabled favorites in the user's module order.</param>
    /// <param name="lastActive">The module opened last, if it is enabled.</param>
    /// <param name="hasContent">Whether a module has something to show right now.</param>
    /// <param name="turn">Increments every rotation interval.</param>
    public static ModuleId? Select(IReadOnlyList<ModuleId> favorites, ModuleId? lastActive, Func<ModuleId, bool> hasContent, long turn)
    {
        if (favorites.Count == 0)
        {
            return lastActive;
        }

        var speaking = favorites.Where(hasContent).ToList();
        var pool = speaking.Count > 0 ? speaking : favorites;
        var index = (int)(((turn % pool.Count) + pool.Count) % pool.Count);
        return pool[index];
    }

    /// <summary>Whether a rotation timer is needed at all (two or more favorites).</summary>
    public static bool Rotates(IReadOnlyList<ModuleId> favorites) => favorites.Count > 1;

    /// <summary>Favorites as saved, reduced to enabled modules in the user's order.</summary>
    public static List<ModuleId> EffectiveFavorites(IEnumerable<string> saved, IReadOnlyList<ModuleId> order, Func<ModuleId, bool> isEnabled)
    {
        var set = saved
            .Select(s => Enum.TryParse<ModuleId>(s, ignoreCase: true, out var id) && Enum.IsDefined(id) ? id : (ModuleId?)null)
            .Where(id => id is not null)
            .Select(id => id!.Value)
            .ToHashSet();
        return order.Where(m => set.Contains(m) && isEnabled(m)).ToList();
    }
}
