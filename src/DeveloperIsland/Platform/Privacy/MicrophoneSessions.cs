using System.Runtime.InteropServices;
using DeveloperIsland.Core.Privacy;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace DeveloperIsland.Platform.Privacy;

/// <summary>
/// Reads the audio sessions of every active recording device (WASAPI, through NAudio): whether any app
/// other than this one captures right now. Only process IDs and states are read, never names or audio.
/// Call it from an MTA thread (a plain background thread or the thread pool).
/// </summary>
internal static class MicrophoneSessions
{
    public static bool InUse(MMDeviceEnumerator enumerator) =>
        CaptureSessions.AnyActive(Read(enumerator), Environment.ProcessId);

    private static List<CaptureSession> Read(MMDeviceEnumerator enumerator)
    {
        var sessions = new List<CaptureSession>();
        using var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active);
        foreach (var device in devices)
        {
            using (device)
            {
                try
                {
                    var manager = device.AudioSessionManager;
                    manager.RefreshSessions();
                    var list = manager.Sessions;
                    for (var i = 0; i < list.Count; i++)
                    {
                        using var session = list[i];
                        sessions.Add(new CaptureSession((int)session.GetProcessID, session.State == AudioSessionState.AudioSessionStateActive));
                    }
                }
                catch (COMException)
                {
                    // A device that was just unplugged or disabled: skip it this round.
                }
            }
        }

        return sessions;
    }
}
