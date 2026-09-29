using DeveloperIsland.Core.Models;

namespace DeveloperIsland.Platform.Media;

/// <summary>Plays a fake queue for <c>--demo</c>. Controls work, and "Next" triggers a song-change event.</summary>
internal sealed class DemoMediaSource : IMediaSource
{
    private static readonly (string Title, string Artist, string Album, int Seconds, string Art)[] Queue =
    [
        ("Weightless Hours", "Lumen Drift", "Night Shift", 231, "demo-art-1.png"),
        ("Carbon Morning", "Oda Veil", "Low Tide", 198, "demo-art-2.png"),
        ("Signal / Noise", "The Quiet Arcade", "Afterimage", 264, "demo-art-3.png"),
    ];

    private readonly string _assetDirectory;
    private MediaSnapshot _snapshot = MediaSnapshot.Empty;
    private int _index;
    private ITimer? _advance;

    public DemoMediaSource(string assetDirectory)
    {
        _assetDirectory = assetDirectory;
    }

    public event Action<MediaSnapshot>? Changed;

    public MediaSnapshot Snapshot => _snapshot;

    public Task StartAsync()
    {
        Load(0, TimeSpan.FromSeconds(102), MediaPlaybackState.Playing);

        // Show one song change a little after launch so the activity can be seen without clicking.
        _advance = TimeProvider.System.CreateTimer(_ => _ = NextAsync(), null, TimeSpan.FromSeconds(6), Timeout.InfiniteTimeSpan);
        return Task.CompletedTask;
    }

    public Task TogglePlayPauseAsync()
    {
        var now = DateTimeOffset.UtcNow;
        var playing = _snapshot.IsPlaying;
        Publish(_snapshot with
        {
            State = playing ? MediaPlaybackState.Paused : MediaPlaybackState.Playing,
            Position = _snapshot.EstimatePosition(now),
            PositionUpdatedAt = now,
        });
        return Task.CompletedTask;
    }

    public Task NextAsync()
    {
        Load((_index + 1) % Queue.Length, TimeSpan.Zero, MediaPlaybackState.Playing);
        return Task.CompletedTask;
    }

    public Task PreviousAsync()
    {
        Load((_index + Queue.Length - 1) % Queue.Length, TimeSpan.Zero, MediaPlaybackState.Playing);
        return Task.CompletedTask;
    }

    public Task SeekAsync(TimeSpan position)
    {
        Publish(_snapshot with { Position = position, PositionUpdatedAt = DateTimeOffset.UtcNow });
        return Task.CompletedTask;
    }

    public void Dispose() => _advance?.Dispose();

    private void Load(int index, TimeSpan position, MediaPlaybackState state)
    {
        _index = index;
        var track = Queue[index];
        byte[]? art = null;
        var path = Path.Combine(_assetDirectory, track.Art);
        if (File.Exists(path))
        {
            art = File.ReadAllBytes(path);
        }

        Publish(new MediaSnapshot
        {
            Title = track.Title,
            Artist = track.Artist,
            Album = track.Album,
            Thumbnail = art,
            State = state,
            Position = position,
            Duration = TimeSpan.FromSeconds(track.Seconds),
            PositionUpdatedAt = DateTimeOffset.UtcNow,
            CanPlayPause = true,
            CanNext = true,
            CanPrevious = true,
            CanSeek = true,
            SourceApp = "Demo",
        });
    }

    private void Publish(MediaSnapshot snapshot)
    {
        _snapshot = snapshot;
        Changed?.Invoke(snapshot);
    }
}
