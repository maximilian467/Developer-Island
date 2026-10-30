using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using DeveloperIsland.Core.Calendar;
using DeveloperIsland.Core.Diagnostics;
using DeveloperIsland.Core.Git;
using DeveloperIsland.Core.Modules;
using DeveloperIsland.Core.Plan;
using DeveloperIsland.Core.Settings;
using DeveloperIsland.UI.Components;

namespace DeveloperIsland.UI.Settings;

/// <summary>Settings, Modules section: order and switches for every module, plan usage, Git repositories, calendars.</summary>
public sealed partial class SettingsWindow
{
    private static string Describe(ModuleId module) => module switch
    {
        ModuleId.Claude => "Token usage and API equivalent from local Claude Code logs.",
        ModuleId.Codex => "Token usage, rate limits and API equivalent from local Codex logs.",
        ModuleId.Music => "What's playing in Spotify, browsers and other media players (MPRIS).",
        ModuleId.Git => "Branch, changes and last commit of your current project. Local only.",
        ModuleId.GitHub => "Notifications, pull requests and CI through the GitHub CLI. Read-only.",
        ModuleId.Focus => "A focus timer with 25, 50 and 90 minute presets.",
        ModuleId.Calendar => "Your next event from a calendar link or an .ics file.",
        ModuleId.Tasks => "A small to-do list, stored on this PC.",
        ModuleId.System => "CPU, memory, GPU, battery and sensors. Speaks up only when something needs attention.",
        ModuleId.Awake => "Keeps this PC awake for a set time or until you end it, also with the lid closed.",
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

            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto"), ColumnSpacing = 12 };
            var icon = new Border
            {
                Width = 32,
                Height = 32,
                CornerRadius = new CornerRadius(8),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new ModuleIcon { Module = module, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
            };
            icon.Bind(Border.BackgroundProperty, this.GetResourceObservable("SettingsSegmentBrush"));
            icon.Child!.Bind(ModuleIcon.ForegroundProperty, this.GetResourceObservable("SettingsTextPrimaryBrush"));
            row.Children.Add(icon);

            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(new TextBlock { Text = name, Classes = { "sRowTitle" } });
            text.Children.Add(new TextBlock { Text = Describe(module), Classes = { "sRowText" } });
            Grid.SetColumn(text, 1);
            row.Children.Add(text);

            var moves = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
            moves.Children.Add(MoveButton(module, -1, "", "Move up", i > 0));
            moves.Children.Add(MoveButton(module, 1, "", "Move down", i < order.Count - 1));
            Grid.SetColumn(moves, 2);
            row.Children.Add(moves);

            var toggle = new ToggleSwitch { IsChecked = s.IsEnabled(module), Tag = "Module:" + module, Classes = { "sToggle" } };
            toggle.IsCheckedChanged += OnToggle;
            Grid.SetColumn(toggle, 3);
            row.Children.Add(toggle);

            ModuleRows.Children.Add(Row(row));
        }
    }

    private Button MoveButton(ModuleId module, int delta, string glyph, string tip, bool enabled)
    {
        var button = new Button
        {
            Content = new GlyphIcon { Glyph = glyph, Size = 14 },
            Width = 32,
            Height = 32,
            Padding = new Thickness(0),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            IsEnabled = enabled,
            Opacity = enabled ? 1 : 0, // the ends: keep the layout, show nothing
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        ToolTip.SetTip(button, tip);
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
        var usage = _isDemo ? null : new PlanUsageStore(AppPaths.ClaudePlan).Load();
        var data = PlanStatus.StateOf(state, usage, DateTimeOffset.UtcNow);
        var lastUpdate = usage is null ? string.Empty : Core.Formatting.DisplayFormat.Ago(DateTimeOffset.UtcNow - usage.MeasuredAt);
        var exe = Platform.Startup.AutostartService.LauncherPath;

        (PlanStatusTitle.Text, PlanStatusText.Text) = (_isDemo, data) switch
        {
            (true, _) => ("Not available in demo mode", "Demo mode never changes Claude Code's settings."),
            (_, PlanDataState.Receiving) => ("Connected, receiving plan usage", $"Last update {lastUpdate}. It refreshes with every reply in a Claude Code terminal session."),
            (_, PlanDataState.Stale) => ("Connected, no recent update", $"Last update {lastUpdate}. Plan usage refreshes with your next reply in a Claude Code terminal session."),
            (_, PlanDataState.WaitingForData) => ("Connected, plan usage unavailable so far", "Claude Code hands over plan usage only in terminal sessions, after a reply (its status line is part of the terminal view; the VS Code extension does not run it). Claude subscriptions only."),
            (_, PlanDataState.OtherStatusLine) => ("Your own status line is in use", "Claude Code already runs a status line, which Developer Island leaves alone. To combine both, pipe its input to: " + ClaudeStatusLineSetup.CommandFor(exe)),
            (_, PlanDataState.Unreadable) => ("Claude Code settings unreadable", "Its settings.json could not be read, so nothing was changed."),
            _ => ("Not connected", "Shows your current and weekly Claude plan usage. Developer Island becomes Claude Code's status line, which Claude Code uses to hand over these two percentages. Nothing else is read or sent."),
        };
        PlanConnectButton.IsVisible = state != PlanConnection.Connected;
        PlanConnectButton.IsEnabled = !_isDemo && state == PlanConnection.NotConnected;
        PlanDisconnectButton.IsVisible = state == PlanConnection.Connected;
    }

    private void OnConnectPlan(object? sender, RoutedEventArgs e)
    {
        if (_isDemo)
        {
            return;
        }

        try
        {
            var result = ClaudeStatusLineSetup.Connect(ClaudeStatusLineSetup.SettingsPath(), Platform.Startup.AutostartService.LauncherPath);
            Log.Info("settings", "Claude plan usage connect", new { result = result.ToString() });
        }
        catch (Exception ex)
        {
            Log.Warn("settings", "Claude plan usage could not be connected", new { error = ex.GetType().Name });
        }

        UpdatePlanConnection();
    }

    private void OnDisconnectPlan(object? sender, RoutedEventArgs e)
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
            RepoRows.Children.Add(ListRow(Core.Formatting.PathNames.LastSegment(path), path, () =>
                _store.Update(x => x.GitRepositories.RemoveAll(p => string.Equals(p, path, StringComparison.Ordinal)))));
            RepoRows.Children.Add(Divider());
        }
    }

    private async void OnAddRepository(object? sender, RoutedEventArgs e)
    {
        try
        {
            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Choose a Git repository", AllowMultiple = false });
            if (folders.Count == 0 || folders[0].TryGetLocalPath() is not { } path)
            {
                return;
            }

            var root = GitStatusParser.FindRepositoryRoot(path);
            if (root is null)
            {
                await Dialog.ShowMessageAsync(this, "Not a Git repository", "Choose the folder of a Git repository (it contains a .git folder), or a folder inside one.");
                return;
            }

            _store.Update(s =>
            {
                if (!s.GitRepositories.Contains(root, StringComparer.Ordinal))
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
            var detail = feed.FilePath ?? "Private link, kept in your keyring";
            var id = feed.Id;
            CalendarRows.Children.Add(ListRow(feed.Name, detail, () => _store.Update(x => CalendarFeeds.Remove(x, _secrets, id))));
            CalendarRows.Children.Add(Divider());
        }
    }

    private void OnCalendarLinkChanged(object? sender, TextChangedEventArgs e)
    {
        var text = CalendarLinkBox.Text ?? string.Empty;
        AddCalendarButton.IsEnabled = IcsCalendarSource.IsValidLocation(text) && IcsCalendarSource.IsWebAddress(text.Trim());
    }

    private void OnCalendarLinkKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is Key.Enter or Key.Return && AddCalendarButton.IsEnabled)
        {
            OnAddCalendarLink(sender, e);
            e.Handled = true;
        }
    }

    private void OnAddCalendarLink(object? sender, RoutedEventArgs e)
    {
        var link = (CalendarLinkBox.Text ?? string.Empty).Trim();
        if (!IcsCalendarSource.IsValidLocation(link))
        {
            return;
        }

        AddCalendar(link);
        CalendarLinkBox.Text = string.Empty;
    }

    private async void OnAddCalendarFile(object? sender, RoutedEventArgs e)
    {
        try
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Add a calendar file",
                AllowMultiple = false,
                FileTypeFilter = [new FilePickerFileType("Calendar (.ics)") { Patterns = ["*.ics"] }],
            });
            if (files.Count > 0 && files[0].TryGetLocalPath() is { } path)
            {
                AddCalendar(path);
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

    private Border ListRow(string title, string detail, Action remove)
    {
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 12 };
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = title, Classes = { "sRowTitle" } });
        text.Children.Add(new TextBlock { Text = detail, Classes = { "sRowText" }, TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap });
        row.Children.Add(text);
        var button = new Button { Content = "Remove", VerticalAlignment = VerticalAlignment.Center };
        button.Click += (_, _) => remove();
        Grid.SetColumn(button, 1);
        row.Children.Add(button);
        return Row(row);
    }

    private static Border Row(Control content) => new() { Classes = { "sRow" }, Child = content };

    private static Rectangle Divider() => new() { Classes = { "sDivider" } };
}

/// <summary>Small modal dialogs in the window's theme (WinUI's ContentDialog on Windows).</summary>
internal static class Dialog
{
    public static Task ShowMessageAsync(Window owner, string title, string message) =>
        ShowAsync(owner, title, new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, MaxWidth = 360 }, primary: null, close: "OK", _ => { });

    /// <returns>True when the primary button was chosen.</returns>
    public static async Task<bool> ShowAsync(Window owner, string title, Control content, string? primary, string close, Action<Button> configurePrimary)
    {
        var result = false;
        var dialog = new Window
        {
            Title = title,
            SizeToContent = SizeToContent.WidthAndHeight,
            CanResize = false,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            RequestedThemeVariant = owner.RequestedThemeVariant,
            Classes = { "settings" },
        };
        dialog.Styles.Add(new Avalonia.Markup.Xaml.Styling.StyleInclude(new Uri("avares://developer-island/"))
        {
            Source = new Uri("avares://developer-island/UI/Settings/SettingsTheme.axaml"),
        });

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        if (primary is not null)
        {
            var ok = new Button { Content = primary, MinWidth = 88, Classes = { "accent" }, IsDefault = true };
            ok.Click += (_, _) =>
            {
                result = true;
                dialog.Close();
            };
            configurePrimary(ok);
            buttons.Children.Add(ok);
        }

        var cancel = new Button { Content = close, MinWidth = 88, IsCancel = true, IsDefault = primary is null };
        cancel.Click += (_, _) => dialog.Close();
        buttons.Children.Add(cancel);

        dialog.Content = new StackPanel
        {
            Margin = new Thickness(24),
            Spacing = 16,
            Children =
            {
                new TextBlock { Text = title, FontSize = 18, FontWeight = FontWeight.SemiBold },
                content,
                buttons,
            },
        };
        await dialog.ShowDialog(owner);
        return result;
    }
}
