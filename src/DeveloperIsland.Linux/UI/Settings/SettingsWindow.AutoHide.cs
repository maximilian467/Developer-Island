using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using DeveloperIsland.Core.Diagnostics;
using DeveloperIsland.Core.HomeCloud;
using DeveloperIsland.Core.Island;
using DeveloperIsland.Core.Settings;
using DeveloperIsland.Platform.Windowing;
using DeveloperIsland.UI.Components;

namespace DeveloperIsland.UI.Settings;

/// <summary>Settings, Position (auto-hide in apps, the GNOME Shell helper) and Home Cloud.</summary>
public sealed partial class SettingsWindow
{
    private void BuildAutoHideRows(AppSettings s)
    {
        AutoHideRows.Children.Clear();
        foreach (var app in s.AutoHideApps)
        {
            AutoHideRows.Children.Add(AppRow(app, s.SmartHideEnabled));
            AutoHideRows.Children.Add(Divider());
        }
    }

    private Border AppRow(AutoHideApp app, bool featureOn)
    {
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto"), ColumnSpacing = 12 };

        var icon = new Image { Width = 24, Height = 24, VerticalAlignment = VerticalAlignment.Center };
        var placeholder = new GlyphIcon { Glyph = "", Size = 18 };
        placeholder.Bind(GlyphIcon.ForegroundProperty, this.GetResourceObservable("SettingsTextSecondaryBrush"));
        var iconHost = new Panel { Width = 28, Height = 28, VerticalAlignment = VerticalAlignment.Center, Children = { placeholder, icon } };
        row.Children.Add(iconHost);
        _ = ShowIconAsync(app, icon, placeholder);

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = app.Name, Classes = { "sRowTitle" } });
        text.Children.Add(new TextBlock { Text = app.Process, Classes = { "sRowText" } });
        Grid.SetColumn(text, 1);
        row.Children.Add(text);

        if (!AutoHideRules.IsBuiltIn(app.Process))
        {
            var remove = new Button { Content = "Remove", VerticalAlignment = VerticalAlignment.Center };
            remove.Click += (_, _) => _store.Update(x => x.AutoHideApps.RemoveAll(a => a.Process == app.Process));
            Grid.SetColumn(remove, 2);
            row.Children.Add(remove);
        }

        var toggle = new ToggleSwitch { IsChecked = app.Enabled, IsEnabled = featureOn, Tag = "App:" + app.Process, Classes = { "sToggle" } };
        toggle.IsCheckedChanged += OnToggle;
        Grid.SetColumn(toggle, 3);
        row.Children.Add(toggle);
        return Row(row);
    }

    private static async Task ShowIconAsync(AutoHideApp app, Image target, Control placeholder)
    {
        if (await AppCatalog.LoadIconAsync(app.Process) is { } bitmap)
        {
            target.Source = bitmap;
            placeholder.IsVisible = false;
        }
    }

    private static void SetAppEnabled(AppSettings s, string process, bool on)
    {
        var app = s.AutoHideApps.FirstOrDefault(a => a.Process == process);
        if (app is not null)
        {
            app.Enabled = on;
        }
    }

    private void AddApp(AppEntry entry)
    {
        _store.Update(s =>
        {
            var existing = s.AutoHideApps.FirstOrDefault(a => a.Process == entry.Process);
            if (existing is not null)
            {
                existing.Enabled = true;
                existing.Path ??= entry.Path;
                return;
            }

            s.AutoHideApps.Add(new AutoHideApp { Process = entry.Process, Name = entry.Name, Path = entry.Path, Enabled = true });
            s.SmartHideEnabled = true;
        });
        Log.Info("settings", "Auto-hide app added", new { process = entry.Process });
    }

    /// <summary>Add application: pick from the apps that are running now.</summary>
    private async void OnAddApplication(object? sender, RoutedEventArgs e)
    {
        try
        {
            AddAppButton.IsEnabled = false;
            var known = _store.Current.AutoHideApps.Select(a => a.Process).ToHashSet();
            var apps = (await Task.Run(AppCatalog.RunningApps)).Where(a => !known.Contains(a.Process)).ToList();

            var list = new ListBox { SelectionMode = SelectionMode.Single, MaxHeight = 360, MinWidth = 380 };
            foreach (var app in apps)
            {
                var image = new Image { Width = 24, Height = 24, VerticalAlignment = VerticalAlignment.Center };
                var item = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 12, Margin = new Thickness(0, 4), Tag = app };
                item.Children.Add(image);
                var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                text.Children.Add(new TextBlock { Text = app.Name });
                text.Children.Add(new TextBlock { Text = app.Process, FontSize = 12, Opacity = 0.7 });
                Grid.SetColumn(text, 1);
                item.Children.Add(text);
                list.Items.Add(item);
                _ = AppCatalog.LoadIconAsync(app.Process).ContinueWith(t => image.Source = t.Result, TaskScheduler.FromCurrentSynchronizationContext());
            }

            var content = new StackPanel { Spacing = 8 };
            content.Children.Add(new TextBlock
            {
                Text = apps.Count == 0 ? "No other apps are running. Use Browse… to pick a program file." : "The island steps aside while the app you choose is in front.",
                TextWrapping = TextWrapping.Wrap,
                Opacity = 0.8,
                MaxWidth = 380,
            });
            if (apps.Count > 0)
            {
                content.Children.Add(list);
            }

            var chosen = await Dialog.ShowAsync(this, "Add application", content, apps.Count > 0 ? "Add" : null, "Cancel", primary =>
            {
                primary.IsEnabled = false;
                list.SelectionChanged += (_, _) => primary.IsEnabled = list.SelectedItem is not null;
                list.DoubleTapped += (_, _) =>
                {
                    if (list.SelectedItem is not null)
                    {
                        primary.Command?.Execute(null);
                        primary.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    }
                };
            });

            if (chosen && list.SelectedItem is Grid { Tag: AppEntry picked })
            {
                AddApp(picked);
            }
        }
        catch (Exception ex)
        {
            Log.Warn("settings", "Application could not be added", new { error = ex.GetType().Name });
        }
        finally
        {
            AddAppButton.IsEnabled = true;
        }
    }

    /// <summary>Browse: pick any program file.</summary>
    private async void OnBrowseApplication(object? sender, RoutedEventArgs e)
    {
        try
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Choose a program",
                AllowMultiple = false,
                SuggestedStartLocation = await StorageProvider.TryGetFolderFromPathAsync("/usr/bin"),
            });
            if (files.Count > 0 && files[0].TryGetLocalPath() is { } path)
            {
                AddApp(AppCatalog.FromExecutable(path));
            }
        }
        catch (Exception ex)
        {
            Log.Warn("settings", "Program could not be added", new { error = ex.GetType().Name });
        }
    }

    // GNOME Shell helper ------------------------------------------------------------------------------

    /// <summary>On GNOME Wayland, auto-hide and fullscreen detection need the bundled Shell extension.</summary>
    private void UpdateShellHelper()
    {
        var status = Platform.Windowing.ShellExtension.Status();
        ShellHelperRow.IsVisible = status != ShellExtensionStatus.NotGnome;
        ShellHelperButton.IsVisible = status is ShellExtensionStatus.NotInstalled or ShellExtensionStatus.Disabled or ShellExtensionStatus.Outdated;
        ShellHelperButton.Content = status == ShellExtensionStatus.Disabled ? "Enable" : status == ShellExtensionStatus.Outdated ? "Update" : "Install";
        ShellHelperButton.IsEnabled = !_isDemo;
        ShellHelperText.Text = status switch
        {
            ShellExtensionStatus.Active => "Active. GNOME Shell tells the island which app is in front, including Wayland apps such as Firefox.",
            ShellExtensionStatus.NeedsRelogin => "Installed. Log out and back in once so GNOME Shell loads it (Wayland sessions load new extensions only at login).",
            ShellExtensionStatus.Disabled => "Installed but turned off. Enable it so the island can see Wayland apps such as Firefox and GNOME apps.",
            ShellExtensionStatus.Outdated => "A newer version comes with this app. Update it, then log out and back in.",
            _ => X11ActiveWindowSource.IsWaylandSession
                ? "On Wayland, only GNOME Shell knows which app is in front. This small extension shares just that (app, maximized, fullscreen) with the island. Without it, auto-hide sees X11 apps only."
                : "Optional on X11. On Wayland it lets the island see which app is in front.",
        };
    }

    private void OnInstallShellHelper(object? sender, RoutedEventArgs e)
    {
        var ok = Platform.Windowing.ShellExtension.InstallAndEnable();
        Log.Info("settings", "GNOME Shell helper", new { installed = ok });
        UpdateShellHelper();
    }

    // Home Cloud -------------------------------------------------------------------------------------

    private void LoadHomeCloud(AppSettings s)
    {
        HomeCloudToggle.IsChecked = s.HomeCloudEnabled;
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

    private void OnHomeCloudToggled(object? sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        var on = HomeCloudToggle.IsChecked == true;
        _store.Update(s => s.HomeCloudEnabled = on);
        Log.Info("settings", "Setting changed", new { setting = "HomeCloudEnabled", value = on });
    }

    private void OnHomeCloudSideChecked(object? sender, RoutedEventArgs e)
    {
        if (_loading || sender is not RadioButton { Tag: string tag, IsChecked: true } || !Enum.TryParse<HomeCloudDropSide>(tag, out var side))
        {
            return;
        }

        _store.Update(s => s.HomeCloudDropSide = side);
    }

    private void OnHomeCloudPathLostFocus(object? sender, RoutedEventArgs e) => CommitHomeCloudPath();

    private void OnHomeCloudPathKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is Key.Enter or Key.Return)
        {
            CommitHomeCloudPath();
            e.Handled = true;
        }
    }

    private void CommitHomeCloudPath()
    {
        var path = (HomeCloudPathBox.Text ?? string.Empty).Trim();
        if (path.StartsWith("~/", StringComparison.Ordinal))
        {
            path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path[2..]);
        }

        if (path.Length == 0 || path == _store.Current.HomeCloudTargetPath)
        {
            HomeCloudPathBox.Text = _store.Current.HomeCloudTargetPath;
            return;
        }

        _store.Update(s => s.HomeCloudTargetPath = path);
        HomeCloudStatus.Text = "Check that the target folder can be reached.";
        Log.Info("settings", "Setting changed", new { setting = "HomeCloudTargetPath" });
    }

    private async void OnHomeCloudTest(object? sender, RoutedEventArgs e)
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

    private void OnHomeCloudOpen(object? sender, RoutedEventArgs e)
    {
        CommitHomeCloudPath();
        if (!HasHomeCloudTarget())
        {
            return;
        }

        HomeCloudStatus.Text = OpenInFileManager(_store.Current.HomeCloudTargetPath)
            ? "Opened the target folder."
            : "Could not open the folder.";
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
