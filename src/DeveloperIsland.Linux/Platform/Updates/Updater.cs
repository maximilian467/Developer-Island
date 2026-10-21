using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using DeveloperIsland.Core.Diagnostics;

namespace DeveloperIsland.Platform.Updates;

/// <summary>
/// In-place updates without reinstalling and without root.
/// <para>
/// An update is a <c>.tar.gz</c> dropped into <c>~/.local/share/developer-island/updates/</c> (or handed to
/// <c>developer-island --update file.tar.gz</c>). It can be a full build (the portable tarball) or a small
/// patch holding only the files that changed: either way it is laid over a copy of the running app in
/// <c>~/.local/share/developer-island/app/</c>, which is swapped in atomically (the previous copy is kept as
/// <c>app.old</c>). The running island notices the file within seconds and restarts into the new build.
/// </para>
/// <para>
/// The packaged launcher (<c>/usr/bin/developer-island</c>, the menu entry, autostart) always starts the
/// newest build: when the per-user copy is newer than the installed package it hands over to it, and when
/// a newer package is installed later the package wins again.
/// </para>
/// </summary>
internal static partial class Updater
{
    private const string Executable = "developer-island";
    private const string MainAssembly = "developer-island.dll";

    public static string UpdatesFolder => Path.Combine(AppPaths.Root, "updates");

    public static string AppFolder => Path.Combine(AppPaths.Root, "app");

    private static string StagingFolder => Path.Combine(AppPaths.Root, "app.staging");

    private static string PreviousFolder => Path.Combine(AppPaths.Root, "app.old");

    private static string AppliedFolder => Path.Combine(UpdatesFolder, "applied");

    public static Version CurrentVersion { get; } = typeof(Updater).Assembly.GetName().Version ?? new Version(0, 0);

    /// <summary>The folder the running build lives in.</summary>
    public static string RunningFolder => AppContext.BaseDirectory.TrimEnd('/');

    /// <summary><c>--update file.tar.gz</c>: queue an update file for the running (or next) island.</summary>
    public static int QueueFromCommandLine(string file)
    {
        if (!File.Exists(file) || !file.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine("Usage: developer-island --update <file.tar.gz>");
            return 2;
        }

        Directory.CreateDirectory(UpdatesFolder);
        var target = Path.Combine(UpdatesFolder, Path.GetFileName(file));
        File.Copy(file, target + ".part", overwrite: true);
        File.Move(target + ".part", target, overwrite: true);
        Console.WriteLine($"Queued {Path.GetFileName(file)}. A running island applies it within a few seconds; otherwise it is applied at the next start.");
        return 0;
    }

    /// <summary>Update files waiting in the updates folder, oldest first (partial copies are skipped).</summary>
    public static IReadOnlyList<FileInfo> Pending()
    {
        try
        {
            var folder = new DirectoryInfo(UpdatesFolder);
            return folder.Exists
                ? [.. folder.EnumerateFiles("*.tar.gz").OrderBy(f => f.LastWriteTimeUtc)]
                : [];
        }
        catch (IOException)
        {
            return [];
        }
    }

    /// <summary>
    /// Applies every pending update to a fresh copy of the newest build and swaps it in.
    /// Returns true when a new build is in <see cref="AppFolder"/>.
    /// </summary>
    public static bool ApplyPending()
    {
        var pending = Pending();
        if (pending.Count == 0)
        {
            return false;
        }

        try
        {
            var baseFolder = NewestBuild() ?? RunningFolder;
            if (Directory.Exists(StagingFolder))
            {
                Directory.Delete(StagingFolder, recursive: true);
            }

            CopyFolder(baseFolder, StagingFolder);
            Directory.CreateDirectory(AppliedFolder);
            foreach (var file in pending)
            {
                Extract(file.FullName, StagingFolder);
                File.Move(file.FullName, Path.Combine(AppliedFolder, file.Name), overwrite: true);
            }

            var executable = Path.Combine(StagingFolder, Executable);
            if (!File.Exists(executable) || !File.Exists(Path.Combine(StagingFolder, MainAssembly)))
            {
                throw new InvalidDataException("The update does not contain a complete build");
            }

            File.SetUnixFileMode(executable, File.GetUnixFileMode(executable) | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);

            // Swap: the running copy may be app/ itself; renaming a folder in use is fine on Linux.
            if (Directory.Exists(PreviousFolder))
            {
                Directory.Delete(PreviousFolder, recursive: true);
            }

            if (Directory.Exists(AppFolder))
            {
                Directory.Move(AppFolder, PreviousFolder);
            }

            Directory.Move(StagingFolder, AppFolder);
            Log.Info("update", "Update applied", new { version = VersionOf(AppFolder)?.ToString(), files = string.Join(", ", pending.Select(f => f.Name)) });
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("update", "Update could not be applied; keeping the current build", ex);
            foreach (var file in pending.Where(f => f.Exists))
            {
                try
                {
                    File.Move(file.FullName, file.FullName + ".failed", overwrite: true);
                }
                catch (IOException)
                {
                }
            }

            return false;
        }
    }

    /// <summary>
    /// Startup: apply what is pending, then make sure the newest build runs. Returns only when this
    /// process is the newest build (otherwise the process is replaced).
    /// </summary>
    public static void EnsureNewest(string[] args)
    {
        try
        {
            ApplyPending();
            if (NewestBuild() is { } newest && !SameFolder(newest, RunningFolder))
            {
                Exec(Path.Combine(newest, Executable), args);
            }
        }
        catch (Exception ex)
        {
            Log.Warn("update", "Update check at startup failed", ex: ex);
        }
    }

    /// <summary>
    /// Watches the updates folder (polling: some desktops run out of inotify watches) and calls
    /// <paramref name="ready"/> on the UI side when a new build was swapped in.
    /// </summary>
    public static IDisposable Watch(Action ready)
    {
        Directory.CreateDirectory(UpdatesFolder);
        var stop = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
            var sizes = new Dictionary<string, long>();
            while (await timer.WaitForNextTickAsync(stop.Token).ConfigureAwait(false))
            {
                var pending = Pending();
                if (pending.Count == 0)
                {
                    sizes.Clear();
                    continue;
                }

                // Wait until each file stopped growing (it may still be copying in).
                var settled = pending.All(f => sizes.TryGetValue(f.FullName, out var size) && size == f.Length);
                sizes = pending.ToDictionary(f => f.FullName, f => f.Length);
                if (settled && ApplyPending())
                {
                    ready();
                    return;
                }
            }
        });
        return new Stopper(stop);
    }

    /// <summary>Starts the newest build after this process has exited (the single-instance socket is free by then).</summary>
    public static void RestartAfterExit(string[] args)
    {
        var target = Path.Combine(NewestBuild() ?? RunningFolder, Executable);
        var quoted = string.Join(' ', args.Where(a => a != "--autostart").Select(Quote));
        var info = new ProcessStartInfo("/bin/sh")
        {
            UseShellExecute = false,
            ArgumentList = { "-c", $"while kill -0 {Environment.ProcessId} 2>/dev/null; do sleep 0.2; done; exec {Quote(target)} {quoted}" },
        };
        using var _ = Process.Start(info);
        Log.Info("update", "Restarting into the updated build", new { target });
    }

    /// <summary>The newest complete build among the per-user copy and the running one, or null for "this one".</summary>
    private static string? NewestBuild()
    {
        var mine = CurrentVersion;
        var user = VersionOf(AppFolder);
        if (user is not null && File.Exists(Path.Combine(AppFolder, Executable)) && user > mine)
        {
            return AppFolder;
        }

        return null;
    }

    private static Version? VersionOf(string folder)
    {
        try
        {
            var assembly = Path.Combine(folder, MainAssembly);
            return File.Exists(assembly) ? AssemblyName.GetAssemblyName(assembly).Version : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Extracts over <paramref name="target"/>, dropping a single top folder if the archive has one.</summary>
    private static void Extract(string archive, string target)
    {
        using var file = File.OpenRead(archive);
        using var gzip = new GZipStream(file, CompressionMode.Decompress);
        using var tar = new TarReader(gzip);
        var entries = new List<(string Name, TarEntryType Type, UnixFileMode Mode, byte[]? Data)>();
        while (tar.GetNextEntry(copyData: true) is { } entry)
        {
            byte[]? data = null;
            if (entry.DataStream is { } stream)
            {
                using var buffer = new MemoryStream();
                stream.CopyTo(buffer);
                data = buffer.ToArray();
            }

            entries.Add((entry.Name.TrimStart('.', '/'), entry.EntryType, entry.Mode, data));
        }

        var names = entries.Select(e => e.Name).Where(n => n.Length > 0).ToList();
        var top = names.Select(n => n.Split('/')[0]).Distinct().ToList();
        var strip = top.Count == 1 && names.Any(n => n.Contains('/')) && !names.Contains(Executable) ? top[0] + "/" : string.Empty;
        var root = Path.GetFullPath(target) + "/";
        foreach (var (name, type, mode, data) in entries)
        {
            var relative = strip.Length > 0 && name.StartsWith(strip, StringComparison.Ordinal) ? name[strip.Length..] : name;
            if (relative.Length == 0)
            {
                continue;
            }

            var path = Path.GetFullPath(Path.Combine(target, relative));
            if (!path.StartsWith(root, StringComparison.Ordinal))
            {
                throw new InvalidDataException($"Unsafe path in update: {name}");
            }

            if (type == TarEntryType.Directory)
            {
                Directory.CreateDirectory(path);
            }
            else if (type is TarEntryType.RegularFile or TarEntryType.V7RegularFile && data is not null)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllBytes(path, data);
                File.SetUnixFileMode(path, mode | UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
        }
    }

    private static void CopyFolder(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(target, Path.GetRelativePath(source, directory)));
        }

        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var destination = Path.Combine(target, Path.GetRelativePath(source, file));
            File.Copy(file, destination, overwrite: true);
            File.SetUnixFileMode(destination, File.GetUnixFileMode(file) | UnixFileMode.UserWrite);
        }
    }

    private static bool SameFolder(string a, string b) =>
        string.Equals(Path.GetFullPath(a).TrimEnd('/'), Path.GetFullPath(b).TrimEnd('/'), StringComparison.Ordinal);

    private static string Quote(string value) => "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";

    /// <summary>Replaces this process with <paramref name="path"/> (same pid, same arguments).</summary>
    private static void Exec(string path, string[] args)
    {
        Log.Info("update", "Handing over to the newer build", new { path });
        Log.Flush();
        var argv = new IntPtr[args.Length + 2];
        try
        {
            argv[0] = Marshal.StringToCoTaskMemUTF8(path);
            for (var i = 0; i < args.Length; i++)
            {
                argv[i + 1] = Marshal.StringToCoTaskMemUTF8(args[i]);
            }

            execv(path, argv);

            // Only returns on failure: keep running the current build.
            Log.Warn("update", "Could not start the newer build", new { errno = Marshal.GetLastPInvokeError() });
        }
        finally
        {
            foreach (var pointer in argv.Where(p => p != IntPtr.Zero))
            {
                Marshal.FreeCoTaskMem(pointer);
            }
        }
    }

    [LibraryImport("libc", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int execv(string path, IntPtr[] argv);

    private sealed class Stopper(CancellationTokenSource stop) : IDisposable
    {
        public void Dispose() => stop.Cancel();
    }
}
