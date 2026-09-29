using DeveloperIsland.Core.Placement;
using DeveloperIsland.Core.Settings;

namespace DeveloperIsland.Tests;

public class SettingsPersistenceTests
{
    private readonly string _path = Path.Combine(TestData.TempDirectory(), "settings.json");

    [Fact]
    public void Missing_file_yields_defaults()
    {
        var store = new SettingsStore(_path);

        Assert.Equal(IslandAnchor.TopCenter, store.Current.Anchor);
        Assert.True(store.Current.AlwaysOnTop);
        Assert.True(store.Current.ClaudeEnabled && store.Current.CodexEnabled && store.Current.MusicEnabled && store.Current.FocusEnabled);
        Assert.False(store.Current.HasCustomPosition);
    }

    [Fact]
    public void Changes_round_trip_through_the_file()
    {
        var store = new SettingsStore(_path);
        store.Update(s =>
        {
            s.Anchor = IslandAnchor.BottomRight;
            s.OffsetX = -42.5;
            s.MonitorDevice = @"\\.\DISPLAY2";
            s.CodexEnabled = false;
            s.Theme = AppTheme.Dark;
            s.StartWithWindows = true;
        });

        var reloaded = new SettingsStore(_path).Current;

        Assert.Equal(IslandAnchor.BottomRight, reloaded.Anchor);
        Assert.Equal(-42.5, reloaded.OffsetX);
        Assert.Equal(@"\\.\DISPLAY2", reloaded.MonitorDevice);
        Assert.False(reloaded.CodexEnabled);
        Assert.Equal(AppTheme.Dark, reloaded.Theme);
        Assert.True(reloaded.StartWithWindows);
        Assert.True(reloaded.HasCustomPosition);
        Assert.Contains("\"bottomRight\"", File.ReadAllText(_path), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Update_raises_changed_with_the_new_settings()
    {
        var store = new SettingsStore(_path);
        AppSettings? received = null;
        store.Changed += s => received = s;

        store.Update(s => s.LaunchHidden = true);

        Assert.True(received?.LaunchHidden);
    }

    [Fact]
    public void Corrupt_file_falls_back_to_defaults_and_is_kept_for_inspection()
    {
        File.WriteAllText(_path, "{ \"anchor\": ");

        var store = new SettingsStore(_path);

        Assert.Equal(IslandAnchor.TopCenter, store.Current.Anchor);
        Assert.True(File.Exists(Path.ChangeExtension(_path, ".corrupt.json")));
    }

    [Fact]
    public void Out_of_range_values_are_sanitized()
    {
        File.WriteAllText(_path, """{ "offsetX": 1e9, "customFocusMinutes": 9999, "anchor": "topLeft" }""");

        var settings = new SettingsStore(_path).Current;

        Assert.Equal(0, settings.OffsetX);
        Assert.Equal(240, settings.CustomFocusMinutes);
        Assert.Equal(IslandAnchor.TopLeft, settings.Anchor);
    }

    [Fact]
    public void Unknown_properties_from_newer_versions_are_ignored()
    {
        File.WriteAllText(_path, """{ "alwaysOnTop": false, "someFutureSetting": { "x": 1 } }""");

        Assert.False(new SettingsStore(_path).Current.AlwaysOnTop);
    }
}
