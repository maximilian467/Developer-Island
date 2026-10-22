using Avalonia.Media.Imaging;
using DeveloperIsland.Core.Diagnostics;

namespace DeveloperIsland.Platform.Windowing;

/// <summary>An app for the auto-hide list: its process name (lower case), display name and executable.</summary>
internal sealed record AppEntry(string Process, string Name, string? Path);

/// <summary>
/// Apps as the desktop knows them: installed applications from freedesktop .desktop entries
/// (system, user, Snap and Flatpak) matched to running processes, with their names and theme icons.
/// </summary>
internal static class AppCatalog
{
    private static readonly string[] ApplicationDirs =
    [
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local/share/applications"),
        "/usr/share/applications",
        "/usr/local/share/applications",
        "/var/lib/snapd/desktop/applications",
        "/var/lib/flatpak/exports/share/applications",
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local/share/flatpak/exports/share/applications"),
    ];

    private static readonly string[] IconDirs =
    [
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local/share/icons"),
        "/usr/share/icons/hicolor",
        "/usr/share/icons/Yaru",
        "/usr/share/icons/Adwaita",
        "/var/lib/snapd/desktop/icons",
        "/var/lib/flatpak/exports/share/icons/hicolor",
        "/usr/share/pixmaps",
    ];

    private static List<DesktopEntry>? s_entries;

    private sealed record DesktopEntry(string Name, string? Exec, string? Icon, string? WmClass);

    /// <summary>Apps that are running now (that this user may inspect), best known first.</summary>
    public static List<AppEntry> RunningApps()
    {
        var running = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var proc in Directory.EnumerateDirectories("/proc"))
        {
            var pid = System.IO.Path.GetFileName(proc);
            if (pid.Length == 0 || !char.IsAsciiDigit(pid[0]))
            {
                continue;
            }

            try
            {
                var exe = new FileInfo(System.IO.Path.Combine(proc, "exe")).LinkTarget;
                if (!string.IsNullOrEmpty(exe))
                {
                    running.TryAdd(System.IO.Path.GetFileName(exe).ToLowerInvariant(), exe);
                }
            }
            catch (Exception)
            {
                // Another user's process, or it just ended.
            }
        }

        var result = new List<AppEntry>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in Entries())
        {
            foreach (var process in ProcessNames(entry))
            {
                if (running.TryGetValue(process, out var path) && seen.Add(process))
                {
                    result.Add(new AppEntry(process, entry.Name, path));
                    break;
                }
            }
        }

        return result.OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public static AppEntry FromExecutable(string path)
    {
        var process = System.IO.Path.GetFileName(path).ToLowerInvariant();
        return new AppEntry(process, FriendlyName(path, process), path);
    }

    /// <summary>The executable path of a process name when it is running or installed, else null.</summary>
    public static string? ResolvePath(string process)
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "/usr/bin").Split(':'))
        {
            var candidate = System.IO.Path.Combine(dir, process);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    public static string FriendlyName(string? path, string fallback)
    {
        var process = System.IO.Path.GetFileName(path ?? fallback).ToLowerInvariant();
        var entry = Entries().FirstOrDefault(e => ProcessNames(e).Contains(process));
        return entry?.Name ?? (fallback.Length > 0 ? char.ToUpperInvariant(fallback[0]) + fallback[1..] : fallback);
    }

    /// <summary>The app's theme icon (48 px or the nearest), decoded; null when it has none (or it is an SVG only).</summary>
    public static Task<Bitmap?> LoadIconAsync(string process) => Task.Run(() =>
    {
        try
        {
            var entry = Entries().FirstOrDefault(e => ProcessNames(e).Contains(process.ToLowerInvariant()));
            var file = entry?.Icon is { } icon ? FindIcon(icon) : null;
            return file is null ? null : Bitmap.DecodeToWidth(File.OpenRead(file), 48);
        }
        catch (Exception ex)
        {
            Log.Debug("settings", "App icon unavailable", new { error = ex.GetType().Name });
            return null;
        }
    });

    private static IEnumerable<string> ProcessNames(DesktopEntry entry)
    {
        if (entry.Exec is { } exec)
        {
            var command = exec.TrimStart('"').Split([' ', '"'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault(t => t != "env" && !t.Contains('='));
            if (command is not null)
            {
                yield return System.IO.Path.GetFileName(command).ToLowerInvariant();
                var resolved = ResolvePath(System.IO.Path.GetFileName(command));
                if (resolved is not null && new FileInfo(resolved).LinkTarget is { } target)
                {
                    yield return System.IO.Path.GetFileName(target).ToLowerInvariant();
                }
            }
        }

        if (entry.WmClass is { } wmClass)
        {
            yield return X11ActiveWindowSource.FromClass(wmClass);
        }

        // Well-known launchers whose process has another name.
        if (entry.Exec?.Contains("google-chrome", StringComparison.Ordinal) == true)
        {
            yield return "chrome";
        }

        if (entry.Exec?.Contains("microsoft-edge", StringComparison.Ordinal) == true)
        {
            yield return "msedge";
        }

        if (entry.Exec?.Contains("brave-browser", StringComparison.Ordinal) == true)
        {
            yield return "brave";
        }
    }

    private static List<DesktopEntry> Entries()
    {
        if (s_entries is not null)
        {
            return s_entries;
        }

        var entries = new List<DesktopEntry>();
        foreach (var dir in ApplicationDirs.Where(Directory.Exists))
        {
            foreach (var file in Directory.EnumerateFiles(dir, "*.desktop"))
            {
                if (Parse(file) is { } entry)
                {
                    entries.Add(entry);
                }
            }
        }

        return s_entries = entries;
    }

    private static DesktopEntry? Parse(string file)
    {
        try
        {
            string? name = null, exec = null, icon = null, wmClass = null;
            var inEntry = false;
            var hidden = false;
            foreach (var line in File.ReadLines(file))
            {
                if (line.StartsWith('['))
                {
                    inEntry = line.Trim() == "[Desktop Entry]";
                    continue;
                }

                if (!inEntry)
                {
                    continue;
                }

                if (line.StartsWith("Name=", StringComparison.Ordinal)) name ??= line[5..];
                else if (line.StartsWith("Exec=", StringComparison.Ordinal)) exec ??= line[5..];
                else if (line.StartsWith("Icon=", StringComparison.Ordinal)) icon ??= line[5..];
                else if (line.StartsWith("StartupWMClass=", StringComparison.Ordinal)) wmClass ??= line[15..];
                else if (line is "NoDisplay=true" or "Hidden=true") hidden = true;
            }

            return name is null || hidden ? null : new DesktopEntry(name, exec, icon, wmClass);
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static string? FindIcon(string icon)
    {
        if (System.IO.Path.IsPathRooted(icon))
        {
            return File.Exists(icon) && !icon.EndsWith(".svg", StringComparison.OrdinalIgnoreCase) ? icon : null;
        }

        foreach (var size in new[] { "48x48", "64x64", "96x96", "128x128", "256x256", "32x32" })
        {
            foreach (var dir in IconDirs.Where(Directory.Exists))
            {
                var candidate = System.IO.Path.Combine(dir, size, "apps", icon + ".png");
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        foreach (var dir in IconDirs.Where(Directory.Exists))
        {
            var candidate = System.IO.Path.Combine(dir, icon + ".png");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }
}
