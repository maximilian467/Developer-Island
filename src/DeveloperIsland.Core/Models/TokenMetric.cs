using DeveloperIsland.Core.Formatting;
using DeveloperIsland.Core.Usage;

namespace DeveloperIsland.Core.Models;

/// <summary>
/// Which token figure the island shows in the capsule, peek, tooltips, usage graph and inspector
/// (Settings, Modules, Token metric). The same for Claude Code and Codex; the breakdown always lists
/// every category.
/// </summary>
public enum TokenMetric
{
    /// <summary><see cref="TokenCounts.Fresh"/>: input, cache writes and output.</summary>
    Fresh,

    /// <summary><see cref="TokenCounts.Processed"/>: fresh tokens plus cache reads.</summary>
    AllProcessed,
}

/// <summary>Values and labels of the chosen <see cref="TokenMetric"/>. A figure is never shown without its label.</summary>
public static class TokenMetrics
{
    /// <summary>The chosen figure of one count.</summary>
    public static long Of(this TokenCounts tokens, TokenMetric metric) =>
        metric == TokenMetric.AllProcessed ? tokens.Processed : tokens.Fresh;

    /// <summary>The chosen figure of one history day, Claude Code and Codex together.</summary>
    public static long Of(this UsageDay day, TokenMetric metric) => day.Claude.Of(metric) + day.Codex.Of(metric);

    /// <summary>The chosen figure summed over several counts (for example both providers).</summary>
    public static long Sum(IEnumerable<TokenCounts> counts, TokenMetric metric) => counts.Sum(c => c.Of(metric));

    /// <summary>Name of the setting's option: "Fresh" or "All processed".</summary>
    public static string OptionName(TokenMetric metric) => metric == TokenMetric.AllProcessed ? "All processed" : "Fresh";

    /// <summary>What a figure counts: "fresh tokens" or "tokens incl. cache reads".</summary>
    public static string Unit(TokenMetric metric) => metric == TokenMetric.AllProcessed ? "tokens incl. cache reads" : "fresh tokens";

    /// <summary>"4.58M fresh tokens" or "390M tokens incl. cache reads".</summary>
    public static string Format(long value, TokenMetric metric) => $"{DisplayFormat.Tokens(value)} {Unit(metric)}";

    /// <summary>
    /// The figure for tight spots (capsule, peek title): "4.58M" for fresh tokens, as the island always
    /// showed them, and "390M incl. cache reads", so the larger figure always says what it contains.
    /// </summary>
    public static string Compact(long value, TokenMetric metric) => metric == TokenMetric.AllProcessed
        ? $"{DisplayFormat.Tokens(value)} incl. cache reads"
        : DisplayFormat.Tokens(value);

    /// <summary>
    /// The full breakdown of a day, led by what the figure counts:
    /// "Fresh tokens: input 56, cache write 4.5M, output 30k · cache read 385M not counted" or
    /// "Tokens incl. cache reads: input 56, cache write 4.5M, output 30k, cache read 385M".
    /// </summary>
    public static string Breakdown(TokenCounts t, TokenMetric metric)
    {
        var parts = $"input {DisplayFormat.Tokens(t.Input)}, cache write {DisplayFormat.Tokens(t.CacheWrite)}, output {DisplayFormat.Tokens(t.Output)}";
        return metric == TokenMetric.AllProcessed
            ? $"Tokens incl. cache reads: {parts}, cache read {DisplayFormat.Tokens(t.CacheRead)}"
            : $"Fresh tokens: {parts} · cache read {DisplayFormat.Tokens(t.CacheRead)} not counted";
    }

    /// <summary>The usage panel's footnote on what the figures count.</summary>
    public static string Note(TokenMetric metric) => metric == TokenMetric.AllProcessed
        ? "Tokens include cache reads: the context re-read on every turn, usually most of the total."
        : "Fresh tokens exclude context re-read from the cache.";

    /// <summary>Graph intensity 0 to 4 per day, by the chosen figure (see <see cref="UsageAggregator.IntensityLevels"/>).</summary>
    public static int[] IntensityLevels(IReadOnlyList<UsageDay> days, TokenMetric metric) =>
        UsageAggregator.IntensityLevels(days.Select(d => d.Of(metric)).ToList());
}
