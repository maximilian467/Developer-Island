namespace DeveloperIsland.Core.HomeCloud;

/// <summary>Bytes copied so far; <see cref="FileIndex"/> is 1-based and names the file being copied.</summary>
public sealed record HomeCloudProgress(long BytesCopied, long TotalBytes, int FileIndex, int FileCount);

public enum HomeCloudUploadStatus
{
    Succeeded,
    Failed,
    Canceled,
}

/// <summary>Outcome of one upload. <see cref="Reason"/> is short and never contains file names or paths.</summary>
public sealed record HomeCloudUploadResult
{
    public HomeCloudUploadStatus Status { get; init; }

    public int FileCount { get; init; }

    public long Bytes { get; init; }

    public TimeSpan Duration { get; init; }

    public string? Reason { get; init; }

    public static HomeCloudUploadResult Succeeded(int fileCount, long bytes, TimeSpan duration) =>
        new() { Status = HomeCloudUploadStatus.Succeeded, FileCount = fileCount, Bytes = bytes, Duration = duration };

    public static HomeCloudUploadResult Failed(string reason, int fileCount = 0, long bytes = 0, TimeSpan duration = default) =>
        new() { Status = HomeCloudUploadStatus.Failed, Reason = reason, FileCount = fileCount, Bytes = bytes, Duration = duration };

    public static HomeCloudUploadResult Canceled(int fileCount = 0, long bytes = 0, TimeSpan duration = default) =>
        new() { Status = HomeCloudUploadStatus.Canceled, FileCount = fileCount, Bytes = bytes, Duration = duration };
}
