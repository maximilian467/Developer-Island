using DeveloperIsland.Core.Diagnostics;
using DeveloperIsland.Core.Models;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace DeveloperIsland.Platform.Media;

/// <summary>A source of media state and transport controls (the system session or demo data).</summary>
internal interface IMediaSource : IMediaController, IDisposable
{
    MediaSnapshot Snapshot { get; }

    /// <summary>Raised on a background thread.</summary>
    event Action<MediaSnapshot>? Changed;

    Task StartAsync();
}

/// <summary>
/// Reads the current Windows media session (System Media Transport Controls), so it works with
/// Spotify, Apple Music, browsers, Media Player and any other SMTC-aware app. No app-specific APIs.
/// Event-driven: nothing polls. Which session to show is decided by <see cref="MediaSessionChooser"/>
/// (the audible one first), re-evaluated when sessions come, go or change playback state.
/// </summary>
internal sealed class MediaSessionService : IMediaSource
{
    private const int MaxThumbnailBytes = 4 * 1024 * 1024;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private GlobalSystemMediaTransportControlsSession? _session;
    private readonly List<GlobalSystemMediaTransportControlsSession> _watched = [];
    private MediaSnapshot _snapshot = MediaSnapshot.Empty;
    private string? _thumbnailTrackKey;
    private byte[]? _thumbnail;
    private CancellationTokenSource? _debounce;
    private bool _disposed;
    private volatile bool _rechoose = true;

    public event Action<MediaSnapshot>? Changed;

    public MediaSnapshot Snapshot => _snapshot;

    public async Task StartAsync()
    {
        try
        {
            _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            _manager.CurrentSessionChanged += (_, _) => ScheduleRefresh(rechoose: true);
            _manager.SessionsChanged += (_, _) => WatchSessions();
            WatchSessions();
            Log.Info("media", "Media sessions available");
        }
        catch (Exception ex)
        {
            // No SMTC (e.g. restricted environment): music stays idle, the app keeps working.
            Log.Warn("media", "Media sessions unavailable", ex: ex);
        }
    }

    public async Task TogglePlayPauseAsync() => await Try(s => s.TryTogglePlayPauseAsync().AsTask());

    public async Task NextAsync() => await Try(s => s.TrySkipNextAsync().AsTask());

    public async Task PreviousAsync() => await Try(s => s.TrySkipPreviousAsync().AsTask());

    public async Task SeekAsync(TimeSpan position) => await Try(s => s.TryChangePlaybackPositionAsync(position.Ticks).AsTask());

    public void Dispose()
    {
        _disposed = true;
        UnwatchSessions();
        Detach();
        _debounce?.Cancel();
    }

    private async Task Try(Func<GlobalSystemMediaTransportControlsSession, Task<bool>> action)
    {
        var session = _session;
        if (session is null)
        {
            return;
        }

        try
        {
            await action(session);
        }
        catch (Exception ex)
        {
            Log.Warn("media", "Media command failed", ex: ex);
        }
    }

    /// <summary>Follows playback changes of every session, so a player that starts playing is noticed.</summary>
    private void WatchSessions()
    {
        try
        {
            UnwatchSessions();
            foreach (var session in _manager?.GetSessions() ?? [])
            {
                session.PlaybackInfoChanged += OnPlaybackChanged;
                _watched.Add(session);
            }
        }
        catch (Exception ex)
        {
            Log.Warn("media", "Media sessions could not be listed", ex: ex);
        }

        ScheduleRefresh(rechoose: true);
    }

    private void UnwatchSessions()
    {
        foreach (var session in _watched)
        {
            try
            {
                session.PlaybackInfoChanged -= OnPlaybackChanged;
            }
            catch
            {
                // The owning app may already be gone.
            }
        }

        _watched.Clear();
    }

    private async Task<GlobalSystemMediaTransportControlsSession?> ChooseSessionAsync()
    {
        if (_manager is null)
        {
            return null;
        }

        var sessions = _manager.GetSessions().ToList();
        var candidates = new List<MediaSessionCandidate>(sessions.Count);
        foreach (var session in sessions)
        {
            var state = MediaPlaybackState.None;
            var hasTrack = false;
            try
            {
                state = MapState(session.GetPlaybackInfo()?.PlaybackStatus);
                var props = await session.TryGetMediaPropertiesAsync();
                hasTrack = !string.IsNullOrWhiteSpace(props?.Title);
            }
            catch
            {
                // A session that cannot be read is not a candidate.
            }

            candidates.Add(new MediaSessionCandidate(session.SourceAppUserModelId ?? string.Empty, state, hasTrack));
        }

        var index = MediaSessionChooser.Choose(candidates, _manager.GetCurrentSession()?.SourceAppUserModelId);
        return index >= 0 ? sessions[index] : null;
    }

    private void Attach(GlobalSystemMediaTransportControlsSession? session)
    {
        if (session is not null && _session is not null && session.SourceAppUserModelId == _session.SourceAppUserModelId)
        {
            return;
        }

        Detach();
        _session = session;
        if (session is not null)
        {
            session.MediaPropertiesChanged += OnSessionChanged;
            session.TimelinePropertiesChanged += OnSessionChanged;
            Log.Debug("media", "Following media session", new { app = FriendlyAppName(session.SourceAppUserModelId) });
        }
    }

    private void Detach()
    {
        if (_session is null)
        {
            return;
        }

        try
        {
            _session.MediaPropertiesChanged -= OnSessionChanged;
            _session.TimelinePropertiesChanged -= OnSessionChanged;
        }
        catch
        {
            // The owning app may already be gone.
        }

        _session = null;
    }

    private void OnSessionChanged(GlobalSystemMediaTransportControlsSession sender, object args) => ScheduleRefresh();

    /// <summary>Play or pause anywhere may change which session is shown.</summary>
    private void OnPlaybackChanged(GlobalSystemMediaTransportControlsSession sender, object args) => ScheduleRefresh(rechoose: true);

    /// <summary>Players fire bursts of events on a track change; coalesce them.</summary>
    private void ScheduleRefresh(bool rechoose = false)
    {
        if (rechoose)
        {
            _rechoose = true;
        }

        _debounce?.Cancel();
        var cts = new CancellationTokenSource();
        _debounce = cts;
        _ = Task.Delay(120, cts.Token).ContinueWith(
            async _ => await RefreshAsync(),
            cts.Token,
            TaskContinuationOptions.OnlyOnRanToCompletion,
            TaskScheduler.Default);
    }

    private async Task RefreshAsync()
    {
        if (_disposed)
        {
            return;
        }

        await _refreshLock.WaitAsync();
        try
        {
            if (_rechoose)
            {
                _rechoose = false;
                Attach(await ChooseSessionAsync());
            }

            var session = _session;
            var snapshot = session is null ? MediaSnapshot.Empty : await ReadAsync(session);
            Publish(snapshot);
        }
        catch (Exception ex)
        {
            // Usually the player quit between the event and the read: show nothing rather than a
            // stale track, and choose again on the next event.
            Log.Debug("media", "Media state could not be read", new { error = ex.GetType().Name });
            Detach();
            _rechoose = true;
            Publish(MediaSnapshot.Empty);
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    private void Publish(MediaSnapshot snapshot)
    {
        _snapshot = snapshot;
        Changed?.Invoke(snapshot);
    }

    private static MediaPlaybackState MapState(GlobalSystemMediaTransportControlsSessionPlaybackStatus? status) => status switch
    {
        GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing => MediaPlaybackState.Playing,
        GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused => MediaPlaybackState.Paused,
        GlobalSystemMediaTransportControlsSessionPlaybackStatus.Stopped => MediaPlaybackState.Stopped,
        _ => MediaPlaybackState.None,
    };

    private async Task<MediaSnapshot> ReadAsync(GlobalSystemMediaTransportControlsSession session)
    {
        var props = await session.TryGetMediaPropertiesAsync();
        var playback = session.GetPlaybackInfo();
        var timeline = session.GetTimelineProperties();

        var state = MapState(playback?.PlaybackStatus);

        var title = props?.Title;
        var artist = string.IsNullOrWhiteSpace(props?.Artist) ? props?.AlbumArtist : props.Artist;
        var trackKey = $"{title}\u0001{artist}\u0001{props?.AlbumTitle}";
        if (trackKey != _thumbnailTrackKey)
        {
            _thumbnailTrackKey = trackKey;
            _thumbnail = await ReadThumbnailAsync(props?.Thumbnail);
        }

        var duration = timeline is null ? TimeSpan.Zero : timeline.EndTime - timeline.StartTime;
        var controls = playback?.Controls;
        return new MediaSnapshot
        {
            Title = title,
            Artist = artist,
            Album = props?.AlbumTitle,
            Thumbnail = _thumbnail,
            State = state,
            Position = timeline is null ? TimeSpan.Zero : timeline.Position - timeline.StartTime,
            Duration = duration > TimeSpan.Zero ? duration : TimeSpan.Zero,
            PositionUpdatedAt = timeline?.LastUpdatedTime ?? DateTimeOffset.UtcNow,
            CanPlayPause = controls is not null && (controls.IsPlayPauseToggleEnabled || controls.IsPlayEnabled || controls.IsPauseEnabled),
            CanNext = controls?.IsNextEnabled ?? false,
            CanPrevious = controls?.IsPreviousEnabled ?? false,
            CanSeek = (controls?.IsPlaybackPositionEnabled ?? false) && duration > TimeSpan.Zero,
            SourceApp = FriendlyAppName(session.SourceAppUserModelId),
        };
    }

    private static async Task<byte[]?> ReadThumbnailAsync(IRandomAccessStreamReference? reference)
    {
        if (reference is null)
        {
            return null;
        }

        try
        {
            using var stream = await reference.OpenReadAsync();
            if (stream.Size == 0 || stream.Size > MaxThumbnailBytes)
            {
                return null;
            }

            var buffer = new byte[stream.Size];
            using var reader = new DataReader(stream.GetInputStreamAt(0));
            await reader.LoadAsync((uint)stream.Size);
            reader.ReadBytes(buffer);
            return buffer;
        }
        catch (Exception ex)
        {
            Log.Debug("media", "Artwork unavailable", new { error = ex.GetType().Name });
            return null;
        }
    }

    /// <summary>Maps an AppUserModelID or executable to a readable name.</summary>
    internal static string? FriendlyAppName(string? aumid)
    {
        if (string.IsNullOrWhiteSpace(aumid))
        {
            return null;
        }

        var id = aumid.ToLowerInvariant();
        if (id.Contains("spotify")) return "Spotify";
        if (id.Contains("applemusic") || id.Contains("appleinc.")) return "Apple Music";
        if (id.Contains("zunemusic") || id.Contains("mediaplayer")) return "Media Player";
        if (id.Contains("msedge")) return "Microsoft Edge";
        if (id.Contains("chrome")) return "Google Chrome";
        if (id.Contains("firefox")) return "Firefox";
        if (id.Contains("brave")) return "Brave";
        if (id.Contains("vlc")) return "VLC";
        if (id.Contains("tidal")) return "TIDAL";
        if (id.Contains("deezer")) return "Deezer";

        var name = aumid;
        var bang = name.IndexOf('!');
        if (bang > 0)
        {
            name = name[..bang];
        }

        name = Path.GetFileNameWithoutExtension(name.Split('\\', '/').Last());
        return name.Length > 0 ? char.ToUpperInvariant(name[0]) + name[1..] : null;
    }
}
