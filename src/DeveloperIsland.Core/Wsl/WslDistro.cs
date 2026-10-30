using System.Globalization;

namespace DeveloperIsland.Core.Wsl;

/// <summary>An installed WSL distro. <see cref="DefaultUid"/> is known when read from the registry.</summary>
public sealed record WslDistro(string Name, bool IsDefault, int Version = 2, int? DefaultUid = null);

/// <summary>One line of a distro's <c>/etc/passwd</c>: only the fields needed to find home folders.</summary>
public sealed record WslUser(string Name, int Uid, string Home);

/// <summary>Parses what <c>wsl.exe</c> prints and what a distro keeps in <c>/etc/passwd</c>.</summary>
public static class WslDistroList
{
    /// <summary>
    /// <c>wsl.exe</c> writes UTF-16; decoded as anything else it leaves NUL characters between the
    /// letters, and some builds start with a byte order mark. Both are removed.
    /// </summary>
    public static string Clean(string output) => output.Replace("\0", string.Empty).Replace("﻿", string.Empty);

    /// <summary>
    /// Parses <c>wsl.exe --list --verbose</c>: a header line (localized), then one line per distro,
    /// the default one marked with <c>*</c>. The state column is localized and may hold several
    /// words, so only the first column (name) and the last (version) are read.
    /// </summary>
    public static IReadOnlyList<WslDistro> ParseVerbose(string output)
    {
        var distros = new List<WslDistro>();
        var lines = Clean(output).Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Trim().Length > 0).ToList();
        foreach (var raw in lines.Skip(1))
        {
            var line = raw.Trim();
            var isDefault = line.StartsWith('*');
            var parts = line.TrimStart('*').Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 3
                || !WslPath.IsValidDistroName(parts[0])
                || !int.TryParse(parts[^1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var version))
            {
                continue;
            }

            distros.Add(new WslDistro(parts[0], isDefault, version));
        }

        return distros;
    }

    /// <summary>
    /// Parses <c>wsl.exe --list --quiet</c> (also with <c>--running</c>): one name per line. Messages
    /// such as "There are no running distributions." are not names and are skipped.
    /// </summary>
    public static IReadOnlyList<string> ParseQuiet(string output) =>
        Clean(output).Split('\n').Select(l => l.Trim()).Where(WslPath.IsValidDistroName).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>
    /// Distros that belong to a tool rather than to the user (Docker Desktop, Rancher Desktop): no one
    /// runs Claude Code in them, so they are not offered.
    /// </summary>
    public static bool IsToolDistro(string name) =>
        name.StartsWith("docker-desktop", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("rancher-desktop", StringComparison.OrdinalIgnoreCase);

    /// <summary>Default distro first, then by name; tool distros left out.</summary>
    public static IReadOnlyList<WslDistro> ForDisplay(IEnumerable<WslDistro> distros) =>
        distros.Where(d => !IsToolDistro(d.Name))
            .DistinctBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(d => d.IsDefault)
            .ThenBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>Parses <c>/etc/passwd</c> (<c>name:x:uid:gid:gecos:home:shell</c>).</summary>
    public static IReadOnlyList<WslUser> ParsePasswd(string text)
    {
        var users = new List<WslUser>();
        foreach (var raw in text.Split('\n'))
        {
            var fields = raw.TrimEnd('\r').Split(':');
            if (fields.Length < 7 || fields[0].Length == 0 || !fields[5].StartsWith('/')
                || !int.TryParse(fields[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var uid))
            {
                continue;
            }

            users.Add(new WslUser(fields[0], uid, fields[5]));
        }

        return users;
    }

    /// <summary>
    /// Home folders where Claude Code or Codex may live: the distro's default user first (from the
    /// registry), then other login users (uid 1000 to 59999), then root. Service accounts are skipped.
    /// </summary>
    public static IReadOnlyList<string> CandidateHomes(IReadOnlyList<WslUser> users, int? defaultUid)
    {
        var homes = new List<string>();
        if (defaultUid is { } uid && users.FirstOrDefault(u => u.Uid == uid) is { } user)
        {
            homes.Add(user.Home);
        }

        homes.AddRange(users.Where(u => u.Uid is >= 1000 and < 60000).Select(u => u.Home));
        homes.AddRange(users.Where(u => u.Uid == 0).Select(u => u.Home));
        return homes.Where(h => h.Length > 1).Distinct(StringComparer.Ordinal).ToList();
    }
}
