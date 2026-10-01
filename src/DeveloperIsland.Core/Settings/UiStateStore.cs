using System.Text.Json;
using DeveloperIsland.Core.Diagnostics;
using DeveloperIsland.Core.Modules;
using DeveloperIsland.Core.Usage;

namespace DeveloperIsland.Core.Settings;

/// <summary>Small UI memory that changes often (unlike settings): the module opened last.</summary>
public sealed record UiState(string? LastActiveModule);

/// <summary>
/// Persists <see cref="UiState"/> in its own file, so selecting a tab does not rewrite and re-apply
/// the settings. Writes only when the value changes.
/// </summary>
public sealed class UiStateStore
{
    private readonly string? _path;
    private UiState _state;

    /// <param name="path">JSON file; null keeps the state in memory only (demo mode).</param>
    public UiStateStore(string? path)
    {
        _path = path;
        _state = Load(path);
    }

    public ModuleId? LastActiveModule =>
        Enum.TryParse<ModuleId>(_state.LastActiveModule, ignoreCase: true, out var id) && Enum.IsDefined(id) ? id : null;

    public void SetLastActive(ModuleId module)
    {
        if (LastActiveModule == module)
        {
            return;
        }

        _state = _state with { LastActiveModule = module.ToString() };
        Save();
    }

    private static UiState Load(string? path)
    {
        try
        {
            if (path is not null && File.Exists(path))
            {
                return JsonSerializer.Deserialize(File.ReadAllText(path), CoreJsonContext.Default.UiState) ?? new UiState(null);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Log.Debug("settings", "UI state unreadable; starting fresh", new { error = ex.GetType().Name });
        }

        return new UiState(null);
    }

    private void Save()
    {
        if (_path is null)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(_state, CoreJsonContext.Default.UiState));
            File.Move(temp, _path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Debug("settings", "UI state could not be saved", new { error = ex.GetType().Name });
        }
    }
}
