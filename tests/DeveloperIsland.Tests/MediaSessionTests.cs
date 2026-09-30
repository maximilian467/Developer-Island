using DeveloperIsland.Core.Models;

namespace DeveloperIsland.Tests;

public class MediaSessionTests
{
    private static MediaSessionCandidate Spotify(MediaPlaybackState state) => new("Spotify.exe", state, HasTrack: true);

    private static MediaSessionCandidate Chrome(MediaPlaybackState state, bool hasTrack = true) => new("Chrome", state, hasTrack);

    [Fact]
    public void No_sessions_means_no_player()
    {
        Assert.Equal(-1, MediaSessionChooser.Choose([], null));
        Assert.Equal(-1, MediaSessionChooser.Choose([], "Spotify.exe"));
    }

    [Fact]
    public void A_playing_current_session_is_shown()
    {
        Assert.Equal(1, MediaSessionChooser.Choose([Chrome(MediaPlaybackState.Playing), Spotify(MediaPlaybackState.Playing)], "Spotify.exe"));
    }

    [Fact]
    public void A_playing_session_beats_a_paused_current_one()
    {
        Assert.Equal(1, MediaSessionChooser.Choose([Spotify(MediaPlaybackState.Paused), Chrome(MediaPlaybackState.Playing)], "Spotify.exe"));
    }

    [Fact]
    public void Without_a_current_session_a_playing_browser_is_shown()
    {
        Assert.Equal(0, MediaSessionChooser.Choose([Chrome(MediaPlaybackState.Playing)], null));
    }

    [Fact]
    public void Paused_current_session_stays_when_nothing_plays()
    {
        Assert.Equal(0, MediaSessionChooser.Choose([Spotify(MediaPlaybackState.Paused), Chrome(MediaPlaybackState.Paused)], "Spotify.exe"));
    }

    [Fact]
    public void Silent_browser_tabs_without_a_title_are_skipped()
    {
        Assert.Equal(1, MediaSessionChooser.Choose([Chrome(MediaPlaybackState.Playing, hasTrack: false), Spotify(MediaPlaybackState.Paused)], null));
        Assert.Equal(-1, MediaSessionChooser.Choose([Chrome(MediaPlaybackState.Stopped, hasTrack: false)], null));
    }

    [Fact]
    public void Track_change_ignores_pause_seek_and_artwork()
    {
        var song = new MediaSnapshot { Title = "Song", Artist = "Band", State = MediaPlaybackState.Playing };

        Assert.False(MediaSessionChooser.IsNewTrack(song, song with { State = MediaPlaybackState.Paused }));
        Assert.False(MediaSessionChooser.IsNewTrack(song, song with { Position = TimeSpan.FromMinutes(2), Thumbnail = [1, 2, 3] }));
        Assert.True(MediaSessionChooser.IsNewTrack(song, song with { Title = "Next song" }));
    }

    [Fact]
    public void Player_closing_is_not_a_track_change_and_empties_the_view()
    {
        var song = new MediaSnapshot { Title = "Song", Artist = "Band", State = MediaPlaybackState.Playing };

        Assert.False(MediaSessionChooser.IsNewTrack(song, MediaSnapshot.Empty));
        Assert.False(MediaSnapshot.Empty.HasTrack);
        Assert.True(MediaSessionChooser.IsNewTrack(MediaSnapshot.Empty, song));
    }

    [Fact]
    public void Browser_sessions_without_artist_or_artwork_are_still_tracks()
    {
        var video = new MediaSnapshot { Title = "Conference talk", State = MediaPlaybackState.Playing, SourceApp = "Google Chrome" };

        Assert.True(video.HasTrack);
        Assert.Null(video.Thumbnail);
    }
}
