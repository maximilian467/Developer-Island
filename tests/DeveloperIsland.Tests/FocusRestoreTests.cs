using DeveloperIsland.Core.Focus;
using DeveloperIsland.Core.Storage;
using Microsoft.Extensions.Time.Testing;

namespace DeveloperIsland.Tests;

public class FocusRestoreTests
{
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 9, 28, 9, 0, 0, TimeSpan.Zero));

    [Fact]
    public void Running_session_keeps_counting_across_a_restart()
    {
        FocusSessionState saved;
        using (var before = new FocusTimer(_time, TimeZoneInfo.Utc))
        {
            before.Start(TimeSpan.FromMinutes(25));
            _time.Advance(TimeSpan.FromMinutes(5));
            saved = before.Capture()!;
        }

        _time.Advance(TimeSpan.FromMinutes(3)); // app closed
        using var after = new FocusTimer(_time, TimeZoneInfo.Utc);
        after.RestoreSession(saved);

        Assert.Equal(FocusState.Running, after.Snapshot.State);
        Assert.Equal(TimeSpan.FromMinutes(17), after.Snapshot.Remaining);
    }

    [Fact]
    public void Paused_session_stays_paused_with_its_remaining_time()
    {
        using var before = new FocusTimer(_time, TimeZoneInfo.Utc);
        before.Start(TimeSpan.FromMinutes(50));
        _time.Advance(TimeSpan.FromMinutes(10));
        before.Pause();
        var saved = before.Capture()!;

        _time.Advance(TimeSpan.FromHours(2));
        using var after = new FocusTimer(_time, TimeZoneInfo.Utc);
        after.RestoreSession(saved);

        Assert.Equal(FocusState.Paused, after.Snapshot.State);
        Assert.Equal(TimeSpan.FromMinutes(40), after.Snapshot.Remaining);
        after.Resume();
        _time.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(TimeSpan.FromMinutes(39), after.Snapshot.Remaining);
    }

    [Fact]
    public void Session_that_ran_out_while_closed_is_recorded_as_completed_at_its_end()
    {
        using var before = new FocusTimer(_time, TimeZoneInfo.Utc);
        before.Start(TimeSpan.FromMinutes(25));
        var saved = before.Capture()!;

        _time.Advance(TimeSpan.FromHours(1));
        using var after = new FocusTimer(_time, TimeZoneInfo.Utc);
        FocusSessionRecord? ended = null;
        after.SessionEnded += r => ended = r;
        after.RestoreSession(saved);

        Assert.Equal(FocusState.Idle, after.Snapshot.State);
        Assert.NotNull(ended);
        Assert.True(ended!.Completed);
        Assert.Equal(25 * 60, ended.FocusedSeconds);
        Assert.Equal(saved.EndsAt, ended.EndedAt);
    }

    [Fact]
    public void Idle_timer_captures_nothing()
    {
        using var timer = new FocusTimer(_time, TimeZoneInfo.Utc);
        timer.Start(TimeSpan.FromMinutes(25));
        timer.Stop();

        Assert.Null(timer.Capture());
    }

    [Fact]
    public void Store_round_trips_and_clears()
    {
        var path = Path.Combine(TestData.TempDirectory(), "focus-session.json");
        var store = new FocusSessionStore(path);
        var state = new FocusSessionState(FocusState.Paused, TimeSpan.FromMinutes(50), _time.GetUtcNow(), null, TimeSpan.FromMinutes(12));

        store.Save(state);
        Assert.Equal(state, store.Load());

        store.Save(null);
        Assert.False(File.Exists(path));
        Assert.Null(store.Load());
    }

    [Fact]
    public void Corrupt_store_starts_idle()
    {
        var path = Path.Combine(TestData.TempDirectory(), "focus-session.json");
        File.WriteAllText(path, "{ not json");

        Assert.Null(new FocusSessionStore(path).Load());
    }
}
