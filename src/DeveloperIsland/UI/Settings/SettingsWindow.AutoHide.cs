using DeveloperIsland.Core.Diagnostics;
using DeveloperIsland.Core.Island;
using DeveloperIsland.Core.Settings;
using DeveloperIsland.Platform.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Storage.Pickers;

namespace DeveloperIsland.UI.Settings;

/// <summary>Settings, Position: Auto-hide in apps (the generalized browser notch).</summary>
public sealed partial class SettingsWindow
{
    private readonly Dictionary<string, ImageSource?> _appIcons = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string?> _appPaths = new(StringComparer.Ordinal);

    private void BuildAutoHideRows(AppSettings s)
    {
        AutoHideRows.Children.Clear();
        foreach (var app in s.AutoHideApps)
        {
            AutoHideRows.Children.Add(AppRow(app, s.SmartHideEnabled));
            AutoHideRows.Children.Add(Divider());
        }
    }

    private Grid AppRow(AutoHideApp app, bool featureOn)
    {
        var row = new Grid { Style = Res("SettingsRowStyle"), ColumnSpacing = 12 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var icon = new Image { Width = 24, Height = 24, VerticalAlignment = VerticalAlignment.Center };
        var placeholder = new FontIcon { Glyph = "", FontSize = 16, Foreground = ThemeBrush("SettingsTextSecondaryBrush") };
        var iconHost = new Grid { Width = 28, Height = 28, VerticalAlignment = VerticalAlignment.Center };
        iconHost.Children.Add(placeholder);
        iconHost.Children.Add(icon);
        row.Children.Add(iconHost);
        _ = ShowIconAsync(app, icon, placeholder);

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = app.Name, Style = Res("SettingsRowTitleStyle") });
        text.Children.Add(new TextBlock { Text = app.Process + ".exe", Style = Res("SettingsRowDescriptionStyle") });
        Grid.SetColumn(text, 1);
        row.Children.Add(text);

        if (!AutoHideRules.IsBuiltIn(app.Process))
        {
            var remove = new Button { Content = "Remove", VerticalAlignment = VerticalAlignment.Center };
            AutomationProperties.SetName(remove, $"Remove {app.Name}");
            remove.Click += (_, _) => _store.Update(x => x.AutoHideApps.RemoveAll(a => a.Process == app.Process));
            Grid.SetColumn(remove, 2);
            row.Children.Add(remove);
        }

        var toggle = new ToggleSwitch { Style = Res("SettingsToggleStyle"), IsOn = app.Enabled, IsEnabled = featureOn, Tag = "App:" + app.Process };
        AutomationProperties.SetName(toggle, app.Name);
        toggle.Toggled += OnToggle;
        Grid.SetColumn(toggle, 3);
        row.Children.Add(toggle);
        return row;
    }

    private async Task ShowIconAsync(AutoHideApp app, Image target, UIElement placeholder)
    {
        if (!_appPaths.TryGetValue(app.Process, out var path))
        {
            path = app.Path ?? await Task.Run(() => AppCatalog.ResolvePath(app.Process));
            _appPaths[app.Process] = path;
        }

        if (path is null)
        {
            return;
        }

        if (!_appIcons.TryGetValue(path, out var source))
        {
            source = await AppCatalog.LoadIconAsync(path);
            _appIcons[path] = source;
        }

        if (source is not null)
        {
            target.Source = source;
            placeholder.Visibility = Visibility.Collapsed;
        }
    }

    private void SetAppEnabled(AppSettings s, string process, bool on)
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
    private async void OnAddApplication(object sender, RoutedEventArgs e)
    {
        try
        {
            AddAppButton.IsEnabled = false;
            var known = _store.Current.AutoHideApps.Select(a => a.Process).ToHashSet();
            var apps = (await Task.Run(AppCatalog.RunningApps)).Where(a => !known.Contains(a.Process)).ToList();

            var list = new ListView { SelectionMode = ListViewSelectionMode.Single, MaxHeight = 360, MinWidth = 380 };
            AutomationProperties.SetName(list, "Running applications");
            foreach (var app in apps)
            {
                var item = new Grid { ColumnSpacing = 12, Padding = new Thickness(0, 6, 0, 6), Tag = app };
                item.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                item.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                var image = new Image { Width = 24, Height = 24, VerticalAlignment = VerticalAlignment.Center };
                item.Children.Add(image);
                var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                text.Children.Add(new TextBlock { Text = app.Name });
                text.Children.Add(new TextBlock { Text = app.Process + ".exe", FontSize = 12, Opacity = 0.7 });
                Grid.SetColumn(text, 1);
                item.Children.Add(text);
                AutomationProperties.SetName(item, $"{app.Name}, {app.Process}.exe");
                list.Items.Add(item);
                _ = Task.Run(() => app.Path).ContinueWith(async t => image.Source = await AppCatalog.LoadIconAsync(t.Result), TaskScheduler.FromCurrentSynchronizationContext());
            }

            var content = new StackPanel { Spacing = 8 };
            content.Children.Add(new TextBlock
            {
                Text = apps.Count == 0 ? "No other apps with a window are running. Use Browse… to pick a program file." : "The island steps aside while the app you choose is in front.",
                TextWrapping = TextWrapping.Wrap,
                Opacity = 0.8,
            });
            if (apps.Count > 0)
            {
                content.Children.Add(list);
            }

            var dialog = new ContentDialog
            {
                Title = "Add application",
                Content = content,
                PrimaryButtonText = "Add",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary,
                IsPrimaryButtonEnabled = false,
                XamlRoot = Root.XamlRoot,
                RequestedTheme = Root.ActualTheme,
            };
            list.SelectionChanged += (_, _) => dialog.IsPrimaryButtonEnabled = list.SelectedItem is not null;
            list.DoubleTapped += (_, _) =>
            {
                if (list.SelectedItem is Grid { Tag: AppEntry picked })
                {
                    AddApp(picked);
                    dialog.Hide();
                }
            };

            if (await dialog.ShowAsync() == ContentDialogResult.Primary && list.SelectedItem is Grid { Tag: AppEntry chosen })
            {
                AddApp(chosen);
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

    /// <summary>Browse executable: pick any .exe.</summary>
    private async void OnBrowseApplication(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.ComputerFolder };
            picker.FileTypeFilter.Add(".exe");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
            var file = await picker.PickSingleFileAsync();
            if (file is not null)
            {
                AddApp(AppCatalog.FromExecutable(file.Path));
            }
        }
        catch (Exception ex)
        {
            Log.Warn("settings", "Executable could not be added", new { error = ex.GetType().Name });
        }
    }
}
