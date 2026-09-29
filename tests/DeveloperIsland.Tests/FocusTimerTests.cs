using DeveloperIsland.Core.Focus;
using DeveloperIsland.Core.Storage;
using Microsoft.Extensions.Time.Testing;

namespace DeveloperIsland.Tests;

public class FocusTimerTests
{
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 9, 28, 9, 0, 0, TimeSpan.Zero));

    [Fact]
    public void Counts_down_while_running()
    {
        using var timer = new FocusTimer(_time, TimeZoneInfo.Utc);
        timer.Start(TimeSpan.FromMinutes(25));

        _time.Advance(TimeSpan.FromMinutes(10));

        Assert.Equal(FocusState.Running, timer.Snapshot.State);
        Assert.Equal(TimeSpan.FromMinutes(15), timer.Snapshot.Remaining);
        Assert.Equal(0.4, timer.Snapshot.Progress, 3);
    }

    [Fact]
    public void Completes_and_records_the_full_session()
    {
        using var timer = new FocusTimer(_time, TimeZoneInfo.Utc);
        FocusSessionRecord? ended = null;
        timer.SessionEnded += r => ended = r;
        timer.Start(TimeSpan.FromMinutes(25));

        _time.Advance(TimeSpan.FromMinutes(25) + TimeSpan.FromSeconds(1));

        Assert.Equal(FocusState.Idle, timer.Snapshot.State);
        Assert.NotNull(ended);
        Assert.True(ended!.Completed);
        Assert.Equal(25 * 60, ended.FocusedSeconds);
        Assert.Equal(new DateOnly(2026, 9, 28), ended.Day);
    }

    [Fact]
    public void Paused_time_does_not_count()
    {
        using var timer = new FocusTimer(_time, TimeZoneInfo.Utc);
        timer.Start(TimeSpan.FromMinutes(50));
        _time.Advance(TimeSpan.FromMinutes(5));

        timer.Pause();
        _time.Advance(TimeSpan.FromMinutes(30));
        Assert.Equal(TimeSpan.FromMinutes(45), timer.Snapshot.Remaining);

        timer.Resume();
        _time.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal(TimeSpan.FromMinutes(40), timer.Snapshot.Remaining);
    }

    [Fact]
    public void Stopping_early_records_the_focused_part()
    {
        using var timer = new FocusTimer(_time, TimeZoneInfo.Utc);
        FocusSessionRecord? ended = null;
        timer.SessionEnded += r => ended = r;
        timer.Start(TimeSpan.FromMinutes(90));
        _time.Advance(TimeSpan.FromMinutes(12));

        timer.Stop();

        Assert.NotNull(ended);
        Assert.False(ended!.Completed);
        Assert.Equal(12 * 60, ended.FocusedSeconds);
        Assert.Equal(90 * 60, ended.PlannedSeconds);
    }

    [Fact]
    public void Sessions_shorter_than_a_minute_are_not_recorded()
    {
        using var timer = new FocusTimer(_time, TimeZoneInfo.Utc);
        var count = 0;
        timer.SessionEnded += _ => count++;
        timer.Start(TimeSpan.FromMinutes(25));
        _time.Advance(TimeSpan.FromSeconds(20));

        timer.Stop();

        Assert.Equal(0, count);
        Assert.Equal(FocusState.Idle, timer.Snapshot.State);
    }

    [Fact]
    public void Ticks_once_per_second_only_while_running()
    {
        using var timer = new FocusTimer(_time, TimeZoneInfo.Utc);
        var changes = 0;
        timer.Changed += _ => changes++;
        timer.Start(TimeSpan.FromMinutes(25));
        changes = 0;

        _time.Advance(TimeSpan.FromSeconds(3) + TimeSpan.FromMilliseconds(10));
        Assert.Equal(3, changes);

        timer.Pause();
        changes = 0;
        _time.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal(0, changes);
    }

    [Fact]
    public void Restore_continues_a_partly_finished_session()
    {
        using var timer = new FocusTimer(_time, TimeZoneInfo.Utc);

        timer.Restore(TimeSpan.FromMinutes(50), TimeSpan.FromSeconds(42 * 60 + 13));

        Assert.Equal(FocusState.Running, timer.Snapshot.State);
        Assert.Equal(TimeSpan.FromSeconds(42 * 60 + 13), timer.Snapshot.Remaining);
    }

    [Fact]
    public void Focus_history_sums_minutes_and_sessions_per_day()
    {
        using var db = UsageDatabase.OpenInMemory();
        var history = new FocusHistory(db, _time, TimeZoneInfo.Utc);
        using var timer = new FocusTimer(_time, TimeZoneInfo.Utc);
        timer.SessionEnded += history.Record;

        timer.Start(TimeSpan.FromMinutes(25));
        _time.Advance(TimeSpan.FromMinutes(26));
        timer.Start(TimeSpan.FromMinutes(50));
        _time.Advance(TimeSpan.FromMinutes(10));
        timer.Stop();

        var today = history.GetToday();
        Assert.Equal(2, today.Sessions);
        Assert.Equal(35 * 60, today.FocusedSeconds);
    }
}
