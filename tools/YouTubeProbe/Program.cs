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
