using DeveloperIsland.Core.Models;

namespace DeveloperIsland.Core.Providers;

public enum AiActivityKind
{
    SessionStarted,
    LimitThreshold,
}

/// <summary>A noteworthy moment the island may briefly surface (never contains content).</summary>
public sealed record AiActivity(AiProviderKind Provider, AiActivityKind Kind, string Title, string? Detail);

/// <summary>A local AI tool whose usage metadata the island displays.</summary>
public interface IAiUsageProvider : IDisposable
{
    AiProviderKind Kind { get; }

    AiUsageSnapshot Snapshot { get; }

    /// <summary>Raised on a background thread whenever the snapshot changes.</summary>
    event Action<AiUsageSnapshot>? SnapshotChanged;

    /// <summary>Raised on a background thread for transient events worth showing.</summary>
    event Action<AiActivity>? Activity;

    /// <summary>Starts scanning and watching. Must not throw for missing installations.</summary>
    Task StartAsync(CancellationToken cancellationToken);
}
