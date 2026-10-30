using System.Runtime.InteropServices;
using DeveloperIsland.Core.Diagnostics;
using DeveloperIsland.Core.Privacy;
using Microsoft.Win32;
using NAudio.CoreAudioApi;
using Microsoft.Win32.SafeHandles;

namespace DeveloperIsland.Platform.Privacy;

/// <summary>
/// Watches whether the microphone or the camera is in use, from two sources:
/// <list type="bullet">
/// <item>The consent store (<see cref="ConsentStore"/>), event-driven: Windows signals a change through
/// <c>RegNotifyChangeKeyValue</c>, and only then are the few entries read again. The only source for the camera.</item>
/// <item>The audio sessions of the recording devices (<see cref="MicrophoneSessions"/>), read once a second
/// on their own thread. Since the Windows 11 updates of October 2026 the consent store is no longer
/// written on some systems, so this is what keeps the microphone mark working.</item>
/// </list>
/// The microphone is in use when either source says so. App names are never logged or kept. Changes
/// are reported on the watching threads.
/// </summary>
internal sealed partial class PrivacyWatcher : IDisposable
{
    private const string StorePath = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\";
    private const uint REG_NOTIFY_CHANGE_NAME = 0x1;
    private const uint REG_NOTIFY_CHANGE_LAST_SET = 0x4;
    private const uint REG_NOTIFY_THREAD_AGNOSTIC = 0x10000000;

    /// <summary>An app opening a device writes several values in a row; read once they settle.</summary>
    private static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(120);

    /// <summary>How often the recording devices' sessions are read (a few COM calls, well under a millisecond).</summary>
    private static readonly TimeSpan SessionInterval = TimeSpan.FromSeconds(1);

    private readonly Action<PrivacyState> _changed;
    private readonly ManualResetEvent _stop = new(false);
    private readonly Thread? _thread;
    private readonly Thread? _sessionThread;
    private readonly Lock _gate = new();
    private PrivacyState _consent;
    private bool _capturing;
    private PrivacyState _state;

    public PrivacyWatcher(Action<PrivacyState> changed)
    {
        _changed = changed;
        _thread = new Thread(Run) { IsBackground = true, Name = "Privacy indicators", Priority = ThreadPriority.BelowNormal };
        _thread.Start();
        _sessionThread = new Thread(RunSessions) { IsBackground = true, Name = "Microphone sessions", Priority = ThreadPriority.BelowNormal };
        _sessionThread.SetApartmentState(ApartmentState.MTA);
        _sessionThread.Start();
    }

    public PrivacyState Current => _state;

    public void Dispose()
    {
        _stop.Set();
        _thread?.Join(TimeSpan.FromSeconds(1));
        _sessionThread?.Join(TimeSpan.FromSeconds(2));
        _stop.Dispose();
    }

    /// <summary>Reads both devices once, without watching (diagnostics).</summary>
    public static PrivacyState ReadNow()
    {
        using var microphone = Registry.CurrentUser.OpenSubKey(StorePath + "microphone");
        using var webcam = Registry.CurrentUser.OpenSubKey(StorePath + "webcam");
        var consent = new PrivacyState(InUse(microphone), InUse(webcam));
        return consent with { Microphone = consent.Microphone || CapturingNow() };
    }

    /// <summary>The recording devices' sessions, read on the thread pool (an MTA thread, as Core Audio wants).</summary>
    public static bool CapturingNow()
    {
        try
        {
            return Task.Run(() =>
            {
                using var enumerator = new MMDeviceEnumerator();
                return MicrophoneSessions.InUse(enumerator);
            }).GetAwaiter().GetResult();
        }
        catch (COMException)
        {
            return false;
        }
    }

    private void RunSessions()
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            Log.Info("privacy", "Watching microphone sessions");
            var failures = 0;
            do
            {
                try
                {
                    Update(capturing: MicrophoneSessions.InUse(enumerator));
                    failures = 0;
                }
                catch (COMException ex) when (++failures < 10)
                {
                    // The audio service restarting, a device change mid-read: try again next round.
                    Log.Debug("privacy", "Microphone sessions unreadable", new { error = ex.HResult });
                }
            }
            while (!_stop.WaitOne(SessionInterval));
        }
        catch (Exception ex)
        {
            Log.Error("privacy", "Microphone session watch failed", ex);
        }
    }

    private void Run()
    {
        try
        {
            using var microphone = Registry.CurrentUser.OpenSubKey(StorePath + "microphone");
            using var webcam = Registry.CurrentUser.OpenSubKey(StorePath + "webcam");
            if (microphone is null && webcam is null)
            {
                Log.Warn("privacy", "Consent store not found; camera and microphone indicators stay off");
                return;
            }

            using var micChanged = new AutoResetEvent(false);
            using var camChanged = new AutoResetEvent(false);
            var waits = new WaitHandle[] { _stop, micChanged, camChanged };
            Log.Info("privacy", "Watching camera and microphone use", new { microphone = microphone is not null, camera = webcam is not null });

            while (true)
            {
                // Arm first, then read: a change between the two is never missed.
                if (!Arm(microphone, micChanged) | !Arm(webcam, camChanged))
                {
                    Log.Warn("privacy", "Change notification unavailable; indicators stop updating");
                    return;
                }

                Update(consent: new PrivacyState(InUse(microphone), InUse(webcam)));
                if (WaitHandle.WaitAny(waits) == 0 || _stop.WaitOne(Settle))
                {
                    return;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error("privacy", "Camera and microphone watch failed", ex);
        }
    }

    /// <summary>
    /// Combines the two sources and reports a change. Reported under the lock, so two threads can never
    /// deliver their states in the wrong order (the callback only queues work on the UI thread).
    /// </summary>
    private void Update(PrivacyState? consent = null, bool? capturing = null)
    {
        lock (_gate)
        {
            _consent = consent ?? _consent;
            _capturing = capturing ?? _capturing;
            var state = new PrivacyState(_consent.Microphone || _capturing, _consent.Camera);
            if (state == _state)
            {
                return;
            }

            _state = state;
            Log.Info("privacy", "Device use changed", new { microphone = state.Microphone, camera = state.Camera });
            _changed(state);
        }
    }

    private static bool Arm(RegistryKey? key, AutoResetEvent signal) =>
        key is null || RegNotifyChangeKeyValue(key.Handle, true, REG_NOTIFY_CHANGE_NAME | REG_NOTIFY_CHANGE_LAST_SET | REG_NOTIFY_THREAD_AGNOSTIC, signal.SafeWaitHandle, true) == 0;

    /// <summary>Any app (packaged, or desktop under "NonPackaged") currently using the device.</summary>
    private static bool InUse(RegistryKey? device)
    {
        if (device is null)
        {
            return false;
        }

        try
        {
            return ConsentStore.AnyInUse(Usages(device));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return false;
        }
    }

    private static IEnumerable<ConsentUsage> Usages(RegistryKey device)
    {
        foreach (var name in device.GetSubKeyNames())
        {
            using var app = device.OpenSubKey(name);
            if (app is null)
            {
                continue;
            }

            if (string.Equals(name, "NonPackaged", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var desktopName in app.GetSubKeyNames())
                {
                    using var desktop = app.OpenSubKey(desktopName);
                    if (desktop is not null)
                    {
                        yield return Usage(desktop);
                    }
                }
            }
            else
            {
                yield return Usage(app);
            }
        }
    }

    private static ConsentUsage Usage(RegistryKey app) =>
        new(ReadLong(app, "LastUsedTimeStart"), ReadLong(app, "LastUsedTimeStop"));

    private static long ReadLong(RegistryKey key, string name) => key.GetValue(name) switch
    {
        long value => value,
        int value => value,
        _ => 0,
    };

    [LibraryImport("advapi32.dll")]
    private static partial int RegNotifyChangeKeyValue(SafeRegistryHandle hKey, [MarshalAs(UnmanagedType.Bool)] bool bWatchSubtree, uint dwNotifyFilter, SafeWaitHandle hEvent, [MarshalAs(UnmanagedType.Bool)] bool fAsynchronous);
}
