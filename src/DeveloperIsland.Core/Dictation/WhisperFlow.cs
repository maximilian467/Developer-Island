using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using DeveloperIsland.Core.Diagnostics;

namespace DeveloperIsland.Core.Dictation;

/// <summary>What the local WhisperFlow dictation is doing, as far as the island shows it.</summary>
public enum DictationPhase
{
    Idle,
    Recording,
    Transcribing,

    /// <summary>The text was inserted (shown briefly).</summary>
    Done,

    /// <summary>The recording held no speech (shown briefly).</summary>
    NoSpeech,

    /// <summary>The microphone or the transcription failed (shown briefly).</summary>
    Failed,
}

/// <summary>One line of WhisperFlow's status file: its state, when it was written and by which process.</summary>
public sealed record WhisperFlowStatus(DictationPhase Phase, DateTimeOffset At, int ProcessId);

/// <summary>
/// WhisperFlow (a local speech-to-text tool, not the commercial product) writes its state to
/// <c>%LOCALAPPDATA%\WhisperFlow\status.json</c>: <c>{"version":1,"state":"recording","at":"...","pid":1234}</c>.
/// It holds no audio and no text. States other than recording, transcribing, done, empty and error
/// (loading, ready, stopped, anything newer) mean nothing to show.
/// </summary>
public static class WhisperFlowStatusFile
{
    /// <summary>A result (done, nothing heard, failed) stays this long after WhisperFlow wrote it.</summary>
    public static readonly TimeSpan ResultDuration = TimeSpan.FromSeconds(3);

    /// <summary>A recording or transcription this old is stale (WhisperFlow was killed, the clock jumped).</summary>
    public static readonly TimeSpan MaxActiveAge = TimeSpan.FromMinutes(30);

    public static string DefaultPath() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WhisperFlow", "status.json");

    public static WhisperFlowStatus? Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("at", out var at) || at.ValueKind != JsonValueKind.String
                || !DateTimeOffset.TryParse(at.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var time))
            {
                return null;
            }

            var state = root.TryGetProperty("state", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null;
            var pid = root.TryGetProperty("pid", out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetInt32(out var id) ? id : 0;
            return new WhisperFlowStatus(PhaseOf(state), time, pid);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static DictationPhase PhaseOf(string? state) => state switch
    {
        "recording" => DictationPhase.Recording,
        "transcribing" => DictationPhase.Transcribing,
        "done" => DictationPhase.Done,
        "empty" => DictationPhase.NoSpeech,
        "error" => DictationPhase.Failed,
        _ => DictationPhase.Idle,
    };

    /// <summary>
    /// What to show now: a recording or transcription while its WhisperFlow process still runs, a
    /// result for <see cref="ResultDuration"/>, otherwise nothing.
    /// </summary>
    public static DictationPhase Effective(WhisperFlowStatus? status, DateTimeOffset now, Func<int, bool> isRunning)
    {
        if (status is null)
        {
            return DictationPhase.Idle;
        }

        var age = now - status.At;
        return status.Phase switch
        {
            DictationPhase.Recording or DictationPhase.Transcribing
                when age < MaxActiveAge && (status.ProcessId <= 0 || isRunning(status.ProcessId)) => status.Phase,
            DictationPhase.Done or DictationPhase.NoSpeech or DictationPhase.Failed
                when age < ResultDuration => status.Phase,
            _ => DictationPhase.Idle,
        };
    }

    /// <summary>When <see cref="Effective"/> may change without a new file write; null when it cannot.</summary>
    public static TimeSpan? NextCheck(WhisperFlowStatus? status, DateTimeOffset now) => status?.Phase switch
    {
        // A killed WhisperFlow never writes again: look at its process now and then.
        DictationPhase.Recording or DictationPhase.Transcribing => TimeSpan.FromSeconds(5),
        DictationPhase.Done or DictationPhase.NoSpeech or DictationPhase.Failed when now - status.At < ResultDuration =>
            status.At + ResultDuration - now,
        _ => null,
    };

    public static bool IsRunning(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }
}

/// <summary>
/// Follows WhisperFlow's status file with a file watcher while the integration is on (Settings,
/// Modules); off, nothing is read. A timer runs only to end a result's few seconds, and every few
/// seconds during a recording to notice a WhisperFlow that was closed mid-way.
/// </summary>
public sealed class WhisperFlowService : IDisposable
{
    private readonly string _path;
    private readonly TimeProvider _time;
    private readonly Func<int, bool> _isRunning;
    private readonly Lock _gate = new();
    private FileSystemWatcher? _watcher;
    private ITimer? _timer;
    private WhisperFlowStatus? _status;
    private DictationPhase _phase;
    private bool _enabled;

    public WhisperFlowService(string path, TimeProvider? time = null, Func<int, bool>? isRunning = null)
    {
        _path = path;
        _time = time ?? TimeProvider.System;
        _isRunning = isRunning ?? WhisperFlowStatusFile.IsRunning;
    }

    /// <summary>Raised on a background thread when the phase to show changes.</summary>
    public event Action<DictationPhase>? Changed;

    public DictationPhase Phase => _phase;

    public void Configure(bool enabled)
    {
        lock (_gate)
        {
            if (enabled == _enabled)
            {
                return;
            }

            _enabled = enabled;
            _watcher?.Dispose();
            _watcher = null;
            if (enabled)
            {
                Watch();
            }
        }

        if (enabled)
        {
            Reload();
        }
        else
        {
            Stop();
        }
    }

    public void Dispose() => Stop();

    private void Stop()
    {
        lock (_gate)
        {
            _enabled = false;
            _watcher?.Dispose();
            _watcher = null;
            _timer?.Dispose();
            _timer = null;
            _status = null;
        }

        Publish(DictationPhase.Idle);
    }

    private void Watch()
    {
        try
        {
            var dir = Path.GetDirectoryName(_path)!;
            Directory.CreateDirectory(dir);
            _watcher = new FileSystemWatcher(dir, Path.GetFileName(_path))
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.CreationTime,
            };
            _watcher.Changed += (_, _) => Reload();
            _watcher.Created += (_, _) => Reload();
            _watcher.Renamed += (_, _) => Reload();
            _watcher.EnableRaisingEvents = true;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            Log.Debug("whisperflow", "Status file not watched", new { error = ex.GetType().Name });
        }
    }

    private void Reload()
    {
        if (!TryLoad(out var status))
        {
            return;
        }

        lock (_gate)
        {
            if (!_enabled)
            {
                return;
            }

            // A rename raises several notifications for one write; an older write never wins.
            if (status is not null && _status is not null && status.At < _status.At)
            {
                return;
            }

            _status = status;
        }

        Evaluate();
    }

    private void Evaluate()
    {
        DictationPhase phase;
        lock (_gate)
        {
            if (!_enabled)
            {
                return;
            }

            var now = _time.GetUtcNow();
            phase = WhisperFlowStatusFile.Effective(_status, now, _isRunning);
            _timer?.Dispose();
            _timer = WhisperFlowStatusFile.NextCheck(_status, now) is { } due && phase != DictationPhase.Idle
                ? _time.CreateTimer(_ => Evaluate(), null, due, Timeout.InfiniteTimeSpan)
                : null;
        }

        Publish(phase);
    }

    private void Publish(DictationPhase phase)
    {
        lock (_gate)
        {
            if (phase == _phase)
            {
                return;
            }

            _phase = phase;
        }

        Changed?.Invoke(phase);
    }

    /// <summary>The file's status (null once it is gone); false when it could not be read, which keeps the last one.</summary>
    private bool TryLoad(out WhisperFlowStatus? status)
    {
        status = null;
        // WhisperFlow replaces the file in one move; a read can still meet it locked for a moment.
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                if (!File.Exists(_path))
                {
                    return true;
                }

                status = WhisperFlowStatusFile.Parse(File.ReadAllText(_path));
                return status is not null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(15);
            }
        }

        return false;
    }
}

/// <summary>The short label beside the microphone mark, and its spoken form.</summary>
public static class DictationText
{
    public static string Label(DictationPhase phase) => phase switch
    {
        DictationPhase.Recording => "WhisperFlow",
        DictationPhase.Transcribing => "Transcribing…",
        DictationPhase.Done => "Inserted",
        DictationPhase.NoSpeech => "No speech",
        DictationPhase.Failed => "Failed",
        _ => string.Empty,
    };

    public static string Description(DictationPhase phase) => phase switch
    {
        DictationPhase.Recording => "WhisperFlow is recording",
        DictationPhase.Transcribing => "WhisperFlow is transcribing",
        DictationPhase.Done => "WhisperFlow inserted the text",
        DictationPhase.NoSpeech => "WhisperFlow heard no speech",
        DictationPhase.Failed => "WhisperFlow failed, see whisperflow.log",
        _ => string.Empty,
    };
}
