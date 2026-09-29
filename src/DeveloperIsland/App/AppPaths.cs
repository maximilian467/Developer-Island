namespace DeveloperIsland;

/// <summary>Local data locations. Everything stays under %LOCALAPPDATA%\DeveloperIsland.</summary>
internal static class AppPaths
{
    public static string Root { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DeveloperIsland");

    public static string Logs => Path.Combine(Root, "logs");

    public static string Database => Path.Combine(Root, "usage.db");

    public static string PricingOverrides => Path.Combine(Root, "pricing.json");

    public static string Settings(bool demo) => Path.Combine(Root, demo ? "settings.demo.json" : "settings.json");

    public static string Assets => Path.Combine(AppContext.BaseDirectory, "Assets");

    public static string Icon => Path.Combine(Assets, "DeveloperIsland.ico");
}

/// <summary>Command-line options: <c>--demo</c>, <c>--autostart</c>, <c>--settings[=section]</c>.</summary>
internal sealed record AppOptions(bool IsDemo, bool IsAutostart, string? SettingsSection)
{
    public static AppOptions Parse(IEnumerable<string> args)
    {
        var list = args.Select(a => a.Trim()).ToList();
        var set = new HashSet<string>(list.Select(a => a.ToLowerInvariant()));
        string? settings = null;
        foreach (var arg in list)
        {
            if (arg.Equals("--settings", StringComparison.OrdinalIgnoreCase))
            {
                settings = "General";
            }
            else if (arg.StartsWith("--settings=", StringComparison.OrdinalIgnoreCase))
            {
                var name = arg["--settings=".Length..];
                settings = name.Length == 0 ? "General" : char.ToUpperInvariant(name[0]) + name[1..].ToLowerInvariant();
            }
        }

        return new AppOptions(set.Contains("--demo"), set.Contains(Platform.Startup.AutostartService.Argument), settings);
    }
}
