using DeveloperIsland.Core.Island;

namespace DeveloperIsland.Core.Privacy;

/// <summary>
/// Whether any app is using the microphone or the camera right now. A global state, not a module:
/// it is shown in every island state and never decides what the island shows.
/// </summary>
public readonly record struct PrivacyState(bool Microphone, bool Camera)
{
    public static PrivacyState None => default;

    public bool IsActive => Microphone || Camera;

    /// <summary>"Microphone and camera in use", for screen readers and the tooltip.</summary>
    public string Description => (Microphone, Camera) switch
    {
        (true, true) => "Microphone and camera in use",
        (true, false) => "Microphone in use",
        (false, true) => "Camera in use",
        _ => string.Empty,
    };
}

/// <summary>One app's entry in Windows' capability consent store (FILETIME values, 0 when unset).</summary>
public readonly record struct ConsentUsage(long LastUsedTimeStart, long LastUsedTimeStop);

/// <summary>
/// Windows records each app's microphone and camera use under
/// <c>HKCU\...\CapabilityAccessManager\ConsentStore\{microphone|webcam}</c>: an app that has started
/// and not yet stopped (start set, stop 0) is using the device now. This is what Windows' own
/// "in use" tray indicator is based on.
/// </summary>
public static class ConsentStore
{
    public static bool InUse(ConsentUsage usage) => usage.LastUsedTimeStart > 0 && usage.LastUsedTimeStop == 0;

    public static bool AnyInUse(IEnumerable<ConsentUsage> usages) => usages.Any(InUse);
}

public static class PrivacyIndicator
{
    /// <summary>
    /// The indicator shows whenever a device is in use and the island is on screen: compact, live
    /// activity, expanded and the notch (Smart Auto-Hide keeps the notch instead of hiding while a
    /// device is in use). Only a hide the user asked for (tray, fullscreen) takes it away.
    /// </summary>
    public static bool IsVisible(IslandMode mode, PrivacyState state) => state.IsActive && mode != IslandMode.Hidden;
}
