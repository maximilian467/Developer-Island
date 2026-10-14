using DeveloperIsland.Core.Diagnostics;
using DeveloperIsland.Core.Formatting;
using DeveloperIsland.Core.Settings;
using Microsoft.UI.Xaml.Controls;

namespace DeveloperIsland.UI.Settings;

/// <summary>Settings, Modules section: the Awake module's battery floor.</summary>
public sealed partial class SettingsWindow
{
    /// <summary>The battery floors offered; 0 turns the rule off.</summary>
    private static readonly int[] AwakeFloors = [0, 5, 10, 15, 20, 30];

    private void InitAwake()
    {
        foreach (var floor in AwakeFloors)
        {
            AwakeBatteryPicker.Items.Add(new ComboBoxItem { Content = floor == 0 ? "Off" : $"{floor}{DisplayFormat.Nbsp}%", Tag = floor });
        }
    }

    private void LoadAwake(AppSettings s)
    {
        // A hand-edited value that is not offered selects the nearest one shown.
        var nearest = AwakeFloors.MinBy(f => Math.Abs(f - s.AwakeBatteryFloor));
        AwakeBatteryPicker.SelectedIndex = Array.IndexOf(AwakeFloors, nearest);
        AwakeBatteryPicker.IsEnabled = s.AwakeEnabled;
    }

    private void OnAwakeBatteryChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || AwakeBatteryPicker.SelectedItem is not ComboBoxItem { Tag: int floor })
        {
            return;
        }

        _store.Update(s => s.AwakeBatteryFloor = floor);
        Log.Info("settings", "Setting changed", new { setting = "AwakeBatteryFloor", value = floor });
    }
}
