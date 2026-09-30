namespace DeveloperIsland.Core.Models;

/// <summary>
/// Token counts normalised across providers. Field semantics follow the Anthropic usage record;
/// Codex (OpenAI) usage is converted into the same shape by <c>CodexLogParser</c>.
/// <list type="table">
/// <item><term><see cref="Input"/></term><description>Input neither read from nor written to the prompt cache
/// (Claude <c>input_tokens</c>; Codex <c>input_tokens</c> minus cached and cache-write input).</description></item>
/// <item><term><see cref="CacheWrite5m"/>, <see cref="CacheWrite1h"/></term><description>Input processed and stored in the
/// prompt cache (Claude <c>cache_creation_input_tokens</c>, split by cache lifetime). In Claude Code this is where
/// nearly all new context (your messages, tool results, files) arrives.</description></item>
/// <item><term><see cref="CacheRead"/></term><description>Input served from the prompt cache: context that was already
/// processed and is re-read on every turn (Claude <c>cache_read_input_tokens</c>; Codex <c>cached_input_tokens</c>).</description></item>
/// <item><term><see cref="Output"/></term><description>Generated tokens, including reasoning/thinking tokens.</description></item>
/// </list>
/// Derived metrics:
/// <see cref="Fresh"/> = Input + CacheWrite + Output (everything the model processed without a cache hit) and
/// <see cref="Processed"/> = Fresh + CacheRead (every token the model handled). Cache reads usually dominate
/// Processed by an order of magnitude, so user-facing summaries use Fresh.
/// </summary>
public readonly record struct TokenCounts(long Input, long Output, long CacheWrite5m, long CacheWrite1h, long CacheRead)
{
    public static readonly TokenCounts Zero = default;

    public long CacheWrite => CacheWrite5m + CacheWrite1h;

    public long Cache => CacheWrite + CacheRead;

    /// <summary>Tokens processed without a cache hit: uncached input, cache writes and output.</summary>
    public long Fresh => Input + CacheWrite + Output;

    /// <summary>All tokens the model handled, including context re-read from the cache.</summary>
    public long Processed => Fresh + CacheRead;

    public bool IsZero => Processed == 0;

    public static TokenCounts operator +(TokenCounts a, TokenCounts b) => new(
        a.Input + b.Input,
        a.Output + b.Output,
        a.CacheWrite5m + b.CacheWrite5m,
        a.CacheWrite1h + b.CacheWrite1h,
        a.CacheRead + b.CacheRead);
}
