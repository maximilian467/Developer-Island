namespace DeveloperIsland.Core.Models;

/// <summary>
/// Token counts normalised across providers.
/// <see cref="Input"/> is uncached input only; cache reads and cache writes are tracked separately
/// so each can be priced at its own rate.
/// </summary>
public readonly record struct TokenCounts(long Input, long Output, long CacheWrite5m, long CacheWrite1h, long CacheRead)
{
    public static readonly TokenCounts Zero = default;

    public long CacheWrite => CacheWrite5m + CacheWrite1h;

    public long Cache => CacheWrite + CacheRead;

    public long Total => Input + Output + CacheWrite + CacheRead;

    public bool IsZero => Total == 0;

    public static TokenCounts operator +(TokenCounts a, TokenCounts b) => new(
        a.Input + b.Input,
        a.Output + b.Output,
        a.CacheWrite5m + b.CacheWrite5m,
        a.CacheWrite1h + b.CacheWrite1h,
        a.CacheRead + b.CacheRead);
}
