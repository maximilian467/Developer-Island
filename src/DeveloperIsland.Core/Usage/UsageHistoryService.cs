using DeveloperIsland.Core.Models;
using DeveloperIsland.Core.Pricing;
using DeveloperIsland.Core.Storage;

namespace DeveloperIsland.Core.Usage;

/// <summary>
/// Ingests usage events into the database, keeps <c>daily_usage</c> up to date and answers
/// "today" and "history" queries. Thread-safe; providers call <see cref="Ingest"/> from background threads.
/// </summary>
/// <remarks>
/// Totals of recently touched days are kept as running accumulators: a day is read from SQLite once,
/// after which only newly inserted events are added. An active tool writes every few seconds, so this
/// keeps each update O(new events) instead of O(events today).
/// </remarks>
public sealed class UsageHistoryService
{
    private const int CachedDays = 3;

    private readonly UsageDatabase _db;
    private readonly ModelPricingService _pricing;
    private readonly TimeProvider _time;
    private readonly TimeZoneInfo _zone;
    private readonly Dictionary<(DateOnly Day, AiProviderKind Provider), UsageAccumulator> _cache = [];
    private readonly object _gate = new();

    public UsageHistoryService(UsageDatabase db, ModelPricingService pricing, TimeProvider? time = null, TimeZoneInfo? zone = null)
    {
        _db = db;
        _pricing = pricing;
        _time = time ?? TimeProvider.System;
        _zone = zone ?? TimeZoneInfo.Local;
    }

    /// <summary>Raised after new events changed at least one day. Carries the affected days.</summary>
    public event Action<IReadOnlyCollection<DateOnly>>? Changed;

    public ModelPricingService Pricing => _pricing;

    public DateOnly Today => UsageAggregator.LocalDay(_time.GetLocalNow(), _zone);

    /// <summary>Stores new events and updates the affected daily totals. Returns the number of changed days.</summary>
    public int Ingest(IReadOnlyCollection<UsageEvent> events)
    {
        if (events.Count == 0)
        {
            return 0;
        }

        List<DateOnly> changedDays;
        lock (_gate)
        {
            var inserted = _db.InsertEvents(events, _zone);
            if (inserted.Count == 0)
            {
                return 0;
            }

            var batch = new Dictionary<(DateOnly Day, AiProviderKind Provider), UsageAccumulator>();
            var loadedFromDb = new HashSet<(DateOnly, AiProviderKind)>();
            foreach (var (e, day) in inserted)
            {
                var key = (day, e.Provider);
                if (!batch.TryGetValue(key, out var accumulator))
                {
                    if (_cache.TryGetValue(key, out accumulator))
                    {
                        batch[key] = accumulator;
                    }
                    else
                    {
                        // Not cached: the database already holds every event of this batch, so the
                        // loaded totals are complete and the batch's events must not be added again.
                        accumulator = LoadAccumulator(day, e.Provider);
                        batch[key] = accumulator;
                        loadedFromDb.Add(key);
                        continue;
                    }
                }

                if (!loadedFromDb.Contains(key))
                {
                    accumulator.Add(e);
                }
            }

            foreach (var ((day, provider), accumulator) in batch)
            {
                var totals = accumulator.ToTotals();
                _db.UpsertDaily(new DailyUsage(day, provider, totals.Tokens, totals.ApiValueEur, totals.HasUnpricedTokens, totals.Sessions));
                _cache[(day, provider)] = accumulator;
            }

            Evict();
            changedDays = batch.Keys.Select(k => k.Day).Distinct().ToList();
        }

        Changed?.Invoke(changedDays);
        return changedDays.Count;
    }

    public UsageTotals GetTotals(AiProviderKind provider, DateOnly day)
    {
        lock (_gate)
        {
            if (!_cache.TryGetValue((day, provider), out var accumulator))
            {
                accumulator = LoadAccumulator(day, provider);
                _cache[(day, provider)] = accumulator;
                Evict();
            }

            return accumulator.ToTotals();
        }
    }

    public UsageTotals GetTodayTotals(AiProviderKind provider) => GetTotals(provider, Today);

    /// <summary>One entry per day in the range (missing days are filled with zeros).</summary>
    public IReadOnlyList<UsageDay> GetHistory(int days)
    {
        var to = Today;
        var from = to.AddDays(-(days - 1));
        var merged = UsageAggregator.MergeByDay(_db.GetDaily(from, to)).ToDictionary(d => d.Day);
        var result = new List<UsageDay>(days);
        for (var day = from; day <= to; day = day.AddDays(1))
        {
            result.Add(merged.TryGetValue(day, out var value) ? value : new UsageDay(day, TokenCounts.Zero, TokenCounts.Zero, 0m, 0m, 0));
        }

        return result;
    }

    private UsageAccumulator LoadAccumulator(DateOnly day, AiProviderKind provider)
    {
        var accumulator = new UsageAccumulator(_pricing);
        foreach (var e in _db.GetEvents(provider, day))
        {
            accumulator.Add(e);
        }

        return accumulator;
    }

    /// <summary>Only the most recent days stay cached; older days are read on demand.</summary>
    private void Evict()
    {
        var keep = Today.AddDays(-(CachedDays - 1));
        foreach (var key in _cache.Keys.Where(k => k.Day < keep).ToList())
        {
            _cache.Remove(key);
        }
    }
}

/// <summary>Running totals of one provider on one day. Events must be unique (the database guarantees it).</summary>
internal sealed class UsageAccumulator
{
    private readonly ModelPricingService _pricing;
    private readonly HashSet<string> _sessions = new(StringComparer.Ordinal);
    private TokenCounts _tokens;
    private decimal _value;
    private bool _priced;
    private bool _unpriced;
    private UsageEvent? _latest;

    public UsageAccumulator(ModelPricingService pricing)
    {
        _pricing = pricing;
    }

    public void Add(UsageEvent e)
    {
        _tokens += e.Tokens;
        var estimate = _pricing.Estimate(e.Model, e.Tokens);
        if (estimate.IsPriced)
        {
            _value += estimate.ValueEur;
            _priced = true;
        }
        else
        {
            _unpriced = true;
        }

        if (!string.IsNullOrEmpty(e.SessionId))
        {
            _sessions.Add(e.SessionId);
        }

        if (_latest is null || e.Timestamp >= _latest.Timestamp)
        {
            _latest = e;
        }
    }

    public UsageTotals ToTotals() => new(_tokens, decimal.Round(_value, 4), _priced, _unpriced, _sessions.Count, _latest);
}
