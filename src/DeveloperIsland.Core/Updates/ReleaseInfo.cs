using System.Text.Json;

namespace DeveloperIsland.Core.Updates;

/// <summary>A file attached to a release.</summary>
public sealed record ReleaseAsset(string Name, Uri Url, long Size);

/// <summary>
/// A published release newer than the running app may be: its version, release page and the files
/// an update needs (the setup, the checksums and their signature, as tools/build-release.ps1 names them).
/// </summary>
public sealed record ReleaseInfo(ReleaseVersion Version, Uri NotesUrl, ReleaseAsset Setup, ReleaseAsset? Checksums, ReleaseAsset? Signature)
{
    public static string SetupName(string version) => $"DeveloperIsland-Setup-{version}.exe";

    public static string ChecksumsName(string version) => $"SHA256SUMS-{version}.txt";

    public static string SignatureName(string version) => ChecksumsName(version) + ".sig";
}

/// <summary>Reads GitHub's "latest release" response. Pure, so it is tested without GitHub.</summary>
public static class ReleaseJson
{
    public const string Repository = "maximilian467/Developer-Island";

    /// <summary>Release pages and downloads must come from the project's own repository.</summary>
    public const string ReleasesPrefix = "https://github.com/" + Repository + "/releases/";

    /// <summary>
    /// The release, or null for a draft, a prerelease, a tag that is no version, or a release without
    /// its setup (still uploading, or built without the installer).
    /// </summary>
    public static ReleaseInfo? Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || Bool(root, "draft") || Bool(root, "prerelease")
            || !ReleaseVersion.TryParse(Str(root, "tag_name"), out var version) || version.IsPrerelease)
        {
            return null;
        }

        // The asset names carry the version as the tag writes it, without the "v".
        var tagVersion = Str(root, "tag_name")!.Trim().TrimStart('v', 'V');
        var assets = new List<ReleaseAsset>();
        if (root.TryGetProperty("assets", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var asset in list.EnumerateArray())
            {
                if (Str(asset, "name") is { } name && TrustedUri(Str(asset, "browser_download_url"), "download/") is { } url)
                {
                    var size = asset.TryGetProperty("size", out var s) && s.TryGetInt64(out var bytes) ? bytes : 0;
                    assets.Add(new ReleaseAsset(name, url, size));
                }
            }
        }

        ReleaseAsset? Find(string name) => assets.FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase));
        var setup = Find(ReleaseInfo.SetupName(tagVersion));
        var notes = TrustedUri(Str(root, "html_url"), "tag/");
        if (setup is null || notes is null)
        {
            return null;
        }

        return new ReleaseInfo(version, notes, setup, Find(ReleaseInfo.ChecksumsName(tagVersion)), Find(ReleaseInfo.SignatureName(tagVersion)));
    }

    private static Uri? TrustedUri(string? text, string section) =>
        text is not null && text.StartsWith(ReleasesPrefix + section, StringComparison.Ordinal)
            && Uri.TryCreate(text, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps
            ? uri
            : null;

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool Bool(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
}
