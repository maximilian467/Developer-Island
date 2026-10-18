namespace DeveloperIsland.Core.Audio;

public sealed record AudioOutputDevice(string Id, string Name, bool IsDefault);

public enum AudioDeviceRole
{
    Console,
    Multimedia,
    Communications,
}

public interface IAudioOutputPlatform
{
    IReadOnlyList<AudioOutputDevice> GetActiveOutputs();

    string? GetDefaultOutputId(AudioDeviceRole role);

    void SetDefaultOutput(string deviceId, AudioDeviceRole role);
}
