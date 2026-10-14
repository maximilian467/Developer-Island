using System.Text.Json;
using DeveloperIsland.Core.Diagnostics;
using DeveloperIsland.Core.Usage;

namespace DeveloperIsland.Core.Awake;

/// <summary>
/// Keeps the awake session's recovery journal in a small JSON file, written atomically (a temp file,
/// then a rename). A null path (demo mode) keeps nothing.
/// </summary>
public sealed class AwakeJournalStore(string? path)
{
    public AwakeJournal? Load()
    {
        try
        {
            if (path is null || !File.Exists(path))
            {
                return null;
            }

            return JsonSerializer.Deserialize(File.ReadAllText(path), CoreJsonContext.Default.AwakeJournal);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Log.Warn("awake", "Awake journal unreadable", new { error = ex.GetType().Name });
            return null;
        }
    }

    /// <summary>Writes (or, for null, deletes) the journal. False when the disk refused.</summary>
    public bool Save(AwakeJournal? journal)
    {
        if (path is null)
        {
            return true;
        }

        try
        {
            if (journal is null)
            {
                File.Delete(path);
                return true;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(journal, CoreJsonContext.Default.AwakeJournal));
            File.Move(temp, path, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn("awake", "Awake journal could not be saved", new { error = ex.GetType().Name });
            return false;
        }
    }
}
