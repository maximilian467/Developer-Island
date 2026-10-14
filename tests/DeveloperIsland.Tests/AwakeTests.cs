using DeveloperIsland.Core.Awake;
using Microsoft.Extensions.Time.Testing;

namespace DeveloperIsland.Tests;

public class AwakeTests
{
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 14, 9, 0, 0, TimeSpan.Zero));
    private readonly FakePower _power = new();
    private readonly FakeBackstop _backstop = new();
    private readonly AwakeJournalStore _store = new(Path.Combine(TestData.TempDirectory(), "awake-session.json"));

    [Fact]
    public void Start_journals_the_original_lid_values_before_changing_them()
    {
        AwakeJournal? journalAtWrite = null;
        _power.OnWrite = () => journalAtWrite ??= _store.Load();
        using var awake = Controller();

        awake.StartTimed(TimeSpan.FromHours(4));

        Assert.NotNull(journalAtWrite);
        Assert.Equal(new LidChange(_power.Scheme, 1, 1), journalAtWrite!.Lid);
        Assert.Equal((0u, 0u), _power.Lid);
        Assert.True(_power.KeptAwake);
        Assert.Equal(AwakeLid.Protected, awake.Snapshot.Lid);
        Assert.Equal(_time.GetUtcNow() + TimeSpan.FromHours(4), _backstop.ScheduledAt);
    }

    [Fact]
    public void Ending_restores_the_original_values_and_clears_everything()
    {
        _power.Lid = (1, 2);
        using var awake = Controller();
        AwakeEndReason? ended = null;
        awake.Ended += r => ended = r;
        awake.StartTimed(TimeSpan.FromHours(1));

        awake.Stop();

        Assert.Equal((1u, 2u), _power.Lid);
        Assert.False(_power.KeptAwake);
        Assert.Null(_store.Load());
        Assert.Null(_backstop.ScheduledAt);
        Assert.Equal(AwakeEndReason.Stopped, ended);
        Assert.False(awake.Snapshot.IsActive);
    }

    [Fact]
    public void A_timed_session_completes_at_its_end_time()
    {
        using var awake = Controller();
        AwakeEndReason? ended = null;
        awake.Ended += r => ended = r;
        awake.StartTimed(TimeSpan.FromHours(1));

        _time.Advance(TimeSpan.FromMinutes(30));
        Assert.Equal(TimeSpan.FromMinutes(30), awake.Snapshot.Remaining);
        Assert.Equal(0.5, awake.Snapshot.Progress, 3);

        _time.Advance(TimeSpan.FromMinutes(30) + TimeSpan.FromSeconds(1));

        Assert.Equal(AwakeEndReason.Completed, ended);
        Assert.False(awake.Snapshot.IsActive);
        Assert.Equal((1u, 1u), _power.Lid);
    }

    [Fact]
    public void A_lid_value_the_user_changed_during_the_session_is_left_alone()
    {
        using var awake = Controller();
        awake.StartIndefinite();
        _power.Lid = (3, 0); // the user set "shut down" on mains meanwhile

        awake.Stop();

        Assert.Equal((3u, 1u), _power.Lid);
    }

    [Fact]
    public void An_already_do_nothing_lid_is_not_touched()
    {
        _power.Lid = (0, 0);
        using var awake = Controller();

        awake.StartTimed(TimeSpan.FromHours(1));
        awake.Stop();

        Assert.Empty(_power.Writes);
        Assert.Equal((0u, 0u), _power.Lid);
    }

    [Fact]
    public void A_refused_lid_setting_still_keeps_the_pc_out_of_idle_sleep()
    {
        _power.RefuseWrites = true;
        using var awake = Controller();

        awake.StartTimed(TimeSpan.FromHours(1));

        Assert.True(awake.Snapshot.IsActive);
        Assert.Equal(AwakeLid.Failed, awake.Snapshot.Lid);
        Assert.True(_power.KeptAwake);
        Assert.Equal((1u, 1u), _power.Lid);
        Assert.Null(_store.Load()!.Lid);
    }

    [Fact]
    public void Without_a_journal_the_lid_is_never_changed()
    {
        var blocker = Path.Combine(TestData.TempDirectory(), "blocker");
        File.WriteAllText(blocker, "a file where the journal folder would be");
        using var awake = new AwakeController(_power, _backstop, new AwakeJournalStore(Path.Combine(blocker, "awake-session.json")), _time);

        awake.StartTimed(TimeSpan.FromHours(1));

        Assert.Empty(_power.Writes);
        Assert.Equal(AwakeLid.Failed, awake.Snapshot.Lid);
    }

    [Fact]
    public void A_session_still_running_after_a_restart_resumes()
    {
        var first = Controller();
        first.StartTimed(TimeSpan.FromHours(4));
        first.Dispose(); // a crash or restart: nothing is undone
        _power.KeptAwake = false; // Windows drops the power request with the process
        _backstop.ScheduledAt = null;
        _time.Advance(TimeSpan.FromHours(1));

        using var second = Controller();
        var action = second.Recover();

        Assert.Equal(AwakeStartupAction.Resume, action);
        Assert.True(second.Snapshot.IsActive);
        Assert.Equal(TimeSpan.FromHours(3), second.Snapshot.Remaining);
        Assert.Equal(AwakeLid.Protected, second.Snapshot.Lid);
        Assert.True(_power.KeptAwake);
        Assert.NotNull(_backstop.ScheduledAt);
    }

    [Fact]
    public void A_session_that_ended_while_the_app_was_closed_is_restored()
    {
        var first = Controller();
        first.StartTimed(TimeSpan.FromHours(1));
        first.Dispose();
        _time.Advance(TimeSpan.FromHours(2));

        using var second = Controller();
        var action = second.Recover();

        Assert.Equal(AwakeStartupAction.Restore, action);
        Assert.False(second.Snapshot.IsActive);
        Assert.Equal((1u, 1u), _power.Lid);
        Assert.Null(_store.Load());
    }

    [Fact]
    public void An_indefinite_session_has_no_backstop_and_resumes_after_days()
    {
        var first = Controller();
        first.StartIndefinite();
        Assert.Null(_backstop.ScheduledAt);
        first.Dispose();
        _time.Advance(TimeSpan.FromDays(3));

        using var second = Controller();

        Assert.Equal(AwakeStartupAction.Resume, second.Recover());
        Assert.True(second.Snapshot.IsIndefinite);
        Assert.Equal(TimeSpan.FromDays(3), second.Snapshot.Elapsed);
    }

    [Fact]
    public void The_backstop_restores_only_an_ended_session()
    {
        var awake = Controller();
        awake.StartTimed(TimeSpan.FromHours(1));
        awake.Dispose();

        Assert.False(AwakeController.RestoreIfEnded(_store, _power, _backstop, _time.GetUtcNow() + TimeSpan.FromMinutes(30)));
        Assert.Equal((0u, 0u), _power.Lid);

        Assert.True(AwakeController.RestoreIfEnded(_store, _power, _backstop, _time.GetUtcNow() + TimeSpan.FromHours(1)));
        Assert.Equal((1u, 1u), _power.Lid);
        Assert.Null(_store.Load());
    }

    [Fact]
    public void A_restore_that_fails_keeps_the_journal_as_an_ended_session()
    {
        var awake = Controller();
        awake.StartIndefinite();
        _power.RefuseWrites = true;

        awake.Stop();
        awake.Dispose();

        var kept = _store.Load();
        Assert.NotNull(kept);
        Assert.Equal(AwakeStartupAction.Restore, AwakeRules.OnStartup(kept, _time.GetUtcNow()));

        _power.RefuseWrites = false;
        using var next = Controller();
        Assert.Equal(AwakeStartupAction.Restore, next.Recover());
        Assert.Equal((1u, 1u), _power.Lid);
    }

    [Fact]
    public void Extending_moves_the_end_and_the_backstop()
    {
        using var awake = Controller();
        awake.StartTimed(TimeSpan.FromHours(1));
        _time.Advance(TimeSpan.FromMinutes(30));

        awake.Extend(TimeSpan.FromHours(1));

        Assert.Equal(TimeSpan.FromMinutes(90), awake.Snapshot.Remaining);
        Assert.Equal(TimeSpan.FromHours(2), awake.Snapshot.Planned);
        Assert.Equal(_time.GetUtcNow() + TimeSpan.FromMinutes(90), _backstop.ScheduledAt);
    }

    [Fact]
    public void Another_preset_while_running_keeps_the_original_values()
    {
        using var awake = Controller();
        awake.StartTimed(TimeSpan.FromHours(1));

        awake.StartIndefinite();
        awake.StartTimed(TimeSpan.FromHours(8));
        awake.Stop();

        Assert.Equal((1u, 1u), _power.Lid);
        Assert.Equal(2, _power.Writes.Count); // one change, one restore
    }

    [Fact]
    public void Low_battery_ends_the_session()
    {
        using var awake = Controller();
        AwakeEndReason? ended = null;
        awake.Ended += r => ended = r;
        awake.StartIndefinite();

        awake.OnBattery(12, charging: false, floor: 10);
        Assert.True(awake.Snapshot.IsActive);

        awake.OnBattery(9, charging: false, floor: 10);

        Assert.Equal(AwakeEndReason.Battery, ended);
        Assert.Equal((1u, 1u), _power.Lid);
    }

    [Theory]
    [InlineData(9, false, 10, true)]
    [InlineData(10, false, 10, false)]
    [InlineData(5, true, 10, false)]
    [InlineData(5, false, 0, false)]
    [InlineData(null, false, 10, false)]
    public void Battery_floor_rule(int? percent, bool charging, int floor, bool ends)
    {
        Assert.Equal(ends, AwakeRules.ShouldEndForBattery(percent, charging, floor));
    }

    [Fact]
    public void Restoration_only_touches_values_that_still_read_do_nothing()
    {
        var change = new LidChange(Guid.NewGuid(), 1, 2);

        Assert.Equal((1u, 2u), AwakeRules.Restoration(change, (0, 0)));
        Assert.Equal((3u, 2u), AwakeRules.Restoration(change, (3, 0)));
        Assert.Null(AwakeRules.Restoration(change, (1, 2)));
    }

    [Fact]
    public void Journal_round_trips()
    {
        var journal = new AwakeJournal(AwakeMode.Timed, _time.GetUtcNow(), TimeSpan.FromHours(4), _time.GetUtcNow() + TimeSpan.FromHours(4), new LidChange(Guid.NewGuid(), 1, 2));

        Assert.True(_store.Save(journal));

        Assert.Equal(journal, _store.Load());
    }

    private AwakeController Controller() => new(_power, _backstop, _store, _time);

    private sealed class FakePower : IPowerControl
    {
        public Guid Scheme { get; } = Guid.NewGuid();

        public (uint Ac, uint Dc) Lid { get; set; } = (1, 1);

        public bool KeptAwake { get; set; }

        public bool RefuseWrites { get; set; }

        public List<(uint Ac, uint Dc)> Writes { get; } = [];

        public Action? OnWrite { get; set; }

        public bool SetKeepAwake(bool on)
        {
            KeptAwake = on;
            return true;
        }

        public Guid? ActiveScheme() => Scheme;

        public (uint Ac, uint Dc)? ReadLidAction(Guid scheme) => scheme == Scheme ? Lid : null;

        public bool WriteLidAction(Guid scheme, uint ac, uint dc)
        {
            OnWrite?.Invoke();
            Writes.Add((ac, dc));
            if (RefuseWrites)
            {
                return false;
            }

            Lid = (ac, dc);
            return true;
        }
    }

    private sealed class FakeBackstop : IAwakeBackstop
    {
        public DateTimeOffset? ScheduledAt { get; set; }

        public void Schedule(DateTimeOffset endsAt) => ScheduledAt = endsAt;

        public void Cancel() => ScheduledAt = null;
    }
}
