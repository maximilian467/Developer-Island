using System.Globalization;

namespace DeveloperIsland.Core.Updates;

/// <summary>
/// A semantic version as release tags carry it ("v0.4.0", "0.4.0-beta.1"). A fourth numeric part
/// (assembly versions are 0.4.0.0) is accepted and ignored; build metadata after '+' too.
/// </summary>
public readonly record struct ReleaseVersion(int Major, int Minor, int Patch, string? Prerelease = null) : IComparable<ReleaseVersion>
{
    public bool IsPrerelease => !string.IsNullOrEmpty(Prerelease);

    public static ReleaseVersion FromAssembly(Version? version) =>
        version is null ? new ReleaseVersion(0, 0, 0) : new ReleaseVersion(version.Major, version.Minor, Math.Max(0, version.Build));

    public static bool TryParse(string? text, out ReleaseVersion version)
    {
        version = default;
        var s = text?.Trim();
        if (string.IsNullOrEmpty(s))
        {
            return false;
        }

        if (s[0] is 'v' or 'V')
        {
            s = s[1..];
        }

        var plus = s.IndexOf('+');
        if (plus >= 0)
        {
            s = s[..plus];
        }

        string? prerelease = null;
        var dash = s.IndexOf('-');
        if (dash >= 0)
        {
            prerelease = s[(dash + 1)..];
            s = s[..dash];
            if (prerelease.Length == 0)
            {
                return false;
            }
        }

        var parts = s.Split('.');
        if (parts.Length is < 3 or > 4)
        {
            return false;
        }

        var numbers = new int[parts.Length];
        for (var i = 0; i < parts.Length; i++)
        {
            if (parts[i].Length == 0 || !parts[i].All(char.IsAsciiDigit)
                || !int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out numbers[i]))
            {
                return false;
            }
        }

        version = new ReleaseVersion(numbers[0], numbers[1], numbers[2], prerelease);
        return true;
    }

    /// <summary>Semantic version precedence: numbers first, then a prerelease sorts before its release.</summary>
    public int CompareTo(ReleaseVersion other)
    {
        var c = Major.CompareTo(other.Major);
        if (c == 0) c = Minor.CompareTo(other.Minor);
        if (c == 0) c = Patch.CompareTo(other.Patch);
        if (c != 0)
        {
            return c;
        }

        return (IsPrerelease, other.IsPrerelease) switch
        {
            (false, false) => 0,
            (true, false) => -1,
            (false, true) => 1,
            _ => ComparePrerelease(Prerelease!, other.Prerelease!),
        };
    }

    public static bool operator >(ReleaseVersion a, ReleaseVersion b) => a.CompareTo(b) > 0;

    public static bool operator <(ReleaseVersion a, ReleaseVersion b) => a.CompareTo(b) < 0;

    public static bool operator >=(ReleaseVersion a, ReleaseVersion b) => a.CompareTo(b) >= 0;

    public static bool operator <=(ReleaseVersion a, ReleaseVersion b) => a.CompareTo(b) <= 0;

    /// <summary>"0.4.0", or "0.4.0-beta.1".</summary>
    public override string ToString() => IsPrerelease ? $"{Major}.{Minor}.{Patch}-{Prerelease}" : $"{Major}.{Minor}.{Patch}";

    private static int ComparePrerelease(string a, string b)
    {
        var x = a.Split('.');
        var y = b.Split('.');
        for (var i = 0; i < Math.Min(x.Length, y.Length); i++)
        {
            var xNumeric = int.TryParse(x[i], NumberStyles.None, CultureInfo.InvariantCulture, out var xn);
            var yNumeric = int.TryParse(y[i], NumberStyles.None, CultureInfo.InvariantCulture, out var yn);
            var c = (xNumeric, yNumeric) switch
            {
                (true, true) => xn.CompareTo(yn),
                (true, false) => -1,
                (false, true) => 1,
                _ => string.CompareOrdinal(x[i], y[i]),
            };
            if (c != 0)
            {
                return c;
            }
        }

        return x.Length.CompareTo(y.Length);
    }
}
