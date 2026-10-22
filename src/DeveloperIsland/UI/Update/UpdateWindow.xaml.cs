using DeveloperIsland.Core.Diagnostics;
using DeveloperIsland.Core.Settings;
using DeveloperIsland.Core.Updates;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using Windows.UI;

namespace DeveloperIsland.UI.Update;

/// <summary>What the update dialog can do; the host supplies each step.</summary>
internal sealed record UpdateDialogActions(
    Action<bool> AnswerQuestion,
    Func<ReleaseInfo, IProgress<double>, CancellationToken, Task<UpdateDownloadResult>> Download,
    Func<string, bool> RunSetup,
    Action<ReleaseInfo> Skip,
    Action<Uri> OpenNotes);

/// <summary>
/// A small window for the update flow. Either the one-time question ("Check for updates?") or a
/// release: Install now downloads and verifies it (UpdateDownloader), then hands the setup to the
/// host, which quits; Release notes opens the release page; Later closes; Skip this version remembers it.
/// </summary>
public sealed partial class UpdateWindow : Window
{
    private enum Mode
    {
        Question,
        Available,
        Downloading,
        Failed,
    }

    private readonly UpdateDialogActions _actions;
    private Mode _mode;
    private ReleaseInfo? _release;
    private ReleaseVersion _current;
    private bool _canInstall;
    private CancellationTokenSource? _download;

    internal UpdateWindow(UpdateDialogActions actions, AppTheme theme)
    {
        _actions = actions;
        InitializeComponent();
        SystemBackdrop = new MicaBackdrop();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(DragArea);
        AppWindow.SetIcon(AppPaths.Icon);
        AppWindow.Title = "Developer Island Update";
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
            presenter.IsResizable = false;
        }

        Root.RequestedTheme = theme == AppTheme.Dark ? ElementTheme.Dark : ElementTheme.Default;
        Root.ActualThemeChanged += (_, _) => ApplyCaptionColors();
        ApplyCaptionColors();
        Closed += (_, _) => _download?.Cancel();
        SizeAndCenter();
    }

    /// <summary>A download is running: the host does not replace the dialog's content.</summary>
    public bool IsBusy => _mode == Mode.Downloading;

    /// <summary>The one-time question whether the island may check for updates.</summary>
    public void ShowQuestion()
    {
        _mode = Mode.Question;
        _release = null;
        TitleText.Text = "Check for updates?";
        BodyText.Text = "Developer Island can ask GitHub once a day whether a newer version exists. The request carries the app's version "
            + "and what every web request carries, such as your IP address; nothing else. Nothing is installed without your click. "
            + "You can change this any time in Settings, General.";
        Progress.Visibility = Visibility.Collapsed;
        SetButtons(primary: "Check for updates", secondary: "Not now", notes: false, skip: false);
    }

    /// <param name="canInstall">False for a copy the setup did not install (portable, development): no in-place update.</param>
    internal void ShowRelease(ReleaseInfo release, ReleaseVersion current, bool canInstall)
    {
        _mode = Mode.Available;
        _release = release;
        _current = current;
        _canInstall = canInstall;
        TitleText.Text = $"Developer Island {release.Version} is available";
        BodyText.Text = canInstall
            ? $"You have {current}. Install now downloads the setup from GitHub, checks its signature, closes Developer Island and starts the new version. Your settings and history stay."
            : $"You have {current}. This copy was not installed with the setup, so it cannot update itself. Download the new version from the release page.";
        Progress.Visibility = Visibility.Collapsed;
        SetButtons(primary: canInstall ? "Install now" : null, secondary: "Later", notes: true, skip: true);
    }

    public void ShowAndFocus() => Activate();

    /// <summary>Development support (<c>--demo --snapshot</c>): renders the dialog offscreen. Mica does not render into a bitmap.</summary>
    public async Task SaveSnapshotAsync(string path)
    {
        AppWindow.Move(new PointInt32(-32000, -32000));
        AppWindow.Show(activateWindow: false);
        Root.Background = new SolidColorBrush(Root.ActualTheme == ElementTheme.Dark ? Color.FromArgb(255, 32, 32, 32) : Color.FromArgb(255, 243, 243, 243));
        await Task.Delay(500);
        await Components.SnapshotWriter.SaveAsync(Root, path);
    }

    /// <summary>Development support: the state while a download runs, without downloading.</summary>
    internal void ShowDownloadingForSnapshot(double progress)
    {
        EnterDownloading();
        Progress.IsIndeterminate = false;
        Progress.Value = progress;
    }

    /// <summary>Development support: a failed verification, without downloading.</summary>
    internal void ShowFailureForSnapshot(UpdateDownloadStatus status) => ShowFailure(FailureText(status), retry: false);

    private void SetButtons(string? primary, string? secondary, bool notes, bool skip)
    {
        PrimaryButton.Visibility = primary is null ? Visibility.Collapsed : Visibility.Visible;
        PrimaryButton.Content = primary;
        PrimaryButton.IsEnabled = true;
        SecondaryButton.Visibility = secondary is null ? Visibility.Collapsed : Visibility.Visible;
        SecondaryButton.Content = secondary;
        NotesButton.Visibility = notes ? Visibility.Visible : Visibility.Collapsed;
        SkipButton.Visibility = skip ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnPrimary(object sender, RoutedEventArgs e)
    {
        switch (_mode)
        {
            case Mode.Question:
                _actions.AnswerQuestion(true);
                Close();
                break;
            case Mode.Available or Mode.Failed when _release is not null && _canInstall:
                _ = InstallAsync(_release);
                break;
        }
    }

    private void OnSecondary(object sender, RoutedEventArgs e)
    {
        switch (_mode)
        {
            case Mode.Question:
                _actions.AnswerQuestion(false);
                Close();
                break;
            case Mode.Downloading:
                _download?.Cancel();
                break;
            default:
                Close();
                break;
        }
    }

    private void OnNotes(object sender, RoutedEventArgs e)
    {
        if (_release is not null)
        {
            _actions.OpenNotes(_release.NotesUrl);
        }
    }

    private void OnSkip(object sender, RoutedEventArgs e)
    {
        if (_release is not null)
        {
            _actions.Skip(_release);
            Close();
        }
    }

    private void EnterDownloading()
    {
        _mode = Mode.Downloading;
        BodyText.Text = "Downloading the setup and checking its signature…";
        Progress.Visibility = Visibility.Visible;
        Progress.IsIndeterminate = true;
        SetButtons(primary: null, secondary: "Cancel", notes: false, skip: false);
    }

    private async Task InstallAsync(ReleaseInfo release)
    {
        EnterDownloading();
        _download = new CancellationTokenSource();
        var progress = new Progress<double>(share =>
        {
            Progress.IsIndeterminate = false;
            Progress.Value = share;
        });

        UpdateDownloadResult result;
        try
        {
            result = await _actions.Download(release, progress, _download.Token);
        }
        catch (Exception ex)
        {
            Log.Error("update", "Update download failed", ex);
            result = new UpdateDownloadResult(UpdateDownloadStatus.Network);
        }

        if (result.Status == UpdateDownloadStatus.Cancelled)
        {
            ShowRelease(release, _current, _canInstall);
            return;
        }

        if (!result.IsReady)
        {
            ShowFailure(FailureText(result.Status), retry: result.Status is UpdateDownloadStatus.Network or UpdateDownloadStatus.Disk);
            return;
        }

        BodyText.Text = "Verified. Starting the setup; Developer Island restarts in a moment.";
        Progress.IsIndeterminate = true;
        SecondaryButton.Visibility = Visibility.Collapsed;
        if (!_actions.RunSetup(result.SetupPath!))
        {
            ShowFailure("The setup could not be started. Nothing was changed.", retry: true);
        }
    }

    private void ShowFailure(string text, bool retry)
    {
        _mode = Mode.Failed;
        if (_release is not null)
        {
            TitleText.Text = $"Developer Island {_release.Version} was not installed";
        }

        BodyText.Text = text;
        Progress.Visibility = Visibility.Collapsed;
        SetButtons(primary: retry ? "Try again" : null, secondary: "Close", notes: true, skip: false);
    }

    private static string FailureText(UpdateDownloadStatus status) => status switch
    {
        UpdateDownloadStatus.NoSigningKey => "This build of Developer Island has no update signing key, so it cannot verify updates. Download the new version from the release page.",
        UpdateDownloadStatus.NotSigned => "This release has no signature, so it was not installed. Download it from the release page if you want it.",
        UpdateDownloadStatus.BadSignature => "The release's signature does not match Developer Island's key. Nothing was installed.",
        UpdateDownloadStatus.NoChecksum or UpdateDownloadStatus.HashMismatch => "The downloaded setup does not match its signed checksum. It was deleted; nothing was installed.",
        UpdateDownloadStatus.Disk => "The update could not be saved. Check the free disk space and try again.",
        _ => "GitHub could not be reached. Nothing was installed. Try again later.",
    };

    private void SizeAndCenter()
    {
        var scale = Platform.Native.NativeMethods.GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)) / 96.0;
        var size = new SizeInt32((int)(500 * scale), (int)(270 * scale));
        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        AppWindow.MoveAndResize(new RectInt32(area.X + (area.Width - size.Width) / 2, area.Y + (area.Height - size.Height) / 2, size.Width, size.Height));
    }

    private void ApplyCaptionColors()
    {
        var dark = Root.ActualTheme == ElementTheme.Dark;
        var bar = AppWindow.TitleBar;
        bar.ButtonBackgroundColor = Colors.Transparent;
        bar.ButtonInactiveBackgroundColor = Colors.Transparent;
        bar.ButtonForegroundColor = dark ? Color.FromArgb(255, 245, 245, 247) : Color.FromArgb(255, 29, 29, 31);
        bar.ButtonInactiveForegroundColor = Color.FromArgb(255, 122, 122, 122);
        bar.ButtonHoverBackgroundColor = dark ? Color.FromArgb(0x1A, 255, 255, 255) : Color.FromArgb(0x14, 0, 0, 0);
        bar.ButtonHoverForegroundColor = bar.ButtonForegroundColor;
    }
}
