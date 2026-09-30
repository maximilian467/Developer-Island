using System.Runtime.InteropServices;
using DeveloperIsland.Core.SystemInfo;

namespace DeveloperIsland.Platform.SystemInfo;

/// <summary>
/// Reads CPU, memory and battery with three cheap Win32 calls (no WMI, no performance counters).
/// CPU load is the share of non-idle time between two consecutive readings.
/// </summary>
internal sealed class SystemMetricsReader
{
    private ulong _lastIdle;
    private ulong _lastTotal;

    public SystemSample? Read()
    {
        if (!GetSystemTimes(out var idleTime, out var kernelTime, out var userTime))
        {
            return null;
        }

        var idle = ToUInt64(idleTime);
        var total = ToUInt64(kernelTime) + ToUInt64(userTime); // kernel time includes idle time
        var cpu = 0.0;
        if (_lastTotal != 0 && total > _lastTotal)
        {
            var busy = (total - _lastTotal) - (idle - _lastIdle);
            cpu = Math.Clamp(100.0 * busy / (total - _lastTotal), 0, 100);
        }

        _lastIdle = idle;
        _lastTotal = total;

        var memory = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (!GlobalMemoryStatusEx(ref memory))
        {
            return null;
        }

        int? battery = null;
        var charging = false;
        if (GetSystemPowerStatus(out var power) && (power.BatteryFlag & 128) == 0 && power.BatteryLifePercent <= 100)
        {
            battery = power.BatteryLifePercent;
            charging = power.ACLineStatus == 1;
        }

        return new SystemSample(cpu, memory.ullTotalPhys - memory.ullAvailPhys, memory.ullTotalPhys, battery, charging);
    }

    private static ulong ToUInt64(FILETIME time) => ((ulong)time.dwHighDateTime << 32) | time.dwLowDateTime;

    [StructLayout(LayoutKind.Sequential)]
    private struct FILETIME
    {
        public uint dwLowDateTime;
        public uint dwHighDateTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public uint BatteryLifeTime;
        public uint BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(out FILETIME idleTime, out FILETIME kernelTime, out FILETIME userTime);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX buffer);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS status);
}
