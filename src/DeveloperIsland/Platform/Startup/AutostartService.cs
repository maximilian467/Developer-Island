using DeveloperIsland.Core.Diagnostics;
using Microsoft.Win32;

namespace DeveloperIsland.Platform.Startup;

/// <summary>
/// "Start with Windows" through the per-user Run key: native, no admin rights, no scripts.
/// The entry is launched with <c>--autostart</c> so "Launch hidden" can apply.
/// </summary>
internal static class AutostartService
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "DeveloperIsland";
    public const string Argument = "--autostart";

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string command && command.Contains(ExecutablePath, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            Log.Warn("startup", "Autostart state could not be read", ex: ex);
            return false;
        }
    }

    public static void SetEnabled(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
            if (enabled)
            {
                key.SetValue(ValueName, $"\"{ExecutablePath}\" {Argument}", RegistryValueKind.String);
            }
            else if (key.GetValue(ValueName) is not null)
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }

            Log.Info("startup", "Autostart updated", new { enabled });
        }
        catch (Exception ex)
        {
            Log.Error("startup", "Autostart could not be changed", ex);
        }
    }

    private static string ExecutablePath => Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "DeveloperIsland.exe");
}
