using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using DeveloperIsland.Core.Diagnostics;
using DeveloperIsland.Core.Island;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.Win32;

namespace DeveloperIsland.Platform.Windowing;

/// <summary>An app the user can add to Auto-hide in apps.</summary>
internal sealed record AppEntry(string Process, string Name, string? Path);

/// <summary>
/// Finds apps for the Auto-hide list: running apps with a visible window, the path of a known
/// executable, its display name and its icon. Read-only; touches no process beyond querying its
/// image path.
/// </summary>
internal static class AppCatalog
{
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    /// <summary>Apps with a visible top-level window, one entry per executable, sorted by name.</summary>
    public static List<AppEntry> RunningApps()
    {
        var own = Environment.ProcessId;
        var byProcess = new Dictionary<string, AppEntry>(StringComparer.Ordinal);
        foreach (var process in Process.GetProcesses())
        {
            try
            {
                if (process.Id == own || process.MainWindowHandle == IntPtr.Zero || string.IsNullOrWhiteSpace(process.MainWindowTitle))
                {
                    continue;
                }

                var path = ImagePath((uint)process.Id);
                var name = SmartHidePolicy.NormalizeProcessName(path ?? process.ProcessName);
                if (name.Length == 0 || byProcess.ContainsKey(name) || name is "applicationframehost" or "textinputhost" or "shellexperiencehost")
                {
                    continue;
                }

                byProcess[name] = new AppEntry(name, FriendlyName(path, name), path);
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // Exited meanwhile, or not accessible.
            }
            finally
            {
                process.Dispose();
            }
        }

        return byProcess.Values.OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>An entry for an executable the user picked.</summary>
    public static AppEntry FromExecutable(string path)
    {
        var name = SmartHidePolicy.NormalizeProcessName(path);
        return new AppEntry(name, FriendlyName(path, name), path);
    }

    /// <summary>The executable path of a process name: a running instance, else the App Paths registration.</summary>
    public static string? ResolvePath(string process)
    {
        foreach (var running in Process.GetProcessesByName(process))
        {
            using (running)
            {
                if (ImagePath((uint)running.Id) is { } path)
                {
                    return path;
                }
            }
        }

        foreach (var root in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            try
            {
                using var key = root.OpenSubKey($@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\{process}.exe");
                if (key?.GetValue(null) is string value)
                {
                    var path = Environment.ExpandEnvironmentVariables(value.Trim('"'));
                    if (File.Exists(path))
                    {
                        return path;
                    }
                }
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
            {
                // Not readable; try the next.
            }
        }

        // Usual install folders of the built-in browsers.
        var known = process switch
        {
            "msedge" => new[] { @"%ProgramFiles(x86)%\Microsoft\Edge\Application\msedge.exe", @"%ProgramFiles%\Microsoft\Edge\Application\msedge.exe" },
            "chrome" => new[] { @"%ProgramFiles%\Google\Chrome\Application\chrome.exe", @"%LocalAppData%\Google\Chrome\Application\chrome.exe" },
            "firefox" => new[] { @"%ProgramFiles%\Mozilla Firefox\firefox.exe", @"%ProgramFiles(x86)%\Mozilla Firefox\firefox.exe" },
            _ => [],
        };
        return known.Select(Environment.ExpandEnvironmentVariables).FirstOrDefault(File.Exists);
    }

    /// <summary>"Google Chrome" from the file description; the executable name otherwise.</summary>
    public static string FriendlyName(string? path, string fallback)
    {
        if (path is not null)
        {
            try
            {
                var info = FileVersionInfo.GetVersionInfo(path);
                var name = !string.IsNullOrWhiteSpace(info.FileDescription) ? info.FileDescription : info.ProductName;
                if (!string.IsNullOrWhiteSpace(name))
                {
                    return name.Trim();
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                // Fall back to the executable name.
            }
        }

        return fallback.Length > 0 ? char.ToUpperInvariant(fallback[0]) + fallback[1..] : fallback;
    }

    /// <summary>The app's icon as shown by Explorer, 32 px; null when unavailable.</summary>
    public static async Task<ImageSource?> LoadIconAsync(string? path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            return null;
        }

        try
        {
            var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(path);
            using var thumbnail = await file.GetThumbnailAsync(Windows.Storage.FileProperties.ThumbnailMode.SingleItem, 32);
            if (thumbnail is null || thumbnail.Size == 0)
            {
                return null;
            }

            var image = new BitmapImage { DecodePixelWidth = 32, DecodePixelType = DecodePixelType.Logical };
            await image.SetSourceAsync(thumbnail);
            return image;
        }
        catch (Exception ex)
        {
            Log.Debug("settings", "App icon unavailable", new { error = ex.GetType().Name });
            return null;
        }
    }

    private static string? ImagePath(uint pid)
    {
        var handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (handle == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            var buffer = new StringBuilder(1024);
            var size = buffer.Capacity;
            return QueryFullProcessImageName(handle, 0, buffer, ref size) ? buffer.ToString() : null;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder exeName, ref int size);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
