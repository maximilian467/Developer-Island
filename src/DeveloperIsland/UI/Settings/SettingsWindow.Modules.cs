using DeveloperIsland.Core.Calendar;
using DeveloperIsland.Core.Diagnostics;
using DeveloperIsland.Core.Git;
using DeveloperIsland.Core.Modules;
using DeveloperIsland.Core.Plan;
using DeveloperIsland.Core.Settings;
using DeveloperIsland.UI.Components;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Storage.Pickers;
using Windows.System;
using Rectangle = Microsoft.UI.Xaml.Shapes.Rectangle;

namespace DeveloperIsland.UI.Settings;

/// <summary>Settings, Modules section: order and switches for every module, Git repositories, calendars.</summary>
public sealed partial class SettingsWindow
{
    private static string Describe(ModuleId module) => module switch
    {
        ModuleId.Claude => "Token usage and API equivalent from local Claude Code logs.",
        ModuleId.Codex => "Token usage, rate limits and API equivalent from local Codex logs.",
        ModuleId.Music => "What's playing in Spotify, Apple Music, browsers and other media apps.",
        ModuleId.Git => "Branch, changes and last commit of your current project. Local only.",
        ModuleId.GitHub => "Notifications, pull requests and CI through the GitHub CLI. Read-only.",
        ModuleId.Focus => "A focus timer with 25, 50 and 90 minute presets.",
        ModuleId.Calendar => "Your next event from a calendar link or an .ics file.",
        ModuleId.Tasks => "A small to-do list, stored on this PC.",
        ModuleId.System => "CPU, memory and battery. Speaks up only when something needs attention.",
        _ => string.Empty,
    };

    private void BuildModuleRows(AppSettings s)
    {
        ModuleRows.Children.Clear();
        var order = s.Order;
        for (var i = 0; i < order.Count; i++)
        {
            var module = order[i];
            var name = ModuleCatalog.DisplayName(module);
            if (i > 0)
            {
                ModuleRows.Children.Add(Divider());
            }

            var row = new Grid { Style = Res("SettingsRowStyle"), ColumnSpacing = 12 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var icon = new Border
            {
                Width = 32,
                Height = 32,
                CornerRadius = new CornerRadius(8),
                Background = ThemeBrush("SettingsSegmentBrush"),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new ModuleIcon { Module = module, Foreground = ThemeBrush("SettingsTextPrimaryBrush"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
            };
            row.Children.Add(icon);

            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(new TextBlock { Text = name, Style = Res("SettingsRowTitleStyle") });
            text.Children.Add(new TextBlock { Text = Describe(module), Style = Res("SettingsRowDescriptionStyle") });
            Grid.SetColumn(text, 1);
            row.Children.Add(text);

            var moves = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
            moves.Children.Add(MoveButton(module, -1, "", $"Move {name} up", i > 0));
            moves.Children.Add(MoveButton(module, 1, "", $"Move {name} down", i < order.Count - 1));
            Grid.SetColumn(moves, 2);
            row.Children.Add(moves);

            var toggle = new ToggleSwitch
            {
                Style = Res("SettingsToggleStyle"),
                IsOn = s.IsEnabled(module),
                Tag = "Module:" + module,
            };
            AutomationProperties.SetName(toggle, name);
            toggle.Toggled += OnToggle;
            Grid.SetColumn(toggle, 3);
            row.Children.Add(toggle);

            ModuleRows.Children.Add(row);
        }
    }

    private Button MoveButton(ModuleId module, int delta, string glyph, string name, bool enabled)
    {
        var button = new Button
        {
            Content = new FontIcon { Glyph = glyph, FontSize = 12 },
            Width = 32,
            Height = 32,
            Padding = new Thickness(0),
            Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
            IsEnabled = enabled,
            Opacity = enabled ? 1 : 0, // the ends: keep the layout, show nothing
        };
        AutomationProperties.SetName(button, name);
        ToolTipService.SetToolTip(button, delta < 0 ? "Move up" : "Move down");
        button.Click += (_, _) =>
        {
            _store.Update(s => s.ModuleOrder = ModuleCatalog.Move(s.Order, module, delta).Select(m => m.ToString()).ToList());
            Log.Info("settings", "Module moved", new { module = module.ToString(), delta });
        };
        return button;
    }

    // Claude plan usage --------------------------------------------------------------------------------

    /// <summary>Shows whether Claude Code hands plan usage to Developer Island (its status line).</summary>
    private void UpdatePlanConnection()
    {
        var state = _isDemo ? PlanConnection.NotConnected : ClaudeStatusLineSetup.Inspect(ClaudeStatusLineSetup.SettingsPath());
        (PlanStatusTitle.Text, PlanStatusText.Text) = (_isDemo, state) switch
        {
            (true, _) => ("Not available in demo mode", "Demo mode never changes Claude Code's settings."),
            (_, PlanConnection.Connected) => ("Connected", "Plan usage updates with every Claude Code reply. Shown for Claude subscriptions only; API keys have no plan usage."),
            (_, PlanConnection.OtherStatusLine) => ("Your own status line is in use", "Claude Code already runs a status line, which Developer Island leaves alone. To combine both, pipe its input to: " + ClaudeStatusLineSetup.CommandFor(Environment.ProcessPath ?? "DeveloperIsland.exe")),
            (_, PlanConnection.Unreadable) => ("Claude Code settings unreadable", "Its settings.json could not be read, so nothing was changed."),
            _ => ("Not connected", "Shows your current and weekly Claude plan usage. Developer Island becomes Claude Code's status line, which Claude Code uses to hand over these two percentages. Nothing else is read or sent."),
        };
        PlanConnectButton.Visibility = state == PlanConnection.Connected ? Visibility.Collapsed : Visibility.Visible;
        PlanConnectButton.IsEnabled = !_isDemo && state == PlanConnection.NotConnected && Environment.ProcessPath is not null;
        PlanDisconnectButton.Visibility = state == PlanConnection.Connected ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnConnectPlan(object sender, RoutedEventArgs e)
    {
        if (_isDemo || Environment.ProcessPath is not { } exe)
        {
            return;
        }

        try
        {
            var result = ClaudeStatusLineSetup.Connect(ClaudeStatusLineSetup.SettingsPath(), exe);
            Log.Info("settings", "Claude plan usage connect", new { result = result.ToString() });
        }
        catch (Exception ex)
        {
            Log.Warn("settings", "Claude plan usage could not be connected", new { error = ex.GetType().Name });
        }

        UpdatePlanConnection();
    }

    private void OnDisconnectPlan(object sender, RoutedEventArgs e)
    {
        try
        {
            var result = ClaudeStatusLineSetup.Disconnect(ClaudeStatusLineSetup.SettingsPath());
            Log.Info("settings", "Claude plan usage disconnect", new { result = result.ToString() });
        }
        catch (Exception ex)
        {
            Log.Warn("settings", "Claude plan usage could not be disconnected", new { error = ex.GetType().Name });
        }

        UpdatePlanConnection();
    }

    // Git repositories -------------------------------------------------------------------------------

    private void BuildRepositoryRows(AppSettings s)
    {
        RepoRows.Children.Clear();
        foreach (var path in s.GitRepositories)
        {
            RepoRows.Children.Add(ListRow(Path.GetFileName(path.TrimEnd('\\', '/')), path, "Remove repository", () =>
                _store.Update(x => x.GitRepositories.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase)))));
            RepoRows.Children.Add(Divider());
        }
    }

    private async void OnAddRepository(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
            picker.FileTypeFilter.Add("*");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
            var folder = await picker.PickSingleFolderAsync();
            if (folder is null)
            {
                return;
            }

            var root = GitStatusParser.FindRepositoryRoot(folder.Path);
            if (root is null)
            {
                await ShowMessageAsync("Not a Git repository", "Choose the folder of a Git repository (it contains a .git folder), or a folder inside one.");
                return;
            }

            _store.Update(s =>
            {
                if (!s.GitRepositories.Contains(root, StringComparer.OrdinalIgnoreCase))
                {
                    s.GitRepositories.Add(root);
                }
            });
        }
        catch (Exception ex)
        {
            Log.Warn("settings", "Repository could not be added", new { error = ex.GetType().Name });
        }
    }

    // Calendars -------------------------------------------------------------------------------------

    private void BuildCalendarRows(AppSettings s)
    {
        CalendarRows.Children.Clear();
        foreach (var feed in s.CalendarFeeds)
        {
            // Links contain a private token: show the provider name, never the address.
            var detail = feed.FilePath ?? "Private link, kept in Windows Credential Manager";
            var id = feed.Id;
            CalendarRows.Children.Add(ListRow(feed.Name, detail, "Remove calendar", () =>
                _store.Update(x => CalendarFeeds.Remove(x, _secrets, id))));
            CalendarRows.Children.Add(Divider());
        }
    }

    private void OnCalendarLinkChanged(object sender, TextChangedEventArgs e) =>
        AddCalendarButton.IsEnabled = IcsCalendarSource.IsValidLocation(CalendarLinkBox.Text) && IcsCalendarSource.IsWebAddress(CalendarLinkBox.Text.Trim());

    private void OnCalendarLinkKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter && AddCalendarButton.IsEnabled)
        {
            OnAddCalendarLink(sender, e);
            e.Handled = true;
        }
    }

    private void OnAddCalendarLink(object sender, RoutedEventArgs e)
    {
        var link = CalendarLinkBox.Text.Trim();
        if (!IcsCalendarSource.IsValidLocation(link))
        {
            return;
        }

        AddCalendar(link);
        CalendarLinkBox.Text = string.Empty;
    }

    private async void OnAddCalendarFile(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
            picker.FileTypeFilter.Add(".ics");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
            var file = await picker.PickSingleFileAsync();
            if (file is not null)
            {
                AddCalendar(file.Path);
            }
        }
        catch (Exception ex)
        {
            Log.Warn("settings", "Calendar file could not be added", new { error = ex.GetType().Name });
        }
    }

    private void AddCalendar(string location)
    {
        _store.Update(s =>
        {
            var duplicate = CalendarFeeds.Resolve(s.CalendarFeeds, _secrets).Contains(location, StringComparer.Ordinal);
            if (!duplicate)
            {
                CalendarFeeds.Add(s, _secrets, location);
            }

            s.CalendarEnabled = true;
        });
        Log.Info("settings", "Calendar added", new { source = IcsCalendarSource.DisplayName(location) });
    }

    // Shared -----------------------------------------------------------------------------------------

    private Grid ListRow(string title, string detail, string removeName, Action remove)
    {
        var row = new Grid { Style = Res("SettingsRowStyle"), ColumnSpacing = 12 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = title, Style = Res("SettingsRowTitleStyle") });
        text.Children.Add(new TextBlock { Text = detail, Style = Res("SettingsRowDescriptionStyle"), TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap });
        row.Children.Add(text);
        var button = new Button { Content = "Remove", VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(button, $"{removeName} {title}");
        button.Click += (_, _) => remove();
        Grid.SetColumn(button, 1);
        row.Children.Add(button);
        return row;
    }

    private Rectangle Divider() => new() { Style = Res("SettingsDividerStyle") };

    /// <summary>Settings styles live in this window's resources, not the application's.</summary>
    private Style Res(string key) => (Style)(Root.Resources.TryGetValue(key, out var value) ? value : Application.Current.Resources[key]);

    private async Task ShowMessageAsync(string title, string message)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = message,
            CloseButtonText = "OK",
            XamlRoot = Root.XamlRoot,
            RequestedTheme = Root.ActualTheme,
        };
        await dialog.ShowAsync();
    }
}
