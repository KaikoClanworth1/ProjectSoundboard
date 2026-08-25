using System.IO;
using System.Windows;
using System.Windows.Controls;
using ProjectSoundboard.App.Services;
using ProjectSoundboard.Core.Storage;

namespace ProjectSoundboard.App.Views;

/// <summary>
/// Paste a link, confirm the name, choose which library folder it lands in.
///
/// The title is looked up before anything is downloaded so it can be offered as the name.
/// Renaming afterwards would work too, but naming it up front is the moment somebody
/// actually knows what they want it called.
/// </summary>
public partial class YouTubeDownloadWindow : Window
{
    private readonly AppServices _services;
    private readonly YtDlpTool _tool = new();

    private CancellationTokenSource? _work;
    private VideoInfo? _found;
    private bool _nameEdited;
    private bool _busy;

    public YouTubeDownloadWindow(AppServices services, string? suggestedFolder)
    {
        InitializeComponent();

        _services = services;

        FillFolders(suggestedFolder);
        RefreshToolState();

        Loaded += (_, _) =>
        {
            OfferClipboardLink();
            UrlBox.Focus();
        };
    }

    /// <summary>The file that was downloaded, once the dialog closes with a result.</summary>
    public string? DownloadedPath { get; private set; }

    // -----------------------------------------------------------------------

    private void FillFolders(string? suggested)
    {
        var folders = _services.Settings.Settings.Library.Folders
            .Where(f => !string.IsNullOrWhiteSpace(f.Path))
            .Select(f => f.Path)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        // The folder being browsed may be a subfolder of a watched one rather than the
        // watched folder itself, and that is where "this library" means to somebody looking
        // at it. Offered first, and it is what the button defaults to.
        if (!string.IsNullOrWhiteSpace(suggested) &&
            !folders.Contains(suggested, StringComparer.OrdinalIgnoreCase))
        {
            folders.Insert(0, suggested);
        }

        foreach (var folder in folders) FolderBox.Items.Add(folder);

        FolderBox.SelectedItem = suggested is not null && folders.Contains(suggested, StringComparer.OrdinalIgnoreCase)
            ? folders.First(f => string.Equals(f, suggested, StringComparison.OrdinalIgnoreCase))
            : folders.FirstOrDefault();

        if (FolderBox.Items.Count == 0)
        {
            Problem("There are no library folders to download into. Add one in Settings first.");
            LookUpButton.IsEnabled = false;
        }
    }

    private void RefreshToolState()
    {
        var present = YtDlpTool.Locate() is not null;

        ToolMissingPanel.Visibility = present ? Visibility.Collapsed : Visibility.Visible;
        LookUpButton.IsEnabled = present && FolderBox.Items.Count > 0;

        if (present && YouTubeDownloader.FindFfmpeg() is null)
        {
            SubtitleText.Text =
                "Paste a link. ffmpeg was not found, so the audio is saved in whatever format " +
                "YouTube served rather than being converted to MP3 — the soundboard plays those too.";
        }
    }

    /// <summary>If a YouTube link is already on the clipboard, that is almost certainly the one.</summary>
    private void OfferClipboardLink()
    {
        try
        {
            if (!Clipboard.ContainsText()) return;

            var text = Clipboard.GetText().Trim();
            if (YouTubeDownloader.LooksLikeYouTube(text)) UrlBox.Text = text;
        }
        catch (Exception ex)
        {
            // The clipboard belongs to whatever is holding it open at the time.
            Log.Debug($"Could not read the clipboard: {ex.Message}");
        }
    }

    // -----------------------------------------------------------------------

    private void OnUrlChanged(object sender, TextChangedEventArgs e)
    {
        _found = null;
        FoundPanel.Visibility = Visibility.Collapsed;
        DownloadButton.IsEnabled = false;
        ProblemText.Visibility = Visibility.Collapsed;
    }

    private void OnNameChanged(object sender, TextChangedEventArgs e)
    {
        // Once it has been typed in, stop replacing it with the title of the next lookup.
        if (FoundPanel.Visibility == Visibility.Visible) _nameEdited = true;

        var stem = YouTubeDownloader.SafeFileName(NameBox.Text);

        NameHint.Text = string.Equals(stem, NameBox.Text.Trim(), StringComparison.Ordinal)
            ? string.Empty
            : $"Saved as “{stem}.mp3” — some characters are not allowed in file names.";

        DownloadButton.IsEnabled = _found is not null && !_busy && NameBox.Text.Trim().Length > 0;
    }

    private async void OnLookUp(object sender, RoutedEventArgs e) => await LookUpAsync();

    private async Task LookUpAsync()
    {
        if (_busy) return;

        var path = YtDlpTool.Locate();
        if (path is null) { RefreshToolState(); return; }

        var url = UrlBox.Text.Trim();

        if (!YouTubeDownloader.LooksLikeYouTube(url))
        {
            Problem("That does not look like a YouTube link.");
            return;
        }

        Busy(true, "Looking it up…");

        try
        {
            _work = new CancellationTokenSource();

            var downloader = new YouTubeDownloader(path);
            var info = await downloader.ProbeAsync(url, _work.Token);

            if (info is null)
            {
                Problem(downloader.LastError ?? "Could not read that link.");
                return;
            }

            _found = info;

            FoundTitle.Text = info.Title;
            FoundDetail.Text = info.Uploader is null
                ? info.DurationText
                : $"{info.Uploader}  ·  {info.DurationText}";

            FoundPanel.Visibility = Visibility.Visible;

            // The title is the obvious name, and almost always the right one.
            if (!_nameEdited || NameBox.Text.Trim().Length == 0)
            {
                NameBox.Text = YouTubeDownloader.SafeFileName(info.Title);
                _nameEdited = false;
            }

            DownloadButton.IsEnabled = NameBox.Text.Trim().Length > 0;
            NameBox.Focus();
            NameBox.SelectAll();
        }
        catch (OperationCanceledException)
        {
            // Closed while looking.
        }
        finally
        {
            Busy(false);
        }
    }

    private async void OnDownload(object sender, RoutedEventArgs e)
    {
        if (_busy || _found is null) return;

        var path = YtDlpTool.Locate();
        if (path is null) { RefreshToolState(); return; }

        if (FolderBox.SelectedItem is not string folder)
        {
            Problem("Choose a folder to download into.");
            return;
        }

        Busy(true, "Starting…");
        ProgressPanel.Visibility = Visibility.Visible;
        Progress.Value = 0;

        try
        {
            _work = new CancellationTokenSource();

            var downloader = new YouTubeDownloader(path);

            var progress = new Progress<DownloadProgress>(p =>
            {
                Progress.Value = p.Percent;
                ProgressText.Text = p.Stage == "Downloading…"
                    ? $"{p.Stage}  {p.Percent:F0}%"
                    : p.Stage;
            });

            var file = await downloader.DownloadMp3Async(
                _found.Url, folder, NameBox.Text, 192, progress, _work.Token);

            if (file is null)
            {
                Problem(downloader.LastError ?? "The download failed.");
                return;
            }

            DownloadedPath = file;
            Log.Info($"Downloaded '{Path.GetFileName(file)}' into {folder}.");

            DialogResult = true;
        }
        catch (OperationCanceledException)
        {
            Problem("Cancelled.");
        }
        catch (Exception ex)
        {
            Problem(ex.Message);
        }
        finally
        {
            Busy(false);
        }
    }

    private async void OnGetTool(object sender, RoutedEventArgs e)
    {
        if (_busy) return;

        Busy(true, "Fetching yt-dlp…");
        ProgressPanel.Visibility = Visibility.Visible;
        Progress.IsIndeterminate = true;

        try
        {
            _work = new CancellationTokenSource();

            var status = new Progress<string>(s => ProgressText.Text = s);
            var path = await _tool.FetchAsync(status, _work.Token);

            if (path is null)
            {
                Problem(_tool.LastError ?? "Could not fetch yt-dlp.");
                return;
            }

            ProblemText.Visibility = Visibility.Collapsed;
            RefreshToolState();
        }
        finally
        {
            Progress.IsIndeterminate = false;
            ProgressPanel.Visibility = Visibility.Collapsed;
            Busy(false);
        }
    }

    private void OnOpenToolPage(object sender, RoutedEventArgs e)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                YtDlpTool.ProjectPageUrl) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Debug($"Could not open the yt-dlp page: {ex.Message}");
        }
    }

    // -----------------------------------------------------------------------

    private void Busy(bool busy, string? what = null)
    {
        _busy = busy;

        UrlBox.IsEnabled = !busy;
        NameBox.IsEnabled = !busy;
        FolderBox.IsEnabled = !busy;
        LookUpButton.IsEnabled = !busy && YtDlpTool.Locate() is not null;
        GetToolButton.IsEnabled = !busy;
        DownloadButton.IsEnabled = !busy && _found is not null && NameBox.Text.Trim().Length > 0;

        if (busy)
        {
            ProblemText.Visibility = Visibility.Collapsed;

            if (what is not null)
            {
                ProgressPanel.Visibility = Visibility.Visible;
                ProgressText.Text = what;
            }
        }
    }

    private void Problem(string message)
    {
        ProblemText.Text = message;
        ProblemText.Visibility = Visibility.Visible;
        ProgressPanel.Visibility = Visibility.Collapsed;
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        // Cancel means stop what is running; the second press closes the window.
        if (_busy)
        {
            _work?.Cancel();
            return;
        }

        DialogResult = false;
    }

    protected override void OnClosed(EventArgs e)
    {
        _work?.Cancel();
        _work?.Dispose();
        base.OnClosed(e);
    }
}
