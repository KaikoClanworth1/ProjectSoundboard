using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using ProjectSoundboard.Core.Storage;

namespace ProjectSoundboard.App.Services;

/// <summary>What a link turned out to be, before anything is downloaded.</summary>
public sealed record VideoInfo(string Url, string Title, string? Uploader, TimeSpan Duration)
{
    public string DurationText => Duration.TotalHours >= 1
        ? Duration.ToString(@"h\:mm\:ss")
        : Duration.ToString(@"m\:ss");
}

public sealed record DownloadProgress(double Percent, string Stage);

/// <summary>A playlist and what is in it, read without downloading any of it.</summary>
public sealed record PlaylistInfo(string Url, string Title, IReadOnlyList<VideoInfo> Items);

/// <summary>
/// Fetches the audio from a YouTube link as an MP3, straight into a library folder.
///
/// The awkward parts here are carried over from a working implementation rather than
/// rediscovered: which links mean what, which player client to ask as, and how a video title
/// becomes a filename Windows will accept.
/// </summary>
public sealed partial class YouTubeDownloader
{
    /// <summary>
    /// Which clients to try, in order. YouTube serves different media depending on what it
    /// thinks is asking, and some of those are refused at download time even though the
    /// format list looked fine. The default goes first because it offers the best quality;
    /// android is the fallback because it keeps working when the default is refused.
    ///
    /// The order is not arbitrary: reversing it would cap every download at the android
    /// client's quality to solve a problem most videos do not have.
    /// </summary>
    private static readonly string?[] ClientFallbacks = { null, "android", "ios", "tv" };

    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(60);

    [GeneratedRegex(@"^https?://(www\.|m\.|music\.)?(youtube\.com|youtu\.be)/", RegexOptions.IgnoreCase)]
    private static partial Regex YouTubeUrl();

    /// <summary>Windows refuses these outright, and a video title may well contain any of them.</summary>
    [GeneratedRegex(@"[<>:""/\\|?*\x00-\x1f]")]
    private static partial Regex ForbiddenInFileName();

    [GeneratedRegex(@"(\d{1,3}(?:\.\d)?)%")]
    private static partial Regex PercentInOutput();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    private readonly string _ytDlp;

    public YouTubeDownloader(string ytDlpPath) => _ytDlp = ytDlpPath;

    public string? LastError { get; private set; }

    // -----------------------------------------------------------------------
    // Links
    // -----------------------------------------------------------------------

    public static bool LooksLikeYouTube(string? url) =>
        !string.IsNullOrWhiteSpace(url) && url.Length <= 500 && YouTubeUrl().IsMatch(url.Trim());

    /// <summary>
    /// Reduce a link to the single video it points at.
    ///
    /// A link copied while a mix is playing carries the queue it came from, as a "list"
    /// parameter. That is context, not intent: somebody clicked a video and the site
    /// remembered what was playing. Taken literally it means "download the whole mix", and
    /// an RD list is an endless automatically generated radio station, so it is never what
    /// was wanted.
    /// </summary>
    public static string Normalise(string url)
    {
        url = (url ?? string.Empty).Trim();

        try
        {
            var parts = new Uri(url);
            var host = parts.Host.ToLowerInvariant();
            var query = System.Web.HttpUtility.ParseQueryString(parts.Query);

            if (host.EndsWith("youtu.be", StringComparison.Ordinal))
            {
                var id = parts.AbsolutePath.Trim('/').Split('/').FirstOrDefault();
                return string.IsNullOrEmpty(id) ? url : $"https://www.youtube.com/watch?v={id}";
            }

            if (parts.AbsolutePath.TrimEnd('/').EndsWith("/watch", StringComparison.OrdinalIgnoreCase)
                && query["v"] is { Length: > 0 } video)
            {
                return $"https://www.youtube.com/watch?v={video}";
            }

            if (parts.AbsolutePath.Contains("/shorts/", StringComparison.OrdinalIgnoreCase))
            {
                var id = parts.AbsolutePath.Split("/shorts/", StringSplitOptions.None)
                    .ElementAtOrDefault(1)?.Split('/').FirstOrDefault();

                return string.IsNullOrEmpty(id) ? url : $"https://www.youtube.com/watch?v={id}";
            }
        }
        catch (UriFormatException)
        {
            // Handed back untouched; validation refuses it separately.
        }

        return url;
    }

    /// <summary>
    /// The video a link names, if it names one at all. A link that only carries a list — a
    /// bare /playlist or a channel — names none.
    /// </summary>
    public static string? VideoIdOf(string? url)
    {
        var single = Normalise(url ?? string.Empty);

        const string marker = "watch?v=";
        var at = single.IndexOf(marker, StringComparison.Ordinal);
        if (at < 0) return null;

        var id = single[(at + marker.Length)..];
        var end = id.IndexOfAny(['&', '#', '?']);

        id = end < 0 ? id : id[..end];
        return id.Length == 0 ? null : id;
    }

    /// <summary>
    /// Whether a link carries a playlist at all, so the playlist tick can be offered already
    /// on when it obviously applies.
    /// </summary>
    public static bool LooksLikePlaylist(string? url)
    {
        if (!LooksLikeYouTube(url)) return false;

        var lowered = url!.ToLowerInvariant();
        return lowered.Contains("/playlist", StringComparison.Ordinal)
               || lowered.Contains("list=", StringComparison.Ordinal);
    }

    /// <summary>
    /// A Mix rather than a playlist: the endless radio YouTube builds as somebody listens,
    /// which nobody made and which is different every time. Their ids begin with RD.
    /// </summary>
    public static bool IsMixList(string? url)
    {
        if (!LooksLikeYouTube(url)) return false;

        try
        {
            var list = System.Web.HttpUtility.ParseQueryString(new Uri(url!.Trim()).Query)["list"];
            return list is not null && list.StartsWith("RD", StringComparison.OrdinalIgnoreCase);
        }
        catch (UriFormatException)
        {
            return false;
        }
    }

    /// <summary>
    /// The link to hand over when the playlist tick is on.
    ///
    /// The opposite of <see cref="Normalise"/>: pasting a link copied mid-playlist and asking
    /// for the playlist is a clear request for the list, where without the tick that same link
    /// means the one video being watched.
    /// </summary>
    public static string AsPlaylistUrl(string url)
    {
        url = (url ?? string.Empty).Trim();

        try
        {
            var parts = new Uri(url);
            var query = System.Web.HttpUtility.ParseQueryString(parts.Query);
            var list = query["list"];

            if (string.IsNullOrEmpty(list)) return url;

            // A watch link that carries a list is handed over exactly as it is, rather than
            // tidied into the shorter playlist?list= form. A Mix has no page of its own —
            // asking for one comes back "this playlist type is unviewable" — and can only be
            // read from the watch link it was built around. Real playlists read fine either
            // way, so keeping the link whole is right for both.
            if (!string.IsNullOrEmpty(query["v"])) return url;

            return $"https://www.youtube.com/playlist?list={list}";
        }
        catch (UriFormatException)
        {
            // Left alone; validation refuses it separately.
        }

        // A channel or a bare /playlist link is already what it needs to be.
        return url;
    }

    /// <summary>A video title turned into something Windows will accept as a filename.</summary>
    public static string SafeFileName(string? text, string fallback = "download")
    {
        var cleaned = ForbiddenInFileName().Replace(text ?? string.Empty, string.Empty).Trim();
        cleaned = Whitespace().Replace(cleaned, " ").Trim(' ', '.');

        if (cleaned.Length == 0) cleaned = fallback;
        return cleaned.Length > 120 ? cleaned[..120].TrimEnd() : cleaned;
    }

    // -----------------------------------------------------------------------
    // Asking what a link is
    // -----------------------------------------------------------------------

    /// <summary>
    /// Read the title, uploader and length without downloading anything, so a name can be
    /// offered before committing to the download.
    /// </summary>
    public async Task<VideoInfo?> ProbeAsync(string url, CancellationToken ct = default)
    {
        LastError = null;
        url = Normalise(url);

        if (!LooksLikeYouTube(url))
        {
            LastError = "That does not look like a YouTube link.";
            return null;
        }

        foreach (var client in ClientFallbacks)
        {
            ct.ThrowIfCancellationRequested();

            var arguments = new List<string>
            {
                "--dump-single-json", "--skip-download", "--no-playlist",
                "--no-warnings", "--quiet", "--socket-timeout", "20"
            };

            AddClient(arguments, client);
            arguments.Add(url);

            var (code, output, error) = await RunAsync(arguments, ProbeTimeout, null, ct)
                .ConfigureAwait(false);

            if (code == 0 && output.Length > 0)
            {
                try
                {
                    using var document = System.Text.Json.JsonDocument.Parse(output);
                    var root = document.RootElement;

                    var title = Text(root, "title") ?? "Untitled";
                    var uploader = Text(root, "uploader") ?? Text(root, "channel");

                    var seconds = root.TryGetProperty("duration", out var d) &&
                                  d.ValueKind == System.Text.Json.JsonValueKind.Number
                        ? d.GetDouble()
                        : 0;

                    return new VideoInfo(url, title, uploader, TimeSpan.FromSeconds(seconds));
                }
                catch (Exception ex)
                {
                    LastError = $"Could not read the video details: {ex.Message}";
                    return null;
                }
            }

            LastError = Tidy(error, output);
        }

        return null;
    }

    /// <summary>
    /// Most tracks taken from one playlist. A channel link can name thousands of videos, and
    /// the first anybody would know about it is a full disk.
    /// </summary>
    public const int MaxPlaylistItems = 100;

    /// <summary>
    /// Read what is in a playlist without downloading any of it.
    ///
    /// Flat, deliberately: asking yt-dlp for full details of every entry means one request
    /// per video, which on a hundred-track list is a minute of waiting before anything can
    /// be shown. Flat gives the title, length and id of each in a single request, which is
    /// all that is needed to list them and name them.
    /// </summary>
    public async Task<PlaylistInfo?> ProbePlaylistAsync(string url, CancellationToken ct = default)
    {
        LastError = null;
        url = AsPlaylistUrl(url);

        if (!LooksLikeYouTube(url))
        {
            LastError = "That does not look like a YouTube link.";
            return null;
        }

        foreach (var client in ClientFallbacks)
        {
            ct.ThrowIfCancellationRequested();

            var arguments = new List<string>
            {
                "--dump-single-json", "--skip-download", "--flat-playlist", "--yes-playlist",
                "--no-warnings", "--quiet", "--socket-timeout", "20",
                "--playlist-items", $"1:{MaxPlaylistItems}"
            };

            AddClient(arguments, client);
            arguments.Add(url);

            // Longer than a single video: a large list takes a moment even flat.
            var (code, output, error) = await RunAsync(
                arguments, TimeSpan.FromMinutes(3), null, ct).ConfigureAwait(false);

            if (code != 0 || output.Length == 0)
            {
                LastError = Tidy(error, output);
                continue;
            }

            try
            {
                using var document = System.Text.Json.JsonDocument.Parse(output);
                var root = document.RootElement;

                if (!root.TryGetProperty("entries", out var entries) ||
                    entries.ValueKind != System.Text.Json.JsonValueKind.Array)
                {
                    LastError = "That link does not contain a playlist.";
                    return null;
                }

                var items = new List<VideoInfo>();

                foreach (var entry in entries.EnumerateArray())
                {
                    // Private and deleted entries come back as nulls with no id. They are
                    // not errors, they are just gone, so they are left out rather than
                    // listed as tracks that will fail.
                    var id = Text(entry, "id");
                    if (string.IsNullOrWhiteSpace(id)) continue;

                    var seconds = entry.TryGetProperty("duration", out var d) &&
                                  d.ValueKind == System.Text.Json.JsonValueKind.Number
                        ? d.GetDouble()
                        : 0;

                    items.Add(new VideoInfo(
                        $"https://www.youtube.com/watch?v={id}",
                        Text(entry, "title") ?? "Untitled",
                        Text(entry, "uploader") ?? Text(entry, "channel"),
                        TimeSpan.FromSeconds(seconds)));
                }

                if (items.Count == 0)
                {
                    LastError = "That playlist has nothing in it that can be downloaded.";
                    return null;
                }

                return new PlaylistInfo(url, Text(root, "title") ?? "Playlist", items);
            }
            catch (Exception ex)
            {
                LastError = $"Could not read the playlist: {ex.Message}";
                return null;
            }
        }

        // yt-dlp's own words for this one are true but unhelpful, and it is the most likely
        // way to arrive here: a Mix link with the video dropped off it has nothing left to
        // build the radio around, so YouTube has no list to give.
        if (LastError is not null &&
            LastError.Contains("unviewable", StringComparison.OrdinalIgnoreCase))
        {
            LastError = "That is a YouTube Mix — the radio it makes up as you listen, which " +
                        "has no fixed list. Paste the full link from the address bar, the one " +
                        "with the video in it, and its Mix can be read.";
        }

        return null;
    }

    // -----------------------------------------------------------------------
    // Downloading
    // -----------------------------------------------------------------------

    /// <summary>
    /// Fetch the audio and convert it to MP3 in <paramref name="folder"/>, named
    /// <paramref name="name"/>. Returns the finished path, or null with
    /// <see cref="LastError"/> set.
    /// </summary>
    public async Task<string?> DownloadMp3Async(
        string url, string folder, string name, int bitrateKbps,
        IProgress<DownloadProgress>? progress, CancellationToken ct = default)
    {
        LastError = null;
        url = Normalise(url);

        if (!LooksLikeYouTube(url))
        {
            LastError = "That does not look like a YouTube link.";
            return null;
        }

        try
        {
            Directory.CreateDirectory(folder);
        }
        catch (Exception ex)
        {
            LastError = $"Could not use that folder: {ex.Message}";
            return null;
        }

        var stem = SafeFileName(name);

        // Never quietly write over something already in the library.
        var target = Unique(Path.Combine(folder, stem + ".mp3"));
        stem = Path.GetFileNameWithoutExtension(target);

        foreach (var client in ClientFallbacks)
        {
            ct.ThrowIfCancellationRequested();

            var arguments = new List<string>
            {
                "--no-playlist",
                "--no-warnings",
                "--newline",              // one progress line at a time rather than redraws
                "--no-part",
                "--retries", "3",
                "--fragment-retries", "5",
                "--socket-timeout", "20",

                // Audio only, then converted. Asking for "best" would fetch the video as
                // well and throw away everything but the sound.
                "-f", "bestaudio/best",
                "-x",
                "--audio-format", "mp3",
                "--audio-quality", $"{bitrateKbps}K",

                // The extension is left to yt-dlp: it downloads as whatever YouTube served
                // and renames once the conversion is done.
                "-o", Path.Combine(folder, stem + ".%(ext)s")
            };

            var ffmpeg = FindFfmpeg();
            if (ffmpeg is not null)
            {
                arguments.Add("--ffmpeg-location");
                arguments.Add(ffmpeg);
            }

            AddClient(arguments, client);
            arguments.Add(url);

            var (code, _, error) = await RunAsync(
                arguments, TimeSpan.FromHours(2), progress, ct).ConfigureAwait(false);

            if (code == 0)
            {
                if (File.Exists(target)) return target;

                // Without ffmpeg the conversion cannot happen and the original audio is
                // kept instead. Whatever landed under this name is the result.
                var produced = Directory
                    .EnumerateFiles(folder, stem + ".*")
                    .FirstOrDefault(f => !f.EndsWith(".part", StringComparison.OrdinalIgnoreCase));

                if (produced is not null) return produced;

                LastError = "The download finished but no file appeared.";
                return null;
            }

            LastError = Tidy(error, string.Empty);
        }

        return null;
    }

    // -----------------------------------------------------------------------

    private static void AddClient(List<string> arguments, string? client)
    {
        if (client is null) return;

        arguments.Add("--extractor-args");
        arguments.Add($"youtube:player_client={client}");
    }

    /// <summary>ffmpeg does the MP3 conversion. Without it yt-dlp keeps the original audio.</summary>
    public static string? FindFfmpeg()
    {
        foreach (var candidate in new[]
                 {
                     Path.Combine(AppPaths.DataRoot, "tools", "ffmpeg.exe"),
                     Path.Combine(AppPaths.AppDirectory, "ffmpeg.exe"),
                     @"C:\ffmpeg\bin\ffmpeg.exe"
                 })
        {
            if (File.Exists(candidate)) return Path.GetDirectoryName(candidate);
        }

        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;

        foreach (var folder in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                if (File.Exists(Path.Combine(folder.Trim(), "ffmpeg.exe"))) return folder.Trim();
            }
            catch { /* a malformed PATH entry is not worth failing over */ }
        }

        return null;
    }

    private static string Unique(string path)
    {
        if (!File.Exists(path)) return path;

        var folder = Path.GetDirectoryName(path)!;
        var stem = Path.GetFileNameWithoutExtension(path);
        var extension = Path.GetExtension(path);

        for (var n = 2; n < 1000; n++)
        {
            var candidate = Path.Combine(folder, $"{stem} ({n}){extension}");
            if (!File.Exists(candidate)) return candidate;
        }

        return Path.Combine(folder, $"{stem} ({Guid.NewGuid():N}){extension}");
    }

    private async Task<(int Code, string Output, string Error)> RunAsync(
        List<string> arguments, TimeSpan timeout,
        IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        var info = new ProcessStartInfo
        {
            FileName = _ytDlp,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        foreach (var argument in arguments) info.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = info, EnableRaisingEvents = true };

        var output = new StringBuilder();
        var error = new StringBuilder();

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) return;

            output.AppendLine(e.Data);
            Report(progress, e.Data);
        };

        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null) error.AppendLine(e.Data);
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout);

        try
        {
            await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
            }
            catch { /* already gone */ }

            throw;
        }

        return (process.ExitCode, output.ToString(), error.ToString());
    }

    /// <summary>
    /// Turn a yt-dlp progress line into something the dialog can show. The stage matters as
    /// much as the number: converting to MP3 happens after the download reaches 100%, and
    /// without saying so it looks like it has stalled at the end.
    /// </summary>
    private static void Report(IProgress<DownloadProgress>? progress, string line)
    {
        if (progress is null) return;

        if (line.StartsWith("[ExtractAudio]", StringComparison.Ordinal))
        {
            progress.Report(new DownloadProgress(100, "Converting to MP3…"));
            return;
        }

        if (!line.StartsWith("[download]", StringComparison.Ordinal)) return;

        var match = PercentInOutput().Match(line);
        if (!match.Success) return;

        if (double.TryParse(match.Groups[1].Value,
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var percent))
        {
            progress.Report(new DownloadProgress(percent, "Downloading…"));
        }
    }

    /// <summary>yt-dlp prefixes its failures with "ERROR:"; that is the line worth showing.</summary>
    private static string Tidy(string error, string output)
    {
        var line = error.Split('\n')
            .Select(l => l.Trim())
            .LastOrDefault(l => l.StartsWith("ERROR:", StringComparison.OrdinalIgnoreCase));

        if (line is not null) return line["ERROR:".Length..].Trim();

        var any = error.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.Length > 0);
        return any ?? (output.Length > 0 ? "Unexpected output from yt-dlp." : "yt-dlp failed.");
    }

    private static string? Text(System.Text.Json.JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) &&
        value.ValueKind == System.Text.Json.JsonValueKind.String
            ? value.GetString()
            : null;
}
