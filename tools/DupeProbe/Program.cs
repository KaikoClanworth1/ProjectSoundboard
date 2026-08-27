using System.IO;
using System.Text;
using System.Text.Json;
using ProjectSoundboard.App.Services;
using ProjectSoundboard.Core.Models;

namespace DupeProbe;

/// <summary>
/// Whether a download is something the library already has, checked before anything is
/// fetched — which means on the title and the length alone.
///
/// The two ways to be wrong are not equal. Missing a duplicate is what happened before this
/// existed, and costs a second copy. Claiming one wrongly unticks a track somebody wanted,
/// which is worse, so the cases that must NOT match matter more than the ones that must.
/// </summary>
internal static class Program
{
    private sealed record Case(string Have, string Want, int HaveSecs, int WantSecs, bool Same);

    private static readonly Case[] Cases =
    [
        // The same song, with the upload's padding on the download's side.
        new("Adele - Rolling in the Deep", "Adele - Rolling in the Deep (Official Music Video)", 228, 228, true),
        new("Avicii - Waiting For Love", "Avicii - Waiting For Love (Lyric Video)", 231, 231, true),
        new("DNCE - Cake By The Ocean", "DNCE - Cake By The Ocean (Lyrics)", 219, 220, true),

        // Renamed by the tidy-up, downloaded again from the original link.
        new("Maroon 5 - Payphone", "Maroon 5 Ft. Wiz Khalifa - Payphone (Lyrics)", 231, 231, true),

        // Lengths a couple of seconds apart are the same recording, differently trimmed.
        new("Coldplay - Hymn For The Weekend", "Coldplay - Hymn For The Weekend", 258, 260, true),

        // Different songs by the same artist.
        new("Adele - Rolling in the Deep", "Adele - Skyfall", 228, 286, false),

        // The same name, a very different length: a cover, a remix, a live take, or an
        // "Intro" that happens to share a name with another "Intro".
        new("Nirvana - Something in the Way", "Nirvana - Something in the Way", 232, 1200, false),
        new("Various - Intro", "Various - Intro", 35, 240, false),

        // Nothing like it at all.
        new("Queen - Bohemian Rhapsody", "Ed Sheeran - Galway Girl", 355, 170, false),

        // A length nobody knows: the name has to carry it on its own.
        new("Metallica - Enter Sandman", "Metallica - Enter Sandman (Official Music Video)", 0, 331, true),
    ];

    private static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;

        if (args.Length > 1 && args[0] == "--live") return Live(args[1]);

        var failures = 0;

        Console.WriteLine("=== is this one we already have? ===");
        Console.WriteLine();

        foreach (var one in Cases)
        {
            var index = new DuplicateIndex([
                new SoundEntry { FilePath = $@"C:\library\{one.Have}.mp3", DurationSeconds = one.HaveSecs }
            ]);

            var found = index.Find(one.Want, TimeSpan.FromSeconds(one.WantSecs));
            var ok = (found is not null) == one.Same;

            if (!ok) failures++;

            Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {(one.Same ? "same " : "other")}  " +
                              $"{Shorten(one.Have)}  vs  {Shorten(one.Want)}");

            if (!ok)
            {
                Console.WriteLine($"        expected {(one.Same ? "a match" : "no match")}, " +
                                  $"got {(found is null ? "none" : found.Name)}");
            }
        }

        Console.WriteLine();
        Console.WriteLine(failures == 0 ? "ALL PASS" : $"{failures} FAILED");

        return failures == 0 ? 0 : 1;
    }

    /// <summary>
    /// The real library, against titles as YouTube gives them. Made-up pairs only prove the
    /// rule does what it was written to do; a real library is where the near misses live.
    /// </summary>
    private static int Live(string libraryFile)
    {
        if (!File.Exists(libraryFile)) { Console.WriteLine($"No library at {libraryFile}"); return 1; }

        var data = JsonSerializer.Deserialize<LibraryData>(File.ReadAllText(libraryFile));
        if (data is null) { Console.WriteLine("That library could not be read."); return 1; }

        var index = new DuplicateIndex(data.Sounds);

        Console.WriteLine($"  library : {data.Sounds.Count} sounds, {index.Count} distinct names");
        Console.WriteLine();

        // Every sound in the library, asked for again the way YouTube would name it. Every
        // one of these must be recognised, or the check is not worth having.
        var missed = 0;
        var checkedCount = 0;

        foreach (var sound in data.Sounds.Where(s => !s.IsMissing).Take(400))
        {
            var asYouTubeWouldSayIt = $"{sound.DisplayName} (Official Music Video)";

            checkedCount++;
            if (index.Find(asYouTubeWouldSayIt, TimeSpan.FromSeconds(sound.DurationSeconds)) is not null) continue;

            missed++;
            if (missed <= 5) Console.WriteLine($"  MISSED  {sound.DisplayName}");
        }

        Console.WriteLine($"  recognised {checkedCount - missed} of {checkedCount} of its own sounds.");
        Console.WriteLine();

        // And something that is certainly not in there.
        var nonsense = index.Find("A Song That Does Not Exist Anywhere At All", TimeSpan.FromSeconds(123));
        Console.WriteLine($"  a song it does not have : {(nonsense is null ? "not claimed" : "WRONGLY CLAIMED as " + nonsense.Name)}");

        var failures = missed + (nonsense is null ? 0 : 1);

        Console.WriteLine();
        Console.WriteLine(failures == 0 ? "LIVE PASS" : $"{failures} FAILED");

        return failures == 0 ? 0 : 1;
    }

    private static string Shorten(string value) =>
        value.Length <= 34 ? value.PadRight(34) : value[..33] + "…";
}
