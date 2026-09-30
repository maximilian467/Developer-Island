using System.Text.Json;
using DeveloperIsland.Core.Diagnostics;
using DeveloperIsland.Core.Usage;

namespace DeveloperIsland.Core.Tasks;

public sealed record TaskItem(Guid Id, string Title, bool Done, DateTimeOffset CreatedAt, DateTimeOffset? CompletedAt = null);

/// <summary>
/// Local to-do list: add, complete, delete and reorder, persisted as JSON next to the settings.
/// Titles are the user's own text; they stay in this file and are never logged.
/// </summary>
public sealed class TaskStore
{
    public const int MaxTitleLength = 200;
    public const int MaxTasks = 500;

    private readonly string? _path;
    private readonly TimeProvider _time;
    private readonly object _gate = new();
    private List<TaskItem> _items;

    /// <param name="path">JSON file; null keeps tasks in memory only (demo mode).</param>
    public TaskStore(string? path, TimeProvider? time = null)
    {
        _path = path;
        _time = time ?? TimeProvider.System;
        _items = Load(path);
    }

    /// <summary>Raised after every change, on the caller's thread.</summary>
    public event Action? Changed;

    public IReadOnlyList<TaskItem> Items
    {
        get
        {
            lock (_gate)
            {
                return _items.ToList();
            }
        }
    }

    public int OpenCount
    {
        get
        {
            lock (_gate)
            {
                return _items.Count(t => !t.Done);
            }
        }
    }

    /// <summary>Adds an open task at the end of the open ones. Blank titles are ignored.</summary>
    public TaskItem? Add(string? title)
    {
        var clean = Clean(title);
        if (clean is null)
        {
            return null;
        }

        var item = new TaskItem(Guid.NewGuid(), clean, false, _time.GetUtcNow());
        Mutate(items =>
        {
            if (items.Count >= MaxTasks)
            {
                return false;
            }

            var firstDone = items.FindIndex(t => t.Done);
            items.Insert(firstDone < 0 ? items.Count : firstDone, item);
            return true;
        });
        return item;
    }

    public void SetDone(Guid id, bool done) => Mutate(items =>
    {
        var index = items.FindIndex(t => t.Id == id);
        if (index < 0 || items[index].Done == done)
        {
            return false;
        }

        items[index] = items[index] with { Done = done, CompletedAt = done ? _time.GetUtcNow() : null };
        return true;
    });

    public void Rename(Guid id, string? title)
    {
        var clean = Clean(title);
        if (clean is null)
        {
            return;
        }

        Mutate(items =>
        {
            var index = items.FindIndex(t => t.Id == id);
            if (index < 0 || items[index].Title == clean)
            {
                return false;
            }

            items[index] = items[index] with { Title = clean };
            return true;
        });
    }

    public void Delete(Guid id) => Mutate(items => items.RemoveAll(t => t.Id == id) > 0);

    public void ClearCompleted() => Mutate(items => items.RemoveAll(t => t.Done) > 0);

    /// <summary>Moves a task up (-1) or down (+1) within the list.</summary>
    public void Move(Guid id, int delta) => Mutate(items =>
    {
        var from = items.FindIndex(t => t.Id == id);
        if (from < 0)
        {
            return false;
        }

        var to = Math.Clamp(from + delta, 0, items.Count - 1);
        if (to == from)
        {
            return false;
        }

        var item = items[from];
        items.RemoveAt(from);
        items.Insert(to, item);
        return true;
    });

    private static string? Clean(string? title)
    {
        var clean = string.Join(' ', (title ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (clean.Length == 0)
        {
            return null;
        }

        return clean.Length > MaxTitleLength ? clean[..MaxTitleLength] : clean;
    }

    private void Mutate(Func<List<TaskItem>, bool> change)
    {
        List<TaskItem> snapshot;
        lock (_gate)
        {
            var items = _items.ToList();
            if (!change(items))
            {
                return;
            }

            _items = items;
            snapshot = items;
        }

        Save(snapshot);
        Changed?.Invoke();
    }

    private static List<TaskItem> Load(string? path)
    {
        if (path is null || !File.Exists(path))
        {
            return [];
        }

        try
        {
            var items = JsonSerializer.Deserialize(File.ReadAllText(path), CoreJsonContext.Default.ListTaskItem) ?? [];
            return items
                .Where(t => t.Id != Guid.Empty && Clean(t.Title) is not null)
                .DistinctBy(t => t.Id)
                .Take(MaxTasks)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // Keep the unreadable file for the user; start with an empty list.
            Log.Warn("tasks", "Task list unreadable; starting empty", new { error = ex.GetType().Name });
            try
            {
                File.Copy(path, path + ".broken", overwrite: true);
            }
            catch (Exception copy) when (copy is IOException or UnauthorizedAccessException)
            {
                // Best effort.
            }

            return [];
        }
    }

    private void Save(List<TaskItem> items)
    {
        if (_path is null)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(items, CoreJsonContext.Default.ListTaskItem));
            File.Move(temp, _path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn("tasks", "Task list could not be saved", new { error = ex.GetType().Name });
        }
    }
}
