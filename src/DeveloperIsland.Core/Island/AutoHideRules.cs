using DeveloperIsland.Core.Settings;

namespace DeveloperIsland.Core.Island;

/// <summary>The per-app auto-hide list: built-in browsers first, then apps the user added.</summary>
public static class AutoHideRules
{
    public static readonly IReadOnlyList<(string Process, string Name)> KnownBrowsers =
    [
        ("chrome", "Google Chrome"),
        ("msedge", "Microsoft Edge"),
        ("firefox", "Firefox"),
    ];

    public const int MaxApps = 40;

    /// <summary>
    /// Cleans the saved list. The first time (no list saved yet), it is created from the older
    /// process list, so every browser keeps its previous on or off state.
    /// </summary>
    public static List<AutoHideApp> Normalize(List<AutoHideApp>? saved, IReadOnlyList<string> legacyEnabled)
    {
        var rules = new List<AutoHideApp>();
        if (saved is null || saved.Count == 0)
        {
            foreach (var (process, name) in KnownBrowsers)
            {
                rules.Add(new AutoHideApp { Process = process, Name = name, Enabled = legacyEnabled.Contains(process) });
            }

            foreach (var process in legacyEnabled.Where(p => KnownBrowsers.All(b => b.Process != p)))
            {
                rules.Add(new AutoHideApp { Process = process, Name = process, Enabled = true });
            }

            return rules;
        }

        foreach (var app in saved)
        {
            var process = SmartHidePolicy.NormalizeProcessName(app?.Process);
            if (app is null || process.Length == 0 || rules.Any(r => r.Process == process) || rules.Count >= MaxApps)
            {
                continue;
            }

            rules.Add(new AutoHideApp
            {
                Process = process,
                Name = string.IsNullOrWhiteSpace(app.Name) ? process : app.Name.Trim(),
                Path = string.IsNullOrWhiteSpace(app.Path) ? null : app.Path.Trim(),
                Enabled = app.Enabled,
            });
        }

        return rules;
    }

    public static bool IsBuiltIn(string process) => KnownBrowsers.Any(b => b.Process == process);
}
