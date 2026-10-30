using System.Diagnostics;
using System.Text.Json;
using DeveloperIsland.Core.Calendar;
using DeveloperIsland.Core.Diagnostics;

namespace DeveloperIsland.Platform.Secrets;

/// <summary>
/// Private calendar links in the desktop's keyring (the freedesktop Secret Service: GNOME Keyring on
/// Ubuntu, KWallet on KDE) through <c>secret-tool</c>, the Linux counterpart of Windows Credential
/// Manager. A link grants read access to a calendar, so it is treated like a password: never in
/// settings.json, never in logs. Without a keyring (a minimal session) the links fall back to a file
/// only this user can read (mode 0600).
/// </summary>
internal sealed class SecretServiceStore : ISecretStore
{
    private const string Attribute = "developer-island";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private readonly bool _keyring;
    private readonly string _fallbackPath;
    private readonly object _gate = new();

    public SecretServiceStore(string fallbackPath)
    {
        _fallbackPath = fallbackPath;
        _keyring = ProcessRunner.FindExecutable("secret-tool", "/usr/bin/secret-tool") is not null
            && !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS"));
        Log.Info("calendar", _keyring ? "Calendar links kept in the keyring" : "No keyring tool; calendar links kept in a private file");
    }

    public void Save(string key, string secret)
    {
        if (_keyring && Run(secret, "store", "--label=Developer Island calendar", "app", Attribute, "key", key) is not null)
        {
            RemoveFromFile(key);
            return;
        }

        lock (_gate)
        {
            var all = ReadFile();
            all[key] = secret;
            WriteFile(all);
        }
    }

    public string? Load(string key)
    {
        if (_keyring && Run(null, "lookup", "app", Attribute, "key", key) is { Length: > 0 } value)
        {
            return value.TrimEnd('\n');
        }

        lock (_gate)
        {
            return ReadFile().TryGetValue(key, out var secret) ? secret : null;
        }
    }

    public void Delete(string key)
    {
        if (_keyring)
        {
            Run(null, "clear", "app", Attribute, "key", key);
        }

        RemoveFromFile(key);
    }

    private void RemoveFromFile(string key)
    {
        lock (_gate)
        {
            var all = ReadFile();
            if (all.Remove(key))
            {
                WriteFile(all);
            }
        }
    }

    private Dictionary<string, string> ReadFile()
    {
        try
        {
            return File.Exists(_fallbackPath)
                ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(_fallbackPath)) ?? []
                : [];
        }
        catch (Exception ex)
        {
            Log.Warn("calendar", "Secret file unreadable", new { error = ex.GetType().Name });
            return [];
        }
    }

    private void WriteFile(Dictionary<string, string> all)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_fallbackPath)!);
        if (all.Count == 0)
        {
            File.Delete(_fallbackPath);
            return;
        }

        var temp = _fallbackPath + ".tmp";
        using (var stream = new FileStream(temp, new FileStreamOptions
        {
            Mode = FileMode.Create,
            Access = FileAccess.Write,
            UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
        }))
        {
            JsonSerializer.Serialize(stream, all);
        }

        File.Move(temp, _fallbackPath, overwrite: true);
    }

    /// <summary>Runs secret-tool; the secret goes through stdin, never the command line.</summary>
    private static string? Run(string? input, params string[] args)
    {
        try
        {
            var info = new ProcessStartInfo("secret-tool")
            {
                UseShellExecute = false,
                RedirectStandardInput = input is not null,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            foreach (var arg in args)
            {
                info.ArgumentList.Add(arg);
            }

            using var process = Process.Start(info);
            if (process is null)
            {
                return null;
            }

            if (input is not null)
            {
                process.StandardInput.Write(input);
                process.StandardInput.Close();
            }

            var output = process.StandardOutput.ReadToEndAsync();
            if (!process.WaitForExit(Timeout))
            {
                process.Kill();
                return null;
            }

            return process.ExitCode == 0 ? output.Result : null;
        }
        catch (Exception ex)
        {
            Log.Debug("calendar", "Keyring unavailable", new { error = ex.GetType().Name });
            return null;
        }
    }
}
