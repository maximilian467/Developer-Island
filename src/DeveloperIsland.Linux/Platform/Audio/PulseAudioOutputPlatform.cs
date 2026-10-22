using System.Diagnostics;
using System.Text.Json;
using DeveloperIsland.Core.Audio;
using DeveloperIsland.Core.Diagnostics;

namespace DeveloperIsland.Platform.Audio;

/// <summary>
/// Playback devices through <c>pactl</c>, which speaks to PulseAudio and to PipeWire (Ubuntu's default
/// sound server since 22.10) alike. Linux has one default output rather than Windows' three roles,
/// so setting any role sets the one default; streams that follow the default move with it.
/// </summary>
internal sealed class PulseAudioOutputPlatform : IAudioOutputPlatform
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(3);

    public static bool IsAvailable => Core.Diagnostics.ProcessRunner.FindExecutable("pactl", "/usr/bin/pactl") is not null;

    public IReadOnlyList<AudioOutputDevice> GetActiveOutputs()
    {
        var json = Run("-f", "json", "list", "sinks");
        if (json is null)
        {
            return [];
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            var outputs = new List<AudioOutputDevice>();
            foreach (var sink in doc.RootElement.EnumerateArray())
            {
                var name = sink.TryGetProperty("name", out var n) ? n.GetString() : null;
                if (string.IsNullOrEmpty(name))
                {
                    continue;
                }

                // A sink whose every port is unplugged (headphones out of the jack) is not an option.
                if (sink.TryGetProperty("ports", out var ports) && ports.ValueKind == JsonValueKind.Array && ports.GetArrayLength() > 0
                    && ports.EnumerateArray().All(p => p.TryGetProperty("availability", out var a) && a.GetString() == "not available"))
                {
                    continue;
                }

                var description = sink.TryGetProperty("description", out var d) ? d.GetString() : null;
                outputs.Add(new AudioOutputDevice(name, string.IsNullOrWhiteSpace(description) ? name : description!, false));
            }

            return outputs;
        }
        catch (JsonException ex)
        {
            Log.Debug("audio", "Sink list unreadable", new { error = ex.GetType().Name });
            return [];
        }
    }

    public string? GetDefaultOutputId(AudioDeviceRole role) => Run("get-default-sink")?.Trim() is { Length: > 0 } name ? name : null;

    public void SetDefaultOutput(string deviceId, AudioDeviceRole role)
    {
        // One default for every role: the first role's call sets it, the others find it set.
        if (GetDefaultOutputId(role) == deviceId)
        {
            return;
        }

        if (Run("set-default-sink", deviceId) is null)
        {
            throw new IOException("The sound server refused the default output.");
        }
    }

    /// <summary>Runs pactl and returns its output, or null on failure. Short and synchronous (a few ms).</summary>
    private static string? Run(params string[] args)
    {
        try
        {
            var info = new ProcessStartInfo("pactl")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var arg in args)
            {
                info.ArgumentList.Add(arg);
            }

            // pactl's JSON and messages are only stable in the C locale.
            info.Environment["LC_ALL"] = "C";
            using var process = Process.Start(info);
            if (process is null)
            {
                return null;
            }

            var output = process.StandardOutput.ReadToEndAsync();
            if (!process.WaitForExit(Timeout))
            {
                process.Kill();
                return null;
            }

            return process.ExitCode == 0 ? output.Result : null;
        }
        catch (Exception ex)
        {
            Log.Debug("audio", "pactl unavailable", new { error = ex.GetType().Name });
            return null;
        }
    }
}
