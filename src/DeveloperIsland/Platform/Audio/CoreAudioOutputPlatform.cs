using System.Runtime.InteropServices;
using DeveloperIsland.Core.Audio;
using NAudio.CoreAudioApi;

namespace DeveloperIsland.Platform.Audio;

internal sealed class CoreAudioOutputPlatform : IAudioOutputPlatform
{
    public IReadOnlyList<AudioOutputDevice> GetActiveOutputs()
    {
        using var enumerator = new MMDeviceEnumerator();
        using var endpoints = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
        var outputs = new List<AudioOutputDevice>(endpoints.Count);
        foreach (var endpoint in endpoints)
        {
            using (endpoint)
            {
                outputs.Add(new AudioOutputDevice(endpoint.ID, endpoint.FriendlyName, false));
            }
        }

        return outputs;
    }

    public string? GetDefaultOutputId(AudioDeviceRole role)
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using var endpoint = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, ToRole(role));
            return endpoint.ID;
        }
        catch (COMException)
        {
            return null;
        }
    }

    public void SetDefaultOutput(string deviceId, AudioDeviceRole role)
    {
        var policy = (IPolicyConfig)new PolicyConfigClient();
        try
        {
            Marshal.ThrowExceptionForHR(policy.SetDefaultEndpoint(deviceId, ToRole(role)));
        }
        finally
        {
            Marshal.FinalReleaseComObject(policy);
        }
    }

    private static Role ToRole(AudioDeviceRole role) => role switch
    {
        AudioDeviceRole.Console => Role.Console,
        AudioDeviceRole.Multimedia => Role.Multimedia,
        AudioDeviceRole.Communications => Role.Communications,
        _ => throw new ArgumentOutOfRangeException(nameof(role)),
    };

    [ComImport]
    [Guid("870AF99C-171D-4F9E-AF0D-E63DF40C2BC9")]
    [ClassInterface(ClassInterfaceType.None)]
    private class PolicyConfigClient { }

    [ComImport]
    [Guid("F8679F50-850A-41CF-9C72-430F290290C8")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPolicyConfig
    {
        [PreserveSig] int GetMixFormat(string deviceId, IntPtr format);
        [PreserveSig] int GetDeviceFormat(string deviceId, int defaultFormat, IntPtr format);
        [PreserveSig] int ResetDeviceFormat(string deviceId);
        [PreserveSig] int SetDeviceFormat(string deviceId, IntPtr format, IntPtr mixFormat);
        [PreserveSig] int GetProcessingPeriod(string deviceId, int defaultPeriod, IntPtr defaultValue, IntPtr minimumValue);
        [PreserveSig] int SetProcessingPeriod(string deviceId, IntPtr period);
        [PreserveSig] int GetShareMode(string deviceId, IntPtr mode);
        [PreserveSig] int SetShareMode(string deviceId, IntPtr mode);
        [PreserveSig] int GetPropertyValue(string deviceId, int store, IntPtr key, IntPtr value);
        [PreserveSig] int SetPropertyValue(string deviceId, int store, IntPtr key, IntPtr value);
        [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string deviceId, Role role);
    }
}
