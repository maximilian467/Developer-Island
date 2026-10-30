using System.Diagnostics;
using DeveloperIsland.Core.Diagnostics;
using Microsoft.Win32;

namespace DeveloperIsland.Platform.Updates;

/// <summary>
/// Runs a verified setup as a silent in-place update. Only a copy that the setup installed is
/// updated this way: a portable or development copy would get a second, installed copy instead.
/// </summary>
internal static class SetupLauncher
{
    /// <summary>The installer's fixed AppId (installer/DeveloperIsland.iss); Inno Setup keys its uninstall entry by it.</summary>
    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\{6F1C2A5E-8B4D-4E7A-9C3B-2D5E7F9A1B3C}_is1";

    /// <summary>
    /// Silent, closes the running app, no reboot; /RESTARTAPP makes the setup start the new version
    /// afterwards. The setup keeps the tasks chosen at the last install (autostart included).
    /// </summary>
    public const string SilentArguments = "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS /RESTARTAPP";

    /// <summary>True when this process runs from the folder the setup installed to.</summary>
    public static bool IsInstalledCopy()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(UninstallKey);
            if (key?.GetValue("InstallLocation") is not string location || location.Length == 0)
            {
                return false;
            }

            static string Normalize(string path) => Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
            return string.Equals(Normalize(location), Normalize(AppContext.BaseDirectory), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>Starts the setup; the caller quits right after so the setup can replace the files.</summary>
    public static bool Start(string setupPath)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(setupPath, SilentArguments)
            {
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(setupPath)!,
            });
            Log.Info("update", "Setup started; quitting for the update");
            return process is not null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            Log.Warn("update", "Setup could not be started", new { error = ex.GetType().Name });
            return false;
        }
    }
}
