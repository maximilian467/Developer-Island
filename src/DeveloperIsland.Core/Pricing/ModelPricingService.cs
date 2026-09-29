using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using DeveloperIsland.Core.Diagnostics;
using DeveloperIsland.Core.Models;
using DeveloperIsland.Core.Usage;

namespace DeveloperIsland.Core.Pricing;

/// <summary>Public list price of one model in USD per million tokens.</summary>
public sealed record ModelPrice
{
    [JsonPropertyName("input")]
    public decimal Input { get; init; }

    [JsonPropertyName("output")]
    public decimal Output { get; init; }

    /// <summary>Cache read price. Defaults to 10% of input when omitted.</summary>
    [JsonPropertyName("cacheRead")]
    public decimal? CacheRead { get; init; }

    /// <summary>5-minute cache write price. Defaults to 125% of input when omitted.</summary>
    [JsonPropertyName("cacheWrite5m")]
    public decimal? CacheWrite5m { get; init; }

    /// <summary>1-hour cache write price. Defaults to 200% of input when omitted.</summary>
    [JsonPropertyName("cacheWrite1h")]
    public decimal? CacheWrite1h { get; init; }

    public decimal EffectiveCacheRead => CacheRead ?? Input * 0.1m;

    public decimal EffectiveCacheWrite5m => CacheWrite5m ?? Input * 1.25m;

    public decimal EffectiveCacheWrite1h => CacheWrite1h ?? Input * 2m;
}

/// <summary>Result of pricing a set of tokens.</summary>
public readonly record struct PriceEstimate(decimal ValueEur, bool IsPriced);

/// <summary>User-editable pricing overrides (<c>pricing.json</c> next to the settings).</summary>
public sealed class PricingOverrides
{
    [JsonPropertyName("usdToEur")]
    public decimal? UsdToEur { get; set; }

    [JsonPropertyName("models")]
    public Dictionary<string, ModelPrice> Models { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Converts token counts into an <b>estimated API equivalent</b>: what the same tokens would cost at
/// public pay-as-you-go API list prices. This is not what a subscriber actually pays.
/// Unknown models are reported as unpriced rather than guessed.
/// </summary>
public sealed class ModelPricingService
{
    /// <summary>Default conversion used for the EUR display. Override it in pricing.json.</summary>
    public const decimal DefaultUsdToEur = 0.86m;

    // Anthropic list prices (USD / MTok). Cache writes are 1.25x (5 min) and 2x (1 h) of input;
    // cache reads 0.1x unless a model publishes its own rate.
    private static readonly (string Prefix, ModelPrice Price)[] BuiltIn =
    [
        ("claude-fable-5-1", new ModelPrice { Input = 10m, Output = 50m, CacheRead = 0.25m }),
        ("claude-mythos-5-1", new ModelPrice { Input = 10m, Output = 50m, CacheRead = 0.25m }),
        ("claude-fable-5", new ModelPrice { Input = 10m, Output = 50m, CacheRead = 1m }),
        ("claude-mythos-5", new ModelPrice { Input = 10m, Output = 50m, CacheRead = 1m }),
        ("claude-opus-5-5", new ModelPrice { Input = 4m, Output = 20m, CacheRead = 0.20m }),
        ("claude-opus-5", new ModelPrice { Input = 5m, Output = 25m }),
        ("claude-opus-4-8", new ModelPrice { Input = 5m, Output = 25m }),
        ("claude-opus-4-7", new ModelPrice { Input = 5m, Output = 25m }),
        ("claude-opus-4-6", new ModelPrice { Input = 5m, Output = 25m }),
        ("claude-opus-4-5", new ModelPrice { Input = 5m, Output = 25m }),
        ("claude-opus-4-1", new ModelPrice { Input = 15m, Output = 75m }),
        ("claude-opus-4-0", new ModelPrice { Input = 15m, Output = 75m }),
        ("claude-opus-4", new ModelPrice { Input = 15m, Output = 75m }),
        ("claude-sonnet-5", new ModelPrice { Input = 2m, Output = 10m }),
        ("claude-sonnet-4-6", new ModelPrice { Input = 3m, Output = 15m }),
        ("claude-sonnet-4-5", new ModelPrice { Input = 3m, Output = 15m }),
        ("claude-sonnet-4-0", new ModelPrice { Input = 3m, Output = 15m }),
        ("claude-sonnet-4", new ModelPrice { Input = 3m, Output = 15m }),
        ("claude-haiku-4-5", new ModelPrice { Input = 1m, Output = 5m }),
        ("claude-3-5-haiku", new ModelPrice { Input = 0.8m, Output = 4m }),
        ("claude-3-haiku", new ModelPrice { Input = 0.25m, Output = 1.25m }),

        // OpenAI list prices for models Codex has used. Cached input is 10% of input.
        ("gpt-5-mini", new ModelPrice { Input = 0.25m, Output = 2m, CacheRead = 0.025m }),
        ("gpt-5-nano", new ModelPrice { Input = 0.05m, Output = 0.4m, CacheRead = 0.005m }),
        ("gpt-5-codex", new ModelPrice { Input = 1.25m, Output = 10m, CacheRead = 0.125m }),
        ("gpt-5.1-codex-mini", new ModelPrice { Input = 0.25m, Output = 2m, CacheRead = 0.025m }),
        ("gpt-5.1-codex", new ModelPrice { Input = 1.25m, Output = 10m, CacheRead = 0.125m }),
        ("gpt-5.1", new ModelPrice { Input = 1.25m, Output = 10m, CacheRead = 0.125m }),
        ("gpt-5", new ModelPrice { Input = 1.25m, Output = 10m, CacheRead = 0.125m }),
        ("codex-mini", new ModelPrice { Input = 1.5m, Output = 6m, CacheRead = 0.375m }),
    ];

    private static readonly Regex DateSuffix = new(@"^-\d{4}-?\d{2}-?\d{2}(-|$)", RegexOptions.Compiled);

    private readonly Dictionary<string, ModelPrice> _overrides;
    private readonly Dictionary<string, ModelPrice?> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    public ModelPricingService(PricingOverrides? overrides = null)
    {
        _overrides = overrides?.Models is { } models
            ? new Dictionary<string, ModelPrice>(models, StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, ModelPrice>(StringComparer.OrdinalIgnoreCase);
        UsdToEur = overrides?.UsdToEur is > 0 ? overrides.UsdToEur.Value : DefaultUsdToEur;
    }

    public decimal UsdToEur { get; }

    /// <summary>Loads <c>pricing.json</c> if it exists; a broken file falls back to built-in prices.</summary>
    public static ModelPricingService Load(string? overridesPath)
    {
        if (overridesPath is null || !File.Exists(overridesPath))
        {
            return new ModelPricingService();
        }

        try
        {
            var overrides = JsonSerializer.Deserialize(File.ReadAllText(overridesPath), CoreJsonContext.Default.PricingOverrides);
            return new ModelPricingService(overrides);
        }
        catch (Exception ex)
        {
            Log.Warn("pricing", "pricing.json could not be read; using built-in prices", ex: ex);
            return new ModelPricingService();
        }
    }

    public ModelPrice? FindPrice(string? model)
    {
        if (string.IsNullOrWhiteSpace(model))
        {
            return null;
        }

        lock (_gate)
        {
            if (_cache.TryGetValue(model, out var cached))
            {
                return cached;
            }

            var resolved = Resolve(NormalizeModel(model));
            _cache[model] = resolved;
            return resolved;
        }
    }

    public bool IsKnownModel(string? model) => FindPrice(model) is not null;

    public PriceEstimate Estimate(string? model, TokenCounts tokens)
    {
        if (tokens.IsZero)
        {
            return new PriceEstimate(0m, true);
        }

        var price = FindPrice(model);
        if (price is null)
        {
            return new PriceEstimate(0m, false);
        }

        const decimal perMillion = 1_000_000m;
        var usd =
            (tokens.Input * price.Input
            + tokens.Output * price.Output
            + tokens.CacheRead * price.EffectiveCacheRead
            + tokens.CacheWrite5m * price.EffectiveCacheWrite5m
            + tokens.CacheWrite1h * price.EffectiveCacheWrite1h) / perMillion;
        return new PriceEstimate(decimal.Round(usd * UsdToEur, 6), true);
    }

    /// <summary>Lower-cases and strips date and provider decorations, e.g. <c>anthropic.claude-sonnet-4-5-20250929-v1:0</c>.</summary>
    internal static string NormalizeModel(string model)
    {
        var m = model.Trim().ToLowerInvariant();
        var slash = m.LastIndexOf('/');
        if (slash >= 0)
        {
            m = m[(slash + 1)..];
        }

        if (m.StartsWith("anthropic.", StringComparison.Ordinal))
        {
            m = m["anthropic.".Length..];
        }

        var at = m.IndexOfAny(['@', ':']);
        if (at >= 0)
        {
            m = m[..at];
        }

        if (m.EndsWith("[1m]", StringComparison.Ordinal))
        {
            m = m[..^4];
        }

        return m;
    }

    private ModelPrice? Resolve(string normalized)
    {
        if (_overrides.TryGetValue(normalized, out var exact))
        {
            return exact;
        }

        // Longest matching prefix wins so "claude-opus-4-1" does not resolve to "claude-opus-4".
        ModelPrice? best = null;
        var bestLength = -1;
        foreach (var (prefix, price) in _overrides.Select(o => (o.Key, o.Value)).Concat(BuiltIn))
        {
            if (prefix.Length > bestLength && MatchesPrefix(normalized, prefix))
            {
                best = price;
                bestLength = prefix.Length;
            }
        }

        return best;
    }

    private static bool MatchesPrefix(string model, string prefix)
    {
        if (!model.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // Only an exact match or a dated snapshot inherits a price: "gpt-5" covers "gpt-5-2025-08-07",
        // but a newer "gpt-5.2" or "claude-opus-5-7" stays unpriced instead of borrowing a guess.
        return model.Length == prefix.Length || DateSuffix.IsMatch(model[prefix.Length..]);
    }
}
