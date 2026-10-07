using System.Diagnostics;
using DeveloperIsland.Core.Diagnostics;

namespace DeveloperIsland.Core.HomeCloud;

/// <summary>
/// Copies files and folders into a target folder. Each file is written as <c>name.part</c> and renamed
/// when complete, so a half-written file never has its final name. Uploads run one after another.
/// </summary>
public sealed class HomeCloudUploader(IFileSink sink)
{
    private const int BufferSize = 4 * 1024 * 1024;
    private const string PartSuffix = ".part";

    private readonly SemaphoreSlim _queue = new(1, 1);

    private sealed record Item(string Source, string[] TargetSegments, bool IsDirectory, long Length);

    public async Task<HomeCloudUploadResult> UploadAsync(
        IReadOnlyList<string> sourcePaths,
        string targetDirectory,
        IProgress<HomeCloudProgress>? progress = null,
        CancellationToken ct = default)
    {
        try
        {
            await _queue.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return HomeCloudUploadResult.Canceled();
        }

        var watch = Stopwatch.StartNew();
        try
        {
            return await RunAsync(sourcePaths, targetDirectory, progress, ct, watch).ConfigureAwait(false);
        }
        finally
        {
            _queue.Release();
        }
    }

    private async Task<HomeCloudUploadResult> RunAsync(
        IReadOnlyList<string> sourcePaths,
        string targetDirectory,
        IProgress<HomeCloudProgress>? progress,
        CancellationToken ct,
        Stopwatch watch)
    {
        var files = 0;
        long copied = 0;
        long total = 0;
        try
        {
            ct.ThrowIfCancellationRequested();
            var items = new List<(string Source, string Name, List<Item> Entries)>();
            foreach (var source in sourcePaths)
            {
                var name = Path.GetFileName(source.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                var entries = new List<Item>();
                if (File.Exists(source))
                {
                    entries.Add(new Item(source, [], false, new FileInfo(source).Length));
                }
                else if (Directory.Exists(source))
                {
                    Collect(source, [], entries);
                }
                else
                {
                    return Fail("Source not found", files, copied, watch);
                }

                items.Add((source, name, entries));
            }

            var fileCount = items.Sum(i => i.Entries.Count(e => !e.IsDirectory));
            total = items.Sum(i => i.Entries.Sum(e => e.Length));
            if (!sink.DirectoryExists(targetDirectory))
            {
                sink.CreateDirectory(targetDirectory);
            }

            foreach (var (source, name, entries) in items)
            {
                ct.ThrowIfCancellationRequested();
                var isFolder = Directory.Exists(source);
                string? root = null;
                if (isFolder)
                {
                    root = Path.Combine(targetDirectory, UniqueFolderName(targetDirectory, name));
                    sink.CreateDirectory(root);
                }

                foreach (var entry in entries)
                {
                    ct.ThrowIfCancellationRequested();
                    if (entry.IsDirectory)
                    {
                        sink.CreateDirectory(Path.Combine([root!, .. entry.TargetSegments]));
                        continue;
                    }

                    var folder = root is null ? targetDirectory : Path.Combine([root, .. entry.TargetSegments[..^1]]);
                    var fileName = root is null ? name : entry.TargetSegments[^1];
                    files++;
                    var index = files;
                    await CopyFileAsync(entry.Source, folder, fileName, ct, delta =>
                    {
                        copied += delta;
                        progress?.Report(new HomeCloudProgress(copied, total, index, fileCount));
                    }).ConfigureAwait(false);
                    if (entry.Length == 0)
                    {
                        progress?.Report(new HomeCloudProgress(copied, total, index, fileCount));
                    }
                }
            }

            watch.Stop();
            Log.Info("homecloud", "upload finished", new { status = "Succeeded", files, bytes = copied, ms = watch.ElapsedMilliseconds });
            return HomeCloudUploadResult.Succeeded(files, copied, watch.Elapsed);
        }
        catch (OperationCanceledException)
        {
            watch.Stop();
            Log.Info("homecloud", "upload finished", new { status = "Canceled", files, bytes = copied, ms = watch.ElapsedMilliseconds });
            return HomeCloudUploadResult.Canceled(files, copied, watch.Elapsed);
        }
        catch (Exception ex)
        {
            return Fail(Describe(ex), files, copied, watch, ex);
        }
    }

    private static HomeCloudUploadResult Fail(string reason, int files, long bytes, Stopwatch watch, Exception? ex = null)
    {
        watch.Stop();
        Log.Warn("homecloud", "upload finished", new { status = "Failed", reason, files, bytes, ms = watch.ElapsedMilliseconds }, ex);
        return HomeCloudUploadResult.Failed(reason, files, bytes, watch.Elapsed);
    }

    private static void Collect(string directory, string[] segments, List<Item> entries)
    {
        foreach (var dir in Directory.EnumerateDirectories(directory))
        {
            string[] child = [.. segments, Path.GetFileName(dir)];
            entries.Add(new Item(dir, child, true, 0));
            Collect(dir, child, entries);
        }

        foreach (var file in Directory.EnumerateFiles(directory))
        {
            entries.Add(new Item(file, [.. segments, Path.GetFileName(file)], false, new FileInfo(file).Length));
        }
    }

    private string UniqueFolderName(string parent, string name)
    {
        var candidate = name;
        for (var n = 1; sink.DirectoryExists(Path.Combine(parent, candidate)) || sink.FileExists(Path.Combine(parent, candidate)); n++)
        {
            candidate = $"{name} ({n})";
        }

        return candidate;
    }

    private async Task CopyFileAsync(string source, string folder, string name, CancellationToken ct, Action<long> copiedBytes)
    {
        if (!sink.DirectoryExists(folder))
        {
            sink.CreateDirectory(folder);
        }

        var stem = Path.GetFileNameWithoutExtension(name);
        var extension = Path.GetExtension(name);
        string CandidateName(int n) => n == 0 ? name : $"{stem} ({n})" + extension;

        // Find a free name and claim it with its .part file (CreateNew fails if another writer has it).
        var attempt = 0;
        string part;
        Stream target;
        while (true)
        {
            var finalPath = Path.Combine(folder, CandidateName(attempt));
            part = finalPath + PartSuffix;
            if (!sink.FileExists(finalPath) && !sink.DirectoryExists(finalPath))
            {
                try
                {
                    target = sink.CreateNew(part);
                    break;
                }
                catch (IOException) when (sink.FileExists(part))
                {
                }
            }

            attempt++;
        }

        try
        {
            await using (target.ConfigureAwait(false))
            {
                await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
                var buffer = new byte[BufferSize];
                int read;
                while ((read = await input.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    await target.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    copiedBytes(read);
                }

                await target.FlushAsync(ct).ConfigureAwait(false);
            }

            // Another writer may have taken the final name meanwhile: move on to the next free one.
            while (true)
            {
                var finalPath = Path.Combine(folder, CandidateName(attempt));
                try
                {
                    sink.Move(part, finalPath);
                    return;
                }
                catch (IOException) when (sink.FileExists(finalPath) || sink.DirectoryExists(finalPath))
                {
                    attempt++;
                }
            }
        }
        catch
        {
            TryDelete(part);
            throw;
        }
    }

    private void TryDelete(string path)
    {
        try
        {
            sink.DeleteFile(path);
        }
        catch (Exception ex)
        {
            Log.Warn("homecloud", "could not remove partial file", null, ex);
        }
    }

    private static string Describe(Exception ex) => ex switch
    {
        UnauthorizedAccessException => "Access denied",
        DirectoryNotFoundException => "Target not reachable",
        IOException io when (io.HResult & 0xFFFF) is 0x27 or 0x70 => "Disk full",
        IOException io when (io.HResult & 0xFFFF) is 0x35 or 0x43 or 0x40 or 0x41 or 0x33 or 0x36 => "Target not reachable",
        _ => "Copy failed",
    };
}

