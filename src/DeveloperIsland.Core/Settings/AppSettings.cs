using System.Text.Json.Serialization;
using DeveloperIsland.Core.Placement;

namespace DeveloperIsland.Core.Settings;

public enum AppTheme
{
    System,
    Dark,
}

/// <summary>What Smart Auto-Hide does over a maximized browser.</summary>
public enum SmartHideBehavior
{
    /// <summary>Retract into the top screen edge as a small notch.</summary>
    Retract,

    /// <summary>Hide completely while the browser is maximized in front.</summary>
    Hide,
}

/// <summary>User settings, persisted as JSON. Every property has a safe default.</summary>
public sealed class AppSettings
{
    public const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;

    // General
    public bool StartWithWindows { get; set; }

    public bool AlwaysOnTop { get; set; } = true;

    public bool LaunchHidden { get; set; }

    // Position
    /// <summary>Device name of the monitor (for example <c>\.\DISPLAY2</c>); null means the primary monitor.</summary>
    public string? MonitorDevice { get; set; }

    [JsonConverter(typeof(JsonStringEnumConverter<IslandAnchor>))]
    public IslandAnchor Anchor { get; set; } = IslandAnchor.TopCenter;

    /// <summary>Offset from the anchor position in device-independent pixels. Non-zero after a drag.</summary>
    public double OffsetX { get; set; }

    public double OffsetY { get; set; }

    [JsonIgnore]
    public bool HasCustomPosition => Math.Abs(OffsetX) > 0.5 || Math.Abs(OffsetY) > 0.5;

    // Modules
    public bool ClaudeEnabled { get; set; } = true;

    public bool CodexEnabled { get; set; } = true;

    public bool MusicEnabled { get; set; } = true;

    public bool FocusEnabled { get; set; } = true;

    // Appearance
    [JsonConverter(typeof(JsonStringEnumConverter<AppTheme>))]
    public AppTheme Theme { get; set; } = AppTheme.System;

    // Focus
    public int CustomFocusMinutes { get; set; } = 45;

    // Smart Auto-Hide (top-center island over maximized browsers)
    public bool SmartHideEnabled { get; set; } = true;

    [JsonConverter(typeof(JsonStringEnumConverter<SmartHideBehavior>))]
    public SmartHideBehavior SmartHideBehavior { get; set; } = SmartHideBehavior.Retract;

    /// <summary>Executable names without extension, lower case (chrome, msedge, firefox, ...).</summary>
    public List<string> SmartHideProcesses { get; set; } = ["chrome", "msedge", "firefox"];

    // Keyboard
    /// <summary>Ctrl+Alt+Space toggles the island from anywhere.</summary>
    public bool GlobalShortcutEnabled { get; set; } = true;

    public AppSettings Clone()
    {
        var copy = (AppSettings)MemberwiseClone();
        copy.SmartHideProcesses = [.. SmartHideProcesses];
        return copy;
    }
}
