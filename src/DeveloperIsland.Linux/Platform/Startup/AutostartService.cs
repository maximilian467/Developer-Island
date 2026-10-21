using DeveloperIsland.Core.Diagnostics;

namespace DeveloperIsland.Platform.Startup;

/// <summary>
/// "Start at login" through an XDG autostart entry (<c>~/.config/autostart/developer-island.desktop</c>),
/// which GNOME, KDE, Xfce and other freedesktop sessions read. No root needed. The entry runs the
/// island with <c>--autostart</c> so "Launch hidden" can apply.
/// </summary>
internal static class AutostartService
{
    public const string Argument = "--autostart";
    private const string FileName = "developer-island.desktop";

    private static string EntryPath => Path.Combine(AppPaths.ConfigHome, "autostart", FileName);

    public static bool IsEnabled()
    {
        try
        {
            if (!File.Exists(EntryPath))
            {
                return false;
            }

            var text = File.ReadAllText(EntryPath);
            return text.Contains(Exec, StringComparison.Ordinal)
                && !text.Contains("Hidden=true", StringComparison.OrdinalIgnoreCase)
                && !text.Contains("X-GNOME-Autostart-enabled=false", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            Log.Warn("startup", "Autostart state could not be read", new { error = ex.GetType().Name });
            return false;
        }
    }

    public static void SetEnabled(bool enabled)
    {
        try
        {
            if (enabled)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(EntryPath)!);
                File.WriteAllText(EntryPath, $"""
                    [Desktop Entry]
                    Type=Application
                    Name=Developer Island
                    Comment=AI usage, music, focus and system status at a glance
                    Exec={Exec}
                    Icon=developer-island
                    Terminal=false
                    NoDisplay=false
                    X-GNOME-Autostart-enabled=true
                    X-GNOME-Autostart-Delay=3

                    """);
            }
            else if (File.Exists(EntryPath))
            {
                File.Delete(EntryPath);
            }

            Log.Info("startup", "Autostart updated", new { enabled });
        }
        catch (Exception ex)
        {
            Log.Error("startup", "Autostart could not be changed", ex);
        }
    }

    /// <summary>The command line, quoted as the desktop entry spec asks.</summary>
    private static string Exec => $"{Quote(LauncherPath)} {Argument}";

    /// <summary>The installed launcher when there is one (stable across updates), else this executable.</summary>
    internal static string LauncherPath =>
        File.Exists("/usr/bin/developer-island") && Environment.ProcessPath?.StartsWith("/opt/developer-island/", StringComparison.Ordinal) == true
            ? "/usr/bin/developer-island"
            : Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "developer-island");

    private static string Quote(string path) =>
        path.Any(c => c is ' ' or '"' or '\'' or '\\' or '$' or '`')
            ? "\"" + path.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("$", "\\$").Replace("`", "\\`") + "\""
            : path;
}
