namespace DeveloperIsland.Core.Formatting;

/// <summary>
/// Display names taken from paths. Splits on both '/' and '\' so a name reads the same on
/// Windows and Linux, including Windows-style paths that reach a Linux build (synced settings,
/// test data, WSL paths).
/// </summary>
public static class PathNames
{
    private static readonly char[] Separators = ['/', '\\'];

    /// <summary>The last segment of a path, ignoring trailing separators ("C:\code\island\" → "island").</summary>
    public static string LastSegment(string path)
    {
        var trimmed = path.TrimEnd(Separators);
        var cut = trimmed.LastIndexOfAny(Separators);
        return cut < 0 ? trimmed : trimmed[(cut + 1)..];
    }

    /// <summary>The last segment without its extension ("C:\docs\uni.ics" → "uni").</summary>
    public static string LastSegmentWithoutExtension(string path)
    {
        var name = LastSegment(path);
        var dot = name.LastIndexOf('.');
        return dot > 0 ? name[..dot] : name;
    }
}
