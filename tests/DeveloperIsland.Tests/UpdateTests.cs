using System.Net;
using System.Security.Cryptography;
using System.Text;
using DeveloperIsland.Core.Updates;
using Microsoft.Extensions.Time.Testing;

namespace DeveloperIsland.Tests;

/// <summary>Answers requests from a function and counts them; no network.</summary>
internal sealed class FakeHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    private int _calls;

    public int Calls => Volatile.Read(ref _calls);

    public List<Uri> Requested { get; } = [];

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _calls);
        lock (Requested)
        {
            Requested.Add(request.RequestUri!);
        }

        return Task.FromResult(respond(request));
    }

    public static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    public static HttpResponseMessage Bytes(byte[] bytes) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
}

internal static class UpdateTestData
{
    public const string Download = "https://github.com/maximilian467/Developer-Island/releases/download/";

    public static string Release(string tag, bool prerelease = false, bool draft = false, string? downloadRoot = null, bool withSetup = true)
    {
        var version = tag.TrimStart('v');
        var root = (downloadRoot ?? Download) + tag + "/";
        var assets = new List<string>
        {
            Asset($"DeveloperIsland-{version}-win-x64-portable.zip", root),
            Asset($"SHA256SUMS-{version}.txt", root),
            Asset($"SHA256SUMS-{version}.txt.sig", root),
        };
        if (withSetup)
        {
            assets.Add(Asset($"DeveloperIsland-Setup-{version}.exe", root));
        }

        return $$"""
            {
              "tag_name": "{{tag}}",
              "html_url": "https://github.com/maximilian467/Developer-Island/releases/tag/{{tag}}",
              "draft": {{(draft ? "true" : "false")}},
              "prerelease": {{(prerelease ? "true" : "false")}},
              "assets": [ {{string.Join(", ", assets)}} ]
            }
            """;
    }

    private static string Asset(string name, string root) =>
        $$"""{ "name": "{{name}}", "size": 1234, "browser_download_url": "{{root}}{{name}}" }""";

    public static async Task WaitUntil(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
        {
            await Task.Delay(25, TestContext.Current.CancellationToken);
        }

        Assert.True(condition(), "Condition not reached in time");
    }
}

public class ReleaseVersionTests
{
    [Theory]
    [InlineData("v0.4.0", "0.3.0")]
    [InlineData("0.10.0", "0.9.9")]
    [InlineData("1.0.0", "0.99.99")]
    [InlineData("0.4.1", "0.4.0")]
    [InlineData("0.4.0", "0.4.0-beta.1")]
    [InlineData("0.4.0-beta.11", "0.4.0-beta.2")]
    [InlineData("0.4.0-rc.1", "0.4.0-beta.9")]
    public void Newer_versions_compare_greater(string newer, string older)
    {
        Assert.True(ReleaseVersion.TryParse(newer, out var a));
        Assert.True(ReleaseVersion.TryParse(older, out var b));

        Assert.True(a > b);
        Assert.True(b < a);
    }

    [Fact]
    public void Tag_assembly_version_and_build_metadata_compare_equal()
    {
        Assert.True(ReleaseVersion.TryParse("v0.4.0", out var tag));
        Assert.True(ReleaseVersion.TryParse("0.4.0+abc123", out var withBuild));
        var assembly = ReleaseVersion.FromAssembly(new Version(0, 4, 0, 0));

        Assert.Equal(0, tag.CompareTo(assembly));
        Assert.Equal(0, withBuild.CompareTo(assembly));
        Assert.Equal("0.4.0", assembly.ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("latest")]
    [InlineData("0.4")]
    [InlineData("0.4.x")]
    [InlineData("0.4.0-")]
    [InlineData("-1.0.0")]
    [InlineData("1.2.3.4.5")]
    public void Tags_that_are_no_version_are_rejected(string text)
    {
        Assert.False(ReleaseVersion.TryParse(text, out _));
    }
}

public class ReleaseJsonTests
{
    [Fact]
    public void Picks_the_setup_checksums_and_signature_of_the_tag()
    {
        var release = ReleaseJson.Parse(UpdateTestData.Release("v0.5.0"));

        Assert.NotNull(release);
        Assert.Equal("0.5.0", release.Version.ToString());
        Assert.Equal("DeveloperIsland-Setup-0.5.0.exe", release.Setup.Name);
        Assert.Equal(new Uri(UpdateTestData.Download + "v0.5.0/DeveloperIsland-Setup-0.5.0.exe"), release.Setup.Url);
        Assert.Equal("SHA256SUMS-0.5.0.txt", release.Checksums?.Name);
        Assert.Equal("SHA256SUMS-0.5.0.txt.sig", release.Signature?.Name);
        Assert.Equal(new Uri("https://github.com/maximilian467/Developer-Island/releases/tag/v0.5.0"), release.NotesUrl);
    }

    [Fact]
    public void Drafts_and_prereleases_are_never_offered()
    {
        Assert.Null(ReleaseJson.Parse(UpdateTestData.Release("v0.5.0", prerelease: true)));
        Assert.Null(ReleaseJson.Parse(UpdateTestData.Release("v0.5.0", draft: true)));
        Assert.Null(ReleaseJson.Parse(UpdateTestData.Release("v0.5.0-beta.1")));
    }

    [Fact]
    public void A_release_without_its_setup_is_not_offered()
    {
        Assert.Null(ReleaseJson.Parse(UpdateTestData.Release("v0.5.0", withSetup: false)));
    }

    [Fact]
    public void Downloads_from_anywhere_but_the_project_repository_are_ignored()
    {
        Assert.Null(ReleaseJson.Parse(UpdateTestData.Release("v0.5.0", downloadRoot: "https://github.com/someone-else/Developer-Island/releases/download/")));
        Assert.Null(ReleaseJson.Parse(UpdateTestData.Release("v0.5.0", downloadRoot: "http://github.com/maximilian467/Developer-Island/releases/download/")));
    }
}

public class UpdateScheduleTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 22, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void First_check_waits_only_for_the_start_delay()
    {
        Assert.Equal(UpdateService.StartDelay, UpdateService.DueIn(UpdateState.Empty, Now, UpdateService.StartDelay));
    }

    [Fact]
    public void An_answered_check_is_not_repeated_within_a_day_even_after_a_restart()
    {
        var state = new UpdateState(Now.AddHours(-2), Now.AddHours(-2), null);

        Assert.Equal(TimeSpan.FromHours(22), UpdateService.DueIn(state, Now, UpdateService.StartDelay));
    }

    [Fact]
    public void A_check_without_answer_is_retried_after_an_hour()
    {
        var state = new UpdateState(Now.AddDays(-3), Now.AddMinutes(-10), null);

        Assert.Equal(TimeSpan.FromMinutes(50), UpdateService.DueIn(state, Now, TimeSpan.Zero));
    }

    [Fact]
    public void A_failure_does_not_shorten_the_day_after_an_answered_check()
    {
        var state = new UpdateState(Now.AddHours(-5), Now.AddMinutes(-10), null);

        Assert.Equal(TimeSpan.FromHours(19), UpdateService.DueIn(state, Now, TimeSpan.Zero));
    }

    [Fact]
    public void A_clock_set_back_waits_at_most_a_day()
    {
        var state = new UpdateState(Now.AddDays(5), Now.AddDays(5), null);

        Assert.Equal(UpdateService.CheckInterval, UpdateService.DueIn(state, Now, UpdateService.StartDelay));
    }
}

public class UpdateServiceTests
{
    private static readonly ReleaseVersion Current = new(0, 4, 0);
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 22, 9, 0, 0, TimeSpan.Zero));

    [Fact]
    public async Task Checks_once_after_start_and_then_once_a_day()
    {
        var handler = new FakeHttpHandler(_ => FakeHttpHandler.Json(UpdateTestData.Release("v0.5.0")));
        using var service = new UpdateService(new HttpClient(handler), Current, new UpdateStateStore(null), _time);
        service.Configure(true);

        _time.Advance(UpdateService.StartDelay - TimeSpan.FromSeconds(1));
        Assert.Equal(0, handler.Calls);

        _time.Advance(TimeSpan.FromSeconds(1));
        await UpdateTestData.WaitUntil(() => service.Status.LastOutcome == UpdateCheckOutcome.Available);
        Assert.Equal(1, handler.Calls);
        Assert.Equal("0.5.0", service.Status.Available?.Version.ToString());
        Assert.Equal(_time.GetUtcNow(), service.Status.LastCheckedAt);
        Assert.Equal(new Uri(UpdateService.LatestReleaseApi), handler.Requested[0]);

        _time.Advance(TimeSpan.FromHours(23));
        await Task.Delay(100, TestContext.Current.CancellationToken);
        Assert.Equal(1, handler.Calls);

        _time.Advance(TimeSpan.FromHours(1));
        await UpdateTestData.WaitUntil(() => handler.Calls == 2);
    }

    [Fact]
    public async Task A_restart_within_the_day_does_not_ask_again_and_keeps_the_notice()
    {
        var path = Path.Combine(TestData.TempDirectory(), "update-state.json");
        var handler = new FakeHttpHandler(_ => FakeHttpHandler.Json(UpdateTestData.Release("v0.5.0")));
        using (var first = new UpdateService(new HttpClient(handler), Current, new UpdateStateStore(path), _time))
        {
            first.Configure(true);
            _time.Advance(UpdateService.StartDelay);
            await UpdateTestData.WaitUntil(() => first.Status.LastOutcome == UpdateCheckOutcome.Available);
        }

        _time.Advance(TimeSpan.FromHours(3));
        using var second = new UpdateService(new HttpClient(handler), Current, new UpdateStateStore(path), _time);
        second.Configure(true);
        _time.Advance(TimeSpan.FromHours(1));
        await Task.Delay(100, TestContext.Current.CancellationToken);

        Assert.Equal(1, handler.Calls);
        Assert.Equal("0.5.0", second.Status.Available?.Version.ToString());
    }

    [Fact]
    public async Task Offline_is_silent_and_retried_after_an_hour()
    {
        var online = false;
        var handler = new FakeHttpHandler(_ => online
            ? FakeHttpHandler.Json(UpdateTestData.Release("v0.5.0"))
            : throw new HttpRequestException("No such host is known."));
        using var service = new UpdateService(new HttpClient(handler), Current, new UpdateStateStore(null), _time);
        service.Configure(true);

        _time.Advance(UpdateService.StartDelay);
        await UpdateTestData.WaitUntil(() => service.Status.LastOutcome == UpdateCheckOutcome.Failed);
        Assert.Null(service.Status.Available);
        Assert.Null(service.Status.LastCheckedAt);

        online = true;
        _time.Advance(UpdateService.RetryInterval);
        await UpdateTestData.WaitUntil(() => service.Status.LastOutcome == UpdateCheckOutcome.Available);
        Assert.Equal(2, handler.Calls);
    }

    [Theory]
    [InlineData("v0.4.0")]
    [InlineData("v0.3.9")]
    public async Task The_running_version_or_an_older_one_is_up_to_date(string tag)
    {
        var handler = new FakeHttpHandler(_ => FakeHttpHandler.Json(UpdateTestData.Release(tag)));
        using var service = new UpdateService(new HttpClient(handler), Current, new UpdateStateStore(null), _time);

        await service.CheckNowAsync();

        Assert.Equal(UpdateCheckOutcome.UpToDate, service.Status.LastOutcome);
        Assert.Null(service.Status.Available);
    }

    [Fact]
    public async Task A_repository_without_releases_is_up_to_date()
    {
        var handler = new FakeHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        using var service = new UpdateService(new HttpClient(handler), Current, new UpdateStateStore(null), _time);

        await service.CheckNowAsync();

        Assert.Equal(UpdateCheckOutcome.UpToDate, service.Status.LastOutcome);
    }

    [Fact]
    public async Task Turned_off_it_never_asks_and_shows_nothing_from_earlier_runs()
    {
        var store = new UpdateStateStore(null);
        var release = ReleaseJson.Parse(UpdateTestData.Release("v0.5.0"))!;
        store.Save(new UpdateState(_time.GetUtcNow(), _time.GetUtcNow(), SavedRelease.From(release)));
        var handler = new FakeHttpHandler(_ => FakeHttpHandler.Json(UpdateTestData.Release("v0.5.0")));
        using var service = new UpdateService(new HttpClient(handler), Current, store, _time);

        _time.Advance(TimeSpan.FromDays(3));
        await Task.Delay(100, TestContext.Current.CancellationToken);

        Assert.Equal(0, handler.Calls);
        Assert.Null(service.Status.Available);

        await service.CheckNowAsync();
        Assert.Equal("0.5.0", service.Status.Available?.Version.ToString());
    }
}

public class UpdateDownloaderTests
{
    private const string Version = "0.5.0";
    private static readonly byte[] Setup = Encoding.ASCII.GetBytes("pretend this is an installer");
    private readonly string _directory = Path.Combine(TestData.TempDirectory(), "updates");
    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly ReleaseInfo _release = ReleaseJson.Parse(UpdateTestData.Release("v" + Version))!;

    private string PublicKey => _key.ExportSubjectPublicKeyInfoPem();

    private byte[] Sums(byte[] setup) =>
        Encoding.ASCII.GetBytes($"{Convert.ToHexStringLower(SHA256.HashData(setup))}  DeveloperIsland-Setup-{Version}.exe\r\n"
            + $"{new string('0', 64)}  DeveloperIsland-{Version}-win-x64-portable.zip\r\n");

    private byte[] Sign(byte[] data, ECDsa? key = null) =>
        (key ?? _key).SignData(data, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);

    private FakeHttpHandler Serve(byte[] sums, byte[] signature, byte[] setup) => new(request =>
    {
        var name = request.RequestUri!.Segments[^1];
        return name.EndsWith(".sig", StringComparison.Ordinal) ? FakeHttpHandler.Bytes(signature)
            : name.EndsWith(".txt", StringComparison.Ordinal) ? FakeHttpHandler.Bytes(sums)
            : FakeHttpHandler.Bytes(setup);
    });

    [Fact]
    public async Task A_signed_matching_setup_is_ready_to_run()
    {
        var sums = Sums(Setup);
        var downloader = new UpdateDownloader(new HttpClient(Serve(sums, Sign(sums), Setup)), _directory, PublicKey);

        var result = await downloader.DownloadAsync(_release, null, TestContext.Current.CancellationToken);

        Assert.True(result.IsReady);
        Assert.Equal(Path.Combine(_directory, $"DeveloperIsland-Setup-{Version}.exe"), result.SetupPath);
        Assert.Equal(Setup, File.ReadAllBytes(result.SetupPath!));
        Assert.True(File.Exists(Path.Combine(_directory, $"SHA256SUMS-{Version}.txt")));
        Assert.True(File.Exists(Path.Combine(_directory, $"SHA256SUMS-{Version}.txt.sig")));
    }

    [Fact]
    public async Task A_signature_by_another_key_is_refused_before_the_setup_is_downloaded()
    {
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var sums = Sums(Setup);
        var handler = Serve(sums, Sign(sums, other), Setup);
        var downloader = new UpdateDownloader(new HttpClient(handler), _directory, PublicKey);

        var result = await downloader.DownloadAsync(_release, null, TestContext.Current.CancellationToken);

        Assert.Equal(UpdateDownloadStatus.BadSignature, result.Status);
        Assert.DoesNotContain(handler.Requested, u => u.AbsolutePath.EndsWith(".exe", StringComparison.Ordinal));
        Assert.Empty(Directory.GetFiles(_directory));
    }

    [Fact]
    public async Task Changed_checksums_break_the_signature()
    {
        var sums = Sums(Setup);
        var signature = Sign(sums);
        var tampered = Sums(Encoding.ASCII.GetBytes("a different installer"));
        var downloader = new UpdateDownloader(new HttpClient(Serve(tampered, signature, Setup)), _directory, PublicKey);

        var result = await downloader.DownloadAsync(_release, null, TestContext.Current.CancellationToken);

        Assert.Equal(UpdateDownloadStatus.BadSignature, result.Status);
    }

    [Fact]
    public async Task A_setup_that_does_not_match_its_checksum_is_deleted()
    {
        var sums = Sums(Setup);
        var downloader = new UpdateDownloader(new HttpClient(Serve(sums, Sign(sums), Encoding.ASCII.GetBytes("swapped installer"))), _directory, PublicKey);

        var result = await downloader.DownloadAsync(_release, null, TestContext.Current.CancellationToken);

        Assert.Equal(UpdateDownloadStatus.HashMismatch, result.Status);
        Assert.Null(result.SetupPath);
        Assert.DoesNotContain(Directory.GetFiles(_directory), f => f.EndsWith(".exe", StringComparison.Ordinal) || f.EndsWith(".part", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Without_a_built_in_key_nothing_is_downloaded()
    {
        var sums = Sums(Setup);
        var handler = Serve(sums, Sign(sums), Setup);
        var downloader = new UpdateDownloader(new HttpClient(handler), _directory, publicKeyPem: null);

        var result = await downloader.DownloadAsync(_release, null, TestContext.Current.CancellationToken);

        Assert.Equal(UpdateDownloadStatus.NoSigningKey, result.Status);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task A_release_without_signature_is_not_installed()
    {
        var unsigned = _release with { Signature = null };
        var sums = Sums(Setup);
        var downloader = new UpdateDownloader(new HttpClient(Serve(sums, Sign(sums), Setup)), _directory, PublicKey);

        var result = await downloader.DownloadAsync(unsigned, null, TestContext.Current.CancellationToken);

        Assert.Equal(UpdateDownloadStatus.NotSigned, result.Status);
    }

    [Fact]
    public void Keys_on_other_curves_are_refused()
    {
        using var p384 = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        var data = Encoding.ASCII.GetBytes("checksums");

        Assert.False(UpdateSignature.Verify(data, p384.SignData(data, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence), p384.ExportSubjectPublicKeyInfoPem()));
        Assert.True(UpdateSignature.Verify(data, Sign(data), PublicKey));
        Assert.False(UpdateSignature.Verify(data, Sign(data), "not a key"));
    }

    [Fact]
    public void Checksum_lines_are_read_as_sha256sum_writes_them()
    {
        var hash = new string('a', 64);
        var sums = ChecksumFile.Parse($"{hash.ToUpperInvariant()}  DeveloperIsland-Setup-0.5.0.exe\r\n{hash} *other.zip\nbroken line\n");

        Assert.Equal(hash, sums["DeveloperIsland-Setup-0.5.0.exe"]);
        Assert.Equal(hash, sums["other.zip"]);
        Assert.Equal(2, sums.Count);
    }
}
