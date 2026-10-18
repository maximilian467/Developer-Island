using DeveloperIsland.Core.Audio;

namespace DeveloperIsland.Tests;

public sealed class AudioOutputServiceTests
{
    [Fact]
    public void GetActiveOutputs_marks_the_multimedia_default_and_sorts_by_name()
    {
        var platform = new FakeAudioOutputPlatform(
            [new("headphones", "Headphones", false), new("soundbar", "Soundbar", false)],
            new Dictionary<AudioDeviceRole, string?> { [AudioDeviceRole.Multimedia] = "soundbar" });

        var outputs = new AudioOutputService(platform).GetActiveOutputs();

        Assert.Equal(new[] { "Headphones", "Soundbar" }, outputs.Select(device => device.Name));
        Assert.True(outputs[1].IsDefault);
        Assert.False(outputs[0].IsDefault);
    }

    [Fact]
    public void SwitchTo_sets_every_windows_default_role()
    {
        var platform = PlatformWithTwoOutputs();

        new AudioOutputService(platform).SwitchTo("headphones");

        Assert.All(Enum.GetValues<AudioDeviceRole>(), role => Assert.Equal("headphones", platform.Defaults[role]));
    }

    [Fact]
    public void SwitchTo_rejects_an_output_that_disconnected_after_the_list_was_loaded()
    {
        var platform = PlatformWithTwoOutputs();
        platform.ActiveOutputs.RemoveAll(device => device.Id == "headphones");

        Assert.Throws<AudioOutputUnavailableException>(() => new AudioOutputService(platform).SwitchTo("headphones"));
        Assert.All(Enum.GetValues<AudioDeviceRole>(), role => Assert.Equal("soundbar", platform.Defaults[role]));
    }

    [Fact]
    public void SwitchTo_restores_changed_roles_when_a_later_role_fails()
    {
        var platform = PlatformWithTwoOutputs();
        platform.FailedRole = AudioDeviceRole.Multimedia;

        Assert.Throws<InvalidOperationException>(() => new AudioOutputService(platform).SwitchTo("headphones"));

        Assert.All(Enum.GetValues<AudioDeviceRole>(), role => Assert.Equal("soundbar", platform.Defaults[role]));
    }

    private static FakeAudioOutputPlatform PlatformWithTwoOutputs() => new(
        [new("soundbar", "Soundbar", true), new("headphones", "Headphones", false)],
        Enum.GetValues<AudioDeviceRole>().ToDictionary(role => role, _ => (string?)"soundbar"));

    private sealed class FakeAudioOutputPlatform(
        List<AudioOutputDevice> activeOutputs,
        Dictionary<AudioDeviceRole, string?> defaults) : IAudioOutputPlatform
    {
        public List<AudioOutputDevice> ActiveOutputs { get; } = activeOutputs;

        public Dictionary<AudioDeviceRole, string?> Defaults { get; } = defaults;

        public AudioDeviceRole? FailedRole { get; set; }

        public IReadOnlyList<AudioOutputDevice> GetActiveOutputs() => ActiveOutputs;

        public string? GetDefaultOutputId(AudioDeviceRole role) => Defaults[role];

        public void SetDefaultOutput(string deviceId, AudioDeviceRole role)
        {
            if (FailedRole == role && deviceId == "headphones")
            {
                throw new InvalidOperationException("Simulated Windows endpoint failure.");
            }

            Defaults[role] = deviceId;
        }
    }
}
