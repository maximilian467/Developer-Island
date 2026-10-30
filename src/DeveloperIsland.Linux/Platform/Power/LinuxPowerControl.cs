using System.Diagnostics;
using DeveloperIsland.Core.Awake;
using DeveloperIsland.Core.Diagnostics;

namespace DeveloperIsland.Platform.Power;

/// <summary>
/// Keeps Linux awake through systemd-logind inhibitor locks (listed by <c>systemd-inhibit --list</c>
/// with their reason), no root needed:
/// <list type="bullet">
/// <item><c>sleep</c> blocks suspend, including the desktop's automatic suspend on idle.</item>
/// <item><c>handle-lid-switch</c> makes logind ignore the lid, so closing it does not suspend.</item>
/// </list>
/// Each lock is held by a small helper (<c>systemd-inhibit … tail --pid=&lt;island&gt;</c>) that ends
/// with the island, however it ends: a crash or kill releases the locks by itself. Nothing persistent
/// changes, so unlike Windows there is no setting to put back and no scheduled backstop.
/// The <see cref="IPowerControl"/> lid calls map onto the lid lock: "do nothing" holds it, any other
/// value releases it.
/// </summary>
internal sealed class LinuxPowerControl : IPowerControl, IDisposable
{
    private static readonly Guid Logind = new("6c6f6769-6e64-4000-8000-000000000001");
    private const uint Suspend = 1;

    private readonly object _gate = new();
    private Process? _sleep;
    private Process? _lid;

    public static bool IsAvailable => Core.Diagnostics.ProcessRunner.FindExecutable("systemd-inhibit", "/usr/bin/systemd-inhibit", "/bin/systemd-inhibit") is not null;

    public bool SetKeepAwake(bool on)
    {
        lock (_gate)
        {
            if (on)
            {
                return Hold(ref _sleep, "sleep", "Awake session: keeps the computer from suspending");
            }

            Release(ref _sleep);
            return true;
        }
    }

    public Guid? ActiveScheme() => Logind;

    public (uint Ac, uint Dc)? ReadLidAction(Guid scheme)
    {
        lock (_gate)
        {
            return IsHeld(_lid) ? (AwakeRules.LidDoNothing, AwakeRules.LidDoNothing) : (Suspend, Suspend);
        }
    }

    public bool WriteLidAction(Guid scheme, uint ac, uint dc)
    {
        lock (_gate)
        {
            if (ac == AwakeRules.LidDoNothing && dc == AwakeRules.LidDoNothing)
            {
                var ok = Hold(ref _lid, "handle-lid-switch", "Awake session: keeps working with the lid closed");
                Log.Info("awake", ok ? "Lid lock held" : "Lid lock refused");
                return ok;
            }

            Release(ref _lid);
            Log.Info("awake", "Lid lock released");
            return true;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            Release(ref _sleep);
            Release(ref _lid);
        }
    }

    private static bool IsHeld(Process? process)
    {
        try
        {
            return process is { HasExited: false };
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static bool Hold(ref Process? slot, string what, string why)
    {
        if (IsHeld(slot))
        {
            return true;
        }

        slot?.Dispose();
        slot = null;
        try
        {
            var info = new ProcessStartInfo("systemd-inhibit")
            {
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                CreateNoWindow = true,
            };
            foreach (var arg in new[]
            {
                "--what=" + what, "--who=Developer Island", "--why=" + why, "--mode=block",
                "tail", "--pid=" + Environment.ProcessId, "-f", "/dev/null",
            })
            {
                info.ArgumentList.Add(arg);
            }

            var process = Process.Start(info);
            if (process is null)
            {
                return false;
            }

            // A refused lock (policy, no logind) makes systemd-inhibit exit at once.
            if (process.WaitForExit(TimeSpan.FromMilliseconds(400)))
            {
                Log.Warn("awake", "Inhibitor lock refused", new { what, exitCode = process.ExitCode });
                process.Dispose();
                return false;
            }

            slot = process;
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn("awake", "Inhibitor lock unavailable", new { what, error = ex.GetType().Name });
            return false;
        }
    }

    private static void Release(ref Process? slot)
    {
        if (slot is null)
        {
            return;
        }

        try
        {
            if (!slot.HasExited)
            {
                // Ending systemd-inhibit (and its tail) releases the lock.
                slot.Kill(entireProcessTree: true);
                slot.WaitForExit(TimeSpan.FromSeconds(2));
            }
        }
        catch (Exception ex)
        {
            Log.Debug("awake", "Inhibitor helper did not end cleanly", new { error = ex.GetType().Name });
        }
        finally
        {
            slot.Dispose();
            slot = null;
        }
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

/// <summary>No scheduled backstop: inhibitor locks end with the process.</summary>
internal sealed class NoBackstop : IAwakeBackstop
{
    public void Schedule(DateTimeOffset endsAt)
    {
    }

    public void Cancel()
    {
    }
}
