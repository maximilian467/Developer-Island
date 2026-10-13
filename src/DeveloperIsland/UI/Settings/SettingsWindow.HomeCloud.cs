using System.Diagnostics;
using DeveloperIsland.Core.Diagnostics;
using DeveloperIsland.Core.HomeCloud;
using DeveloperIsland.Core.Settings;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.System;

namespace DeveloperIsland.UI.Settings;

/// <summary>Settings, Home Cloud: the drop zone that copies dropped files to the home server.</summary>
public sealed partial class SettingsWindow
{
    private void LoadHomeCloud(AppSettings s)
    {
        HomeCloudToggle.IsOn = s.HomeCloudEnabled;
        if (HomeCloudPathBox.Text != s.HomeCloudTargetPath)
        {
            HomeCloudPathBox.Text = s.HomeCloudTargetPath;
        }

        (s.HomeCloudDropSide == HomeCloudDropSide.Left ? HomeCloudSideLeft : HomeCloudSideRight).IsChecked = true;
        foreach (var dependent in new Control[] { HomeCloudPathBox, HomeCloudSideLeft, HomeCloudSideRight })
        {
            dependent.IsEnabled = s.HomeCloudEnabled;
        }

        if (s.HomeCloudEnabled && !s.HomeCloudActive)
        {
            HomeCloudStatus.Text = "Set a target folder to use the drop zone.";
        }
    }

    private void OnHomeCloudToggled(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        var on = HomeCloudToggle.IsOn;
        _store.Update(s => s.HomeCloudEnabled = on);
        Log.Info("settings", "Setting changed", new { setting = "HomeCloudEnabled", value = on });
    }

    private void OnHomeCloudSideChecked(object sender, RoutedEventArgs e)
    {
        if (_loading || sender is not RadioButton { Tag: string tag } || !Enum.TryParse<HomeCloudDropSide>(tag, out var side))
        {
            return;
        }

        _store.Update(s => s.HomeCloudDropSide = side);
        Log.Info("settings", "Setting changed", new { setting = "HomeCloudDropSide", value = side.ToString() });
    }

    private void OnHomeCloudPathLostFocus(object sender, RoutedEventArgs e) => CommitHomeCloudPath();

    private void OnHomeCloudPathKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            CommitHomeCloudPath();
            e.Handled = true;
        }
    }

    private void CommitHomeCloudPath()
    {
        var path = HomeCloudPathBox.Text.Trim();
        if (path.Length == 0 || path == _store.Current.HomeCloudTargetPath)
        {
            HomeCloudPathBox.Text = _store.Current.HomeCloudTargetPath;
            return;
        }

        _store.Update(s => s.HomeCloudTargetPath = path);
        HomeCloudStatus.Text = "Check that the target folder can be reached.";
        Log.Info("settings", "Setting changed", new { setting = "HomeCloudTargetPath" });
    }

    private async void OnHomeCloudTest(object sender, RoutedEventArgs e)
    {
        CommitHomeCloudPath();
        if (!HasHomeCloudTarget())
        {
            return;
        }

        HomeCloudTestButton.IsEnabled = false;
        HomeCloudStatus.Text = "Checking…";
        try
        {
            var result = await HomeCloudReachability.CheckAsync(_store.Current.HomeCloudTargetPath);
            HomeCloudStatus.Text = result.Reachable ? "Connected. The target folder is reachable." : result.Reason ?? "Home Cloud not reachable";
        }
        catch (Exception ex)
        {
            Log.Warn("homecloud", "connection test failed", null, ex);
            HomeCloudStatus.Text = "The connection test failed.";
        }
        finally
        {
            HomeCloudTestButton.IsEnabled = true;
        }
    }

    private void OnHomeCloudOpen(object sender, RoutedEventArgs e)
    {
        CommitHomeCloudPath();
        if (!HasHomeCloudTarget())
        {
            return;
        }

        try
        {
            Process.Start("explorer.exe", $"\"{_store.Current.HomeCloudTargetPath}\"")?.Dispose();
            HomeCloudStatus.Text = "Opened the target folder in Explorer.";
        }
        catch (Exception ex)
        {
            Log.Warn("homecloud", "could not open the target folder", null, ex);
            HomeCloudStatus.Text = "Could not open the folder in Explorer.";
        }
    }

    private bool HasHomeCloudTarget()
    {
        if (!string.IsNullOrWhiteSpace(_store.Current.HomeCloudTargetPath))
        {
            return true;
        }

        HomeCloudStatus.Text = "Set a target folder first.";
        return false;
    }
}
