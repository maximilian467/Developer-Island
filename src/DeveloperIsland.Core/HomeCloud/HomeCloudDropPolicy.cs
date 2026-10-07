using DeveloperIsland.Core.Island;
using DeveloperIsland.Core.Placement;

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

    /// <summary>
    /// Should the drag-arming mouse hook run? Only while the island is a notch or hidden (otherwise the
    /// visible capsule is the drop target), only with Home Cloud on, and only for an island at the top
    /// edge, where the arm zone is.
    /// </summary>
    public static bool ShouldArm(bool enabled, IslandMode mode, bool topAnchored) =>
        enabled && topAnchored && mode is IslandMode.Retracted or IslandMode.Hidden;
}

/// <summary>Where a file drag arms the hidden or retracted island: a strip at the top edge above its center.</summary>
public static class HomeCloudArmZone
{
    /// <summary>Half the strip's width, in DIPs.</summary>
    public const double HalfWidth = 150;

    /// <summary>How far down from the monitor's top edge the strip reaches, in DIPs.</summary>
    public const double Depth = 60;

    /// <summary>The strip in screen pixels, for an island centered at <paramref name="centerX"/> (pixels) on a monitor of this DPI scale.</summary>
    public static PixelRect OnScreen(double centerX, int monitorTop, double scale) =>
        new((int)Math.Round(centerX - HalfWidth * scale), monitorTop, (int)Math.Round(2 * HalfWidth * scale), (int)Math.Round(Depth * scale));
}
