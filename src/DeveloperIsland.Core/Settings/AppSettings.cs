using System.Text.Json.Serialization;
using DeveloperIsland.Core.Placement;

namespace DeveloperIsland.Core.Settings;

public enum AppTheme
{
    System,
    Dark,
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

    public AppSettings Clone() => (AppSettings)MemberwiseClone();
}
