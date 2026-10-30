using System.Net;
using System.Text.Json;
using DeveloperIsland.Core.Diagnostics;

namespace DeveloperIsland.Core.Updates;

public enum UpdateCheckOutcome
{
    /// <summary>No check has run in this session.</summary>
    None,
    UpToDate,
    Available,

    /// <summary>GitHub could not be reached or answered with an error.</summary>
    Failed,
}

/// <summary>What Settings, the tray and the island show about updates.</summary>
/// <param name="Available">A release newer than the running app; null when there is none (or checks are off).</param>
/// <param name="LastCheckedAt">The last check that got an answer, also from an earlier run.</param>
public sealed record UpdateStatus(ReleaseVersion Current, bool Enabled, bool IsChecking, UpdateCheckOutcome LastOutcome, ReleaseInfo? Available, DateTimeOffset? LastCheckedAt);

/// <summary>
/// The optional update check: asks GitHub for the latest release of Developer Island (one anonymous
/// GET with a User-Agent, no token) at most once a day, the first time shortly after the app starts,
/// and compares its tag with the running version. Drafts and prereleases are never offered. A check
/// that gets no answer (offline, rate limit) is logged and tried again after an hour; nothing is shown.
/// Off until the user turns it on in Settings. "Check now" asks at once.
/// </summary>
public sealed class UpdateService : IDisposable
{
    public const string LatestReleaseApi = "https://api.github.com/repos/" + ReleaseJson.Repository + "/releases/latest";
    public static readonly TimeSpan CheckInterval = TimeSpan.FromDays(1);
    public static readonly TimeSpan RetryInterval = TimeSpan.FromHours(1);

    /// <summary>The first check waits a little: at sign-in the network is often not up yet.</summary>
    public static readonly TimeSpan StartDelay = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(20);

    private readonly HttpClient _http;
    private readonly ReleaseVersion _current;
    private readonly UpdateStateStore _store;
    private readonly TimeProvider _time;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _checkLock = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private ITimer? _timer;
    private bool _enabled;
    private bool _checking;
    private bool _checkedManually;
    private UpdateCheckOutcome _outcome;

    public UpdateService(HttpClient http, ReleaseVersion current, UpdateStateStore store, TimeProvider? time = null)
    {
        _http = http;
        _current = current;
        _store = store;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Raised on a background thread.</summary>
    public event Action? Changed;

    /// <summary>The client for the check and the download: identifies the app and its version, nothing else.</summary>
    public static HttpClient CreateHttpClient(ReleaseVersion current)
    {
        var client = new HttpClient
        {
            // Each request has its own timeout; downloads take as long as they take.
            Timeout = Timeout.InfiniteTimeSpan,
            MaxResponseContentBufferSize = 1024 * 1024,
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"DeveloperIsland/{current}");
        return client;
    }

    public UpdateStatus Status
    {
        get
        {
            var state = _store.State;
            lock (_gate)
            {
                var latest = state.Latest?.ToRelease();
                var available = latest is not null && latest.Version > _current && (_enabled || _checkedManually) ? latest : null;
                return new UpdateStatus(_current, _enabled, _checking, _outcome, available, state.LastCheckedAt);
            }
        }
    }

    /// <summary>Turns the daily check on or off (Settings, "Check for updates").</summary>
    public void Configure(bool enabled)
    {
        lock (_gate)
        {
            if (enabled == _enabled)
            {
                return;
            }

            _enabled = enabled;
            _timer?.Dispose();
            _timer = enabled ? _time.CreateTimer(_ => _ = CheckAsync(manual: false), null, DueIn(_store.State, _time.GetUtcNow(), StartDelay), Timeout.InfiniteTimeSpan) : null;
        }

        Changed?.Invoke();
    }

    /// <summary>"Check now": asks at once, also while the daily check is off.</summary>
    public Task CheckNowAsync() => CheckAsync(manual: true);

    public void Dispose()
    {
        lock (_gate)
        {
            _enabled = false;
            _timer?.Dispose();
            _timer = null;
        }

        _shutdown.Cancel();
    }

    /// <summary>
    /// How long until the next scheduled check: a day after the last answered check, an hour after
    /// a check that got no answer, never sooner than <paramref name="minimum"/> and never later than a day.
    /// </summary>
    internal static TimeSpan DueIn(UpdateState state, DateTimeOffset now, TimeSpan minimum)
    {
        var next = now;
        if (state.LastAttemptAt is { } attempt)
        {
            next = attempt + (attempt == state.LastCheckedAt ? CheckInterval : RetryInterval);
        }

        if (state.LastCheckedAt is { } answered && answered + CheckInterval > next)
        {
            next = answered + CheckInterval;
        }

        var due = next - now;
        return due < minimum ? minimum : due > CheckInterval ? CheckInterval : due;
    }

    private async Task CheckAsync(bool manual)
    {
        if (!await _checkLock.WaitAsync(0))
        {
            return;
        }

        try
        {
            lock (_gate)
            {
                if (!manual && !_enabled)
                {
                    return;
                }

                _checking = true;
                _checkedManually |= manual;
            }

            Changed?.Invoke();
            var previous = _store.State;
            var now = _time.GetUtcNow();
            UpdateCheckOutcome outcome;
            try
            {
                var release = await FetchLatestAsync();
                outcome = release is not null && release.Version > _current ? UpdateCheckOutcome.Available : UpdateCheckOutcome.UpToDate;
                _store.Save(new UpdateState(now, now, release is null ? null : SavedRelease.From(release)));
                Log.Info("update", "Update check finished", new { manual, available = outcome == UpdateCheckOutcome.Available });
            }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException or IOException)
            {
                outcome = UpdateCheckOutcome.Failed;
                _store.Save(previous with { LastAttemptAt = now });
                Log.Warn("update", "Update check failed", new { manual, error = ex.GetType().Name, status = (int?)(ex as HttpRequestException)?.StatusCode });
            }

            lock (_gate)
            {
                _checking = false;
                _outcome = outcome;
                _timer?.Change(DueIn(_store.State, _time.GetUtcNow(), TimeSpan.Zero), Timeout.InfiniteTimeSpan);
            }

            Changed?.Invoke();
        }
        finally
        {
            _checkLock.Release();
        }
    }

    /// <summary>The latest published release, or null when the repository has none.</summary>
    private async Task<ReleaseInfo?> FetchLatestAsync()
    {
        using var timeout = new CancellationTokenSource(RequestTimeout, _time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, _shutdown.Token);
        using var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseApi);
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        using var response = await _http.SendAsync(request, linked.Token);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        return ReleaseJson.Parse(await response.Content.ReadAsStringAsync(linked.Token));
    }
}
