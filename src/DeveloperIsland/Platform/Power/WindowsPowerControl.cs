using System.Runtime.InteropServices;
using DeveloperIsland.Core.Awake;
using DeveloperIsland.Core.Diagnostics;
using static DeveloperIsland.Platform.Power.PowerNative;

namespace DeveloperIsland.Platform.Power;

/// <summary>
/// Keeps Windows awake through a power request (listed by <c>powercfg /requests</c> with its reason)
/// and reads and writes the lid-close action of a power scheme. Neither needs admin rights.
/// </summary>
internal sealed class WindowsPowerControl : IPowerControl
{
    private const string Reason = "Developer Island: Awake session (keeps the PC awake, also with the lid closed)";
    private readonly object _gate = new();
    private IntPtr _request;

    public bool SetKeepAwake(bool on)
    {
        lock (_gate)
        {
            if (on)
            {
                return Acquire();
            }

            Release();
            return true;
        }
    }

    public Guid? ActiveScheme()
    {
        var result = PowerGetActiveScheme(IntPtr.Zero, out var pointer);
        if (result != ERROR_SUCCESS || pointer == IntPtr.Zero)
        {
            Log.Warn("awake", "Active power scheme unreadable", new { result });
            return null;
        }

        try
        {
            return Marshal.PtrToStructure<Guid>(pointer);
        }
        finally
        {
            LocalFree(pointer);
        }
    }

    public (uint Ac, uint Dc)? ReadLidAction(Guid scheme)
    {
        var subgroup = SubButtons;
        var setting = LidAction;
        var ac = PowerReadACValueIndex(IntPtr.Zero, ref scheme, ref subgroup, ref setting, out var acValue);
        var dc = PowerReadDCValueIndex(IntPtr.Zero, ref scheme, ref subgroup, ref setting, out var dcValue);
        if (ac != ERROR_SUCCESS || dc != ERROR_SUCCESS)
        {
            Log.Warn("awake", "Lid setting unreadable", new { ac, dc });
            return null;
        }

        return (acValue, dcValue);
    }

    public bool WriteLidAction(Guid scheme, uint ac, uint dc)
    {
        var subgroup = SubButtons;
        var setting = LidAction;
        var acResult = PowerWriteACValueIndex(IntPtr.Zero, ref scheme, ref subgroup, ref setting, ac);
        var dcResult = PowerWriteDCValueIndex(IntPtr.Zero, ref scheme, ref subgroup, ref setting, dc);

        // Written values take effect once the scheme is (re)applied, and only if it is the active one.
        var applied = ERROR_SUCCESS;
        if (ActiveScheme() == scheme)
        {
            applied = PowerSetActiveScheme(IntPtr.Zero, ref scheme);
        }

        var ok = acResult == ERROR_SUCCESS && dcResult == ERROR_SUCCESS && applied == ERROR_SUCCESS;
        Log.Info("awake", ok ? "Lid setting written" : "Lid setting refused", new { ac, dc, acResult, dcResult, applied });
        return ok;
    }

    /// <summary>The battery level (null without a battery) and whether the PC runs on mains power.</summary>
    public static (int? Percent, bool OnMains) ReadBattery()
    {
        if (!GetSystemPowerStatus(out var status))
        {
            return (null, true);
        }

        // BatteryFlag 128: no system battery; 255: unknown status. ACLineStatus 1: online.
        int? percent = (status.BatteryFlag & 128) == 0 && status.BatteryFlag != 255 && status.BatteryLifePercent <= 100
            ? status.BatteryLifePercent
            : null;
        return (percent, status.ACLineStatus == 1);
    }

    private bool Acquire()
    {
        if (_request != IntPtr.Zero)
        {
            return true;
        }

        var text = Marshal.StringToHGlobalUni(Reason);
        try
        {
            var context = new ReasonContext { Version = POWER_REQUEST_CONTEXT_VERSION, Flags = POWER_REQUEST_CONTEXT_SIMPLE_STRING, SimpleReasonString = text };
            var request = PowerCreateRequest(ref context);
            if (request == IntPtr.Zero || request == new IntPtr(-1))
            {
                Log.Warn("awake", "Power request could not be created", new { error = Marshal.GetLastWin32Error() });
                return false;
            }

            if (!PowerSetRequest(request, PowerRequestType.SystemRequired))
            {
                Log.Warn("awake", "System-required request refused", new { error = Marshal.GetLastWin32Error() });
                CloseHandle(request);
                return false;
            }

            // Keeps the system out of low-power idle on Modern Standby devices; harmless elsewhere.
            if (!PowerSetRequest(request, PowerRequestType.ExecutionRequired))
            {
                Log.Debug("awake", "Execution-required request refused", new { error = Marshal.GetLastWin32Error() });
            }

            _request = request;
            return true;
        }
        finally
        {
            Marshal.FreeHGlobal(text);
        }
    }

    private void Release()
    {
        if (_request == IntPtr.Zero)
        {
            return;
        }

        PowerClearRequest(_request, PowerRequestType.ExecutionRequired);
        PowerClearRequest(_request, PowerRequestType.SystemRequired);
        CloseHandle(_request);
        _request = IntPtr.Zero;
    }
}

/// <summary>Demo mode: remembers the values in memory and never touches the system.</summary>
internal sealed class DemoPowerControl : IPowerControl
{
    private static readonly Guid Scheme = new("381b4222-f694-41f0-9685-ff5bb260df2e");
    private (uint Ac, uint Dc) _lid = (1, 1);

    public bool SetKeepAwake(bool on) => true;

    public Guid? ActiveScheme() => Scheme;

    public (uint Ac, uint Dc)? ReadLidAction(Guid scheme) => _lid;

    public bool WriteLidAction(Guid scheme, uint ac, uint dc)
    {
        _lid = (ac, dc);
        return true;
    }
}

/// <summary>Demo mode: no scheduled task.</summary>
internal sealed class NoBackstop : IAwakeBackstop
{
    public void Schedule(DateTimeOffset endsAt)
    {
    }

    public void Cancel()
    {
    }
}
