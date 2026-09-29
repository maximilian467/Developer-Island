using DeveloperIsland.Core.Diagnostics;
using DeveloperIsland.Core.Storage;
using DeveloperIsland.Core.Usage;

namespace DeveloperIsland.Core.Focus;

/// <summary>Persists finished focus sessions and answers daily statistics.</summary>
public sealed class FocusHistory
{
    private readonly UsageDatabase _db;
    private readonly TimeProvider _time;
    private readonly TimeZoneInfo _zone;

    public FocusHistory(UsageDatabase db, TimeProvider? time = null, TimeZoneInfo? zone = null)
    {
        _db = db;
        _time = time ?? TimeProvider.System;
        _zone = zone ?? TimeZoneInfo.Local;
    }

    public event Action? Changed;

    public DateOnly Today => UsageAggregator.LocalDay(_time.GetLocalNow(), _zone);

    /// <summary>Records a finished session. Never throws: a failing disk must not break the timer.</summary>
    public void Record(FocusSessionRecord session)
    {
        try
        {
            _db.InsertFocusSession(session);
            Log.Info("focus", "Session recorded", new { minutes = session.FocusedSeconds / 60, session.Completed });
            Changed?.Invoke();
        }
        catch (Exception ex)
        {
            Log.Error("database", "Focus session could not be saved", ex);
        }
    }

    public FocusDay GetToday()
    {
        var today = Today;
        return _db.GetFocusDays(today, today).FirstOrDefault() ?? new FocusDay(today, 0, 0);
    }

    public IReadOnlyList<FocusDay> GetDays(int days)
    {
        var to = Today;
        return _db.GetFocusDays(to.AddDays(-(days - 1)), to);
    }
}
