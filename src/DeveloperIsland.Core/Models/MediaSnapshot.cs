namespace DeveloperIsland.Core.Models;

public enum MediaPlaybackState
{
    None,
    Stopped,
    Paused,
    Playing,
}

/// <summary>State of the current system media session (any SMTC-compatible player).</summary>
public sealed record MediaSnapshot
{
    public static readonly MediaSnapshot Empty = new();

    public string? Title { get; init; }

    public string? Artist { get; init; }

    public string? Album { get; init; }

    /// <summary>Encoded thumbnail image (PNG/JPEG) as delivered by the player.</summary>
    public byte[]? Thumbnail { get; init; }

    public MediaPlaybackState State { get; init; }

    public TimeSpan Position { get; init; }

    public TimeSpan Duration { get; init; }

    public DateTimeOffset PositionUpdatedAt { get; init; }

    public bool CanPlayPause { get; init; }

    public bool CanNext { get; init; }

    public bool CanPrevious { get; init; }

    public bool CanSeek { get; init; }

    public string? SourceApp { get; init; }

    public bool HasTrack => !string.IsNullOrWhiteSpace(Title);

    public bool IsPlaying => State == MediaPlaybackState.Playing;

    /// <summary>Identity of the track, used to detect song changes.</summary>
    public string TrackKey => $"{Title}\u0001{Artist}\u0001{Album}";

    /// <summary>Position extrapolated from the last timeline update while playing.</summary>
    public TimeSpan EstimatePosition(DateTimeOffset now)
    {
        var position = Position;
        if (IsPlaying && PositionUpdatedAt != default)
        {
            position += now - PositionUpdatedAt;
        }

        if (position < TimeSpan.Zero)
        {
            return TimeSpan.Zero;
        }

        return Duration > TimeSpan.Zero && position > Duration ? Duration : position;
    }
}

/// <summary>Transport controls for the current media session.</summary>
public interface IMediaController
{
    Task TogglePlayPauseAsync();

    Task NextAsync();

    Task PreviousAsync();

    Task SeekAsync(TimeSpan position);
}
