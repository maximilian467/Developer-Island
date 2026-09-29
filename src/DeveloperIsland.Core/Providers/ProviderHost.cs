using DeveloperIsland.Core.Diagnostics;
using DeveloperIsland.Core.Models;

namespace DeveloperIsland.Core.Providers;

/// <summary>
/// Isolates providers from each other and from the app: a provider that throws while starting,
/// or while raising events, is logged and marked as <see cref="ProviderState.Error"/>. It never
/// propagates, so the rest of Developer Island keeps working.
/// </summary>
public sealed class ProviderHost : IDisposable
{
    private readonly List<IAiUsageProvider> _providers = [];
    private readonly Dictionary<AiProviderKind, AiUsageSnapshot> _faulted = [];
    private readonly object _gate = new();

    /// <summary>Raised for every snapshot change, including synthetic error snapshots.</summary>
    public event Action<AiUsageSnapshot>? SnapshotChanged;

    public event Action<AiActivity>? Activity;

    public IReadOnlyList<IAiUsageProvider> Providers
    {
        get
        {
            lock (_gate)
            {
                return _providers.ToList();
            }
        }
    }

    public AiUsageSnapshot GetSnapshot(AiProviderKind kind)
    {
        lock (_gate)
        {
            if (_faulted.TryGetValue(kind, out var faulted))
            {
                return faulted;
            }

            var provider = _providers.FirstOrDefault(p => p.Kind == kind);
            if (provider is null)
            {
                return AiUsageSnapshot.Initial(kind) with { State = ProviderState.Disabled };
            }

            try
            {
                return provider.Snapshot;
            }
            catch (Exception ex)
            {
                return Fault(kind, ex, "snapshot");
            }
        }
    }

    /// <summary>Adds and starts a provider. Never throws.</summary>
    public async Task AddAsync(IAiUsageProvider provider, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            _providers.Add(provider);
            _faulted.Remove(provider.Kind);
        }

        provider.SnapshotChanged += OnSnapshotChanged;
        provider.Activity += OnActivity;

        try
        {
            await provider.StartAsync(cancellationToken).ConfigureAwait(false);
            Log.Info("provider", "Provider started", new { provider = provider.Kind.ToString() });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Shutdown in progress.
        }
        catch (Exception ex)
        {
            var snapshot = Fault(provider.Kind, ex, "start");
            Publish(snapshot);
        }
    }

    public void Remove(AiProviderKind kind)
    {
        IAiUsageProvider? provider;
        lock (_gate)
        {
            provider = _providers.FirstOrDefault(p => p.Kind == kind);
            if (provider is not null)
            {
                _providers.Remove(provider);
            }

            _faulted.Remove(kind);
        }

        if (provider is null)
        {
            return;
        }

        provider.SnapshotChanged -= OnSnapshotChanged;
        provider.Activity -= OnActivity;
        SafeDispose(provider);
        Publish(AiUsageSnapshot.Initial(kind) with { State = ProviderState.Disabled });
    }

    public void Dispose()
    {
        List<IAiUsageProvider> providers;
        lock (_gate)
        {
            providers = _providers.ToList();
            _providers.Clear();
        }

        foreach (var provider in providers)
        {
            provider.SnapshotChanged -= OnSnapshotChanged;
            provider.Activity -= OnActivity;
            SafeDispose(provider);
        }
    }

    private void OnSnapshotChanged(AiUsageSnapshot snapshot)
    {
        lock (_gate)
        {
            _faulted.Remove(snapshot.Provider);
        }

        Publish(snapshot);
    }

    private void OnActivity(AiActivity activity)
    {
        try
        {
            Activity?.Invoke(activity);
        }
        catch (Exception ex)
        {
            Log.Error("provider", "Activity handler failed", ex, new { provider = activity.Provider.ToString() });
        }
    }

    private void Publish(AiUsageSnapshot snapshot)
    {
        try
        {
            SnapshotChanged?.Invoke(snapshot);
        }
        catch (Exception ex)
        {
            Log.Error("provider", "Snapshot handler failed", ex, new { provider = snapshot.Provider.ToString() });
        }
    }

    private AiUsageSnapshot Fault(AiProviderKind kind, Exception ex, string stage)
    {
        Log.Error("provider", "Provider failed", ex, new { provider = kind.ToString(), stage });
        var snapshot = AiUsageSnapshot.Initial(kind) with
        {
            State = ProviderState.Error,
            ErrorMessage = $"Couldn't read {kind} data. Details are in the log.",
        };
        lock (_gate)
        {
            _faulted[kind] = snapshot;
        }

        return snapshot;
    }

    private static void SafeDispose(IAiUsageProvider provider)
    {
        try
        {
            provider.Dispose();
        }
        catch (Exception ex)
        {
            Log.Warn("provider", "Provider dispose failed", new { provider = provider.Kind.ToString() }, ex);
        }
    }
}
