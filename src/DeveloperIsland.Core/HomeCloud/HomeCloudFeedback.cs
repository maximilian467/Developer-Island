using System.Globalization;
using DeveloperIsland.Core.Formatting;

namespace DeveloperIsland.Core.HomeCloud;

/// <summary>The drop zone's texts. Never names a file, folder or path: counts, sizes and short reasons only.</summary>
public static class HomeCloudFeedback
{
    public const string HoverTitle = "Save to Home Cloud";
    public const string HoverDetail = "Release to copy";
    public const string UploadTitle = "Saving to Home Cloud";
    public const string PreparingDetail = "Preparing…";
    public const string SucceededTitle = "Saved to Home Cloud";
    public const string UnreachableTitle = "Home Cloud not reachable";
    public const string FailedTitle = "Could not save to Home Cloud";
    public const string CanceledTitle = "Upload canceled";

    /// <summary>Reasons that mean the server or its folder could not be reached (from the uploader and the reachability check).</summary>
    private static readonly HashSet<string> UnreachableReasons = new(StringComparer.Ordinal)
    {
        "Target not reachable",
        "Target folder not found",
        "Home Cloud not reachable",
    };

    /// <summary>Whole percent of the bytes copied, 0 to 100; an upload of empty files counts by file.</summary>
    public static int Percent(HomeCloudProgress progress)
    {
        if (progress.TotalBytes <= 0)
        {
            return progress.FileCount <= 0 ? 100 : (int)Math.Clamp(100L * progress.FileIndex / progress.FileCount, 0, 100);
        }

        return (int)Math.Clamp(100 * progress.BytesCopied / progress.TotalBytes, 0, 100);
    }

    /// <summary>"3/5 · 42 %".</summary>
    public static string ProgressText(HomeCloudProgress progress) =>
        $"{progress.FileIndex}/{progress.FileCount} · {Percent(progress)} %";

    /// <summary>Title and detail after an upload, e.g. ("Saved to Home Cloud", "3 files · 12.4 MB").</summary>
    public static (string Title, string Detail) Result(HomeCloudUploadResult result, CultureInfo? culture = null) => result.Status switch
    {
        HomeCloudUploadStatus.Succeeded => (SucceededTitle, $"{DisplayFormat.Count(result.FileCount, "file", "files")} · {Size(result.Bytes, culture)}"),
        HomeCloudUploadStatus.Canceled => (CanceledTitle, "Nothing half-written was kept"),
        _ => (IsUnreachable(result.Reason) ? UnreachableTitle : FailedTitle, result.Reason ?? "Copy failed"),
    };

    /// <summary>Shown on the zone before the drop when the reachability check failed.</summary>
    public static (string Title, string Detail) Unreachable(string? reason) =>
        (UnreachableTitle, string.IsNullOrEmpty(reason) || reason == UnreachableTitle ? "Check the connection to your server" : reason);

    public static bool IsUnreachable(string? reason) => reason is not null && UnreachableReasons.Contains(reason);

    /// <summary>"512 B", "48 KB", "12.4 MB", "1.2 GB" (binary units).</summary>
    public static string Size(long bytes, CultureInfo? culture = null)
    {
        culture ??= CultureInfo.CurrentCulture;
        string[] units = ["KB", "MB", "GB", "TB"];
        if (bytes < 1024)
        {
            return $"{Math.Max(0, bytes)} B";
        }

        var value = bytes / 1024.0;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return value.ToString(value < 10 ? "0.0" : "0", culture) + " " + units[unit];
    }
}
