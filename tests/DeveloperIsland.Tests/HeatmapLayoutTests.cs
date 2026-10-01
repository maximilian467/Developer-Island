using DeveloperIsland.Core.Usage;

namespace DeveloperIsland.Tests;

public class HeatmapLayoutTests
{
    private static IReadOnlyList<DateOnly> Days(DateOnly from, int count) =>
        Enumerable.Range(0, count).Select(i => from.AddDays(i)).ToList();

    [Theory]
    [InlineData(DayOfWeek.Monday)]
    [InlineData(DayOfWeek.Sunday)]
    public void Every_cell_maps_back_to_its_own_date(DayOfWeek firstDay)
    {
        var days = Days(new DateOnly(2026, 3, 1), 200); // across the March DST switch
        var layout = HeatmapLayout.For(days, firstDay);
        const double pitch = 14;

        for (var i = 0; i < days.Count; i++)
        {
            var (column, row) = layout.Positions[i];
            // Anywhere inside the cell, including the gap to its right and below, hits the same day.
            Assert.Equal(i, layout.IndexAt(column * pitch + 0.5, row * pitch + 0.5, pitch));
            Assert.Equal(i, layout.IndexAt(column * pitch + 13.9, row * pitch + 13.9, pitch));
            Assert.Equal(HeatmapLayout.RowOf(days[i], firstDay), row);
        }
    }

    [Fact]
    public void A_column_is_exactly_one_week_starting_on_the_first_weekday()
    {
        var days = Days(new DateOnly(2026, 9, 30), 21); // starts on a Wednesday
        var layout = HeatmapLayout.For(days, DayOfWeek.Monday);

        // Wed 30 Sep is in the first column (week of Mon 28 Sep), Mon 5 Oct opens the second.
        Assert.Equal((0, 2), layout.Positions[0]);
        Assert.Equal((1, 0), layout.Positions[5]);
        Assert.Equal(4, layout.Columns);
        foreach (var group in days.Select((d, i) => (d, layout.Positions[i].Column)).GroupBy(x => x.Column))
        {
            Assert.Single(group.Select(x => HeatmapLayout.WeekStart(x.d, DayOfWeek.Monday)).Distinct());
        }
    }

    [Fact]
    public void Empty_spots_and_outside_points_hit_nothing()
    {
        var layout = HeatmapLayout.For(Days(new DateOnly(2026, 9, 30), 3), DayOfWeek.Monday);

        Assert.Equal(-1, layout.IndexAt(1, 1, 14));      // Monday slot before the first day
        Assert.Equal(-1, layout.IndexAt(-3, 30, 14));
        Assert.Equal(-1, layout.IndexAt(14 * 5, 1, 14)); // beyond the last column
        Assert.Empty(HeatmapLayout.For([], DayOfWeek.Monday).Positions);
    }
}
