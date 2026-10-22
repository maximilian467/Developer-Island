using System.Text.Json;
using DeveloperIsland.Core.Diagnostics;
using DeveloperIsland.Core.Usage;

namespace DeveloperIsland.Core.Updates;

/// <summary>The newest release seen by the last successful check, as saved between runs.</summary>
public sealed record SavedRelease(string Version, Uri NotesUrl, ReleaseAsset Setup, ReleaseAsset? Checksums, ReleaseAsset? Signature)
{
    public static SavedRelease From(ReleaseInfo release) =>
        new(release.Version.ToString(), release.NotesUrl, release.Setup, release.Checksums, release.Signature);

    public ReleaseInfo? ToRelease() =>
        ReleaseVersion.TryParse(Version, out var version) ? new ReleaseInfo(version, NotesUrl, Setup, Checksums, Signature) : null;
}

/// <summary>When the update check last ran, and what it found.</summary>
/// <param name="LastCheckedAt">The last check that got an answer from GitHub.</param>
/// <param name="LastAttemptAt">The last check, answered or not.</param>
/// <param name="Latest">The newest release then; null when there was none.</param>
public sealed record UpdateState(DateTimeOffset? LastCheckedAt, DateTimeOffset? LastAttemptAt, SavedRelease? Latest)
{
    public static readonly UpdateState Empty = new(null, null, null);
}

/// <summary>
/// Keeps <see cref="UpdateState"/> in its own small file (not in the settings, so a daily check never
/// rewrites and re-applies them).
/// </summary>
public sealed class UpdateStateStore
{
    private readonly string? _path;
    private readonly object _gate = new();
    private UpdateState _state;

    /// <param name="path">JSON file; null keeps the state in memory only (tests).</param>
    public UpdateStateStore(string? path)
    {
        _path = path;
        _state = Load(path);
    }

    public UpdateState State
    {
        get
        {
            lock (_gate)
            {
                return _state;
            }
        }
    }

    public void Save(UpdateState state)
    {
        lock (_gate)
        {
            _state = state;
            if (_path is null)
            {
                return;
            }

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                var temp = _path + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(state, CoreJsonContext.Default.UpdateState));
                File.Move(temp, _path, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Debug("update", "Update state could not be saved", new { error = ex.GetType().Name });
            }
        }
    }

    private static UpdateState Load(string? path)
    {
        try
        {
            if (path is not null && File.Exists(path))
            {
                return JsonSerializer.Deserialize(File.ReadAllText(path), CoreJsonContext.Default.UpdateState) ?? UpdateState.Empty;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            Log.Debug("update", "Update state unreadable; starting fresh", new { error = ex.GetType().Name });
        }

        return UpdateState.Empty;
    }
}
