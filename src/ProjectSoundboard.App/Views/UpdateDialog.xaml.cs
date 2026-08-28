using System.Windows;
using ProjectSoundboard.App.Services;

namespace ProjectSoundboard.App.Views;

public partial class UpdateDialog : Window
{
    private readonly UpdateService _updates;
    private readonly UpdateInfo _update;
    private CancellationTokenSource? _cts;

    public UpdateDialog(UpdateService updates, UpdateInfo update)
    {
        InitializeComponent();

        _updates = updates;
        _update = update;

        TitleText.Text = update.Title;

        var size = update.SizeBytes > 0 ? $"  ·  {update.SizeBytes / 1024d / 1024d:0.#} MB" : string.Empty;
        SubtitleText.Text =
            $"You are running {UpdateService.CurrentVersion.ToString(3)}, and " +
            $"{update.Version.ToString(3)} is available{size}.";

        NotesText.Text = string.IsNullOrWhiteSpace(update.Notes)
            ? "No release notes were provided."
            : update.Notes.Trim();

        if (!UpdateService.CanApplyInPlace())
        {
            // Installed somewhere we cannot write, so the in-place swap would fail.
            UpdateButton.IsEnabled = false;
            ShowError("Project Soundboard is installed in a folder this account cannot write to, " +
                      "so it cannot update itself. Download the new version from the release page instead.");
        }
    }

    private async void OnUpdate(object sender, RoutedEventArgs e)
    {
        _cts = new CancellationTokenSource();

        UpdateButton.IsEnabled = false;
        SkipButton.IsEnabled = false;
        LaterButton.Content = "Cancel";
        ProgressPanel.Visibility = Visibility.Visible;
        ErrorText.Visibility = Visibility.Collapsed;
        ProgressText.Text = "Downloading…";

        var progress = new Progress<double>(fraction =>
        {
            DownloadProgress.Value = fraction;
            ProgressText.Text = $"Downloading… {fraction:P0}";
        });

        try
        {
            var staged = await _updates.DownloadAsync(_update, progress, _cts.Token);

            if (staged is null)
            {
                ShowError(_updates.LastError ?? "The update could not be downloaded.");
                ResetButtons();
                return;
            }

            ProgressText.Text = "Installing…";

            if (!_updates.ApplyAndRestart(staged))
            {
                ShowError(_updates.LastError ?? "The update could not be applied.");
                ResetButtons();
                return;
            }

            // The swap script is waiting for this process to exit before it copies anything.
            this.Answer(true);
            Application.Current.Shutdown();
        }
        catch (OperationCanceledException)
        {
            ProgressPanel.Visibility = Visibility.Collapsed;
            ResetButtons();
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
            ResetButtons();
        }
    }

    private void ResetButtons()
    {
        UpdateButton.IsEnabled = UpdateService.CanApplyInPlace();
        SkipButton.IsEnabled = true;
        LaterButton.Content = "Later";
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }

    private void OnSkip(object sender, RoutedEventArgs e)
    {
        _updates.SkipVersion(_update.Version);
        this.Answer(false);
    }

    private void OnLater(object sender, RoutedEventArgs e)
    {
        // Mid-download this button reads "Cancel" and only stops the download.
        if (_cts is { IsCancellationRequested: false } && ProgressPanel.IsVisible)
        {
            _cts.Cancel();
            return;
        }

        // Otherwise leave them alone about this version for a few hours, rather than
        // asking again the next time they open the app.
        _updates.SnoozeVersion(_update.Version);
        this.Answer(false);
    }

    private void OnOpenReleasePage(object sender, RoutedEventArgs e) =>
        UpdateService.OpenReleasesPage(_update.ReleasePageUrl);
}
