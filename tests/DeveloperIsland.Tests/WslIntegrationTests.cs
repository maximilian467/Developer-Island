using DeveloperIsland.Core.Diagnostics;
using DeveloperIsland.Core.Git;
using DeveloperIsland.Core.Modules;
using DeveloperIsland.Core.Pricing;
using DeveloperIsland.Core.Providers.Claude;
using DeveloperIsland.Core.Storage;
using DeveloperIsland.Core.Usage;
using DeveloperIsland.Core.Wsl;

namespace DeveloperIsland.Tests;

/// <summary>
/// Against a real distro, opt-in: set <c>DI_WSL_DISTRO</c> to the name of a running distro with git.
/// Skipped otherwise, and never starts a distro. Works only in a fresh folder under <c>/tmp</c>,
/// removed afterwards.
/// </summary>
public class WslIntegrationTests
{
    [Fact]
    public async Task Reads_git_and_usage_inside_a_running_distro()
    {
        var distro = Environment.GetEnvironmentVariable("DI_WSL_DISTRO");
        Assert.SkipWhen(string.IsNullOrWhiteSpace(distro), "DI_WSL_DISTRO is not set");
        var wsl = new WslService();
        Assert.SkipWhen(wsl.Executable is null, "wsl.exe not found");
        Assert.SkipUnless(await wsl.IsRunningAsync(distro!, TestContext.Current.CancellationToken), $"{distro} is not running");

        var ct = TestContext.Current.CancellationToken;
        var discovered = await wsl.DiscoverAsync();
        Assert.Contains(discovered, d => string.Equals(d.Name, distro, StringComparison.OrdinalIgnoreCase));

        // Homes come from the distro's own /etc/passwd over the WSL share.
        var users = WslDistroList.ParsePasswd(await File.ReadAllTextAsync(WslPath.ToWindows(distro!, "/etc/passwd")!, ct));
        var distroInfo = discovered.First(d => string.Equals(d.Name, distro, StringComparison.OrdinalIgnoreCase));
        Assert.NotEmpty(WslDistroList.CandidateHomes(users, distroInfo.DefaultUid));

        var linuxRoot = $"/tmp/di-wsl-test-{Guid.NewGuid():N}";
        var repoLinux = linuxRoot + "/proj";
        async Task Run(params string[] args) =>
            Assert.True((await ProcessRunner.RunAsync(wsl.Executable!, WslCommand.Exec(distro!, "/", args[0], args[1..]), cancellationToken: ct)).Succeeded, string.Join(' ', args));
        try
        {
            await Run("mkdir", "-p", repoLinux + "/src");
            await Run("git", "-C", repoLinux, "init", "-q", "-b", "main");
            await Run("git", "-C", repoLinux, "-c", "user.email=t@example.com", "-c", "user.name=T", "-c", "commit.gpgsign=false", "commit", "-q", "--allow-empty", "-m", "first commit");
            await Run("git", "-C", repoLinux, "remote", "add", "origin", "https://github.com/owner/proj.git");
            await File.WriteAllTextAsync(WslPath.ToWindows(distro!, repoLinux + "/src/new.txt")!, "new", ct);

            // Git: the distro's git, found from a sub folder an AI session reports.
            using var git = new GitService(findGit: () => null, wsl: wsl);
            var ready = new TaskCompletionSource();
            git.Changed += () => { if (git.Active is not null) ready.TrySetResult(); };
            git.Configure(true, [], []);
            git.NoteProjectPath(WslPath.ToWindows(distro!, repoLinux + "/src"));
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(30), ct);

            var active = git.Active!;
            Assert.Equal(ModuleState.Ready, git.Status.State);
            Assert.Equal(WslPath.ToWindows(distro!, repoLinux), active.Root);
            Assert.Equal("main", active.Branch);
            Assert.Equal(1, active.Untracked);
            Assert.Equal("first commit", active.LastCommitSubject);
            Assert.Equal("owner/proj", active.GitHubRepository);

            // Usage: a Claude Code folder inside the distro, polled; its Linux working folder is translated.
            var claude = WslPath.ToWindows(distro!, linuxRoot + "/.claude")!;
            var project = Directory.CreateDirectory(Path.Combine(claude, "projects", "-tmp-proj")).FullName;
            await File.WriteAllTextAsync(Path.Combine(project, "s.jsonl"),
                TestData.ClaudeAssistantLine("m1", "r1", DateTimeOffset.UtcNow, input: 100, output: 10, cacheCreation: 0, cacheRead: 0, cwd: repoLinux) + "\n", ct);

            using var db = UsageDatabase.OpenInMemory();
            var history = new UsageHistoryService(db, new ModelPricingService());
            using var provider = new ClaudeUsageProvider(Path.Combine(TestData.TempDirectory(), ".claude"), history, db);
            provider.SetWslFolders(_ => Task.FromResult<IReadOnlyList<string>>([claude.Replace(WslPath.Prefix, @"\\wsl$\")]));
            await provider.StartAsync(ct);
            await provider.PollWslAsync();

            Assert.Equal(110, provider.Snapshot.Today.Processed);
            Assert.Equal(WslPath.ToWindows(distro!, repoLinux), provider.Snapshot.CurrentProjectPath);
        }
        finally
        {
            await ProcessRunner.RunAsync(wsl.Executable!, WslCommand.Exec(distro!, "/", "rm", ["-rf", linuxRoot]));
        }
    }
}
