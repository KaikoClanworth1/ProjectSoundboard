using System.IO;
using ProjectSoundboard.App.Services;

namespace YouTubeProbe;

/// <summary>
/// Checks the parts of the YouTube download that can go wrong quietly: which link means
/// which video, and what a title becomes once Windows has had its say about filenames.
///
/// The link cases are the ones carried over from the working web version, and they are the
/// ones worth guarding — a link copied while a mix is playing carries the queue it came
/// from, and taking that literally downloads an endless radio station instead of the song.
/// </summary>
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--live") return Live(args.Skip(1).ToArray()).GetAwaiter().GetResult();
        if (args.Length > 0 && args[0] == "--playlist") return ListPlaylist(args.Skip(1).ToArray()).GetAwaiter().GetResult();

        var failures = 0;

        Console.WriteLine("=== links ===");

        failures += Link("https://www.youtube.com/watch?v=ABCDEFGHIJK",
                         "https://www.youtube.com/watch?v=ABCDEFGHIJK");

        // The case that matters: context, not intent.
        failures += Link("https://www.youtube.com/watch?v=ABCDEFGHIJK&list=RDxyz&index=5",
                         "https://www.youtube.com/watch?v=ABCDEFGHIJK");

        failures += Link("https://youtu.be/ABCDEFGHIJK?si=tracking",
                         "https://www.youtube.com/watch?v=ABCDEFGHIJK");

        failures += Link("https://www.youtube.com/shorts/ABCDEFGHIJK",
                         "https://www.youtube.com/watch?v=ABCDEFGHIJK");

        failures += Link("https://music.youtube.com/watch?v=ABCDEFGHIJK",
                         "https://www.youtube.com/watch?v=ABCDEFGHIJK");

        Console.WriteLine();
        Console.WriteLine("=== what counts as a YouTube link ===");

        failures += Accepts("https://www.youtube.com/watch?v=x", true);
        failures += Accepts("https://youtu.be/x", true);
        failures += Accepts("https://music.youtube.com/watch?v=x", true);
        failures += Accepts("https://example.com/watch?v=x", false);
        failures += Accepts("not a link at all", false);
        failures += Accepts("", false);

        Console.WriteLine();
        Console.WriteLine("=== playlist links ===");

        // Without the tick, a watch link carrying a list means the video. With it, the list —
        // and the link is handed over whole, because a Mix can only be read from the watch
        // link it was built around. Rewriting it to playlist?list= gets "unviewable".
        failures += Playlist("https://www.youtube.com/watch?v=ABCDEFGHIJK&list=PL123", true,
                             "https://www.youtube.com/watch?v=ABCDEFGHIJK&list=PL123");

        failures += Playlist("https://www.youtube.com/watch?v=5QIQ5QHqDbw&list=RDG6AcBEz3Qxg&start_radio=1&index=1", true,
                             "https://www.youtube.com/watch?v=5QIQ5QHqDbw&list=RDG6AcBEz3Qxg&start_radio=1&index=1");

        failures += Playlist("https://www.youtube.com/playlist?list=PL123", true,
                             "https://www.youtube.com/playlist?list=PL123");

        failures += Playlist("https://www.youtube.com/watch?v=ABCDEFGHIJK", false,
                             "https://www.youtube.com/watch?v=ABCDEFGHIJK");

        Console.WriteLine();
        Console.WriteLine("=== mixes, which are not playlists anybody made ===");

        // The tick is offered on its own for a real list, but never for a Mix: a link copied
        // while the radio plays nearly always means that song, not the next four hundred.
        failures += Mix("https://www.youtube.com/watch?v=ABCDEFGHIJK&list=RDG6AcBEz3Qxg", true);
        failures += Mix("https://www.youtube.com/watch?v=ABCDEFGHIJK&list=RDMMG6AcBEz3Qxg", true);
        failures += Mix("https://www.youtube.com/watch?v=ABCDEFGHIJK&list=PL123", false);
        failures += Mix("https://www.youtube.com/watch?v=ABCDEFGHIJK", false);

        Console.WriteLine();
        Console.WriteLine("=== the video a link names, for falling back to it ===");

        failures += Video("https://www.youtube.com/watch?v=ABCDEFGHIJK&list=RDxyz", "ABCDEFGHIJK");
        failures += Video("https://youtu.be/ABCDEFGHIJK?si=tracking", "ABCDEFGHIJK");
        failures += Video("https://www.youtube.com/playlist?list=PL123", null);
        failures += Video("https://www.youtube.com/@jawed/videos", null);

        Console.WriteLine();
        Console.WriteLine("=== titles into filenames ===");

        failures += Name("AC/DC - Back in Black", "ACDC - Back in Black");
        failures += Name("What? A song: part 2 <live>", "What A song part 2 live");
        failures += Name("   spaced    out   ", "spaced out");
        failures += Name("...", "download");
        failures += Name("", "download");

        Console.WriteLine();
        Console.WriteLine("=== tools ===");
        Console.WriteLine($"  yt-dlp : {YtDlpTool.Locate() ?? "(not found — the dialog offers to fetch it)"}");
        Console.WriteLine($"  ffmpeg : {YouTubeDownloader.FindFfmpeg() ?? "(not found — audio kept as served)"}");

        Console.WriteLine();
        Console.WriteLine(failures == 0 ? "ALL PASS" : $"{failures} FAILED");
        return failures == 0 ? 0 : 1;
    }

    /// <summary>
    /// Reads a real playlist and lists what is in it, without downloading any of it. This is
    /// the half the dialog depends on: the list has to be there before anything can be named.
    /// </summary>
    private static async Task<int> ListPlaylist(string[] args)
    {
        // A channel's videos tab is a playlist as far as yt-dlp is concerned, and this one
        // has a single nineteen second video on it — small enough to be a decent test.
        var url = args.FirstOrDefault() ?? "https://www.youtube.com/@jawed/videos";

        var tool = YtDlpTool.Locate();
        if (tool is null)
        {
            var fetcher = new YtDlpTool();
            tool = await fetcher.FetchAsync(new Progress<string>(s => Console.WriteLine($"  {s}")));

            if (tool is null)
            {
                Console.WriteLine($"FAIL - could not fetch yt-dlp: {fetcher.LastError}");
                return 1;
            }
        }

        Console.WriteLine($"reading playlist: {url}");
        Console.WriteLine();

        var downloader = new YouTubeDownloader(tool);
        var list = await downloader.ProbePlaylistAsync(url);

        if (list is null)
        {
            Console.WriteLine($"FAIL - {downloader.LastError}");
            return 1;
        }

        Console.WriteLine($"  title  : {list.Title}");
        Console.WriteLine($"  tracks : {list.Items.Count}");
        Console.WriteLine();

        var n = 1;
        foreach (var item in list.Items.Take(10))
        {
            Console.WriteLine($"   {n++,3}. {Shorten(item.Title),-58} {item.DurationText,8}");
        }

        if (list.Items.Count > 10) Console.WriteLine($"   … and {list.Items.Count - 10} more");

        var failures = 0;

        if (list.Items.Count == 0) { Console.WriteLine("FAIL - no tracks listed."); failures++; }
        if (list.Items.Any(i => string.IsNullOrWhiteSpace(i.Title))) { Console.WriteLine("FAIL - a track has no title."); failures++; }
        if (list.Items.Any(i => !i.Url.Contains("watch?v=", StringComparison.Ordinal))) { Console.WriteLine("FAIL - a track has no usable link."); failures++; }

        Console.WriteLine();
        Console.WriteLine(failures == 0 ? "PLAYLIST PASS" : $"{failures} FAILED");
        return failures == 0 ? 0 : 1;
    }

    /// <summary>
    /// The real thing, end to end: fetch the tool if it is missing, read a link, download it.
    /// Uses one short clip and deletes it afterwards — this is checking the pipeline works,
    /// not collecting anything.
    /// </summary>
    private static async Task<int> Live(string[] args)
    {
        var url = args.FirstOrDefault() ?? "https://www.youtube.com/watch?v=jNQXAC9IVRw";
        var failures = 0;

        var tool = YtDlpTool.Locate();

        if (tool is null)
        {
            Console.WriteLine("yt-dlp not present; fetching it as the dialog would.");

            var fetcher = new YtDlpTool();
            var status = new Progress<string>(s => Console.WriteLine($"  {s}"));

            tool = await fetcher.FetchAsync(status);

            if (tool is null)
            {
                Console.WriteLine($"FAIL — could not fetch yt-dlp: {fetcher.LastError}");
                return 1;
            }
        }

        Console.WriteLine($"yt-dlp: {tool}");
        Console.WriteLine();

        var downloader = new YouTubeDownloader(tool);

        Console.WriteLine("reading the link…");
        var info = await downloader.ProbeAsync(url);

        if (info is null)
        {
            Console.WriteLine($"FAIL — could not read the link: {downloader.LastError}");
            return 1;
        }

        Console.WriteLine($"  title    : {info.Title}");
        Console.WriteLine($"  uploader : {info.Uploader}");
        Console.WriteLine($"  length   : {info.DurationText}");
        Console.WriteLine();

        var folder = Path.Combine(Path.GetTempPath(), "psb-youtube-probe");
        if (Directory.Exists(folder)) Directory.Delete(folder, true);

        Console.WriteLine("downloading…");

        var last = -1;
        var progress = new Progress<DownloadProgress>(p =>
        {
            var step = (int)(p.Percent / 20) * 20;
            if (step == last && p.Stage == "Downloading…") return;

            last = step;
            Console.WriteLine($"  {p.Stage} {p.Percent:F0}%");
        });

        var file = await downloader.DownloadMp3Async(
            info.Url, folder, YouTubeDownloader.SafeFileName(info.Title), 192, progress);

        if (file is null || !File.Exists(file))
        {
            Console.WriteLine($"FAIL — the download failed: {downloader.LastError}");
            return 1;
        }

        var size = new FileInfo(file).Length;

        Console.WriteLine();
        Console.WriteLine($"  file  : {Path.GetFileName(file)}");
        Console.WriteLine($"  size  : {size / 1024:N0} KB");

        if (!file.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine("FAIL — expected an .mp3 (is ffmpeg present?)");
            failures++;
        }

        if (size < 20_000)
        {
            Console.WriteLine("FAIL — that is too small to be the audio.");
            failures++;
        }

        try { Directory.Delete(folder, true); Console.WriteLine("  cleaned up"); }
        catch { /* leaving a temp folder behind is not a failure */ }

        Console.WriteLine();
        Console.WriteLine(failures == 0 ? "LIVE PASS" : $"{failures} FAILED");
        return failures == 0 ? 0 : 1;
    }

    private static int Mix(string input, bool expected)
    {
        var actual = YouTubeDownloader.IsMixList(input);
        var ok = actual == expected;

        Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {(expected ? "a mix    " : "a list   ")}  {Shorten(input)}");
        return ok ? 0 : 1;
    }

    private static int Video(string input, string? expected)
    {
        var actual = YouTubeDownloader.VideoIdOf(input);
        var ok = actual == expected;

        Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {actual ?? "(no video)",-14}  {Shorten(input)}");
        if (!ok) Console.WriteLine($"        expected {expected ?? "(no video)"}");

        return ok ? 0 : 1;
    }

    private static int Playlist(string input, bool expectedIsPlaylist, string expectedUrl)
    {
        var isPlaylist = YouTubeDownloader.LooksLikePlaylist(input);
        var url = YouTubeDownloader.AsPlaylistUrl(input);
        var ok = isPlaylist == expectedIsPlaylist && url == expectedUrl;

        Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  carries a list={isPlaylist,-5}  {Shorten(input)}");
        if (!ok) Console.WriteLine($"        expected list={expectedIsPlaylist} url={expectedUrl}\n        got      url={url}");

        return ok ? 0 : 1;
    }

    private static int Link(string input, string expected)
    {
        var actual = YouTubeDownloader.Normalise(input);
        var ok = actual == expected;

        Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {Shorten(input)}");
        if (!ok) Console.WriteLine($"        expected {expected}\n        got      {actual}");

        return ok ? 0 : 1;
    }

    private static int Accepts(string input, bool expected)
    {
        var actual = YouTubeDownloader.LooksLikeYouTube(input);
        var ok = actual == expected;

        Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {(expected ? "accepts" : "refuses")}  {Shorten(input)}");
        return ok ? 0 : 1;
    }

    private static int Name(string input, string expected)
    {
        var actual = YouTubeDownloader.SafeFileName(input);
        var ok = actual == expected;

        Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  \"{input}\" -> \"{actual}\"");
        if (!ok) Console.WriteLine($"        expected \"{expected}\"");

        return ok ? 0 : 1;
    }

    private static string Shorten(string value) =>
        value.Length <= 58 ? (value.Length == 0 ? "(empty)" : value) : value[..57] + "…";
}
