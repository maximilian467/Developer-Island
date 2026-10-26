using DeveloperIsland.Core.Wsl;

namespace DeveloperIsland.Core.Git;

/// <summary>
/// One git invocation for a repository. Windows folders use Git for Windows with <c>-C root</c>.
/// Repositories inside WSL run the distro's own git through <c>wsl.exe</c>: Windows git on the
/// <c>\\wsl.localhost</c> share is slow and refuses the files as "dubious ownership".
/// </summary>
public sealed record GitCommandLine(string FileName, IReadOnlyList<string> Arguments)
{
    /// <summary>Repository state for the module; <c>--no-optional-locks</c> keeps git from rewriting the index.</summary>
    public static readonly IReadOnlyList<string> Status = ["--no-optional-locks", "-c", "core.fsmonitor=false", "status", "--porcelain=v2", "--branch"];

    public static readonly IReadOnlyList<string> LastCommit = ["--no-optional-locks", "log", "-1", "--format=%ct%x1f%s"];

    public static readonly IReadOnlyList<string> OriginUrl = ["config", "--get", "remote.origin.url"];

    public static readonly IReadOnlyList<string> Version = ["--version"];

    /// <summary>
    /// The command line for <paramref name="gitArguments"/> in <paramref name="root"/>; null when the
    /// program it needs (git, or wsl.exe for a WSL repository) is missing.
    /// </summary>
    public static GitCommandLine? For(string root, IEnumerable<string> gitArguments, string? git, string? wsl)
    {
        if (WslPath.TryParse(root, out var distro, out var linuxPath))
        {
            return wsl is null ? null : new GitCommandLine(wsl, WslCommand.Exec(distro, linuxPath, "git", gitArguments));
        }

        return git is null ? null : new GitCommandLine(git, ["-C", root, .. gitArguments]);
    }
}
