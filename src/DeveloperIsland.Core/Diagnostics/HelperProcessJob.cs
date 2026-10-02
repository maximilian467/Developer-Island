using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace DeveloperIsland.Core.Diagnostics;

/// <summary>
/// Ties the command-line tools the app starts (git, gh) to the app's lifetime: they join a Windows job
/// object that kills them when the app's process ends, however it ends (Quit, a crash, Task Manager).
/// Without it, a tool still running when the app quits would outlive it until its own timeout.
/// Only these helpers join; a link or folder opened in the browser or Explorer does not.
/// </summary>
internal static class HelperProcessJob
{
    private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;
    private const int JobObjectExtendedLimitInformation = 9;

    // Never closed: the handle closes when the process ends, and that ends the helpers.
    private static readonly Lazy<IntPtr> Job = new(Create);

    /// <summary>Adds a started helper to the job. False where that is not possible; the helper then ends by its timeout.</summary>
    public static bool Adopt(Process process)
    {
        if (!OperatingSystem.IsWindows() || Job.Value == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            return AssignProcessToJobObject(Job.Value, process.Handle);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            return false; // already exited
        }
    }

    /// <summary>Whether a process is in the helper job.</summary>
    public static bool Contains(Process process) =>
        OperatingSystem.IsWindows() && Job.Value != IntPtr.Zero && IsProcessInJob(process.Handle, Job.Value, out var inJob) && inJob;

    private static IntPtr Create()
    {
        if (!OperatingSystem.IsWindows())
        {
            return IntPtr.Zero;
        }

        var job = CreateJobObject(IntPtr.Zero, null);
        if (job == IntPtr.Zero)
        {
            Log.Debug("process", "Helper job unavailable", new { error = Marshal.GetLastWin32Error() });
            return IntPtr.Zero;
        }

        var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
        info.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
        if (!SetInformationJobObject(job, JobObjectExtendedLimitInformation, ref info, Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>()))
        {
            Log.Debug("process", "Helper job unavailable", new { error = Marshal.GetLastWin32Error() });
            CloseHandle(job);
            return IntPtr.Zero;
        }

        return job;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateJobObject(IntPtr attributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(IntPtr job, int infoClass, ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION info, int length);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsProcessInJob(IntPtr process, IntPtr job, [MarshalAs(UnmanagedType.Bool)] out bool result);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
