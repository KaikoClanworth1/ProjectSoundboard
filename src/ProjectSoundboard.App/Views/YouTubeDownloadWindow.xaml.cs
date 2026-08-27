using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using ProjectSoundboard.App.Services;
using ProjectSoundboard.Core.Storage;

namespace ProjectSoundboard.App.Views;

/// <summary>One track of a playlist, as the list shows it: included or not, and named.</summary>
public sealed class PlaylistTrack : INotifyPropertyChanged
{
    private bool _include = true;
    private string _name = string.Empty;

    public required VideoInfo Video { get; init; }
    public required int Number { get; init; }

    public bool Include
    {
        get => _include;
        set { if (_include != value) { _include = value; Changed(); } }
    }

    public string Name
    {
        get => _name;
        set { if (_name != value) { _name = value; Changed(); } }
    }

    public string DurationText => Video.Duration > TimeSpan.Zero ? Video.DurationText : "—";

    /// <summary>The sound already in the library that this track looks like, if there is one.</summary>
    public AlreadyHave? Have { get; init; }

    public bool IsDuplicate => Have is not null;

    public string HaveText => Have is null ? string.Empty : $"already have “{Have.Name}”";

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Changed([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>
/// Paste a link, confirm the name, choose which library folder it lands in.
///
/// The title is looked up before anything is downloaded so it can be offered as the name.
/// Renaming afterwards would work too, but naming it up front is the moment somebody
/// actually knows what they want it called — and for a playlist, naming twenty tracks
/// afterwards is a chore nobody would do.
/// </summary>
public partial class YouTubeDownloadWindow : Window
{
    private readonly AppServices _services;
    private readonly YtDlpTool _tool = new();
    private readonly ObservableCollection<PlaylistTrack> _tracks = new();
    private readonly List<string> _downloaded = new();

    private CancellationTokenSource? _work;
    private VideoInfo? _found;
    private PlaylistInfo? _playlist;
    private bool _nameEdited;
    private bool _busy;
    private bool _closed;

    public YouTubeDownloadWindow(AppServices services, string? suggestedFolder)
    {
        InitializeComponent();

        _services = services;
        TrackList.ItemsSource = _tracks;

        FillFolders(suggestedFolder);
        RefreshToolState();

        Loaded += (_, _) =>
        {
            OfferClipboardLink();
            UrlBox.Focus();
        };
    }

    /// <summary>What was downloaded, once the dialog closes with a result.</summary>
    public IReadOnlyList<string> DownloadedPaths => _downloaded;

    /// <summary>Tracks that could not be fetched, so the caller can say so.</summary>
    public int FailedCount { get; private set; }

    // -----------------------------------------------------------------------

    private void FillFolders(string? suggested)
    {
        var folders = _services.Settings.Settings.Library.Folders
            .Where(f => !string.IsNullOrWhiteSpace(f.Path))
            .Select(f => f.Path)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        // The folder being browsed may be a group's folder rather than a watched root, and
        // that is where "this library" means to somebody looking at it. Offered first, and
        // it is what the dialog defaults to.
        if (!string.IsNullOrWhiteSpace(suggested) &&
            !folders.Contains(suggested, StringComparer.OrdinalIgnoreCase))
        {
            folders.Insert(0, suggested);
        }

        foreach (var folder in folders) FolderBox.Items.Add(folder);

        FolderBox.SelectedItem = suggested is not null &&
                                 folders.Contains(suggested, StringComparer.OrdinalIgnoreCase)
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
        ClearResults();

        // A link that plainly carries a playlist offers the tick already on, rather than
        // making somebody look it up, get one video, and work out why.
        //
        // Not for a Mix, though. Copying a link while the radio plays is how most links get
        // copied, and it almost always means this song rather than the next four hundred
        // YouTube has lined up. The tick still works on it for anybody who does want them.
        if (!_busy &&
            YouTubeDownloader.LooksLikePlaylist(UrlBox.Text) &&
            !YouTubeDownloader.IsMixList(UrlBox.Text) &&
            PlaylistCheck.IsChecked != true)
        {
            PlaylistCheck.IsChecked = true;
        }
    }

    private void OnPlaylistToggled(object sender, RoutedEventArgs e)
    {
        // Ticking it by hand means the last lookup no longer applies. The tick moving on its
        // own during a lookup — the fallback turning it off after a Mix could not be read —
        // means the opposite, so it must not throw away what the fallback just found.
        if (_busy) return;

        ClearResults();
    }

    private void ClearResults()
    {
        _found = null;
        _playlist = null;
        _tracks.Clear();

        FoundPanel.Visibility = Visibility.Collapsed;
        PlaylistPanel.Visibility = Visibility.Collapsed;
        SingleNamePanel.Visibility = Visibility.Visible;
        ProblemText.Visibility = Visibility.Collapsed;
        DownloadButton.IsEnabled = false;
        DownloadButton.Content = "Download";

        // Nothing looked up yet, so there is no title to tidy or to go back to.
        AutoNameButton.IsEnabled = false;
        ResetNameButton.IsEnabled = false;
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

    /// <summary>
    /// Tidy every name at once. Offered rather than done automatically: the titles are what
    /// the uploader called them, and replacing them without being asked is presumptuous when
    /// the rules can only ever be mostly right. Reset is next to it for when they are not.
    /// </summary>
    private void OnAutoName(object sender, RoutedEventArgs e)
    {
        var changed = Rename(track => TrackNaming.Clean(track.Video.Title));

        PlaylistHint.Text = changed == 0
            ? $"Nothing to tidy — those {_tracks.Count} names are already as short as they get."
            : $"Renamed {changed} of {_tracks.Count}. Change any of them by hand, or Reset to put them back.";
    }

    private void OnResetNames(object sender, RoutedEventArgs e)
    {
        var changed = Rename(track => track.Video.Title);

        PlaylistHint.Text = changed == 0
            ? "Those are the titles as they came."
            : $"Put {changed} back to the title YouTube gave it.";
    }

    private int Rename(Func<PlaylistTrack, string> naming)
    {
        var changed = 0;

        foreach (var track in _tracks)
        {
            var name = YouTubeDownloader.SafeFileName(naming(track));
            if (string.Equals(name, track.Name, StringComparison.Ordinal)) continue;

            track.Name = name;
            changed++;
        }

        return changed;
    }

    /// <summary>The same tidy-up as the playlist button, for the one video.</summary>
    private void OnAutoNameSingle(object sender, RoutedEventArgs e)
    {
        if (_found is null) return;

        NameBox.Text = YouTubeDownloader.SafeFileName(TrackNaming.Clean(_found.Title));
        NameBox.Focus();
        NameBox.SelectAll();
    }

    private void OnResetNameSingle(object sender, RoutedEventArgs e)
    {
        if (_found is null) return;

        NameBox.Text = YouTubeDownloader.SafeFileName(_found.Title);
        NameBox.Focus();
        NameBox.SelectAll();
    }

    private void OnSelectAll(object sender, RoutedEventArgs e) => SetAll(true);

    private void OnSelectNone(object sender, RoutedEventArgs e) => SetAll(false);

    private void SetAll(bool include)
    {
        foreach (var track in _tracks) track.Include = include;
        RefreshPlaylistCount();
    }

    private void RefreshPlaylistCount()
    {
        var chosen = _tracks.Count(t => t.Include);
        var known = _tracks.Count(t => t.IsDuplicate);

        PlaylistHint.Text = chosen == _tracks.Count
            ? $"{_tracks.Count} track{(_tracks.Count == 1 ? "" : "s")}. Rename any of them before downloading."
            : $"{chosen} of {_tracks.Count} chosen.";

        // Said once, up here, as well as against each track: with a hundred rows the ones
        // that were left unticked are easy to scroll straight past.
        if (known > 0)
        {
            PlaylistHint.Text +=
                $" {known} {(known == 1 ? "is" : "are")} already in your library and left unticked — " +
                "tick any of them to download it anyway.";
        }

        DownloadButton.Content = chosen > 0 ? $"Download {chosen}" : "Download";
        DownloadButton.IsEnabled = chosen > 0 && !_busy;
    }

    // -----------------------------------------------------------------------

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

        var wantsPlaylist = PlaylistCheck.IsChecked == true;
        Busy(true, wantsPlaylist ? "Reading the playlist…" : "Looking it up…");

        try
        {
            _work = new CancellationTokenSource();
            var downloader = new YouTubeDownloader(path);

            if (wantsPlaylist)
            {
                await LookUpPlaylistAsync(downloader, url);
            }
            else
            {
                await LookUpSingleAsync(downloader, url);
            }
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

    private async Task LookUpSingleAsync(YouTubeDownloader downloader, string url)
    {
        var info = await downloader.ProbeAsync(url, _work!.Token);

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
        SingleNamePanel.Visibility = Visibility.Visible;
        PlaylistPanel.Visibility = Visibility.Collapsed;

        // A warning, not a refusal: the Download button stays live, because wanting a second
        // copy is a perfectly good reason to be here.
        var already = new DuplicateIndex(_services.Library.Sounds).Find(info.Title, info.Duration);

        if (already is not null)
        {
            Notice($"“{already.Name}” is already in your library. Download it again if you want to.");
        }

        // The title is the obvious name, and almost always the right one.
        if (!_nameEdited || NameBox.Text.Trim().Length == 0)
        {
            NameBox.Text = YouTubeDownloader.SafeFileName(info.Title);
            _nameEdited = false;
        }

        DownloadButton.IsEnabled = NameBox.Text.Trim().Length > 0;

        AutoNameButton.IsEnabled = true;
        ResetNameButton.IsEnabled = true;

        NameBox.Focus();
        NameBox.SelectAll();
    }

    private async Task LookUpPlaylistAsync(YouTubeDownloader downloader, string url)
    {
        var list = await downloader.ProbePlaylistAsync(url, _work!.Token);

        if (list is null)
        {
            var why = downloader.LastError ?? "Could not read that playlist.";

            // A list that cannot be read is not a reason to leave somebody staring at a
            // dialog that will not do anything. If the link still names a video — and a
            // link copied out of a playlist always does — fall back to offering that one,
            // ready to download, and say why the rest of them are not there.
            if (!string.IsNullOrEmpty(YouTubeDownloader.VideoIdOf(url)))
            {
                await LookUpSingleAsync(downloader, YouTubeDownloader.Normalise(url));

                if (_found is not null)
                {
                    PlaylistCheck.IsChecked = false;
                    Problem($"{why}  The video itself is ready to download.");
                    return;
                }
            }

            Problem(why);
            return;
        }

        _playlist = list;
        _tracks.Clear();

        var have = new DuplicateIndex(_services.Library.Sounds);
        var number = 1;

        foreach (var item in list.Items)
        {
            var already = have.Find(item.Title, item.Duration);

            var track = new PlaylistTrack
            {
                Video = item,
                Number = number++,
                Name = YouTubeDownloader.SafeFileName(item.Title),
                Have = already,

                // Left unticked rather than hidden or refused: this is somebody's library and
                // they may well want the other copy. It is one click to take it anyway.
                Include = already is null
            };

            // Re-count as tracks are ticked and unticked, so the button always says how
            // many are actually going to be fetched.
            track.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(PlaylistTrack.Include)) RefreshPlaylistCount();
            };

            _tracks.Add(track);
        }

        PlaylistTitle.Text = list.Title;

        FoundPanel.Visibility = Visibility.Collapsed;
        SingleNamePanel.Visibility = Visibility.Collapsed;
        PlaylistPanel.Visibility = Visibility.Visible;

        RefreshPlaylistCount();

        if (list.Items.Count >= YouTubeDownloader.MaxPlaylistItems)
        {
            PlaylistHint.Text += $" Only the first {YouTubeDownloader.MaxPlaylistItems} are listed.";
        }
    }

    // -----------------------------------------------------------------------

    private async void OnDownload(object sender, RoutedEventArgs e)
    {
        if (_busy) return;

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

            if (_playlist is not null)
            {
                await DownloadPlaylistAsync(path, folder);
            }
            else if (_found is not null)
            {
                await DownloadSingleAsync(downloader, folder);
            }
        }
        catch (OperationCanceledException)
        {
            // Whatever finished before it was stopped is still worth keeping.
            if (_downloaded.Count > 0) Finish();
            else Problem("Cancelled.");
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

    /// <summary>
    /// Close with what was downloaded, if there is still a window to close.
    ///
    /// A download that is cancelled finishes after the window has gone, and setting the
    /// result on a window that is no longer a dialog throws. Thrown from inside the catch
    /// that handles the cancelling, it goes straight past the catch below it and out of an
    /// async void, which ends the whole app rather than the download.
    /// </summary>
    private void Finish()
    {
        if (_closed) return;

        try
        {
            DialogResult = true;
        }
        catch (InvalidOperationException ex)
        {
            // Closed between the check and here.
            Log.Debug($"The download window had already closed: {ex.Message}");
        }
    }

    private async Task DownloadSingleAsync(YouTubeDownloader downloader, string folder)
    {
        var progress = new Progress<DownloadProgress>(p =>
        {
            Progress.Value = p.Percent;
            ProgressText.Text = p.Stage == "Downloading…" ? $"{p.Stage}  {p.Percent:F0}%" : p.Stage;
        });

        var file = await downloader.DownloadMp3Async(
            _found!.Url, folder, NameBox.Text, 192, progress, _work!.Token);

        if (file is null)
        {
            Problem(downloader.LastError ?? "The download failed.");
            return;
        }

        _downloaded.Add(file);
        Log.Info($"Downloaded '{Path.GetFileName(file)}' into {folder}.");

        Finish();
    }

    /// <summary>How many tracks are fetched at the same time.</summary>
    private const int AtOnce = 4;

    /// <summary>
    /// Four at a time. One at a time leaves the connection idle while each track is being
    /// converted, and a long list spends a good part of its time doing nothing; many more
    /// than four only divides the same connection into thinner shares and makes YouTube
    /// likelier to start refusing.
    ///
    /// Each gets its own downloader: the last error is kept on the instance, and four of them
    /// sharing one would overwrite each other's.
    /// </summary>
    private async Task DownloadPlaylistAsync(string toolPath, string folder)
    {
        var chosen = _tracks.Where(t => t.Include).ToList();
        if (chosen.Count == 0) return;

        // Taken once. The window may close while these are still winding down, and reading
        // it off the source afterwards would be reading a disposed object.
        var token = _work!.Token;

        var failures = new List<string>();
        var percent = new double[chosen.Count];
        var running = 0;
        var done = 0;
        string? firstError = null;

        // Made here, on the UI thread, so each one reports back onto it.
        var progresses = new IProgress<DownloadProgress>[chosen.Count];

        for (var i = 0; i < chosen.Count; i++)
        {
            var index = i;

            progresses[i] = new Progress<DownloadProgress>(p =>
            {
                percent[index] = p.Percent;
                ShowOverall();
            });
        }

        void ShowOverall()
        {
            Progress.Value = percent.Sum() / chosen.Count;

            ProgressText.Text = running > 0
                ? $"{done} of {chosen.Count} done  ·  {running} downloading"
                : $"{done} of {chosen.Count} done";
        }

        using var slots = new SemaphoreSlim(AtOnce, AtOnce);

        async Task Fetch(PlaylistTrack track, int index)
        {
            await slots.WaitAsync(token);

            running++;
            ShowOverall();

            try
            {
                token.ThrowIfCancellationRequested();

                var worker = new YouTubeDownloader(toolPath);
                var name = track.Name.Trim().Length > 0 ? track.Name : track.Video.Title;

                var file = await worker.DownloadMp3Async(
                    track.Video.Url, folder, name, 192, progresses[index], token);

                if (file is null)
                {
                    // One unavailable track does not stop the other nineteen.
                    failures.Add(track.Name);
                    firstError ??= worker.LastError;
                    Log.Warn($"Playlist track '{track.Name}' failed: {worker.LastError}");
                    return;
                }

                _downloaded.Add(file);
                track.Include = false;   // shows what is done if this is stopped part way
                percent[index] = 100;
                done++;
            }
            finally
            {
                running--;
                ShowOverall();
                slots.Release();
            }
        }

        // WhenAll waits for every one of them before it throws, so by the time a cancel comes
        // back out of here nothing is still writing to the folder.
        await Task.WhenAll(chosen.Select(Fetch));

        FailedCount = failures.Count;
        Log.Info($"Playlist: {_downloaded.Count} downloaded into {folder}, {failures.Count} failed.");

        if (_downloaded.Count > 0)
        {
            Finish();
            return;
        }

        Problem(failures.Count > 0
            ? $"None of them could be fetched. The first said: {firstError}"
            : "Nothing was downloaded.");
    }

    // -----------------------------------------------------------------------

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
        AutoNameButton.IsEnabled = !busy && _found is not null;
        ResetNameButton.IsEnabled = !busy && _found is not null;
        FolderBox.IsEnabled = !busy;
        PlaylistCheck.IsEnabled = !busy;
        // The whole panel, not just the list: renaming or unticking tracks while they are
        // being fetched changes what the run is doing halfway through it.
        PlaylistPanel.IsEnabled = !busy;
        LookUpButton.IsEnabled = !busy && YtDlpTool.Locate() is not null;
        GetToolButton.IsEnabled = !busy;

        DownloadButton.IsEnabled = !busy && (_playlist is not null
            ? _tracks.Any(t => t.Include)
            : _found is not null && NameBox.Text.Trim().Length > 0);

        if (!busy) return;

        ProblemText.Visibility = Visibility.Collapsed;

        if (what is null) return;

        ProgressPanel.Visibility = Visibility.Visible;
        ProgressText.Text = what;
    }

    private void Problem(string message)
    {
        ProblemText.Text = message;
        ProblemText.Foreground = (System.Windows.Media.Brush)FindResource("DangerBrush");
        ProblemText.Visibility = Visibility.Visible;
        ProgressPanel.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// Something worth saying that is not something going wrong. Same place, different
    /// colour: already having a song does not stop anything, so it should not look like
    /// a failure.
    /// </summary>
    private void Notice(string message)
    {
        ProblemText.Text = message;
        ProblemText.Foreground = (System.Windows.Media.Brush)FindResource("WarningBrush");
        ProblemText.Visibility = Visibility.Visible;
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        // Cancel means stop what is running; the second press closes the window.
        if (_busy)
        {
            Log.Debug("Download cancelled by the Cancel button.");
            _work?.Cancel();

            // Cancel carries IsCancel so Escape works, and that is WPF's cue to close the
            // dialog. Not while there is something to stop: the window has to outlive the
            // download long enough to report what it managed to get.
            e.Handled = true;
            return;
        }

        DialogResult = _downloaded.Count > 0;
    }

    protected override void OnClosed(EventArgs e)
    {
        _closed = true;
        Log.Debug($"Download window closed (a download was running: {_busy}).");

        // Cancelled, but not disposed: a download that is still winding down holds a token
        // from this, and taking the source away underneath it throws where it is awaited.
        // It is collected once the last of them has let go.
        _work?.Cancel();

        base.OnClosed(e);
    }
}
