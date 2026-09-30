using DeveloperIsland.Core.Diagnostics;
using DeveloperIsland.Core.Git;
using DeveloperIsland.Core.Modules;

namespace DeveloperIsland.Tests;

public class GitModuleTests
{
    [Fact]
    public void Parses_branch_upstream_and_change_counts()
    {
        const string output = """
            # branch.oid 7464d94c0ffee
            # branch.head main
            # branch.upstream origin/main
            # branch.ab +2 -1
            1 .M N... 100644 100644 100644 abc abc src/a.cs
            1 M. N... 100644 100644 100644 abc abc src/b.cs
            1 MM N... 100644 100644 100644 abc abc src/c.cs
            2 R. N... 100644 100644 100644 abc abc R100 src/d.cs	src/old.cs
            u UU N... 100644 100644 100644 100644 abc abc abc src/e.cs
            ? notes.txt
            ? tmp/
            """;

        var s = GitStatusParser.ParseStatus(@"C:\code\island", output);

        Assert.Equal("island", s.Name);
        Assert.Equal("main", s.Branch);
        Assert.True(s.HasUpstream);
        Assert.Equal(2, s.Ahead);
        Assert.Equal(1, s.Behind);
        Assert.Equal(3, s.Staged);
        Assert.Equal(2, s.Modified);
        Assert.Equal(2, s.Untracked);
        Assert.Equal(1, s.Conflicts);
        Assert.Equal(7, s.Changed); // a, b, c (once), d, e, notes.txt, tmp/
        Assert.False(s.IsClean);
    }

    [Fact]
    public void Detached_head_and_fresh_repository()
    {
        var detached = GitStatusParser.ParseStatus("r", "# branch.oid 1234567890\n# branch.head (detached)\n");
        Assert.Null(detached.Branch);
        Assert.Equal("1234567", detached.DetachedAt);

        var fresh = GitStatusParser.ParseStatus("r", "# branch.oid (initial)\n# branch.head main\n");
        Assert.Equal("main", fresh.Branch);
        Assert.True(fresh.IsClean);
        Assert.False(fresh.HasUpstream);
    }

    [Fact]
    public void Parses_last_commit_line()
    {
        var (at, subject) = GitStatusParser.ParseLastCommit("1727600000\u001ffix: define fresh tokens\n");

        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1727600000), at);
        Assert.Equal("fix: define fresh tokens", subject);
        Assert.Equal((null, null), GitStatusParser.ParseLastCommit(string.Empty));
    }

    [Theory]
    [InlineData("https://github.com/maximilian467/Developer-Island.git", "maximilian467/Developer-Island")]
    [InlineData("https://github.com/maximilian467/Developer-Island", "maximilian467/Developer-Island")]
    [InlineData("git@github.com:owner/repo.git", "owner/repo")]
    [InlineData("ssh://git@github.com/owner/repo.name", "owner/repo.name")]
    [InlineData("https://gitlab.com/owner/repo.git", null)]
    [InlineData("https://github.com/owner", null)]
    [InlineData("", null)]
    public void Recognises_github_remotes(string url, string? expected)
    {
        Assert.Equal(expected, GitStatusParser.ParseGitHubRepository(url));
    }

    [Fact]
    public void Repository_root_is_found_by_walking_up_only()
    {
        var root = TestData.TempDirectory();
        Directory.CreateDirectory(Path.Combine(root, ".git"));
        var nested = Directory.CreateDirectory(Path.Combine(root, "src", "deep")).FullName;

        Assert.Equal(root.TrimEnd('\\'), GitStatusParser.FindRepositoryRoot(nested)!.TrimEnd('\\'));
        Assert.Null(GitStatusParser.FindRepositoryRoot(null));
    }

    [Fact]
    public void Branch_is_read_from_head_without_git()
    {
        var gitDir = Directory.CreateDirectory(Path.Combine(TestData.TempDirectory(), ".git")).FullName;
        File.WriteAllText(Path.Combine(gitDir, "HEAD"), "ref: refs/heads/feature/notch\n");
        Assert.Equal("feature/notch", GitStatusParser.ReadHeadBranch(gitDir));

        File.WriteAllText(Path.Combine(gitDir, "HEAD"), "7464d94c0ffee7464d94c0ffee7464d94c0ffee0\n");
        Assert.Null(GitStatusParser.ReadHeadBranch(gitDir));
        Assert.Null(GitStatusParser.ReadHeadBranch(Path.Combine(gitDir, "missing")));
    }

    [Fact]
    public void Worktree_git_file_points_to_its_git_directory()
    {
        var root = TestData.TempDirectory();
        var target = Directory.CreateDirectory(Path.Combine(root, "real-git")).FullName;
        var tree = Directory.CreateDirectory(Path.Combine(root, "tree")).FullName;
        File.WriteAllText(Path.Combine(tree, ".git"), "gitdir: ../real-git\n");

        Assert.Equal(target, GitStatusParser.GitDirectory(tree));
    }

    [Theory]
    [InlineData("HEAD", true)]
    [InlineData("index", true)]
    [InlineData(@"refs\heads\main", true)]
    [InlineData(@"objects\ab\cdef", false)]
    [InlineData(@"logs\HEAD", false)]
    [InlineData("index.lock", false)]
    public void Only_metadata_that_changes_the_view_triggers_a_refresh(string relative, bool relevant)
    {
        Assert.Equal(relevant, GitService.IsRelevant(@"C:\r\.git", Path.Combine(@"C:\r\.git", relative)));
    }

    [Fact]
    public void Missing_git_is_reported_as_unavailable()
    {
        using var git = new GitService(findGit: () => null);
        git.Configure(true, [], []);

        Assert.Equal(ModuleState.Unavailable, git.Status.State);
    }

    [Fact]
    public void Disabled_module_reports_disabled_and_ignores_projects()
    {
        using var git = new GitService(findGit: () => "git");
        git.Configure(false, [], []);
        git.NoteProjectPath(Environment.CurrentDirectory);

        Assert.Equal(ModuleState.Disabled, git.Status.State);
        Assert.Empty(git.Recent);
    }

    [Fact]
    public void Without_repositories_the_module_is_empty()
    {
        using var git = new GitService(findGit: () => "git");
        git.Configure(true, [], []);

        Assert.Equal(ModuleState.Empty, git.Status.State);
    }

    [Fact]
    public async Task Reads_a_real_repository_end_to_end()
    {
        var gitPath = ProcessRunner.FindExecutable("git", @"%ProgramFiles%\Git\cmd\git.exe");
        Assert.SkipWhen(gitPath is null, "git is not installed");

        var root = Directory.CreateDirectory(Path.Combine(TestData.TempDirectory(), "sample-repo")).FullName;
        async Task Git(params string[] args) => Assert.True((await ProcessRunner.RunAsync(gitPath!, ["-C", root, .. args])).Succeeded);
        await Git("init", "-q", "-b", "main");
        await Git("config", "user.email", "test@example.com");
        await Git("config", "user.name", "Test");
        await Git("config", "commit.gpgsign", "false");
        await Git("remote", "add", "origin", "https://github.com/owner/sample-repo.git");
        File.WriteAllText(Path.Combine(root, "a.txt"), "one");
        await Git("add", ".");
        await Git("commit", "-q", "-m", "first commit");
        File.WriteAllText(Path.Combine(root, "a.txt"), "two");
        File.WriteAllText(Path.Combine(root, "b.txt"), "new");

        using var git = new GitService(findGit: () => gitPath);
        var changed = new TaskCompletionSource();
        git.Changed += () =>
        {
            if (git.Active is not null)
            {
                changed.TrySetResult();
            }
        };
        git.Configure(true, [], []);
        git.NoteProjectPath(Path.Combine(root, "sub", "folder"));
        await changed.Task.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);

        var status = git.Active!;
        Assert.Equal(ModuleState.Ready, git.Status.State);
        Assert.Equal("sample-repo", status.Name);
        Assert.Equal("main", status.Branch);
        Assert.Equal(2, status.Changed);
        Assert.Equal("first commit", status.LastCommitSubject);
        Assert.Equal("owner/sample-repo", status.GitHubRepository);
        Assert.Equal([root], git.Recent);
    }

    [Fact]
    public async Task Ai_activity_rereads_only_while_the_panel_is_visible()
    {
        var gitPath = ProcessRunner.FindExecutable("git", @"%ProgramFiles%Gitcmdgit.exe");
        Assert.SkipWhen(gitPath is null, "git is not installed");

        var root = Directory.CreateDirectory(Path.Combine(TestData.TempDirectory(), "quiet-repo")).FullName;
        Assert.True((await ProcessRunner.RunAsync(gitPath!, ["-C", root, "init", "-q"])).Succeeded);
        var time = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(DateTimeOffset.UtcNow);
        using var git = new GitService(findGit: () => gitPath, time: time);
        var first = new TaskCompletionSource();
        git.Changed += () => { if (git.Active is not null) first.TrySetResult(); };
        git.Configure(true, [], []);
        git.NoteProjectPath(root);
        await first.Task.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);

        var reads = 0;
        git.Changed += () => Interlocked.Increment(ref reads);
        time.Advance(TimeSpan.FromMinutes(1));
        git.NoteProjectPath(root);
        await Task.Delay(1500, TestContext.Current.CancellationToken);
        Assert.Equal(0, reads); // hidden panel: no git processes

        var again = new TaskCompletionSource();
        git.Changed += () => again.TrySetResult();
        git.SetVisible(true);
        time.Advance(TimeSpan.FromMinutes(1));
        git.NoteProjectPath(root);
        await again.Task.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
    }
}
