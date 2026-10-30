using System.Security.Cryptography;
using System.Text;
using DeveloperIsland.Core.Diagnostics;

namespace DeveloperIsland.Core.Updates;

public enum UpdateDownloadStatus
{
    /// <summary>The setup is downloaded and matches the signed checksum.</summary>
    Ready,

    /// <summary>This build has no public key, so nothing can be verified.</summary>
    NoSigningKey,

    /// <summary>The release has no checksum file or no signature.</summary>
    NotSigned,

    /// <summary>The signature does not match the checksums and the built-in key.</summary>
    BadSignature,

    /// <summary>The signed checksums do not list the setup.</summary>
    NoChecksum,

    /// <summary>The downloaded setup does not match its checksum.</summary>
    HashMismatch,
    Network,
    Disk,
    Cancelled,
}

public sealed record UpdateDownloadResult(UpdateDownloadStatus Status, string? SetupPath = null)
{
    public bool IsReady => Status == UpdateDownloadStatus.Ready && SetupPath is not null;
}

/// <summary>
/// Downloads an update into its own folder and proves it before anything runs: first the checksum
/// file and its signature (verified against the key built into the app), then the setup, hashed while
/// it streams to a <c>.part</c> file that becomes the setup only if its SHA-256 matches the signed list.
/// Anything that fails verification is deleted.
/// </summary>
public sealed class UpdateDownloader
{
    private const long MaxSetupBytes = 512L * 1024 * 1024;
    private static readonly TimeSpan SmallFileTimeout = TimeSpan.FromSeconds(30);

    private readonly HttpClient _http;
    private readonly string _directory;
    private readonly string? _publicKeyPem;

    /// <param name="directory">%LOCALAPPDATA%\DeveloperIsland\updates; emptied of older downloads first.</param>
    public UpdateDownloader(HttpClient http, string directory, string? publicKeyPem)
    {
        _http = http;
        _directory = directory;
        _publicKeyPem = publicKeyPem;
    }

    /// <param name="progress">Share of the setup downloaded, 0 to 1.</param>
    public async Task<UpdateDownloadResult> DownloadAsync(ReleaseInfo release, IProgress<double>? progress, CancellationToken cancel)
    {
        if (_publicKeyPem is null)
        {
            return Fail(UpdateDownloadStatus.NoSigningKey);
        }

        if (release.Checksums is null || release.Signature is null)
        {
            return Fail(UpdateDownloadStatus.NotSigned);
        }

        var part = Path.Combine(_directory, release.Setup.Name + ".part");
        try
        {
            ClearDirectory();

            var sums = await GetSmallAsync(release.Checksums.Url, 64 * 1024, cancel);
            var signature = await GetSmallAsync(release.Signature.Url, 4 * 1024, cancel);
            if (!UpdateSignature.Verify(sums, signature, _publicKeyPem))
            {
                return Fail(UpdateDownloadStatus.BadSignature);
            }

            if (!ChecksumFile.Parse(Encoding.ASCII.GetString(sums)).TryGetValue(release.Setup.Name, out var expected))
            {
                return Fail(UpdateDownloadStatus.NoChecksum);
            }

            await File.WriteAllBytesAsync(Path.Combine(_directory, release.Checksums.Name), sums, cancel);
            await File.WriteAllBytesAsync(Path.Combine(_directory, release.Signature.Name), signature, cancel);

            var actual = await DownloadHashedAsync(release.Setup, part, progress, cancel);
            if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
            {
                TryDelete(part);
                return Fail(UpdateDownloadStatus.HashMismatch);
            }

            var setup = Path.Combine(_directory, release.Setup.Name);
            File.Move(part, setup, overwrite: true);
            Log.Info("update", "Update downloaded and verified");
            return new UpdateDownloadResult(UpdateDownloadStatus.Ready, setup);
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            TryDelete(part);
            return new UpdateDownloadResult(UpdateDownloadStatus.Cancelled);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or InvalidDataException)
        {
            TryDelete(part);
            return Fail(UpdateDownloadStatus.Network, ex);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TryDelete(part);
            return Fail(UpdateDownloadStatus.Disk, ex);
        }
    }

    private static UpdateDownloadResult Fail(UpdateDownloadStatus status, Exception? ex = null)
    {
        // The type only: IO messages name paths.
        Log.Warn("update", "Update not installed", new { status = status.ToString(), error = ex?.GetType().Name });
        return new UpdateDownloadResult(status);
    }

    private async Task<byte[]> GetSmallAsync(Uri url, int maxBytes, CancellationToken cancel)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        timeout.CancelAfter(SmallFileTimeout);
        using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(chunk, timeout.Token)) > 0)
        {
            buffer.Write(chunk, 0, read);
            if (buffer.Length > maxBytes)
            {
                throw new InvalidDataException("Release file is larger than expected");
            }
        }

        return buffer.ToArray();
    }

    /// <summary>Streams the setup to <paramref name="path"/> and returns its SHA-256 as lower-case hex.</summary>
    private async Task<string> DownloadHashedAsync(ReleaseAsset asset, string path, IProgress<double>? progress, CancellationToken cancel)
    {
        using var response = await _http.GetAsync(asset.Url, HttpCompletionOption.ResponseHeadersRead, cancel);
        response.EnsureSuccessStatusCode();
        var total = response.Content.Headers.ContentLength ?? asset.Size;
        if (total > MaxSetupBytes)
        {
            throw new InvalidDataException("Setup is larger than expected");
        }

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using (var source = await response.Content.ReadAsStreamAsync(cancel))
        await using (var target = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
        {
            var buffer = new byte[81920];
            long written = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, cancel)) > 0)
            {
                hash.AppendData(buffer, 0, read);
                await target.WriteAsync(buffer.AsMemory(0, read), cancel);
                written += read;
                if (written > MaxSetupBytes)
                {
                    throw new InvalidDataException("Setup is larger than expected");
                }

                if (total > 0)
                {
                    progress?.Report(Math.Min(1, (double)written / total));
                }
            }
        }

        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    /// <summary>Older downloads (an earlier update, an interrupted one) are not kept.</summary>
    private void ClearDirectory()
    {
        Directory.CreateDirectory(_directory);
        foreach (var file in Directory.EnumerateFiles(_directory))
        {
            TryDelete(file);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A setup still running from an earlier update keeps its file; it is removed next time.
        }
    }
}
