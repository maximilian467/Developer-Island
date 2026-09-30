namespace DeveloperIsland.Core.Models;

/// <summary>One system media session as far as choosing what to show is concerned.</summary>
public sealed record MediaSessionCandidate(string SourceAppId, MediaPlaybackState State, bool HasTrack);

/// <summary>
/// Windows names one "current" media session, but it can be missing (a browser tab went quiet) or
/// stale (paused while another app plays). The island shows what is audible first.
/// </summary>
public static class MediaSessionChooser
{
    /// <returns>The index of the session to show, or -1 for none.</returns>
    public static int Choose(IReadOnlyList<MediaSessionCandidate> sessions, string? currentSourceAppId)
    {
        var current = -1;
        for (var i = 0; i < sessions.Count; i++)
        {
            if (current < 0 && currentSourceAppId is not null && sessions[i].SourceAppId == currentSourceAppId)
            {
                current = i;
            }
        }

        // The current session wins while it plays, or when nothing else does.
        if (current >= 0 && sessions[current].State == MediaPlaybackState.Playing)
        {
            return current;
        }

        for (var i = 0; i < sessions.Count; i++)
        {
            if (sessions[i].State == MediaPlaybackState.Playing && sessions[i].HasTrack)
            {
                return i;
            }
        }

        if (current >= 0)
        {
            return current;
        }

        for (var i = 0; i < sessions.Count; i++)
        {
            if (sessions[i].HasTrack)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>A different song started (not a pause, seek or artwork update).</summary>
    public static bool IsNewTrack(MediaSnapshot previous, MediaSnapshot next) =>
        next.HasTrack && next.TrackKey != previous.TrackKey;
}
