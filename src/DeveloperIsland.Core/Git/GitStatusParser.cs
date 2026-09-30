using System.Globalization;

namespace DeveloperIsland.Core.Git;

/// <summary>One repository as the Git module shows it. Holds counts only: no file names, no diffs.</summary>
public sealed record GitRepoStatus
{
    public required string Root { get; init; }

    public string Name => Path.GetFileName(Root.TrimEnd('\\', '/'));

    /// <summary>Branch name, or null when HEAD is detached.</summary>
    public string? Branch { get; init; }

    /// <summary>Short commit id when detached.</summary>
    public string? DetachedAt { get; init; }

    public bool HasUpstream { get; init; }

    public int Ahead { get; init; }

    public int Behind { get; init; }

    public int Staged { get; init; }

    public int Modified { get; init; }

    public int Untracked { get; init; }

    public int Conflicts { get; init; }

    /// <summary>Entries counted as both staged and modified.</summary>
    public int Overlap { get; init; }

    /// <summary>Every path with a change, staged or not, tracked or not.</summary>
    public int Changed => Staged + Modified + Untracked + Conflicts - Overlap;

    public bool IsClean => Changed == 0;

    public string? LastCommitSubject { get; init; }

    public DateTimeOffset? LastCommitAt { get; init; }

    /// <summary>"owner/name" of a GitHub origin remote, if any.</summary>
    public string? GitHubRepository { get; init; }

    public DateTimeOffset UpdatedAt { get; init; }
}

public static class GitStatusParser
{
    /// <summary>Parses <c>git status --porcelain=v2 --branch</c>.</summary>
    public static GitRepoStatus ParseStatus(string root, string output)
    {
        string? branch = null;
        string? oid = null;
        var upstream = false;
        int ahead = 0, behind = 0, staged = 0, modified = 0, untracked = 0, conflicts = 0, overlap = 0;

        foreach (var raw in output.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.StartsWith("# branch.head ", StringComparison.Ordinal))
            {
                var head = line["# branch.head ".Length..];
                branch = head == "(detached)" ? null : head;
            }
            else if (line.StartsWith("# branch.oid ", StringComparison.Ordinal))
            {
                var value = line["# branch.oid ".Length..];
                oid = value == "(initial)" ? null : value[..Math.Min(7, value.Length)];
            }
            else if (line.StartsWith("# branch.upstream ", StringComparison.Ordinal))
            {
                upstream = true;
            }
            else if (line.StartsWith("# branch.ab ", StringComparison.Ordinal))
            {
                var parts = line["# branch.ab ".Length..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 2)
                {
                    int.TryParse(parts[0].TrimStart('+'), NumberStyles.Integer, CultureInfo.InvariantCulture, out ahead);
                    int.TryParse(parts[1].TrimStart('-'), NumberStyles.Integer, CultureInfo.InvariantCulture, out behind);
                }
            }
            else if (line.Length > 3 && (line[0] == '1' || line[0] == '2') && line[1] == ' ')
            {
                var isStaged = line[2] != '.';
                var isModified = line[3] != '.';
                staged += isStaged ? 1 : 0;
                modified += isModified ? 1 : 0;
                overlap += isStaged && isModified ? 1 : 0;
            }
            else if (line.StartsWith("u ", StringComparison.Ordinal))
            {
                conflicts++;
            }
            else if (line.StartsWith("? ", StringComparison.Ordinal))
            {
                untracked++;
            }
        }

        return new GitRepoStatus
        {
            Root = root,
            Branch = branch,
            DetachedAt = branch is null ? oid : null,
            HasUpstream = upstream,
            Ahead = ahead,
            Behind = behind,
            Staged = staged,
            Modified = modified,
            Untracked = untracked,
            Conflicts = conflicts,
            Overlap = overlap,
        };
    }

    /// <summary>Parses <c>git log -1 --format=%ct%x1f%s</c>.</summary>
    public static (DateTimeOffset? At, string? Subject) ParseLastCommit(string output)
    {
        var line = output.Split('\n')[0].TrimEnd('\r');
        var separator = line.IndexOf('\u001f');
        if (separator <= 0 || !long.TryParse(line[..separator], NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds))
        {
            return (null, null);
        }

        var subject = line[(separator + 1)..].Trim();
        return (DateTimeOffset.FromUnixTimeSeconds(seconds), subject.Length > 0 ? subject : null);
    }

    /// <summary>"owner/name" for GitHub remotes in https, ssh or scp form; null otherwise.</summary>
    public static string? ParseGitHubRepository(string? remoteUrl)
    {
        if (string.IsNullOrWhiteSpace(remoteUrl))
        {
            return null;
        }

        var url = remoteUrl.Trim();
        string? path = null;
        foreach (var prefix in new[] { "https://github.com/", "http://github.com/", "ssh://git@github.com/", "git@github.com:", "git://github.com/" })
        {
            if (url.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                path = url[prefix.Length..];
                break;
            }
        }

        if (path is null)
        {
            return null;
        }

        path = path.TrimEnd('/');
        if (path.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
        {
            path = path[..^4];
        }

        var parts = path.Split('/');
        return parts.Length == 2 && parts.All(p => p.Length > 0 && p.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.'))
            ? $"{parts[0]}/{parts[1]}"
            : null;
    }

    /// <summary>
    /// The repository containing <paramref name="path"/>: walks up the parent folders only (never
    /// down), so there is no disk crawling. Returns the working-tree root, or null.
    /// </summary>
    public static string? FindRepositoryRoot(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            var directory = new DirectoryInfo(Path.GetFullPath(path));
            for (var depth = 0; directory is not null && depth < 32; depth++, directory = directory.Parent)
            {
                var git = Path.Combine(directory.FullName, ".git");
                if (Directory.Exists(git) || File.Exists(git))
                {
                    return directory.FullName;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            // Unreadable or malformed path.
        }

        return null;
    }

    /// <summary>The checked-out branch from <c>.git/HEAD</c> without running git; null when detached or unreadable.</summary>
    public static string? ReadHeadBranch(string gitDir)
    {
        try
        {
            var head = File.ReadAllText(Path.Combine(gitDir, "HEAD")).Trim();
            const string Prefix = "ref: refs/heads/";
            return head.StartsWith(Prefix, StringComparison.Ordinal) ? head[Prefix.Length..] : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>The git directory of a working tree (worktrees and submodules use a .git file).</summary>
    public static string? GitDirectory(string root)
    {
        var git = Path.Combine(root, ".git");
        if (Directory.Exists(git))
        {
            return git;
        }

        try
        {
            if (File.Exists(git))
            {
                var line = File.ReadLines(git).FirstOrDefault() ?? string.Empty;
                if (line.StartsWith("gitdir:", StringComparison.OrdinalIgnoreCase))
                {
                    var target = line["gitdir:".Length..].Trim();
                    return Path.GetFullPath(Path.IsPathRooted(target) ? target : Path.Combine(root, target));
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Fall through.
        }

        return null;
    }
}
