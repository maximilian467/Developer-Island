using System.Runtime.Versioning;
using System.Text;
using DeveloperIsland.Core.Diagnostics;
using Microsoft.Win32;

namespace DeveloperIsland.Core.Wsl;

/// <summary>What the Git module and the usage providers need to know about WSL.</summary>
public interface IWslRuntime
{
    /// <summary>Full path of <c>wsl.exe</c>; null when WSL is not installed.</summary>
    string? Executable { get; }

    /// <summary>Whether the distro runs right now. Never starts it.</summary>
    Task<bool> IsRunningAsync(string distro, CancellationToken cancellationToken = default);
}

/// <summary>Arguments for <c>wsl.exe</c>, passed as a list (never through a shell).</summary>
public static class WslCommand
{
    public static readonly IReadOnlyList<string> ListVerbose = ["--list", "--verbose"];

    public static readonly IReadOnlyList<string> ListRunning = ["--list", "--running", "--quiet"];

    /// <summary>
    /// Runs <paramref name="file"/> inside <paramref name="distro"/> in <paramref name="linuxDirectory"/>.
    /// <c>--exec</c> starts the program directly, without a login shell: no profile scripts run and no
    /// argument is interpreted by a shell.
    /// </summary>
    public static IReadOnlyList<string> Exec(string distro, string linuxDirectory, string file, IEnumerable<string> arguments) =>
        ["--distribution", distro, "--cd", linuxDirectory, "--exec", file, .. arguments];
}

/// <summary>
/// WSL distros on this PC, for the "Include WSL" setting and for the modules that read inside them.
/// <list type="bullet">
/// <item>Discovery reads the registry (<c>HKCU\…\Lxss</c>), which starts nothing; <c>wsl.exe --list</c>
/// is the fallback.</item>
/// <item>Nothing here starts a stopped distro: callers ask <see cref="IsRunningAsync"/> first, which
/// asks <c>wsl.exe --list --running</c> (at most every 15 s), and only then touch
/// <c>\\wsl.localhost\&lt;distro&gt;</c>, which would boot it.</item>
/// </list>
/// </summary>
public sealed class WslService : IWslRuntime
{
    private static readonly TimeSpan RunningTtl = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan FoldersTtl = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan MissingFoldersTtl = TimeSpan.FromSeconds(30);
    private static readonly StringComparer NameComparer = StringComparer.OrdinalIgnoreCase;

    private readonly Func<IReadOnlyList<WslDistro>> _readRegistry;
    private readonly Func<string, IReadOnlyList<string>, Task<ProcessResult>> _run;
    private readonly TimeProvider _time;
    private readonly object _gate = new();
    private readonly Dictionary<(string Distro, string Folder), (IReadOnlyList<string> Folders, DateTimeOffset At)> _folders = new();
    private readonly Lazy<string?> _executable;
    private IReadOnlyList<WslDistro> _distros = [];
    private HashSet<string> _excluded = new(NameComparer);
    private bool _include;
    private Task<IReadOnlySet<string>>? _runningTask;
    private DateTimeOffset _runningAt = DateTimeOffset.MinValue;

    public WslService(
        Func<string?>? findWsl = null,
        Func<IReadOnlyList<WslDistro>>? readRegistry = null,
        Func<string, IReadOnlyList<string>, Task<ProcessResult>>? run = null,
        TimeProvider? time = null)
    {
        _executable = new Lazy<string?>(findWsl ?? (() => ProcessRunner.FindExecutable("wsl", @"%SystemRoot%\System32\wsl.exe")));
        _readRegistry = readRegistry ?? ReadRegistry;
        _run = run ?? ((file, args) => ProcessRunner.RunAsync(file, args, timeout: TimeSpan.FromSeconds(10), outputEncoding: Encoding.Unicode));
        _time = time ?? TimeProvider.System;
    }

    public string? Executable => _executable.Value;

    /// <summary>"Include WSL" is on.</summary>
    public bool Include
    {
        get
        {
            lock (_gate)
            {
                return _include;
            }
        }
    }

    /// <summary>The distros found by the last <see cref="DiscoverAsync"/>, default first, tool distros left out.</summary>
    public IReadOnlyList<WslDistro> Distros
    {
        get
        {
            lock (_gate)
            {
                return _distros;
            }
        }
    }

    /// <summary>Raised after the distro list or the WSL settings changed.</summary>
    public event Action? Changed;

    /// <summary>Applies the settings: whether WSL is read at all, and which distros the user unticked.</summary>
    public void Configure(bool include, IEnumerable<string> excluded)
    {
        lock (_gate)
        {
            _include = include;
            _excluded = new HashSet<string>(excluded, NameComparer);
        }

        Changed?.Invoke();
    }

    /// <summary>Distros whose logs are read: "Include WSL" on, found, and not unticked.</summary>
    public IReadOnlyList<WslDistro> EnabledDistros
    {
        get
        {
            lock (_gate)
            {
                return _include ? _distros.Where(d => !_excluded.Contains(d.Name)).ToList() : [];
            }
        }
    }

    /// <summary>Finds installed distros without starting any (registry first, then <c>wsl.exe --list</c>).</summary>
    public async Task<IReadOnlyList<WslDistro>> DiscoverAsync()
    {
        IReadOnlyList<WslDistro> found = [];
        try
        {
            found = _readRegistry();
            if (found.Count == 0 && Executable is { } wsl)
            {
                var result = await _run(wsl, WslCommand.ListVerbose);
                found = result.Succeeded ? WslDistroList.ParseVerbose(result.Output) : [];
            }
        }
        catch (Exception ex)
        {
            Log.Debug("wsl", "Distros not listed", new { error = ex.GetType().Name });
        }

        var distros = WslDistroList.ForDisplay(found);
        lock (_gate)
        {
            _distros = distros;
        }

        Log.Info("wsl", "Distros found", new { count = distros.Count });
        Changed?.Invoke();
        return distros;
    }

    public async Task<bool> IsRunningAsync(string distro, CancellationToken cancellationToken = default) =>
        (await GetRunningAsync()).Contains(distro);

    /// <summary>Names of running distros (cached for 15 s); empty without WSL.</summary>
    public Task<IReadOnlySet<string>> GetRunningAsync()
    {
        lock (_gate)
        {
            if (_runningTask is not null && (!_runningTask.IsCompleted || _time.GetUtcNow() - _runningAt < RunningTtl))
            {
                return _runningTask;
            }

            _runningAt = _time.GetUtcNow();
            _runningTask = ListRunningAsync();
            return _runningTask;
        }
    }

    /// <summary>
    /// The tool folders (<paramref name="folderName"/> is <c>.claude</c> or <c>.codex</c>) in the homes of
    /// every enabled, running distro, as <c>\\wsl.localhost\…</c> paths. Stopped distros are skipped,
    /// so reading never boots one.
    /// </summary>
    public async Task<IReadOnlyList<string>> GetToolFoldersAsync(string folderName, CancellationToken cancellationToken = default)
    {
        var enabled = EnabledDistros;
        if (enabled.Count == 0)
        {
            return [];
        }

        var running = await GetRunningAsync();
        var folders = new List<string>();
        foreach (var distro in enabled.Where(d => running.Contains(d.Name)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            folders.AddRange(await Task.Run(() => FindToolFolders(distro, folderName), cancellationToken));
        }

        return folders;
    }

    private IReadOnlyList<string> FindToolFolders(WslDistro distro, string folderName)
    {
        var key = (distro.Name, folderName);
        var now = _time.GetUtcNow();
        lock (_gate)
        {
            if (_folders.TryGetValue(key, out var cached) && now - cached.At < (cached.Folders.Count > 0 ? FoldersTtl : MissingFoldersTtl))
            {
                return cached.Folders;
            }
        }

        var found = new List<string>();
        try
        {
            var passwd = File.ReadAllText(WslPath.ToWindows(distro.Name, "/etc/passwd")!);
            foreach (var home in WslDistroList.CandidateHomes(WslDistroList.ParsePasswd(passwd), distro.DefaultUid))
            {
                var folder = WslPath.ToWindows(distro.Name, home.TrimEnd('/') + "/" + folderName)!;
                if (Directory.Exists(folder))
                {
                    found.Add(folder);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Debug("wsl", "Homes not readable", new { error = ex.GetType().Name });
        }

        lock (_gate)
        {
            _folders[key] = (found, now);
        }

        return found;
    }

    private async Task<IReadOnlySet<string>> ListRunningAsync()
    {
        if (Executable is not { } wsl)
        {
            return new HashSet<string>(NameComparer);
        }

        try
        {
            // With nothing running some builds exit non-zero and print a message instead of names;
            // ParseQuiet skips anything that is not a distro name either way.
            var result = await _run(wsl, WslCommand.ListRunning);
            return new HashSet<string>(WslDistroList.ParseQuiet(result.Output), NameComparer);
        }
        catch (Exception ex)
        {
            Log.Debug("wsl", "Running distros not listed", new { error = ex.GetType().Name });
            return new HashSet<string>(NameComparer);
        }
    }

    private static IReadOnlyList<WslDistro> ReadRegistry() => OperatingSystem.IsWindows() ? ReadLxss() : [];

    [SupportedOSPlatform("windows")]
    private static List<WslDistro> ReadLxss()
    {
        var distros = new List<WslDistro>();
        using var lxss = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Lxss");
        if (lxss is null)
        {
            return distros;
        }

        var defaultId = lxss.GetValue("DefaultDistribution") as string;
        foreach (var id in lxss.GetSubKeyNames())
        {
            using var key = lxss.OpenSubKey(id);
            if (key?.GetValue("DistributionName") is not string name || !WslPath.IsValidDistroName(name))
            {
                continue;
            }

            // State 1 is installed; other values mean installing or uninstalling.
            if (key.GetValue("State") is int state && state != 1)
            {
                continue;
            }

            distros.Add(new WslDistro(
                name,
                string.Equals(id, defaultId, StringComparison.OrdinalIgnoreCase),
                key.GetValue("Version") is int version ? version : 2,
                key.GetValue("DefaultUid") is int uid ? uid : null));
        }

        return distros;
    }
}
