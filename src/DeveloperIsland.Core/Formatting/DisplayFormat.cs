using System.Globalization;

namespace DeveloperIsland.Core.Formatting;

/// <summary>
/// Locale-aware formatting of everything the island shows. Numbers and units are joined with a
/// non-breaking space, and no dash characters other than the hyphen are used.
/// </summary>
public static class DisplayFormat
{
    public const char Nbsp = ' ';

    /// <summary>Compact token count: 940, 12.4k, 620k, 1.82M, 2.3B.</summary>
    public static string Tokens(long value, CultureInfo? culture = null)
    {
        culture ??= CultureInfo.CurrentCulture;
        var v = Math.Abs((double)value);
        string Format(double scaled, string suffix)
        {
            var format = scaled >= 100 ? "0" : scaled >= 10 ? "0.#" : "0.##";
            return (value < 0 ? "-" : string.Empty) + scaled.ToString(format, culture) + suffix;
        }

        return v switch
        {
            >= 1_000_000_000 => Format(v / 1_000_000_000, "B"),
            >= 1_000_000 => Format(v / 1_000_000, "M"),
            >= 1_000 => Format(v / 1_000, "k"),
            _ => value.ToString("0", culture),
        };
    }

    /// <summary>EUR in the user's number format, always two decimals: "€18.42" or "18,42 €".</summary>
    public static string Euro(decimal value, CultureInfo? culture = null)
    {
        var format = (NumberFormatInfo)(culture ?? CultureInfo.CurrentCulture).NumberFormat.Clone();
        format.CurrencySymbol = "€";
        return value.ToString("C2", format).Replace(' ', Nbsp);
    }

    /// <summary>Countdown or clock time: 42:13 or 1:05:09.</summary>
    public static string Clock(TimeSpan value)
    {
        if (value < TimeSpan.Zero)
        {
            value = TimeSpan.Zero;
        }

        // Round up so a countdown shows 25:00 at the start and 0:01 in the last second.
        var totalSeconds = (long)Math.Ceiling(value.TotalSeconds - 0.001);
        var hours = totalSeconds / 3600;
        var minutes = totalSeconds / 60 % 60;
        var seconds = totalSeconds % 60;
        return hours > 0 ? $"{hours}:{minutes:00}:{seconds:00}" : $"{minutes}:{seconds:00}";
    }

    /// <summary>Media position (rounded down): 1:42.</summary>
    public static string MediaTime(TimeSpan value)
    {
        if (value < TimeSpan.Zero)
        {
            value = TimeSpan.Zero;
        }

        var total = (long)value.TotalSeconds;
        return total >= 3600 ? $"{total / 3600}:{total / 60 % 60:00}:{total % 60:00}" : $"{total / 60}:{total % 60:00}";
    }

    /// <summary>Focus totals: "45 min", "1 h 15 min", "2 h".</summary>
    public static string Duration(TimeSpan value)
    {
        var minutes = (int)Math.Round(value.TotalMinutes);
        if (minutes < 60)
        {
            return $"{minutes}{Nbsp}min";
        }

        var h = minutes / 60;
        var m = minutes % 60;
        return m == 0 ? $"{h}{Nbsp}h" : $"{h}{Nbsp}h {m}{Nbsp}min";
    }

    /// <summary>Short day for the history readout: "24 Sep" (culture ordering).</summary>
    public static string ShortDay(DateOnly day, CultureInfo? culture = null)
    {
        culture ??= CultureInfo.CurrentCulture;
        var pattern = culture.DateTimeFormat.MonthDayPattern.Contains("MMMM", StringComparison.Ordinal)
            ? culture.DateTimeFormat.MonthDayPattern.Replace("MMMM", "MMM", StringComparison.Ordinal)
            : "d MMM";
        return day.ToString(pattern, culture).TrimEnd('.').Replace(' ', Nbsp);
    }

    public static string Count(int value, string singular, string plural) =>
        $"{value}{Nbsp}{(value == 1 ? singular : plural)}";

    /// <summary>"5-hour limit" / "90-minute limit".</summary>
    public static string LimitWindow(int windowMinutes) => windowMinutes switch
    {
        >= 60 when windowMinutes % 60 == 0 => $"{windowMinutes / 60}-hour limit",
        > 0 => $"{windowMinutes}-minute limit",
        _ => "limit",
    };

    /// <summary>Shortens model ids for small labels: "claude-opus-5-5" becomes "Opus 5.5".</summary>
    public static string ModelLabel(string? model)
    {
        if (string.IsNullOrWhiteSpace(model))
        {
            return string.Empty;
        }

        var m = model.Trim();
        if (m.StartsWith("claude-", StringComparison.OrdinalIgnoreCase))
        {
            var parts = m["claude-".Length..].Split('-');
            var family = parts.Length > 0 ? CultureInfo.InvariantCulture.TextInfo.ToTitleCase(parts[0]) : m;
            var version = string.Join('.', parts.Skip(1).TakeWhile(p => p.Length <= 2 && p.All(char.IsDigit)));
            return version.Length > 0 ? $"{family}{Nbsp}{version}" : family;
        }

        return m;
    }
}
