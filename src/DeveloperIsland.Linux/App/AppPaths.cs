namespace DeveloperIsland;

/// <summary>
/// Local data locations, following the XDG base directory spec: everything under
/// <c>$XDG_DATA_HOME/developer-island</c> (normally <c>~/.local/share/developer-island</c>).
/// </summary>
internal static class AppPaths
{
    public static string Root { get; } = Path.Combine(DataHome, "developer-island");

    private static string DataHome =>
        Environment.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } xdg
            ? xdg
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");

    public static string ConfigHome =>
        Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } xdg
            ? xdg
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");

    /// <summary>Per-session runtime directory (sockets); falls back to the data root.</summary>
    public static string Runtime =>
        Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR") is { Length: > 0 } run ? run : Root;

    public static string Logs => Path.Combine(Root, "logs");

    public static string Database => Path.Combine(Root, "usage.db");

    public static string PricingOverrides => Path.Combine(Root, "pricing.json");

    public static string FocusSession => Path.Combine(Root, "focus-session.json");

    /// <summary>The Awake session's journal (crash recovery; on Linux nothing persistent needs undoing).</summary>
    public static string AwakeSession => Path.Combine(Root, "awake-session.json");

    public static string Tasks => Path.Combine(Root, "tasks.json");

    public static string UiState => Path.Combine(Root, "ui-state.json");

    /// <summary>Plan usage handed over by Claude Code's status line (numbers only).</summary>
    public static string ClaudePlan => Path.Combine(Root, "claude-plan.json");

    /// <summary>Calendar links when no Secret Service (GNOME Keyring, KWallet) is reachable; mode 0600.</summary>
    public static string SecretsFallback => Path.Combine(Root, "secrets.json");

    public static string Settings(bool demo) => Path.Combine(Root, demo ? "settings.demo.json" : "settings.json");

    public static string Assets => Path.Combine(AppContext.BaseDirectory, "Assets");

    public static string Icon => Path.Combine(Assets, "DeveloperIsland-256.png");
}

/// <summary>
/// Command-line options: <c>--demo</c>, <c>--autostart</c>, <c>--settings[=section]</c>, <c>--toggle</c>
/// and <c>--show</c> (sent to a running island; the global shortcut runs <c>--toggle</c>), and for
/// development <c>--snapshot=folder</c> (demo only), <c>--culture=en-US</c> and
/// <c>--homecloud-upload=path</c>.
/// </summary>
internal sealed record AppOptions(bool IsDemo, bool IsAutostart, string? SettingsSection, string? SnapshotDirectory = null, string? SnapshotCulture = null, string? HomeCloudUploadPath = null, bool Toggle = false)
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
            return new AppOptions(demo, false, null, HomeCloudUploadPath: upload);
        }

        var snapshotDirectory = demo ? snapshot : null;
        return new AppOptions(demo, set.Contains(Platform.Startup.AutostartService.Argument), settings, snapshotDirectory,
            snapshotDirectory is null ? null : culture, Toggle: set.Contains("--toggle"));
    }
}
