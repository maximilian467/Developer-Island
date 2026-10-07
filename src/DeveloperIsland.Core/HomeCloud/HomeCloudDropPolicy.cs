using DeveloperIsland.Core.Island;

namespace DeveloperIsland.Core.HomeCloud;

/// <summary>Pure decisions of the Home Cloud drop target, so the window code only asks.</summary>
public static class HomeCloudDropPolicy
{
    /// <summary>
    /// DragEnter: does this drag open (or keep) the drop zone? Only files and folders do, and never
    /// while an upload is still running or while Home Cloud is off.
    /// </summary>
    public static bool AcceptsDrag(bool enabled, bool hasFiles, IslandMode mode, DropPhase phase) =>
        enabled && hasFiles && !(mode == IslandMode.Drop && phase == DropPhase.Uploading);

    /// <summary>DragOver and Drop: is a copy offered? Only while the open zone waits for the drop.</summary>
    public static bool AcceptsDrop(bool enabled, bool hasFiles, IslandMode mode, DropPhase phase) =>
        enabled && hasFiles && mode == IslandMode.Drop && phase == DropPhase.Hover;
}
