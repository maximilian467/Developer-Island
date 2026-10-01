using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using DeveloperIsland.Core.Diagnostics;
using DeveloperIsland.Core.Settings;

namespace DeveloperIsland.Core.Calendar;

/// <summary>Where calendar secrets (private iCal links) are kept, away from settings files.</summary>
public interface ISecretStore
{
    void Save(string key, string secret);

    string? Load(string key);

    void Delete(string key);
}

/// <summary>Secrets in memory only (demo mode, tests).</summary>
public sealed class MemorySecretStore : ISecretStore
{
    private readonly Dictionary<string, string> _secrets = new(StringComparer.Ordinal);

    public void Save(string key, string secret) => _secrets[key] = secret;

    public string? Load(string key) => _secrets.TryGetValue(key, out var value) ? value : null;

    public void Delete(string key) => _secrets.Remove(key);
}

/// <summary>
/// Windows Credential Manager (generic credentials, persisted for this user on this PC), the
/// place Windows offers for app secrets. A private calendar link grants read access to the
/// calendar, so it is treated like a password: never in settings.json, never in logs.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class CredentialManagerSecretStore(string prefix = "DeveloperIsland") : ISecretStore
{
    private const int CRED_TYPE_GENERIC = 1;
    private const int CRED_PERSIST_LOCAL_MACHINE = 2;
    private const int ERROR_NOT_FOUND = 1168;

    public void Save(string key, string secret)
    {
        var blob = Encoding.Unicode.GetBytes(secret);
        var handle = GCHandle.Alloc(blob, GCHandleType.Pinned);
        try
        {
            var credential = new CREDENTIAL
            {
                Type = CRED_TYPE_GENERIC,
                TargetName = Target(key),
                CredentialBlobSize = blob.Length,
                CredentialBlob = handle.AddrOfPinnedObject(),
                Persist = CRED_PERSIST_LOCAL_MACHINE,
                UserName = Environment.UserName,
            };
            if (!CredWrite(ref credential, 0))
            {
                throw new IOException($"Credential Manager refused the secret (error {Marshal.GetLastWin32Error()}).");
            }
        }
        finally
        {
            handle.Free();
        }
    }

    public string? Load(string key)
    {
        if (!CredRead(Target(key), CRED_TYPE_GENERIC, 0, out var pointer))
        {
            return null;
        }

        try
        {
            var credential = Marshal.PtrToStructure<CREDENTIAL>(pointer);
            if (credential.CredentialBlob == IntPtr.Zero || credential.CredentialBlobSize == 0)
            {
                return null;
            }

            var bytes = new byte[credential.CredentialBlobSize];
            Marshal.Copy(credential.CredentialBlob, bytes, 0, bytes.Length);
            return Encoding.Unicode.GetString(bytes);
        }
        finally
        {
            CredFree(pointer);
        }
    }

    public void Delete(string key)
    {
        if (!CredDelete(Target(key), CRED_TYPE_GENERIC, 0) && Marshal.GetLastWin32Error() != ERROR_NOT_FOUND)
        {
            Log.Debug("calendar", "Secret not deleted", new { error = Marshal.GetLastWin32Error() });
        }
    }

    private string Target(string key) => $"{prefix}/{key}";

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CREDENTIAL
    {
        public int Flags;
        public int Type;
        public string TargetName;
        public string? Comment;
        public long LastWritten;
        public int CredentialBlobSize;
        public IntPtr CredentialBlob;
        public int Persist;
        public int AttributeCount;
        public IntPtr Attributes;
        public string? TargetAlias;
        public string? UserName;
    }

    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredWrite(ref CREDENTIAL credential, int flags);

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredRead(string target, int type, int flags, out IntPtr credential);

    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredDelete(string target, int type, int flags);

    [DllImport("advapi32.dll")]
    private static extern void CredFree(IntPtr buffer);
}

/// <summary>
/// Calendar feeds as settings keep them: a link is stored in the secret store under the feed's id and
/// only its provider name ("Google Calendar") stays in settings; an .ics file keeps its path.
/// </summary>
public static class CalendarFeeds
{
    public static string SecretKey(string feedId) => "calendar/" + feedId;

    /// <summary>Adds a calendar link or .ics file; links go to the secret store.</summary>
    public static CalendarFeed Add(AppSettings settings, ISecretStore secrets, string location)
    {
        var trimmed = location.Trim();
        var isLink = IcsCalendarSource.IsWebAddress(trimmed);
        var feed = new CalendarFeed
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = IcsCalendarSource.DisplayName(trimmed),
            FilePath = isLink ? null : trimmed,
        };
        if (isLink)
        {
            secrets.Save(SecretKey(feed.Id), trimmed);
        }

        settings.CalendarFeeds.Add(feed);
        return feed;
    }

    public static void Remove(AppSettings settings, ISecretStore secrets, string feedId)
    {
        settings.CalendarFeeds.RemoveAll(f => f.Id == feedId);
        secrets.Delete(SecretKey(feedId));
    }

    /// <summary>The locations to fetch: file paths as saved, links from the secret store (missing ones skipped).</summary>
    public static List<string> Resolve(IEnumerable<CalendarFeed> feeds, ISecretStore secrets) =>
        feeds.Select(f => f.FilePath ?? secrets.Load(SecretKey(f.Id)))
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .Select(l => l!)
            .ToList();

    /// <summary>
    /// Moves calendar links saved in plain text by earlier versions into the secret store. Returns
    /// whether settings changed (and must be saved).
    /// </summary>
    public static bool MigratePlainText(AppSettings settings, ISecretStore secrets)
    {
        if (settings.CalendarSources.Count == 0)
        {
            return false;
        }

        foreach (var location in settings.CalendarSources.ToList())
        {
            Add(settings, secrets, location);
        }

        settings.CalendarSources.Clear();
        return true;
    }
}
