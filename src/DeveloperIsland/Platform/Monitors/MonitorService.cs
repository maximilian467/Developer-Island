using System.Runtime.InteropServices;
using DeveloperIsland.Core.Placement;
using DeveloperIsland.Platform.Native;

namespace DeveloperIsland.Platform.Monitors;

/// <summary>Enumerates displays with their work areas and effective DPI scale.</summary>
internal static class MonitorService
{
    private const int MDT_EFFECTIVE_DPI = 0;

    public static IReadOnlyList<MonitorInfo> GetMonitors()
    {
        var monitors = new List<MonitorInfo>();
        NativeMethods.MonitorEnumProc callback = (hMonitor, _, _, _) =>
        {
            var info = new NativeMethods.MONITORINFOEX { cbSize = Marshal.SizeOf<NativeMethods.MONITORINFOEX>() };
            if (NativeMethods.GetMonitorInfo(hMonitor, ref info))
            {
                var scale = NativeMethods.GetDpiForMonitor(hMonitor, MDT_EFFECTIVE_DPI, out var dpiX, out _) == 0 ? dpiX / 96.0 : 1.0;
                monitors.Add(new MonitorInfo(
                    info.szDevice,
                    ToRect(info.rcMonitor),
                    ToRect(info.rcWork),
                    scale,
                    (info.dwFlags & NativeMethods.MONITORINFOF_PRIMARY) != 0,
                    MonitorId(info.szDevice)));
            }

            return true;
        };
        NativeMethods.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero);
        GC.KeepAlive(callback);

        if (monitors.Count == 0)
        {
            // Should not happen; keep the app usable with a sane default.
            monitors.Add(new MonitorInfo(@"\\.\DISPLAY1", new PixelRect(0, 0, 1920, 1080), new PixelRect(0, 0, 1920, 1040), 1.0, true));
        }

        return monitors;
    }

    /// <summary>Friendly label for Settings, e.g. "Display 2 (2560 × 1440)".</summary>
    public static string DisplayName(MonitorInfo monitor)
    {
        var number = new string(monitor.DeviceName.Where(char.IsDigit).ToArray());
        var name = string.IsNullOrEmpty(number) ? monitor.DeviceName : $"Display {number}";
        var primary = monitor.IsPrimary ? ", primary" : string.Empty;
        return $"{name} ({monitor.Bounds.Width}\u00A0×\u00A0{monitor.Bounds.Height}{primary})";
    }

    /// <summary>
    /// The device interface path of the active monitor on a display output, for example
    /// <c>\\?\DISPLAY#DELA0F4#5&amp;...#{e6f07b5f-...}</c>. It names the monitor model and its
    /// connection, and stays the same when Windows renumbers the <c>\\.\DISPLAYn</c> names.
    /// </summary>
    private static string? MonitorId(string deviceName)
    {
        var device = new NativeMethods.DISPLAY_DEVICE { cb = Marshal.SizeOf<NativeMethods.DISPLAY_DEVICE>() };
        for (uint i = 0; NativeMethods.EnumDisplayDevices(deviceName, i, ref device, NativeMethods.EDD_GET_DEVICE_INTERFACE_NAME); i++)
        {
            if ((device.StateFlags & NativeMethods.DISPLAY_DEVICE_ACTIVE) != 0 && !string.IsNullOrEmpty(device.DeviceID))
            {
                return device.DeviceID;
            }

            device = new NativeMethods.DISPLAY_DEVICE { cb = Marshal.SizeOf<NativeMethods.DISPLAY_DEVICE>() };
        }

        return null;
    }

    private static PixelRect ToRect(NativeMethods.RECT r) => new(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
}
