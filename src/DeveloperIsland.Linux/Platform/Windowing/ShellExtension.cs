using System.Diagnostics;
using System.Text.Json;
using DeveloperIsland.Core.Diagnostics;

namespace DeveloperIsland.Platform.Windowing;

internal enum ShellExtensionStatus
{
    /// <summary>Not a GNOME session: the helper does not apply.</summary>
    NotGnome,
    NotInstalled,
    Outdated,
    Disabled,

    /// <summary>Installed and enabled, but GNOME Shell has not loaded it yet (Wayland: next login).</summary>
    NeedsRelogin,
    Active,
}

/// <summary>
/// The bundled GNOME Shell extension (gnome-extension/ next to the app): installs it into the user's
/// extension folder and enables it. Only on the user's request (Settings, Position).
/// </summary>
internal static class ShellExtension
{
    public const string Uuid = "developer-island@maximilian467.github.io";

    private static string Bundled => Path.Combine(AppContext.BaseDirectory, "gnome-extension", Uuid);

    private static string Installed => Path.Combine(
        Environment.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } x ? x : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share"),
        "gnome-shell", "extensions", Uuid);

    public static bool IsGnome =>
        (Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP") ?? string.Empty).Contains("GNOME", StringComparison.OrdinalIgnoreCase);

    public static ShellExtensionStatus Status()
    {
        if (!IsGnome)
        {
            return ShellExtensionStatus.NotGnome;
        }

        var installed = Version(Path.Combine(Installed, "metadata.json"));
        if (installed is null)
        {
            // A system-wide copy (from the .deb) counts as installed.
            installed = Version(Path.Combine("/usr/share/gnome-shell/extensions", Uuid, "metadata.json"));
            if (installed is null)
            {
                return ShellExtensionStatus.NotInstalled;
            }
        }

        if (Version(Path.Combine(Bundled, "metadata.json")) is { } bundled && bundled > installed)
        {
            return ShellExtensionStatus.Outdated;
        }

        if (!EnabledList().Contains(Uuid))
        {
            return ShellExtensionStatus.Disabled;
        }

        return IsServing() ? ShellExtensionStatus.Active : ShellExtensionStatus.NeedsRelogin;
    }

    /// <summary>Copies the extension into the user's folder and adds it to GNOME's enabled list.</summary>
    public static bool InstallAndEnable()
    {
        try
        {
            if (Directory.Exists(Bundled))
            {
                Directory.CreateDirectory(Installed);
                foreach (var file in Directory.EnumerateFiles(Bundled))
                {
                    File.Copy(file, Path.Combine(Installed, Path.GetFileName(file)), overwrite: true);
                }
            }

            // gnome-extensions enable works for an extension the shell already knows; a new one is
            // enabled through the same setting it writes, and loads at the next login on Wayland.
            if (Run("gnome-extensions", "enable", Uuid) is null)
            {
                var list = EnabledList();
                if (!list.Contains(Uuid))
                {
                    list.Add(Uuid);
                    Run("gsettings", "set", "org.gnome.shell", "enabled-extensions", "[" + string.Join(", ", list.Select(u => $"'{u}'")) + "]");
                }
            }

            return true;
        }
        catch (Exception ex)
        {
            Log.Warn("window", "GNOME Shell helper could not be installed", new { error = ex.GetType().Name });
            return false;
        }
    }

    private static List<string> EnabledList()
    {
        var value = Run("gsettings", "get", "org.gnome.shell", "enabled-extensions") ?? string.Empty;
        if (!value.Contains('['))
        {
            return [];
        }

        var inner = value[(value.IndexOf('[') + 1)..value.LastIndexOf(']')];
        return inner.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(s => s.Trim('\'', '"')).Where(s => s.Length > 0).ToList();
    }

    private static bool IsServing()
    {
        var output = Run("gdbus", "call", "--session", "--dest", "org.freedesktop.DBus", "--object-path", "/org/freedesktop/DBus",
            "--method", "org.freedesktop.DBus.NameHasOwner", GnomeShellActiveWindowSource.BusName);
        return output?.Contains("true", StringComparison.Ordinal) == true;
    }

    private static int? Version(string metadata)
    {
        try
        {
            if (!File.Exists(metadata))
            {
                return null;
            }

            using var doc = JsonDocument.Parse(File.ReadAllText(metadata));
            return doc.RootElement.TryGetProperty("version", out var v) && v.TryGetInt32(out var n) ? n : 0;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string? Run(string file, params string[] args)
    {
        try
        {
            var info = new ProcessStartInfo(file) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var arg in args)
            {
                info.ArgumentList.Add(arg);
            }

            using var process = Process.Start(info);
            if (process is null)
            {
                return null;
            }

            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(5000);
            return process.ExitCode == 0 ? output.Trim() : null;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
