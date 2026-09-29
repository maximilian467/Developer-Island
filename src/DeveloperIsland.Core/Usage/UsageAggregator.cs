using DeveloperIsland.Core.Models;
using DeveloperIsland.Core.Pricing;

namespace DeveloperIsland.Core.Usage;

/// <summary>Summary of a set of usage events.</summary>
public sealed record UsageTotals(
    TokenCounts Tokens,
    decimal ApiValueEur,
    bool HasPricedTokens,
    bool HasUnpricedTokens,
    int Sessions,
    UsageEvent? Latest)
{
    public static readonly UsageTotals Empty = new(TokenCounts.Zero, 0m, false, false, 0, null);

    /// <summary>API value to display: null when nothing could be priced.</summary>
    public decimal? DisplayValueEur => HasPricedTokens || !HasUnpricedTokens ? ApiValueEur : null;
}

/// <summary>Pure aggregation of usage events into totals and per-day statistics.</summary>
public static class UsageAggregator
{
    /// <summary>The local calendar day an event belongs to.</summary>
    public static DateOnly LocalDay(DateTimeOffset timestamp, TimeZoneInfo zone) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(timestamp, zone).DateTime);

    /// <summary>Removes duplicate events (same provider and key); the first occurrence wins.</summary>
    public static IEnumerable<UsageEvent> Deduplicate(IEnumerable<UsageEvent> events)
    {
        var seen = new HashSet<(AiProviderKind, string)>();
        foreach (var e in events)
        {
            if (seen.Add((e.Provider, e.Key)))
            {
                yield return e;
            }
        }
    }

    public static UsageTotals Summarize(IEnumerable<UsageEvent> events, ModelPricingService pricing)
    {
        var tokens = TokenCounts.Zero;
        decimal value = 0m;
        bool priced = false, unpriced = false;
        var sessions = new HashSet<string>(StringComparer.Ordinal);
        UsageEvent? latest = null;

        foreach (var e in Deduplicate(events))
        {
            tokens += e.Tokens;
            var estimate = pricing.Estimate(e.Model, e.Tokens);
            if (estimate.IsPriced)
            {
                value += estimate.ValueEur;
                priced = true;
            }
            else
            {
                unpriced = true;
            }

            if (!string.IsNullOrEmpty(e.SessionId))
            {
                sessions.Add(e.SessionId);
            }

            if (latest is null || e.Timestamp >= latest.Timestamp)
            {
                latest = e;
            }
        }

        return new UsageTotals(tokens, decimal.Round(value, 4), priced, unpriced, sessions.Count, latest);
    }

    /// <summary>Groups events into one <see cref="DailyUsage"/> per provider and local day.</summary>
    public static IReadOnlyList<DailyUsage> AggregateDaily(IEnumerable<UsageEvent> events, ModelPricingService pricing, TimeZoneInfo zone)
    {
        return Deduplicate(events)
            .GroupBy(e => (Day: LocalDay(e.Timestamp, zone), e.Provider))
            .Select(g =>
            {
                var totals = Summarize(g, pricing);
                return new DailyUsage(g.Key.Day, g.Key.Provider, totals.Tokens, totals.ApiValueEur, totals.HasUnpricedTokens, totals.Sessions);
            })
            .OrderBy(d => d.Day)
            .ThenBy(d => d.Provider)
            .ToList();
    }

    /// <summary>Combines per-provider rows into one row per day for the history graph.</summary>
    public static IReadOnlyList<UsageDay> MergeByDay(IEnumerable<DailyUsage> daily)
    {
        return daily
            .GroupBy(d => d.Day)
            .Select(g =>
            {
                var claude = g.Where(d => d.Provider == AiProviderKind.Claude).ToList();
                var codex = g.Where(d => d.Provider == AiProviderKind.Codex).ToList();
                return new UsageDay(
                    g.Key,
                    claude.Sum(d => d.Tokens.Total),
                    codex.Sum(d => d.Tokens.Total),
                    claude.Sum(d => d.ApiValueEur),
                    codex.Sum(d => d.ApiValueEur),
                    g.Sum(d => d.Sessions),
                    g.Any(d => d.ApiValuePartial));
            })
            .OrderBy(d => d.Day)
            .ToList();
    }

    /// <summary>
    /// Maps a day's token total to an intensity level 0 to 4 for the contribution graph.
    /// Levels follow quartiles of the non-empty days, so one heavy day does not flatten the rest.
    /// </summary>
    public static int[] IntensityLevels(IReadOnlyList<long> totals)
    {
        var levels = new int[totals.Count];
        var nonZero = totals.Where(t => t > 0).OrderBy(t => t).ToArray();
        if (nonZero.Length == 0)
        {
            return levels;
        }

        long Quantile(double q) => nonZero[(int)Math.Clamp(Math.Ceiling(q * nonZero.Length) - 1, 0, nonZero.Length - 1)];
        var q1 = Quantile(0.25);
        var q2 = Quantile(0.5);
        var q3 = Quantile(0.75);

        for (var i = 0; i < totals.Count; i++)
        {
            var t = totals[i];
            levels[i] = t <= 0 ? 0 : t <= q1 ? 1 : t <= q2 ? 2 : t <= q3 ? 3 : 4;
        }

        return levels;
    }
}
