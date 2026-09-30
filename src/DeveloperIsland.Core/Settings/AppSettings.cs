using System.Text.Json.Serialization;
using DeveloperIsland.Core.Modules;
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

    public bool GitEnabled { get; set; } = true;

    public bool GitHubEnabled { get; set; } = true;

    public bool CalendarEnabled { get; set; } = true;

    public bool TasksEnabled { get; set; } = true;

    public bool SystemEnabled { get; set; } = true;

    /// <summary>Module names in tab order; see <see cref="ModuleCatalog.NormalizeOrder"/>.</summary>
    public List<string> ModuleOrder { get; set; } = ModuleCatalog.DefaultOrder.Select(m => m.ToString()).ToList();

    /// <summary>Repositories added by the user (working-tree folders).</summary>
    public List<string> GitRepositories { get; set; } = [];

    /// <summary>Repositories of recent Claude Code and Codex sessions, most recent first.</summary>
    public List<string> GitRecentRepositories { get; set; } = [];

    /// <summary>iCalendar links (https or webcal) or .ics file paths.</summary>
    public List<string> CalendarSources { get; set; } = [];

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
    /// <summary>The global shortcut toggles the island from anywhere.</summary>
    public bool GlobalShortcutEnabled { get; set; } = true;

    /// <summary>One of <see cref="ShortcutGesture.Presets"/>, by its text.</summary>
    public string GlobalShortcut { get; set; } = ShortcutGesture.Default.Text;

    [JsonIgnore]
    public IReadOnlyList<ModuleId> Order => ModuleCatalog.NormalizeOrder(ModuleOrder);

    public bool IsEnabled(ModuleId module) => module switch
    {
        ModuleId.Claude => ClaudeEnabled,
        ModuleId.Codex => CodexEnabled,
        ModuleId.Music => MusicEnabled,
        ModuleId.Git => GitEnabled,
        ModuleId.GitHub => GitHubEnabled,
        ModuleId.Focus => FocusEnabled,
        ModuleId.Calendar => CalendarEnabled,
        ModuleId.Tasks => TasksEnabled,
        ModuleId.System => SystemEnabled,
        _ => false,
    };

    public void SetEnabled(ModuleId module, bool enabled)
    {
        switch (module)
        {
            case ModuleId.Claude: ClaudeEnabled = enabled; break;
            case ModuleId.Codex: CodexEnabled = enabled; break;
            case ModuleId.Music: MusicEnabled = enabled; break;
            case ModuleId.Git: GitEnabled = enabled; break;
            case ModuleId.GitHub: GitHubEnabled = enabled; break;
            case ModuleId.Focus: FocusEnabled = enabled; break;
            case ModuleId.Calendar: CalendarEnabled = enabled; break;
            case ModuleId.Tasks: TasksEnabled = enabled; break;
            case ModuleId.System: SystemEnabled = enabled; break;
        }
    }

    public AppSettings Clone()
    {
        var copy = (AppSettings)MemberwiseClone();
        copy.SmartHideProcesses = [.. SmartHideProcesses];
        copy.ModuleOrder = [.. ModuleOrder];
        copy.GitRepositories = [.. GitRepositories];
        copy.GitRecentRepositories = [.. GitRecentRepositories];
        copy.CalendarSources = [.. CalendarSources];
        return copy;
    }
}
