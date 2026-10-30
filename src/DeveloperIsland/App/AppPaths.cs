namespace DeveloperIsland;

/// <summary>Local data locations. Everything stays under %LOCALAPPDATA%\DeveloperIsland.</summary>
internal static class AppPaths
{
    public static string Root { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DeveloperIsland");

    public static string Logs => Path.Combine(Root, "logs");

    public static string Database => Path.Combine(Root, "usage.db");

    public static string PricingOverrides => Path.Combine(Root, "pricing.json");

    public static string FocusSession => Path.Combine(Root, "focus-session.json");

    /// <summary>The Awake session's recovery journal (the lid setting to put back).</summary>
    public static string AwakeSession => Path.Combine(Root, "awake-session.json");

    public static string Tasks => Path.Combine(Root, "tasks.json");

    public static string UiState => Path.Combine(Root, "ui-state.json");

    /// <summary>Plan usage handed over by Claude Code's status line (numbers only).</summary>
    public static string ClaudePlan => Path.Combine(Root, "claude-plan.json");

    /// <summary>When the update check last ran and what it found.</summary>
    public static string UpdateState => Path.Combine(Root, "update-state.json");

    /// <summary>A downloaded update (setup, checksums, signature) until the next one replaces it.</summary>
    public static string Updates => Path.Combine(Root, "updates");

    public static string Settings(bool demo) => Path.Combine(Root, demo ? "settings.demo.json" : "settings.json");

    public static string Assets => Path.Combine(AppContext.BaseDirectory, "Assets");

    public static string Icon => Path.Combine(Assets, "DeveloperIsland.ico");
}

/// <summary>
/// Command-line options: <c>--demo</c>, <c>--autostart</c>, <c>--settings[=section]</c>, and for development
/// <c>--snapshot=folder</c> (demo only: renders every state to PNG offscreen, then exits), with an
/// optional <c>--culture=en-US</c> so documentation renders use one number and date format, and
/// <c>--homecloud-upload=path</c> (sends one file or folder through the Home Cloud drop pipeline, then
/// exits with 0 on success; reads the settings, never writes them; with <c>--demo</c> the demo settings).
/// </summary>
internal sealed record AppOptions(bool IsDemo, bool IsAutostart, string? SettingsSection, string? SnapshotDirectory = null, string? SnapshotCulture = null, string? HomeCloudUploadPath = null)
{
    public static AppOptions Parse(IEnumerable<string> args)
    {
        var list = args.Select(a => a.Trim()).ToList();
        var set = new HashSet<string>(list.Select(a => a.ToLowerInvariant()));
        string? settings = null;
        string? snapshot = null;
        string? culture = null;
        string? upload = null;
        foreach (var arg in list)
        {
            if (arg.StartsWith("--homecloud-upload=", StringComparison.OrdinalIgnoreCase))
            {
                var path = arg["--homecloud-upload=".Length..].Trim('"');
                upload = path.Length == 0 ? null : Path.GetFullPath(path);
            }
            else if (arg.Equals("--settings", StringComparison.OrdinalIgnoreCase))
            {
                settings = "General";
            }
            else if (arg.StartsWith("--snapshot=", StringComparison.OrdinalIgnoreCase))
            {
                snapshot = arg["--snapshot=".Length..].Trim('"');
            }
            else if (arg.StartsWith("--culture=", StringComparison.OrdinalIgnoreCase))
            {
                culture = arg["--culture=".Length..].Trim('"');
            }
            else if (arg.StartsWith("--settings=", StringComparison.OrdinalIgnoreCase))
            {
                var name = arg["--settings=".Length..];
                settings = name.Length == 0 ? "General" : char.ToUpperInvariant(name[0]) + name[1..].ToLowerInvariant();
            }
        }

        var demo = set.Contains("--demo");
        if (upload is not null)
        {
            // The upload debug run is a short pipeline run of its own: no snapshots, no Settings window.
            return new AppOptions(demo, false, null, HomeCloudUploadPath: upload);
        }

        var snapshotDirectory = demo ? snapshot : null;
        return new AppOptions(demo, set.Contains(Platform.Startup.AutostartService.Argument), settings, snapshotDirectory, snapshotDirectory is null ? null : culture);
    }
}
