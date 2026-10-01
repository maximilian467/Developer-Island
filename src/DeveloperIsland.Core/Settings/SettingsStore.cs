using System.Text.Json;
using System.Text.Json.Serialization;
using DeveloperIsland.Core.Diagnostics;

namespace DeveloperIsland.Core.Settings;

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(AppSettings))]
internal sealed partial class SettingsJsonContext : JsonSerializerContext;

/// <summary>
/// Loads and saves <see cref="AppSettings"/> atomically (write to a temp file, then replace).
/// A corrupt file is kept as <c>settings.corrupt.json</c> and defaults are used instead.
/// </summary>
public sealed class SettingsStore
{
    private readonly string _path;
    private readonly object _gate = new();

    public SettingsStore(string path)
    {
        _path = path;
        IsFirstRun = !File.Exists(path);
        Current = Load();
    }

    /// <summary>True when no settings file existed yet (fresh install).</summary>
    public bool IsFirstRun { get; }

    public AppSettings Current { get; private set; }

    public string FilePath => _path;

    /// <summary>Raised on the calling thread after settings were changed and saved.</summary>
    public event Action<AppSettings>? Changed;

    public AppSettings Load()
    {
        lock (_gate)
        {
            if (!File.Exists(_path))
            {
                return Sanitize(new AppSettings());
            }

            try
            {
                var settings = JsonSerializer.Deserialize(File.ReadAllText(_path), SettingsJsonContext.Default.AppSettings) ?? new AppSettings();
                return Sanitize(settings);
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
            {
                Log.Warn("settings", "Settings file is unreadable; defaults are used", ex: ex);
                TryBackupCorruptFile();
                return Sanitize(new AppSettings());
            }
        }
    }

    /// <summary>Applies a change, persists it and notifies listeners.</summary>
    public void Update(Action<AppSettings> change)
    {
        AppSettings snapshot;
        lock (_gate)
        {
            var next = Current.Clone();
            change(next);
            Current = Sanitize(next);
            Save(Current);
            snapshot = Current;
        }

        Changed?.Invoke(snapshot);
    }

    private void Save(AppSettings settings)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
            var temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(settings, SettingsJsonContext.Default.AppSettings));
            File.Move(temp, _path, overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Error("settings", "Settings could not be saved", ex);
        }
    }

    private void TryBackupCorruptFile()
    {
        try
        {
            File.Copy(_path, Path.ChangeExtension(_path, ".corrupt.json"), overwrite: true);
        }
        catch
        {
            // Best effort only.
        }
    }

    private static AppSettings Sanitize(AppSettings s)
    {
        if (!Enum.IsDefined(s.Anchor))
        {
            s.Anchor = Placement.IslandAnchor.TopCenter;
        }

        if (!double.IsFinite(s.OffsetX) || Math.Abs(s.OffsetX) > 20_000)
        {
            s.OffsetX = 0;
        }

        if (!double.IsFinite(s.OffsetY) || Math.Abs(s.OffsetY) > 20_000)
        {
            s.OffsetY = 0;
        }

        s.CustomFocusMinutes = Math.Clamp(s.CustomFocusMinutes, 5, 240);
        if (!Enum.IsDefined(s.SmartHideBehavior))
        {
            s.SmartHideBehavior = SmartHideBehavior.Retract;
        }

        s.SmartHideProcesses = (s.SmartHideProcesses ?? [])
            .Select(Island.SmartHidePolicy.NormalizeProcessName)
            .Where(n => n.Length > 0)
            .Distinct()
            .ToList();
        s.AutoHideApps = Island.AutoHideRules.Normalize(s.AutoHideApps, s.SmartHideProcesses);
        s.SmartHideProcesses = s.AutoHideApps.Where(a => a.Enabled).Select(a => a.Process).ToList();
        s.GlobalShortcut = ShortcutGesture.FromText(s.GlobalShortcut).Text;
        s.ModuleOrder = Modules.ModuleCatalog.NormalizeOrder(s.ModuleOrder).Select(m => m.ToString()).ToList();
        s.FavoriteModules = (s.FavoriteModules ?? [])
            .Select(f => Enum.TryParse<Modules.ModuleId>(f?.Trim(), ignoreCase: true, out var id) && Enum.IsDefined(id) ? id.ToString() : null)
            .Where(f => f is not null)
            .Select(f => f!)
            .Distinct()
            .ToList();
        s.GitRepositories = CleanList(s.GitRepositories, 20);
        s.GitRecentRepositories = CleanList(s.GitRecentRepositories, Git.GitService.MaxRecent);
        s.CalendarSources = CleanList(s.CalendarSources, 10);
        s.Version = AppSettings.CurrentVersion;
        return s;
    }

    private static List<string> CleanList(List<string>? values, int max) =>
        (values ?? [])
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v => v.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(max)
            .ToList();
}
