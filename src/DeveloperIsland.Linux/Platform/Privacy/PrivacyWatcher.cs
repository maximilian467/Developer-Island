using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using DeveloperIsland.Core.Diagnostics;
using DeveloperIsland.Core.Privacy;

namespace DeveloperIsland.Platform.Privacy;

/// <summary>
/// Watches whether the microphone or the camera is in use, event-driven:
/// <list type="bullet">
/// <item>Microphone: <c>pactl subscribe</c> reports capture streams coming and going (PulseAudio and
/// PipeWire); then the live capture streams are counted, leaving out monitors of outputs (screen
/// recorders of system sound) and paused (corked) streams.</item>
/// <item>Camera: the kernel reports opens and closes of <c>/dev/video*</c> (inotify); then
/// <c>/proc</c> says whether any process still has a camera open.</item>
/// </list>
/// App names are never logged or kept. Changes are reported on the watcher threads.
/// </summary>
internal sealed partial class PrivacyWatcher : IDisposable
{
    private static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(150);

    private readonly Action<PrivacyState> _changed;
    private readonly CancellationTokenSource _stop = new();
    private readonly object _gate = new();
    private Process? _pactl;
    private int _inotify = -1;
    private PrivacyState _state;
    private bool _mic;
    private bool _camera;

    public PrivacyWatcher(Action<PrivacyState> changed)
    {
        _changed = changed;
        _mic = MicrophoneInUse();
        _camera = CameraInUse();
        Publish();
        StartMicrophoneWatch();
        StartCameraWatch();
    }

    public PrivacyState Current => _state;

    /// <summary>Reads both devices once, without watching (diagnostics).</summary>
    public static PrivacyState ReadNow() => new(MicrophoneInUse(), CameraInUse());

    public void Dispose()
    {
        _stop.Cancel();
        try
        {
            if (_pactl is { HasExited: false })
            {
                _pactl.Kill();
            }
        }
        catch (InvalidOperationException)
        {
        }

        _pactl?.Dispose();
        if (_inotify >= 0)
        {
            close(_inotify); // wakes the blocked read
            _inotify = -1;
        }
    }

    private void Publish()
    {
        PrivacyState state;
        lock (_gate)
        {
            state = new PrivacyState(_mic, _camera);
            if (state == _state)
            {
                return;
            }

            _state = state;
        }

        Log.Debug("privacy", "Device use changed", new { microphone = state.Microphone, camera = state.Camera });
        _changed(state);
    }

    // Microphone -------------------------------------------------------------------------------------

    private void StartMicrophoneWatch()
    {
        try
        {
            var info = new ProcessStartInfo("pactl", "subscribe")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            info.Environment["LC_ALL"] = "C";
            _pactl = Process.Start(info);
            if (_pactl is null)
            {
                return;
            }

            new Thread(() =>
            {
                try
                {
                    string? line;
                    while (!_stop.IsCancellationRequested && (line = _pactl.StandardOutput.ReadLine()) is not null)
                    {
                        // "Event 'new' on source-output #42", also 'change' and 'remove'.
                        if (line.Contains("source-output", StringComparison.Ordinal))
                        {
                            Thread.Sleep(Settle);
                            _mic = MicrophoneInUse();
                            Publish();
                        }
                    }
                }
                catch (Exception ex) when (!_stop.IsCancellationRequested)
                {
                    Log.Debug("privacy", "Microphone watch ended", new { error = ex.GetType().Name });
                }
            }) { IsBackground = true, Name = "Microphone indicator", Priority = ThreadPriority.BelowNormal }.Start();
            Log.Info("privacy", "Watching microphone use");
        }
        catch (Exception ex)
        {
            Log.Warn("privacy", "pactl unavailable; the microphone indicator stays off", new { error = ex.GetType().Name });
        }
    }

    private static bool MicrophoneInUse()
    {
        try
        {
            var info = new ProcessStartInfo("pactl")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            foreach (var arg in new[] { "-f", "json", "list", "source-outputs" })
            {
                info.ArgumentList.Add(arg);
            }

            info.Environment["LC_ALL"] = "C";
            using var process = Process.Start(info);
            if (process is null)
            {
                return false;
            }

            var json = process.StandardOutput.ReadToEnd();
            process.WaitForExit(2000);
            var monitors = MonitorSources();
            using var doc = JsonDocument.Parse(json);
            foreach (var output in doc.RootElement.EnumerateArray())
            {
                if (output.TryGetProperty("corked", out var corked) && corked.ValueKind == JsonValueKind.True)
                {
                    continue;
                }

                // Recording what the speakers play (a monitor source) is not the microphone.
                if (output.TryGetProperty("source", out var source) && source.ValueKind == JsonValueKind.Number && monitors.Contains(source.GetInt32()))
                {
                    continue;
                }

                if (output.TryGetProperty("properties", out var props))
                {
                    // Level meters (volume sliders, pavucontrol) peek at the input without recording it.
                    if (props.TryGetProperty("stream.monitor", out var m) && m.GetString() == "true")
                    {
                        continue;
                    }

                    if (props.TryGetProperty("media.name", out var media) && media.GetString() is { } name
                        && name.Contains("Peak detect", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                }

                return true;
            }
        }
        catch (Exception)
        {
            // No sound server: no indicator.
        }

        return false;
    }

    private static HashSet<int> MonitorSources()
    {
        var result = new HashSet<int>();
        try
        {
            var info = new ProcessStartInfo("pactl")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            foreach (var arg in new[] { "-f", "json", "list", "sources" })
            {
                info.ArgumentList.Add(arg);
            }

            info.Environment["LC_ALL"] = "C";
            using var process = Process.Start(info);
            if (process is null)
            {
                return result;
            }

            var json = process.StandardOutput.ReadToEnd();
            process.WaitForExit(2000);
            using var doc = JsonDocument.Parse(json);
            foreach (var source in doc.RootElement.EnumerateArray())
            {
                var isMonitor = source.TryGetProperty("monitor_of_sink", out var of) && of.ValueKind == JsonValueKind.String
                    && of.GetString() is { Length: > 0 } sink && sink != "n/a";
                if ((isMonitor || (source.TryGetProperty("name", out var n) && n.GetString()?.EndsWith(".monitor", StringComparison.Ordinal) == true))
                    && source.TryGetProperty("index", out var index))
                {
                    result.Add(index.GetInt32());
                }
            }
        }
        catch (Exception)
        {
        }

        return result;
    }

    // Camera -----------------------------------------------------------------------------------------

    private void StartCameraWatch()
    {
        try
        {
            _inotify = inotify_init1(IN_CLOEXEC);
            if (_inotify < 0)
            {
                return;
            }

            var watched = 0;
            foreach (var device in Directory.EnumerateFiles("/dev", "video*"))
            {
                if (inotify_add_watch(_inotify, device, IN_OPEN | IN_CLOSE_WRITE | IN_CLOSE_NOWRITE) >= 0)
                {
                    watched++;
                }
            }

            // New cameras (a USB webcam plugged in later) appear in /dev.
            inotify_add_watch(_inotify, "/dev", IN_CREATE);
            new Thread(CameraLoop) { IsBackground = true, Name = "Camera indicator", Priority = ThreadPriority.BelowNormal }.Start();
            Log.Info("privacy", "Watching camera use", new { cameras = watched });
        }
        catch (Exception ex)
        {
            Log.Warn("privacy", "Camera watch unavailable; the camera indicator stays off", new { error = ex.GetType().Name });
        }
    }

    private unsafe void CameraLoop()
    {
        var buffer = new byte[4096];
        while (!_stop.IsCancellationRequested)
        {
            int count;
            fixed (byte* p = buffer)
            {
                count = (int)read(_inotify, p, (nuint)buffer.Length);
            }

            if (count <= 0 || _stop.IsCancellationRequested)
            {
                return;
            }

            // A new /dev entry may be a camera: watch it too.
            if (ContainsCreatedVideoDevice(buffer, count, out var created))
            {
                foreach (var name in created)
                {
                    inotify_add_watch(_inotify, "/dev/" + name, IN_OPEN | IN_CLOSE_WRITE | IN_CLOSE_NOWRITE);
                }
            }

            Thread.Sleep(Settle);
            _camera = CameraInUse();
            Publish();
        }
    }

    private static bool ContainsCreatedVideoDevice(byte[] buffer, int length, out List<string> names)
    {
        names = [];
        var offset = 0;
        while (offset + 16 <= length)
        {
            var mask = BitConverter.ToUInt32(buffer, offset + 4);
            var nameLength = BitConverter.ToInt32(buffer, offset + 12);
            if ((mask & IN_CREATE) != 0 && nameLength > 0)
            {
                var name = System.Text.Encoding.UTF8.GetString(buffer, offset + 16, nameLength).TrimEnd('\0');
                if (name.StartsWith("video", StringComparison.Ordinal))
                {
                    names.Add(name);
                }
            }

            offset += 16 + nameLength;
        }

        return names.Count > 0;
    }

    /// <summary>Whether any process (that this user may inspect) has a /dev/video* device open.</summary>
    private static bool CameraInUse()
    {
        var self = Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        try
        {
            foreach (var proc in Directory.EnumerateDirectories("/proc"))
            {
                var pid = Path.GetFileName(proc);
                if (pid.Length == 0 || !char.IsAsciiDigit(pid[0]) || pid == self)
                {
                    continue;
                }

                IEnumerable<string> fds;
                try
                {
                    fds = Directory.EnumerateFiles(Path.Combine(proc, "fd"));
                }
                catch (Exception)
                {
                    continue; // another user's process, or it just ended
                }

                try
                {
                    foreach (var fd in fds)
                    {
                        var target = new FileInfo(fd).LinkTarget;
                        if (target is not null && target.StartsWith("/dev/video", StringComparison.Ordinal))
                        {
                            // WirePlumber opens cameras briefly to list them; that is not use.
                            var comm = SystemInfo.SystemMetricsReader.ReadText(Path.Combine(proc, "comm"));
                            if (comm is not "wireplumber")
                            {
                                return true;
                            }
                        }
                    }
                }
                catch (Exception)
                {
                    // The process ended while it was being read.
                }
            }
        }
        catch (Exception)
        {
        }

        return false;
    }

    private const int IN_CLOEXEC = 0x80000;
    private const uint IN_OPEN = 0x20;
    private const uint IN_CLOSE_WRITE = 0x8;
    private const uint IN_CLOSE_NOWRITE = 0x10;
    private const uint IN_CREATE = 0x100;

    [LibraryImport("libc", SetLastError = true)]
    private static partial int inotify_init1(int flags);

    [LibraryImport("libc", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int inotify_add_watch(int fd, string path, uint mask);

    [LibraryImport("libc", SetLastError = true)]
    private static unsafe partial nint read(int fd, byte* buffer, nuint count);

    [LibraryImport("libc")]
    private static partial int close(int fd);
}
