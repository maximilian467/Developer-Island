using System.Text.Json;
using DeveloperIsland.Core.Diagnostics;
using DeveloperIsland.Core.Usage;

namespace DeveloperIsland.Core.Focus;

/// <summary>
/// Keeps the running or paused focus session in a small JSON file, so it survives a restart or a
/// crash. Written on start, pause, resume and stop only, never per tick.
/// </summary>
public sealed class FocusSessionStore(string path)
{
    public FocusSessionState? Load()
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            return JsonSerializer.Deserialize(File.ReadAllText(path), CoreJsonContext.Default.FocusSessionState);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Log.Warn("focus", "Saved focus session unreadable; starting idle", new { error = ex.GetType().Name });
            return null;
        }
    }

    public void Save(FocusSessionState? state)
    {
        try
        {
            if (state is null)
            {
                File.Delete(path);
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(state, CoreJsonContext.Default.FocusSessionState));
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn("focus", "Focus session could not be saved", new { error = ex.GetType().Name });
        }
    }
}
