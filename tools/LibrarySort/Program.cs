using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ProjectSoundboard.App.Services;

namespace LibrarySort;

internal sealed record Move(string From, string To, string Why);

internal sealed record Plan(string Root, List<Move> Moves, List<string> Untouched);

/// <summary>
/// Tidies a music library: one folder per artist, consistent track names, and the lesser of
/// two versions of the same song set aside rather than sitting next to the real one.
///
/// It plans first and applies second, on purpose. Everything here is somebody's actual music
/// collection, moving a thousand files is not something to find out about afterwards, and a
/// plan can be read before it happens. Nothing is ever deleted — the losing version of a
/// song is moved, not removed — and applying writes an undo file that puts every last file
/// back exactly where it was.
/// </summary>
internal static partial class Program
{
    private static readonly string[] AudioTypes =
        [".flac", ".mp3", ".opus", ".m4a", ".wav", ".ogg", ".aac", ".wma", ".alac", ".aiff"];

    /// <summary>Files that belong to an album and should travel with it.</summary>
    private static readonly string[] SidecarTypes =
        [".jpg", ".jpeg", ".png", ".gif", ".cue", ".log", ".txt", ".nfo", ".pdf"];

    /// <summary>
    /// Versions that lose to a plain or album version of the same song. Deliberately short:
    /// anything not on this list is treated as a different recording rather than a copy, so
    /// a box set full of alternate mixes and rough mixes survives intact.
    /// </summary>
    private static readonly string[] LesserVersions =
    [
        "clean", "clean version", "edited", "edited version", "censored", "censored version",
        "radio edit", "radio version", "radio mix", "instrumental", "instrumental version",
        "karaoke", "karaoke version", "acapella", "a cappella", "tv track", "tv size",
        "backing track", "no vocals"
    ];

    private static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;

        if (args.Length > 1 && args[0] == "--apply") return Apply(args[1]);
        if (args.Length > 1 && args[0] == "--undo") return Undo(args[1]);

        if (args.Length > 1 && args[0] == "--tidy")
        {
            // Sorting leaves the folders the files came out of standing empty.
            var gone = RemoveEmptyFolders(args[1]);

            Console.WriteLine($"  removed {gone} empty folder(s).");
            return 0;
        }

        if (args.Length > 1 && args[0] == "--tags")
        {
            Tags_.FfProbe = Find("ffprobe.exe");

            var one = new Song(args[1], Tags_.Read(args[1]));

            Console.WriteLine($"  album_artist : '{one.Tags?.AlbumArtist}'");
            Console.WriteLine($"  artist       : '{one.Tags?.Artist}'");
            Console.WriteLine($"  -> filed as  : '{one.Artist}'");
            Console.WriteLine($"  album        : '{one.Album}'");
            Console.WriteLine($"  title        : '{one.Title}'");
            Console.WriteLine($"  loose        : {one.IsLoose}");

            return 0;
        }

        var root = Argument(args, "--root") ?? @"E:\1. Plex Backup\Music";
        var ignores = args.Where((a, i) => i > 0 && args[i - 1] == "--ignore").ToArray();
        var output = Argument(args, "--out") ?? "plan.json";

        Tags_.FfProbe = Find("ffprobe.exe");

        if (Tags_.FfProbe is null)
        {
            Console.WriteLine("ffprobe was not found, and the tags are what makes this safe.");
            return 1;
        }

        return MakePlan(root, ignores, output);
    }

    // -----------------------------------------------------------------------

    private static int MakePlan(string root, string[] ignores, string output)
    {
        if (!Directory.Exists(root)) { Console.WriteLine($"No such folder: {root}"); return 1; }

        Console.WriteLine($"Reading {root}");
        foreach (var ignore in ignores) Console.WriteLine($"  leaving alone: {ignore}");
        Console.WriteLine();

        var files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Where(f => !ignores.Any(i => f.StartsWith(i, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        var songs = new List<Song>();
        var untouched = new List<string>();
        var read = 0;

        foreach (var file in files)
        {
            var extension = Path.GetExtension(file).ToLowerInvariant();

            if (!AudioTypes.Contains(extension))
            {
                // Artwork and cue sheets follow their album; anything else — the video files
                // that turned out to be guitar lessons — is not this tool's business.
                if (!SidecarTypes.Contains(extension)) untouched.Add(file);
                continue;
            }

            if (++read % 50 == 0) Console.WriteLine($"  read {read} files…");

            var tags = Tags_.Read(file);
            songs.Add(new Song(file, tags));
        }

        Console.WriteLine($"  read {read} music files.");
        Console.WriteLine();

        Curated = CuratedYears(songs);

        var moves = new List<Move>();

        PlanLooseFiles(root, songs, moves);
        PlanTaggedFiles(root, songs, moves);
        PlanLesserVersions(root, songs, moves);
        PlanSidecars(root, files, songs, moves);

        var plan = new Plan(root, moves, untouched);

        File.WriteAllText(output,
            JsonSerializer.Serialize(plan, new JsonSerializerOptions { WriteIndented = true }));

        Report(plan);

        Console.WriteLine();
        Console.WriteLine($"Plan written to {output}. Nothing has been moved.");
        Console.WriteLine($"Apply it with:  LibrarySort --apply {output}");

        return 0;
    }

    /// <summary>
    /// The download folder, where names came from YouTube and the tags carry the same
    /// padding the names do. These are tidied where they stand: a grab bag of one-off
    /// songs, comedy and lyric videos does not divide into artists.
    /// </summary>
    private static void PlanLooseFiles(string root, List<Song> songs, List<Move> moves)
    {
        foreach (var song in songs.Where(s => s.IsLoose))
        {
            var stem = TrackNaming.Clean(Path.GetFileNameWithoutExtension(song.Path));
            var name = YouTubeDownloader.SafeFileName(stem) + Path.GetExtension(song.Path);

            var target = Path.Combine(Path.GetDirectoryName(song.Path)!, name);
            if (!Different(song.Path, target)) continue;

            song.Planned = target;
            moves.Add(new Move(song.Path, target, "tidied name"));
        }
    }

    private static void PlanTaggedFiles(string root, List<Song> songs, List<Move> moves)
    {
        foreach (var song in songs.Where(s => !s.IsLoose))
        {
            var target = TargetFor(root, song);
            song.Planned = target;

            if (!Different(song.Path, target)) continue;

            var why = string.Equals(
                Path.GetDirectoryName(song.Path), Path.GetDirectoryName(target),
                StringComparison.OrdinalIgnoreCase)
                ? "tidied name"
                : "one folder per artist";

            moves.Add(new Move(song.Path, target, why));
        }
    }

    /// <summary>The year each album already had in the folder it was filed under.</summary>
    private static Dictionary<string, string> Curated = new();

    private static string AlbumKey(Song song) =>
        $"{song.Artist}|{song.Album}".ToLowerInvariant();

    /// <summary>
    /// The year somebody already wrote on the folder, where the folder is about this album.
    ///
    /// Worth more than the tag. A tag says when this particular pressing came out — Queen's
    /// Greatest Hits is tagged 2012 for a reissue of a 1981 record, and Unia is tagged 2005
    /// for a 2007 one — where the folder name says when the record came out, which is what
    /// makes an artist's folder read in order. The tag is only used where there is no folder
    /// saying otherwise, or where a track is moving to an album it was not filed under.
    /// </summary>
    private static Dictionary<string, string> CuratedYears(List<Song> songs)
    {
        var votes = new Dictionary<string, Dictionary<string, int>>();

        foreach (var song in songs.Where(s => !s.IsLoose))
        {
            var folder = Path.GetDirectoryName(song.Path) ?? string.Empty;

            // The album folder is sometimes a level up, with a pressing folder beneath it —
            // but the nearest folder that names the album is the one to believe. "Ecliptica -
            // Revisited" sits inside "1999 - Ecliptica", and it is a re-recording from 2014,
            // not the 1999 record; its own folder names it exactly and carries no year, so
            // the year has to come from the tag rather than from the folder above it.
            var best = 0;
            string? bestYear = null;

            for (var level = 0; level < 2 && folder.Length > 0; level++)
            {
                var name = Path.GetFileName(folder);
                var score = Closeness(name, song.Album);

                if (score > best)
                {
                    best = score;
                    bestYear = AnyYear().Match(name) is { Success: true } y ? y.Value : null;
                }

                folder = Path.GetDirectoryName(folder) ?? string.Empty;
            }

            if (best == 0 || bestYear is null) continue;

            var key = AlbumKey(song);

            if (!votes.TryGetValue(key, out var counts)) votes[key] = counts = new();
            counts[bestYear] = counts.GetValueOrDefault(bestYear) + 1;
        }

        return votes.ToDictionary(
            v => v.Key,
            v => v.Value.OrderByDescending(c => c.Value).First().Key);
    }

    /// <summary>
    /// How well a folder name matches an album: named exactly, roughly, or not at all.
    /// </summary>
    private static int Closeness(string folder, string album)
    {
        var left = NotLetters().Replace(AnyYear().Replace(folder, " "), string.Empty).ToLowerInvariant();
        var right = NotLetters().Replace(album, string.Empty).ToLowerInvariant();

        if (left.Length < 3 || right.Length < 3) return 0;
        if (left == right) return 2;

        return left.Contains(right, StringComparison.Ordinal)
            || right.Contains(left, StringComparison.Ordinal) ? 1 : 0;
    }

    private static string TargetFor(string root, Song song)
    {
        var artist = YouTubeDownloader.SafeFileName(Readable(song.Artist));

        // The year in front, which is how this library was already arranged and which sorts
        // an artist's folder into the order the records came out.
        var album = YouTubeDownloader.SafeFileName(Readable(song.Album));

        var year = Curated.GetValueOrDefault(AlbumKey(song)) ?? song.Year;
        if (year is { Length: 4 } && !album.StartsWith(year, StringComparison.Ordinal))
        {
            album = $"{year} - {album}";
        }

        var title = YouTubeDownloader.SafeFileName(Readable(song.Title));
        var extension = Path.GetExtension(song.Path);

        var number = song.Tags?.Track > 0 ? $"{song.Tags.Track:00} - " : string.Empty;

        // Only where there is more than one disc: "1-01" on a single album is noise.
        if (song.Tags is { Disc: > 0, DiscCount: > 1 })
        {
            number = $"{song.Tags.Disc}-{song.Tags.Track:00} - ";
        }

        return Path.Combine(root, artist, album, number + title + extension);
    }

    /// <summary>
    /// Two of the same song, where one of them is the clean or radio cut. The plain one stays
    /// where it belongs; the other moves to Other, still filed under its artist and album so
    /// it can be found and put back.
    ///
    /// Only within one album, and only against the short list of versions that really are the
    /// same recording with something done to it. A live take, an alternate mix or a demo is a
    /// different recording and is left where it is.
    /// </summary>
    private static void PlanLesserVersions(string root, List<Song> songs, List<Move> moves)
    {
        var groups = songs
            .Where(s => !s.IsLoose)
            .GroupBy(s => (s.Artist.ToLowerInvariant(), s.Album.ToLowerInvariant(), BaseTitle(s.Title)))
            .Where(g => g.Count() > 1);

        foreach (var group in groups)
        {
            var lesser = group.Where(s => Marker(s.Title) is { } m && LesserVersions.Contains(m)).ToList();
            var keepers = group.Except(lesser).ToList();

            // Nothing to prefer it over: if every copy is a clean version, they all stay.
            if (lesser.Count == 0 || keepers.Count == 0) continue;

            foreach (var song in lesser)
            {
                var from = song.Planned ?? song.Path;

                var target = Path.Combine(
                    root, "Other",
                    YouTubeDownloader.SafeFileName(song.Artist),
                    YouTubeDownloader.SafeFileName(song.Album),
                    Path.GetFileName(from));

                var index = moves.FindIndex(m => m.To == from);
                var why = $"'{Marker(song.Title)}' of a song that is also here plain";

                if (index >= 0) moves[index] = moves[index] with { To = target, Why = why };
                else if (Different(song.Path, target)) moves.Add(new Move(song.Path, target, why));

                song.Planned = target;
            }
        }
    }

    /// <summary>
    /// Cover art and cue sheets go where their album went. Worked out from the songs that
    /// shared their folder, so a folder whose music did not move keeps its artwork too.
    /// </summary>
    private static void PlanSidecars(string root, List<string> files, List<Song> songs, List<Move> moves)
    {
        var destinations = songs
            .Where(s => s.Planned is not null && !s.IsLoose)
            .GroupBy(s => Path.GetDirectoryName(s.Path)!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                g => g.Key,
                g => g.GroupBy(s => Path.GetDirectoryName(s.Planned!)!)
                      .OrderByDescending(x => x.Count())
                      .First().Key,
                StringComparer.OrdinalIgnoreCase);

        foreach (var file in files)
        {
            if (!SidecarTypes.Contains(Path.GetExtension(file).ToLowerInvariant())) continue;

            var folder = Path.GetDirectoryName(file)!;
            string to;

            if (destinations.TryGetValue(folder, out var target))
            {
                to = Path.Combine(target, Path.GetFileName(file));
            }
            else
            {
                // A folder of its own inside the album — the "Tech.info" a rip leaves behind.
                // Its album is the one the music in the folder above went to, and it keeps
                // its own name so it stays the separate thing it was.
                var above = Path.GetDirectoryName(folder);

                if (above is null || !destinations.TryGetValue(above, out var albumTarget)) continue;

                to = Path.Combine(albumTarget, Path.GetFileName(folder), Path.GetFileName(file));
            }

            if (!Different(file, to)) continue;

            moves.Add(new Move(file, to, "artwork follows its album"));
        }
    }

    // -----------------------------------------------------------------------

    private static void Report(Plan plan)
    {
        Console.WriteLine($"  {plan.Moves.Count} file(s) to move or rename.");
        Console.WriteLine();

        foreach (var group in plan.Moves.GroupBy(m => m.Why.StartsWith('\'') ? "set aside as a lesser version" : m.Why)
                                        .OrderByDescending(g => g.Count()))
        {
            Console.WriteLine($"    {group.Count(),5}  {group.Key}");
        }

        Console.WriteLine();
        Console.WriteLine("  artists after the move:");

        var artists = plan.Moves
            .Select(m => Path.GetRelativePath(plan.Root, m.To).Split(Path.DirectorySeparatorChar)[0])
            .GroupBy(a => a)
            .OrderByDescending(g => g.Count());

        foreach (var artist in artists) Console.WriteLine($"    {artist.Count(),5}  {artist.Key}");

        if (plan.Untouched.Count == 0) return;

        Console.WriteLine();
        Console.WriteLine($"  {plan.Untouched.Count} file(s) left alone (not music):");

        foreach (var group in plan.Untouched.GroupBy(f => Path.GetExtension(f).ToLowerInvariant()))
        {
            Console.WriteLine($"    {group.Count(),5}  {group.Key}");
        }
    }

    // -----------------------------------------------------------------------

    private static int Apply(string planFile)
    {
        var plan = JsonSerializer.Deserialize<Plan>(File.ReadAllText(planFile));
        if (plan is null) { Console.WriteLine("That plan could not be read."); return 1; }

        var done = new List<Move>();
        var failed = 0;

        foreach (var move in plan.Moves)
        {
            try
            {
                if (!File.Exists(move.From)) continue;

                Directory.CreateDirectory(Path.GetDirectoryName(move.To)!);

                var target = Free(move.To, move.From);
                File.Move(move.From, target);

                done.Add(move with { To = target });
            }
            catch (Exception ex)
            {
                failed++;
                Console.WriteLine($"  could not move {Path.GetFileName(move.From)}: {ex.Message}");
            }
        }

        var undo = planFile + ".undo.json";

        File.WriteAllText(undo,
            JsonSerializer.Serialize(done, new JsonSerializerOptions { WriteIndented = true }));

        var emptied = RemoveEmptyFolders(plan.Root);

        Console.WriteLine();
        Console.WriteLine($"  moved {done.Count} file(s), {failed} could not be moved.");
        Console.WriteLine($"  removed {emptied} folder(s) that were left empty.");
        Console.WriteLine($"  undo file: {undo}");
        Console.WriteLine($"  put everything back with:  LibrarySort --undo {undo}");

        return failed == 0 ? 0 : 1;
    }

    private static int Undo(string manifest)
    {
        var moves = JsonSerializer.Deserialize<List<Move>>(File.ReadAllText(manifest));
        if (moves is null) { Console.WriteLine("That undo file could not be read."); return 1; }

        var back = 0;

        // Backwards, so a file moved twice lands where it started.
        foreach (var move in Enumerable.Reverse(moves))
        {
            try
            {
                if (!File.Exists(move.To)) continue;

                Directory.CreateDirectory(Path.GetDirectoryName(move.From)!);
                File.Move(move.To, move.From);
                back++;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  could not put back {Path.GetFileName(move.To)}: {ex.Message}");
            }
        }

        Console.WriteLine($"  put {back} file(s) back.");
        return 0;
    }

    // -----------------------------------------------------------------------

    private static string Free(string target, string from)
    {
        if (!File.Exists(target)) return target;
        if (string.Equals(target, from, StringComparison.OrdinalIgnoreCase)) return target;

        var folder = Path.GetDirectoryName(target)!;
        var stem = Path.GetFileNameWithoutExtension(target);
        var extension = Path.GetExtension(target);

        for (var n = 2; n < 999; n++)
        {
            var candidate = Path.Combine(folder, $"{stem} ({n}){extension}");
            if (!File.Exists(candidate)) return candidate;
        }

        return target;
    }

    /// <summary>
    /// The folders the files came out of, now standing empty. Deepest first and repeated
    /// until nothing more goes, so a folder that only held other empty folders goes too.
    /// Only ever removes a folder with no file anywhere beneath it.
    /// </summary>
    private static int RemoveEmptyFolders(string root)
    {
        var removed = 0;

        for (var pass = 0; pass < 8; pass++)
        {
            var went = 0;

            foreach (var folder in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories)
                         .OrderByDescending(f => f.Length)
                         .ToList())
            {
                try
                {
                    if (!Directory.Exists(folder)) continue;
                    if (Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).Any()) continue;

                    Directory.Delete(folder, recursive: true);
                    went++;
                }
                catch { /* in use, or gone already */ }
            }

            removed += went;
            if (went == 0) break;
        }

        return removed;
    }

    /// <summary>
    /// A slash in a name is a real separator — "N 2 Gether Now / Break Stuff" is a double A
    /// side — and a file name cannot hold one. Taking it out runs the two titles together,
    /// so it becomes the dash it is standing in for.
    /// </summary>
    private static string Readable(string text) =>
        text.Replace(" / ", " - ").Replace("/", " - ").Replace(" \\ ", " - ").Trim();

    private static bool Different(string from, string to) =>
        !string.Equals(from, to, StringComparison.Ordinal);

    /// <summary>The song without its version, so two versions of it group together.</summary>
    private static string BaseTitle(string title)
    {
        var bare = Brackets().Replace(title, " ");
        return NotLetters().Replace(bare, string.Empty).ToLowerInvariant();
    }

    /// <summary>The version a title names, if it names one.</summary>
    private static string? Marker(string title)
    {
        var match = Brackets().Match(title);
        return match.Success ? match.Groups["inner"].Value.Trim().ToLowerInvariant() : null;
    }

    private static string? Argument(string[] args, string name)
    {
        var at = Array.IndexOf(args, name);
        return at >= 0 && at + 1 < args.Length ? args[at + 1] : null;
    }

    private static string? Find(string exe)
    {
        var places = new[]
        {
            @"C:\ffmpeg\bin", @"C:\Program Files\ffmpeg\bin",
            Environment.GetFolderPath(Environment.SpecialFolder.System)
        };

        foreach (var place in places)
        {
            var path = Path.Combine(place, exe);
            if (File.Exists(path)) return path;
        }

        foreach (var place in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(';'))
        {
            if (place.Length == 0) continue;

            try
            {
                var path = Path.Combine(place, exe);
                if (File.Exists(path)) return path;
            }
            catch { /* a bad PATH entry */ }
        }

        return null;
    }

    [GeneratedRegex(@"[\(\[](?<inner>[^\)\]]*)[\)\]]")]
    private static partial Regex Brackets();

    [GeneratedRegex(@"[^a-zA-Z0-9]")]
    private static partial Regex NotLetters();

    [GeneratedRegex(@"(?:19|20)\d{2}")]
    private static partial Regex AnyYear();
}

/// <summary>One music file, and where it is going.</summary>
internal sealed partial class Song(string path, Tags? tags)
{
    /// <summary>Where a guest is named, so the band can be taken from in front of it.</summary>
    private static readonly System.Text.RegularExpressions.Regex Featuring = new(
        @"\s*[\(\[]?\s*\b(?:feat|ft|featuring)\b\.?\s",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    public string Path { get; } = path;
    public Tags? Tags { get; } = tags;
    public string? Planned { get; set; }

    /// <summary>
    /// A file with nothing to file it by, or one out of the download folder, where the tags
    /// repeat the YouTube title and are no better than the name.
    /// </summary>
    public bool IsLoose =>
        Tags?.Title is null ||
        (Tags.AlbumArtist is null && Tags.Artist is null) ||
        Tags.Album is null ||
        Path.Contains(@"\Youtube Download\", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The band, not the line-up on this particular track. "Limp Bizkit Feat. Method Man" is
    /// Limp Bizkit; filing it separately is exactly the pile of near-identical folders this
    /// is meant to get rid of.
    ///
    /// Only the guest is taken off. "The Chainsmokers &amp; Coldplay" is the name of the thing,
    /// not a band with a guest, so ampersands are left alone.
    /// </summary>
    public string Artist
    {
        get
        {
            var artist = Tags?.AlbumArtist ?? Tags?.Artist ?? "Unknown Artist";
            var cut = Featuring.Match(artist);

            return cut.Success ? artist[..cut.Index].Trim() : artist.Trim();
        }
    }

    /// <summary>The year the record came out, from the tags or from the folder it is in.</summary>
    public string? Year
    {
        get
        {
            var digits = System.Text.RegularExpressions.Regex.Match(Tags?.Date ?? "", @"\d{4}");
            if (digits.Success) return digits.Value;

            var folder = System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(Path)) ?? "";
            var leading = System.Text.RegularExpressions.Regex.Match(folder, @"^\s*(\d{4})");

            return leading.Success ? leading.Groups[1].Value : null;
        }
    }

    public string Album =>
        Tags?.Album ?? System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(Path)) ?? "Unknown Album";

    public string Title =>
        Tags?.Title ?? System.IO.Path.GetFileNameWithoutExtension(Path);
}
