namespace DeveloperIsland.Core.Modules;

/// <summary>
/// The favorite star of an expanded-island tab. Every tab has exactly one star, in the same place
/// in the header, and it stands for all enabled modules the tab shows: one module for most tabs,
/// Claude and Codex together for Usage (only the enabled ones, so with one provider turned off the
/// star is that provider's).
/// <list type="bullet">
/// <item>The star is filled when any of the tab's modules is a favorite.</item>
/// <item>A filled star removes all of them; an empty star adds all of them.</item>
/// </list>
/// </summary>
public static class TabFavorite
{
    /// <param name="tabModules">The enabled modules the tab shows.</param>
    /// <param name="favorites">The current favorites.</param>
    public static bool IsFavorite(IReadOnlyCollection<ModuleId> tabModules, IReadOnlyCollection<ModuleId> favorites) =>
        tabModules.Any(favorites.Contains);

    /// <summary>What a click on the star does: the modules to change and their new favorite state.</summary>
    public static (IReadOnlyList<ModuleId> Modules, bool Favorite) Toggle(IReadOnlyCollection<ModuleId> tabModules, IReadOnlyCollection<ModuleId> favorites) =>
        (tabModules.ToList(), !IsFavorite(tabModules, favorites));
}
