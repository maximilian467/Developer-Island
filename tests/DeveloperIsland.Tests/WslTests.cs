using System.Text;
using DeveloperIsland.Core.Diagnostics;
using DeveloperIsland.Core.Git;
using DeveloperIsland.Core.Models;
using DeveloperIsland.Core.Modules;
using DeveloperIsland.Core.Pricing;
using DeveloperIsland.Core.Providers.Claude;
using DeveloperIsland.Core.Storage;
using DeveloperIsland.Core.Usage;
using DeveloperIsland.Core.Wsl;
using Microsoft.Extensions.Time.Testing;

namespace DeveloperIsland.Tests;

/// <summary>WSL support without WSL: paths, wsl.exe output, git command lines and the polling rules.</summary>
public class WslTests
{
    // Paths ------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("/home/dev/proj", @"\\wsl.localhost\Ubuntu\home\dev\proj")]
    [InlineData("/home/dev/proj/", @"\\wsl.localhost\Ubuntu\home\dev\proj")]
    [InlineData("/", @"\\wsl.localhost\Ubuntu\")]
    [InlineData("/mnt/c/Users/dev/code", @"C:\Users\dev\code")]
    [InlineData("/mnt/d", @"D:\")]
    [InlineData("/mnt/wsl/shared", @"\\wsl.localhost\Ubuntu\mnt\wsl\shared")]
    [InlineData(@"C:\code\island", @"C:\code\island")]
    [InlineData("relative/path", "relative/path")]
    public void Linux_paths_become_windows_paths(string linux, string expected) =>
        Assert.Equal(expected, WslPath.ToWindows("Ubuntu", linux));

    [Fact]
    public void Null_and_invalid_distros_leave_the_path_alone()
    {
        Assert.Null(WslPath.ToWindows("Ubuntu", null));
        Assert.Equal("/home/dev", WslPath.ToWindows("bad name", "/home/dev"));
    }

    [Theory]
    [InlineData(@"\\wsl.localhost\Ubuntu-22.04\home\dev\proj", "Ubuntu-22.04", "/home/dev/proj")]
    [InlineData(@"\\wsl$\Ubuntu\home\dev\proj\", "Ubuntu", "/home/dev/proj")]
    [InlineData("//wsl.localhost/Debian/home/dev", "Debian", "/home/dev")]
    [InlineData(@"\\WSL.LOCALHOST\Ubuntu", "Ubuntu", "/")]
    public void Windows_share_paths_become_linux_paths(string windows, string distro, string linux)
    {
        Assert.True(WslPath.TryParse(windows, out var parsedDistro, out var parsedLinux));
        Assert.Equal(distro, parsedDistro);
        Assert.Equal(linux, parsedLinux);
        Assert.Equal(linux, WslPath.ToLinux(windows));
    }

    [Theory]
    [InlineData(@"C:\code\island")]
    [InlineData(@"\\server\share\code")]
    [InlineData(@"\\wsl.localhost\")]
    [InlineData(@"\\wsl$\bad name\home")]
    [InlineData("")]
    [InlineData(null)]
    public void Other_paths_are_not_wsl_paths(string? path)
    {
        Assert.False(WslPath.IsWslPath(path));
        Assert.Null(WslPath.ToLinux(path));
        Assert.Null(WslPath.DistroOf(path));
    }

    [Theory]
    [InlineData("/home/dev/proj")]
    [InlineData("/root/.claude")]
    [InlineData("/")]
    public void Paths_round_trip(string linux) =>
        Assert.Equal(linux, WslPath.ToLinux(WslPath.ToWindows("Ubuntu", linux)));

    [Fact]
    public void Both_shares_normalize_to_one_spelling()
    {
        Assert.Equal(@"\\wsl.localhost\Ubuntu\home\dev\proj", WslPath.Normalize(@"\\wsl$\Ubuntu\home\dev\proj\"));
        Assert.Equal(@"\\wsl.localhost\Ubuntu\home\dev\proj", WslPath.Normalize("//wsl.localhost/Ubuntu/home/dev/proj"));
        Assert.Equal(@"C:\code", WslPath.Normalize(@"C:\code"));
    }

    // wsl.exe output -----------------------------------------------------------------------------------

    /// <summary>What <c>wsl.exe --list --verbose</c> printed on a real machine (UTF-16, CRLF, padded columns).</summary>
    private const string VerboseOutput =
        "  NAME              STATE           VERSION\r\n" +
        "* Ubuntu-22.04      Stopped         2\r\n" +
        "  docker-desktop    Stopped         2\r\n";

    [Fact]
    public void Verbose_list_is_read_from_utf16_with_the_default_marker()
    {
        var output = Encoding.Unicode.GetString(Encoding.Unicode.GetBytes(VerboseOutput));
        var distros = WslDistroList.ParseVerbose(output);

        Assert.Equal(
            [new WslDistro("Ubuntu-22.04", true, 2), new WslDistro("docker-desktop", false, 2)],
            distros);
    }

    [Fact]
    public void Utf16_read_as_utf8_and_a_byte_order_mark_still_parse()
    {
        var bytes = Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes(VerboseOutput)).ToArray();
        var misread = Encoding.UTF8.GetString(bytes); // NUL between every letter

        Assert.Contains('\0', misread);
        Assert.Equal(["Ubuntu-22.04", "docker-desktop"], WslDistroList.ParseVerbose(misread).Select(d => d.Name));
    }

    [Fact]
    public void Localized_header_and_states_with_spaces_are_handled()
    {
        var output =
            "  NAME            STATUS             VERSION\r\n" +
            "* Ubuntu          Wird ausgeführt    2\r\n" +
            "  Debian          Beendet            1\r\n";

        Assert.Equal(
            [new WslDistro("Ubuntu", true, 2), new WslDistro("Debian", false, 1)],
            WslDistroList.ParseVerbose(output));
    }

    [Fact]
    public void Quiet_list_keeps_names_and_skips_messages()
    {
        Assert.Equal(["Ubuntu", "Debian"], WslDistroList.ParseQuiet("Ubuntu\r\nDebian\r\n\r\n"));
        Assert.Empty(WslDistroList.ParseQuiet("There are no running distributions.\r\n"));
        Assert.Empty(WslDistroList.ParseQuiet(string.Empty));
        Assert.Equal(["Ubuntu"], WslDistroList.ParseQuiet(Encoding.UTF8.GetString(Encoding.Unicode.GetBytes("Ubuntu\r\n"))));
    }

    [Fact]
    public void Tool_distros_are_hidden_and_the_default_comes_first()
    {
        var display = WslDistroList.ForDisplay(
        [
            new WslDistro("docker-desktop", false),
            new WslDistro("docker-desktop-data", false),
            new WslDistro("Alpine", false),
            new WslDistro("Ubuntu", true),
            new WslDistro("ubuntu", false),
        ]);

        Assert.Equal(["Ubuntu", "Alpine"], display.Select(d => d.Name));
    }

    [Fact]
    public void Homes_come_from_passwd_default_user_first()
    {
        var users = WslDistroList.ParsePasswd(
            "root:x:0:0:root:/root:/bin/bash\n" +
            "daemon:x:1:1:daemon:/usr/sbin:/usr/sbin/nologin\n" +
            "dev:x:1000:1000:,,,:/home/dev:/bin/bash\r\n" +
            "ops:x:1001:1001::/home/ops:/bin/zsh\n" +
            "nobody:x:65534:65534:nobody:/nonexistent:/usr/sbin/nologin\n" +
            "broken line\n");

        Assert.Equal(5, users.Count);
        Assert.Equal(["/home/ops", "/home/dev", "/root"], WslDistroList.CandidateHomes(users, defaultUid: 1001));
        Assert.Equal(["/home/dev", "/home/ops", "/root"], WslDistroList.CandidateHomes(users, defaultUid: null));
    }

    // WslService ---------------------------------------------------------------------------------------

    [Fact]
    public async Task Discovery_uses_the_registry_and_starts_nothing()
    {
        var calls = new List<IReadOnlyList<string>>();
        var wsl = new WslService(
            findWsl: () => "wsl.exe",
            readRegistry: () => [new WslDistro("docker-desktop", false), new WslDistro("Ubuntu", true, 2, 1000)],
            run: (_, args) => { calls.Add(args); return Task.FromResult(new ProcessResult(0, string.Empty, string.Empty)); });

        var distros = await wsl.DiscoverAsync();

        Assert.Equal(["Ubuntu"], distros.Select(d => d.Name));
        Assert.Empty(calls);
    }

    [Fact]
    public async Task Discovery_falls_back_to_the_verbose_list()
    {
        var calls = new List<IReadOnlyList<string>>();
        var wsl = new WslService(
            findWsl: () => "wsl.exe",
            readRegistry: () => [],
            run: (_, args) => { calls.Add(args); return Task.FromResult(new ProcessResult(0, VerboseOutput, string.Empty)); });

        var distros = await wsl.DiscoverAsync();

        Assert.Equal(["Ubuntu-22.04"], distros.Select(d => d.Name));
        Assert.Equal([WslCommand.ListVerbose], calls);
    }

    [Fact]
    public async Task Only_included_distros_are_read_and_wsl_off_reads_none()
    {
        var wsl = new WslService(findWsl: () => null, readRegistry: () => [new WslDistro("Ubuntu", true), new WslDistro("Debian", false)]);
        await wsl.DiscoverAsync();

        Assert.Empty(wsl.EnabledDistros);
        wsl.Configure(true, ["debian"]);
        Assert.Equal(["Ubuntu"], wsl.EnabledDistros.Select(d => d.Name));
        wsl.Configure(false, []);
        Assert.Empty(wsl.EnabledDistros);
        Assert.Empty(await wsl.GetToolFoldersAsync(".claude", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Running_distros_are_asked_at_most_every_15_seconds()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var calls = 0;
        var wsl = new WslService(
            findWsl: () => "wsl.exe",
            readRegistry: () => [],
            run: (_, args) =>
            {
                Assert.Equal(WslCommand.ListRunning, args);
                Interlocked.Increment(ref calls);
                return Task.FromResult(new ProcessResult(0, "Ubuntu\r\n", string.Empty));
            },
            time: time);

        Assert.True(await wsl.IsRunningAsync("ubuntu", TestContext.Current.CancellationToken));
        Assert.False(await wsl.IsRunningAsync("Debian", TestContext.Current.CancellationToken));
        time.Advance(TimeSpan.FromSeconds(10));
        Assert.True(await wsl.IsRunningAsync("Ubuntu", TestContext.Current.CancellationToken));
        Assert.Equal(1, calls);

        time.Advance(TimeSpan.FromSeconds(6));
        await wsl.IsRunningAsync("Ubuntu", TestContext.Current.CancellationToken);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task Without_wsl_nothing_runs()
    {
        var wsl = new WslService(findWsl: () => null, readRegistry: () => [], run: (_, _) => throw new InvalidOperationException("must not run"));

        Assert.False(await wsl.IsRunningAsync("Ubuntu", TestContext.Current.CancellationToken));
        Assert.Empty(await wsl.DiscoverAsync());
    }

    // Git command lines --------------------------------------------------------------------------------

    [Fact]
    public void Wsl_repositories_run_the_distros_git_with_the_same_status_arguments()
    {
        var command = GitCommandLine.For(@"\\wsl$\Ubuntu\home\dev\proj", GitCommandLine.Status, git: @"C:\Git\git.exe", wsl: @"C:\Windows\System32\wsl.exe");

        Assert.NotNull(command);
        Assert.Equal(@"C:\Windows\System32\wsl.exe", command.FileName);
        Assert.Equal(
            ["--distribution", "Ubuntu", "--cd", "/home/dev/proj", "--exec", "git",
             "--no-optional-locks", "-c", "core.fsmonitor=false", "status", "--porcelain=v2", "--branch"],
            command.Arguments);
    }

    [Fact]
    public void Windows_repositories_keep_git_for_windows()
    {
        var command = GitCommandLine.For(@"C:\code\island", GitCommandLine.Status, git: "git.exe", wsl: "wsl.exe");

        Assert.NotNull(command);
        Assert.Equal("git.exe", command.FileName);
        Assert.Equal(["-C", @"C:\code\island", "--no-optional-locks", "-c", "core.fsmonitor=false", "status", "--porcelain=v2", "--branch"], command.Arguments);
    }

    [Fact]
    public void A_missing_program_gives_no_command()
    {
        Assert.Null(GitCommandLine.For(@"\\wsl.localhost\Ubuntu\home\dev\proj", GitCommandLine.Status, git: "git.exe", wsl: null));
        Assert.Null(GitCommandLine.For(@"C:\code\island", GitCommandLine.Status, git: null, wsl: "wsl.exe"));
    }

    // GitService with WSL repositories -----------------------------------------------------------------

    private const string WslRepo = @"\\wsl.localhost\Ubuntu\home\dev\proj";

    private sealed class FakeWsl(bool running, string? executable = "wsl.exe") : IWslRuntime
    {
        public bool Running { get; set; } = running;

        public string? Executable => executable;

        public Task<bool> IsRunningAsync(string distro, CancellationToken cancellationToken = default) => Task.FromResult(Running);
    }

    /// <summary>Stands in for git inside the distro; records every command line.</summary>
    private sealed class FakeGit
    {
        public List<(string File, IReadOnlyList<string> Args)> Calls { get; } = [];

        public bool GitInstalled { get; set; } = true;

        public int StatusCalls => Calls.Count(c => c.Args.Contains("status"));

        public event Action? Ran;

        public Task<ProcessResult> Run(string file, IReadOnlyList<string> args, TimeSpan timeout)
        {
            lock (Calls)
            {
                Calls.Add((file, args));
            }

            var result = !GitInstalled ? new ProcessResult(1, string.Empty, "execvpe(git) failed: No such file or directory")
                : args.Contains("status") ? new ProcessResult(0, "# branch.oid 1234567890\n# branch.head main\n# branch.upstream origin/main\n# branch.ab +1 -0\n1 .M N... 100644 100644 100644 a a src/app.py\n", string.Empty)
                : args.Contains("log") ? new ProcessResult(0, "1760000000\u001fInitial commit\n", string.Empty)
                : args.Contains("config") ? new ProcessResult(0, "git@github.com:owner/proj.git\n", string.Empty)
                : new ProcessResult(0, "git version 2.43.0\n", string.Empty);
            Ran?.Invoke();
            return Task.FromResult(result);
        }
    }

    private static async Task WaitForAsync(GitService git, Func<bool> condition)
    {
        var done = new TaskCompletionSource();
        git.Changed += () => { if (condition()) done.TrySetResult(); };
        if (!condition())
        {
            await done.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task Configured_wsl_repository_is_read_with_git_inside_the_distro()
    {
        var fake = new FakeGit();
        using var git = new GitService(findGit: () => null, wsl: new FakeWsl(running: true), run: fake.Run);

        git.Configure(true, [@"\\wsl$\Ubuntu\home\dev\proj\"], []);
        await WaitForAsync(git, () => git.Active is not null);

        var active = git.Active!;
        Assert.Equal(ModuleState.Ready, git.Status.State);
        Assert.Equal(WslRepo, active.Root);
        Assert.Equal("proj", active.Name);
        Assert.Equal("main", active.Branch);
        Assert.Equal(1, active.Ahead);
        Assert.Equal(1, active.Modified);
        Assert.Equal("Initial commit", active.LastCommitSubject);
        Assert.Equal("owner/proj", active.GitHubRepository);
        Assert.All(fake.Calls, c => Assert.Equal("wsl.exe", c.File));
        Assert.Equal(WslCommand.Exec("Ubuntu", "/home/dev/proj", "git", GitCommandLine.Status), fake.Calls[0].Args);
    }

    [Fact]
    public async Task A_stopped_distro_is_never_started_and_says_so()
    {
        var fake = new FakeGit();
        using var git = new GitService(findGit: () => "git.exe", wsl: new FakeWsl(running: false), run: fake.Run);

        git.Configure(true, [WslRepo], []);
        await WaitForAsync(git, () => git.Status.State == ModuleState.Error);

        Assert.Equal("Ubuntu is not running", git.Status.Title);
        Assert.Empty(fake.Calls);
        Assert.Equal("Ubuntu is not running, so its repositories are not shown.", git.Note);
    }

    [Fact]
    public async Task Missing_git_inside_the_distro_is_named()
    {
        var fake = new FakeGit { GitInstalled = false };
        using var git = new GitService(findGit: () => null, wsl: new FakeWsl(running: true), run: fake.Run);

        git.Configure(true, [WslRepo], []);
        await WaitForAsync(git, () => git.Status.State == ModuleState.Error);

        Assert.Equal("Git not found in Ubuntu", git.Status.Title);
        Assert.Contains(fake.Calls, c => c.Args.SequenceEqual(WslCommand.Exec("Ubuntu", "/", "git", GitCommandLine.Version)));
    }

    [Fact]
    public async Task Missing_wsl_is_named()
    {
        var fake = new FakeGit();
        using var git = new GitService(findGit: () => "git.exe", wsl: new FakeWsl(running: true, executable: null), run: fake.Run);

        git.Configure(true, [WslRepo], []);
        await WaitForAsync(git, () => git.Status.State == ModuleState.Error);

        Assert.Equal("WSL not found", git.Status.Title);
        Assert.Empty(fake.Calls);
    }

    [Fact]
    public void Without_git_for_windows_a_wsl_repository_is_not_unavailable()
    {
        using var git = new GitService(findGit: () => null, wsl: new FakeWsl(running: false), run: new FakeGit().Run);

        git.Configure(true, [], []);
        Assert.Equal(ModuleState.Unavailable, git.Status.State);

        git.Configure(true, [WslRepo], []);
        Assert.NotEqual(ModuleState.Unavailable, git.Status.State);
    }

    [Fact]
    public async Task Wsl_repositories_are_polled_only_while_the_panel_is_visible()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var fake = new FakeGit();
        using var git = new GitService(findGit: () => null, time: time, wsl: new FakeWsl(running: true), run: fake.Run);
        git.Configure(true, [WslRepo], []);
        await WaitForAsync(git, () => git.Active is not null);
        Assert.Equal(1, fake.StatusCalls);

        time.Advance(TimeSpan.FromSeconds(30));
        await Task.Delay(300, TestContext.Current.CancellationToken);
        Assert.Equal(1, fake.StatusCalls); // hidden: no timer, no git

        var again = new TaskCompletionSource();
        fake.Ran += () => { if (fake.StatusCalls >= 2) again.TrySetResult(); };
        git.SetVisible(true);
        time.Advance(TimeSpan.FromSeconds(10));
        await again.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        git.SetVisible(false);
        await Task.Delay(300, TestContext.Current.CancellationToken);
        var calls = fake.StatusCalls;
        time.Advance(TimeSpan.FromSeconds(30));
        await Task.Delay(300, TestContext.Current.CancellationToken);
        Assert.Equal(calls, fake.StatusCalls);
    }

    // Usage from WSL folders ---------------------------------------------------------------------------

    [Fact]
    public async Task Usage_from_a_wsl_folder_counts_once_even_when_reached_twice()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        using var db = UsageDatabase.OpenInMemory();
        var history = new UsageHistoryService(db, new ModelPricingService(), time);
        var root = TestData.TempDirectory();
        var windowsRoot = Path.Combine(root, "windows", ".claude"); // not installed on Windows
        var first = Path.Combine(root, "first", ".claude");
        var second = Path.Combine(root, "second", ".claude");
        var line = TestData.ClaudeAssistantLine("m1", "r1", time.GetUtcNow(), input: 100, output: 10, cacheCreation: 0, cacheRead: 0, cwd: "/home/dev/proj") + "\n";
        foreach (var folder in new[] { first, second })
        {
            File.WriteAllText(Path.Combine(Directory.CreateDirectory(Path.Combine(folder, "projects", "-home-dev-proj")).FullName, "s.jsonl"), line);
        }

        IReadOnlyList<string> folders = [first];
        using var provider = new ClaudeUsageProvider(windowsRoot, history, db, time);
        provider.SetWslFolders(_ => Task.FromResult(folders));
        await provider.StartAsync(TestContext.Current.CancellationToken);
        Assert.Equal(ProviderState.NotDetected, provider.Snapshot.State);

        await provider.PollWslAsync();
        Assert.Equal(ProviderState.Ready, provider.Snapshot.State);
        Assert.Equal(110, provider.Snapshot.Today.Processed);
        Assert.Equal("proj", provider.Snapshot.CurrentProject);
        Assert.Equal("/home/dev/proj", provider.Snapshot.CurrentProjectPath); // not a WSL share here, so not translated

        // The same events through another path (a second spelling, a copied home): stored once.
        folders = [first, second, first.ToUpperInvariant()];
        await provider.PollWslAsync();
        Assert.Equal(110, provider.Snapshot.Today.Processed);

        // Appended lines are found by the next poll, before the next full rescan.
        time.Advance(TimeSpan.FromSeconds(5));
        File.AppendAllText(Path.Combine(first, "projects", "-home-dev-proj", "s.jsonl"),
            TestData.ClaudeAssistantLine("m2", "r2", time.GetUtcNow(), input: 100, output: 10, cacheCreation: 0, cacheRead: 0, cwd: "/home/dev/proj") + "\n");
        await provider.PollWslAsync();
        Assert.Equal(220, provider.Snapshot.Today.Processed);
    }

    [Fact]
    public async Task Turning_wsl_off_stops_reading_it()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        using var db = UsageDatabase.OpenInMemory();
        var history = new UsageHistoryService(db, new ModelPricingService(), time);
        var root = TestData.TempDirectory();
        var folder = Path.Combine(root, "wsl", ".claude");
        File.WriteAllText(Path.Combine(Directory.CreateDirectory(Path.Combine(folder, "projects", "p")).FullName, "s.jsonl"),
            TestData.ClaudeAssistantLine("m1", "r1", time.GetUtcNow(), input: 100, output: 10, cacheCreation: 0, cacheRead: 0) + "\n");
        var asked = 0;
        using var provider = new ClaudeUsageProvider(Path.Combine(root, "windows", ".claude"), history, db, time);
        provider.SetWslFolders(_ => { asked++; return Task.FromResult<IReadOnlyList<string>>([folder]); });
        await provider.StartAsync(TestContext.Current.CancellationToken);

        provider.SetWslFolders(null);
        await provider.PollWslAsync();

        Assert.Equal(0, asked);
        Assert.Equal(0, provider.Snapshot.Today.Processed);
    }
}
