namespace DeveloperIsland.Core.Usage;

/// <summary>
/// Where each day sits in the contribution graph: one column per week (starting on the culture's
/// first day of the week), one row per weekday. Hit-testing uses the same grid, so a hovered
/// cell and its tooltip always describe the same date.
/// </summary>
public sealed class HeatmapLayout
{
    private readonly Dictionary<(int Column, int Row), int> _index = [];

    private HeatmapLayout(IReadOnlyList<(int Column, int Row)> positions, int columns)
    {
        Positions = positions;
        Columns = columns;
        for (var i = 0; i < positions.Count; i++)
        {
            _index[positions[i]] = i;
        }
    }

    /// <summary>Column and row of each day, in the order of the days.</summary>
    public IReadOnlyList<(int Column, int Row)> Positions { get; }

    public int Columns { get; }

    /// <param name="days">Consecutive days, oldest first (missing days filled in by the history).</param>
    public static HeatmapLayout For(IReadOnlyList<DateOnly> days, DayOfWeek firstDayOfWeek)
    {
        var positions = new List<(int, int)>(days.Count);
        if (days.Count == 0)
        {
            return new HeatmapLayout(positions, 0);
        }

        var firstWeekStart = WeekStart(days[0], firstDayOfWeek);
        var columns = 0;
        foreach (var day in days)
        {
            // Column from the calendar, not from counting rows: a gap in the data cannot shift weeks.
            var column = (WeekStart(day, firstDayOfWeek).DayNumber - firstWeekStart.DayNumber) / 7;
            var row = RowOf(day, firstDayOfWeek);
            positions.Add((column, row));
            columns = Math.Max(columns, column + 1);
        }

        return new HeatmapLayout(positions, columns);
    }

    public static int RowOf(DateOnly day, DayOfWeek firstDayOfWeek) => ((int)day.DayOfWeek - (int)firstDayOfWeek + 7) % 7;

    public static DateOnly WeekStart(DateOnly day, DayOfWeek firstDayOfWeek) => day.AddDays(-RowOf(day, firstDayOfWeek));

    /// <summary>The day index under a point (in cell pitches), or -1 for an empty spot.</summary>
    public int IndexAt(double x, double y, double pitch)
    {
        if (x < 0 || y < 0)
        {
            return -1;
        }

        var key = ((int)Math.Floor(x / pitch), (int)Math.Floor(y / pitch));
        return _index.TryGetValue(key, out var index) ? index : -1;
    }
}
