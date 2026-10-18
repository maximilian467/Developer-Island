namespace DeveloperIsland.Core.Audio;

public sealed class AudioOutputService(IAudioOutputPlatform platform)
{
    private static readonly AudioDeviceRole[] DefaultRoles =
    [
        AudioDeviceRole.Console,
        AudioDeviceRole.Multimedia,
        AudioDeviceRole.Communications,
    ];

    public IReadOnlyList<AudioOutputDevice> GetActiveOutputs()
    {
        var currentDefault = platform.GetDefaultOutputId(AudioDeviceRole.Multimedia);
        return platform.GetActiveOutputs()
            .OrderBy(device => device.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(device => device with { IsDefault = device.Id == currentDefault })
            .ToArray();
    }

    public void SwitchTo(string deviceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        if (!platform.GetActiveOutputs().Any(device => device.Id == deviceId))
        {
            throw new AudioOutputUnavailableException();
        }

        var previousDefaults = DefaultRoles.ToDictionary(role => role, platform.GetDefaultOutputId);
        var changedRoles = new List<AudioDeviceRole>();
        try
        {
            foreach (var role in DefaultRoles)
            {
                platform.SetDefaultOutput(deviceId, role);
                changedRoles.Add(role);
            }
        }
        catch (Exception switchError)
        {
            RollBack(previousDefaults, changedRoles, switchError);
            throw;
        }
    }

    private void RollBack(IReadOnlyDictionary<AudioDeviceRole, string?> previousDefaults, IReadOnlyList<AudioDeviceRole> changedRoles, Exception switchError)
    {
        List<Exception>? rollbackErrors = null;
        foreach (var role in changedRoles.Reverse())
        {
            if (previousDefaults[role] is not { } previousDefault)
            {
                continue;
            }

            try
            {
                platform.SetDefaultOutput(previousDefault, role);
            }
            catch (Exception rollbackError)
            {
                (rollbackErrors ??= []).Add(rollbackError);
            }
        }

        if (rollbackErrors is not null)
        {
            throw new AggregateException("Audio output switching and recovery both failed.", rollbackErrors.Prepend(switchError));
        }
    }
}

public sealed class AudioOutputUnavailableException : Exception
{
    public AudioOutputUnavailableException() : base("The selected audio output is no longer active.")
    {
    }
}
