namespace DeveloperIsland.Core.Wsl;

/// <summary>
/// Paths into WSL distros, both ways: Windows reaches a distro's files through
/// <c>\\wsl.localhost\&lt;distro&gt;\home\u\proj</c> (older builds: <c>\\wsl$\</c>), while tools running
/// inside write Linux paths (<c>/home/u/proj</c>). Pure string work: nothing here touches the disk,
/// so nothing here can start a distro.
/// </summary>
public static class WslPath
{
    /// <summary>The share Developer Island uses for display and for reading.</summary>
    public const string Prefix = @"\\wsl.localhost\";

    private const string LegacyPrefix = @"\\wsl$\";

    /// <summary>True for <c>\\wsl.localhost\…</c> and <c>\\wsl$\…</c> paths, with either slash.</summary>
    public static bool IsWslPath(string? path) => TryParse(path, out _, out _);

    /// <summary>Splits a WSL share path into the distro name and the Linux path inside it.</summary>
    public static bool TryParse(string? path, out string distro, out string linuxPath)
    {
        distro = string.Empty;
        linuxPath = string.Empty;
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var p = path.Trim().Replace('/', '\\');
        string rest;
        if (p.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
        {
            rest = p[Prefix.Length..];
        }
        else if (p.StartsWith(LegacyPrefix, StringComparison.OrdinalIgnoreCase))
        {
            rest = p[LegacyPrefix.Length..];
        }
        else
        {
            return false;
        }

        var cut = rest.IndexOf('\\');
        var name = cut < 0 ? rest : rest[..cut];
        if (!IsValidDistroName(name))
        {
            return false;
        }

        var segments = (cut < 0 ? string.Empty : rest[(cut + 1)..]).Split('\\', StringSplitOptions.RemoveEmptyEntries);
        distro = name;
        linuxPath = "/" + string.Join('/', segments);
        return true;
    }

    /// <summary>
    /// The Windows path of a Linux path inside <paramref name="distro"/>: <c>/home/u/proj</c> becomes
    /// <c>\\wsl.localhost\Ubuntu\home\u\proj</c>, and a Windows drive mounted by WSL
    /// (<c>/mnt/c/Users/u</c>) becomes <c>C:\Users\u</c> again. Anything that is not an absolute Linux
    /// path (a Windows path, null) is returned unchanged.
    /// </summary>
    public static string? ToWindows(string distro, string? linuxPath)
    {
        if (string.IsNullOrEmpty(linuxPath) || linuxPath[0] != '/' || !IsValidDistroName(distro))
        {
            return linuxPath;
        }

        var segments = linuxPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length >= 2 && segments[0] == "mnt" && segments[1].Length == 1 && char.IsAsciiLetter(segments[1][0]))
        {
            return char.ToUpperInvariant(segments[1][0]) + @":\" + string.Join('\\', segments[2..]);
        }

        return Prefix + distro + @"\" + string.Join('\\', segments);
    }

    /// <summary>The Linux path of a WSL share path (<c>\\wsl$\Ubuntu\home\u</c> → <c>/home/u</c>); null otherwise.</summary>
    public static string? ToLinux(string? windowsPath) => TryParse(windowsPath, out _, out var linux) ? linux : null;

    /// <summary>
    /// One spelling per location, so a repository or log file reached through <c>\\wsl$\</c> and through
    /// <c>\\wsl.localhost\</c> is the same entry. Other paths are returned unchanged.
    /// </summary>
    public static string Normalize(string path) =>
        TryParse(path, out var distro, out var linux) ? ToWindows(distro, linux)!.TrimEnd('\\') : path;

    /// <summary>The distro a WSL share path points into; null for other paths.</summary>
    public static string? DistroOf(string? path) => TryParse(path, out var distro, out _) ? distro : null;

    /// <summary>WSL distro names: letters, digits, dot, dash and underscore.</summary>
    public static bool IsValidDistroName(string? name) =>
        !string.IsNullOrEmpty(name) && name.Length <= 64 && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_');
}
