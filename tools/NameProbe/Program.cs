using System.Text;
using ProjectSoundboard.App.Services;

namespace NameProbe;

/// <summary>
/// Every one of the names from a real download, with what it was called and what it should
/// have been called. These came from somebody renaming a playlist by hand, which makes them
/// the only honest measure of whether automatic naming is worth pressing.
///
/// Two of them are marked as expected misses rather than quietly dropped, because a naming
/// pass that scores itself only against the cases it already handles is not measuring
/// anything. They are the cases where the wanted name is not in the title at all.
/// </summary>
internal static class Program
{
    private sealed record Case(string From, string To, string? Impossible = null);

    private static readonly Case[] Music =
    [
        new("Pitbull, Ne-Yo - Time of Our Lives (Official Video)",
            "Pitbull Ne-Yo - Time of Our Lives"),

        new("The Black Eyed Peas - I Gotta Feeling (Lyrics)",
            "The Black Eyed Peas - I Gotta Feeling"),

        new("Avicii - Waiting For Love",
            "Avicii - Waiting For Love"),

        new("Myles Smith - Stargazing (Take My Heart Don’t Break It) - Visualiser",
            "Myles Smith - Stargazing"),

        new("Coldplay - Hymn For The Weekend (Official Video)",
            "Coldplay - Hymn For The Weekend"),

        new("Ed Sheeran - Galway Girl [Official Music Video]",
            "Ed Sheeran - Galway Girl"),

        new("Olly Murs - Troublemaker ft. Flo Rida",
            "Olly Murs - Troublemaker"),

        new("P!nk - Raise Your Glass (Official Video)",
            "P!nk - Raise Your Glass"),

        new("Katy Perry - Part Of Me (Official)",
            "Katy Perry - Part Of Me"),

        new("Ke$ha - TiK ToK (Official HD Video)",
            "Ke$ha - TiK ToK"),

        new("Justin Timberlake - SexyBack (Official Video) ft. Timbaland",
            "Justin Timberlake - SexyBack"),

        new("Katy Perry - California Gurls (Lyrics) Feat. Snoop Dogg",
            "Katy Perry - California Gurls"),

        new("Aaron Smith - Dancin (KRONO Remix) - Lyrics",
            "Aaron Smith - Dancin (KRONO Remix)"),

        new("Milky Chance - Stolen Dance (Official 4K Music Video)",
            "Milky Chance - Stolen Dance"),

        new("OneRepublic - I Ain’t Worried (From “Top Gun Maverick”) [Official Music Video]",
            "OneRepublic - I Ain’t Worried"),

        new("Moves Like Jagger - Maroon 5 (Feat. Christina Aguilera) (Lyrics) \U0001F3B5",
            "Maroon 5 - Moves Like Jagger",
            "the title says the song first and the artist second, and nothing in the text says which is which"),

        new("DNCE - Cake By The Ocean (Lyrics)",
            "DNCE - Cake By The Ocean"),

        // Off the same mix. The guest is named in the artist rather than after the song, and
        // cutting from "Ft." to the end of the line took the song with it.
        new("Maroon 5 Ft. Wiz Khalifa - Payphone (Lyrics)",
            "Maroon 5 - Payphone"),

        new("Calvin Harris, Rihanna - This Is What You Came For (Official Video)",
            "Calvin Harris Rihanna - This Is What You Came For"),

        // A version word in the bracket, but "performance" is about the video, not the cut.
        new("Sia - Cheap Thrills (Performance Edit)",
            "Sia - Cheap Thrills"),

        // The label signed on the end. The remix is a different recording, so it stays —
        // see the note with this case in the release.
        new("OMI - Cheerleader (Felix Jaehn Remix) Ultra Records",
            "OMI - Cheerleader (Felix Jaehn Remix)"),
    ];

    private static readonly Case[] Anime =
    [
        new("TVアニメ『呪術廻戦』第2期「渋谷事変」ノンクレジットOPムービー／OPテーマ：King Gnu「SPECIALZ」｜毎週木曜夜11時56分～MBSTBS系列全國28局にて放送中!!",
            "Jujutsu Kaisen Opening Special Z",
            "the wanted name is an English translation of a Japanese title; none of those words are in the text"),

        new("『チェンソーマン』ノンクレジットオープニング CHAINSAW MAN Opening│米津玄師 「KICK BACK」",
            "CHAINSAW MAN Opening"),

        new("Attack on Titan The Final Season Part 2 Opening｜The Rumbling - SiM",
            "Attack on Titan Opening｜The Rumbling"),

        new("Soul Eater Opening Resonance by T.M. Revolution",
            "Soul Eater Opening Resonance"),

        new("Guilty Crown - 【Official OP】 - Extreme HD",
            "Guilty Crown Opening"),

        new("Naruto Shippuden Opening 16 Silhouette by KANA-BOON",
            "Naruto Shippuden Opening Silhouette"),

        new("My Hero Academia Season 2 Opening 1 Peace Sign",
            "My Hero Academia Opening Peace Sign"),

        new("One Piece Opening 20 Hope by Namie Amuro",
            "One Piece Opening Hope"),

        // The show is spelt out in English inside the first bracket, even though the rest of
        // the title is not. Taking the whole title at once found the artist and the hashtag
        // and called it "Creepy Nuts #BBBB".
        new("TVアニメ「マッシュル-MASHLE-」第2期ノンクレジットOPムービー｜Creepy Nuts「Bling-Bang-Bang-Born」#BBBBダンス",
            "MASHLE Opening"),

        // The same shape, but the English name of the show is nowhere in the text — the only
        // Latin in the first half is "Season 2", which names nothing.
        new("『俺だけレベルアップな件 Season 2』ノンクレジットOPムービー｜LiSA「ReawakeR (feat. Felix of Stray Kids)」",
            "Solo Leveling Season 2 Opening",
            "\"Solo Leveling\" appears nowhere in the title; the only English in it is \"Season 2\""),
    ];

    /// <summary>
    /// Names that are already right, and the ways the rules could plausibly ruin them. An
    /// automatic rename that damages good names is worse than none at all, so these matter
    /// more than the ones it is supposed to fix.
    /// </summary>
    private static readonly Case[] LeaveAlone =
    [
        new("Ed Sheeran - Galway Girl", "Ed Sheeran - Galway Girl"),
        new("Ariana Grande - Stuck with U", "Ariana Grande - Stuck with U"),
        new("Lana Del Rey - Video Games", "Lana Del Rey - Video Games"),
        new("Live - Lightning Crashes", "Live - Lightning Crashes"),
        new("Fatboy Slim - Right Here, Right Now", "Fatboy Slim - Right Here, Right Now"),
        new("Blink-182 - All the Small Things", "Blink-182 - All the Small Things"),
        new("Simon & Garfunkel - The Sound of Silence", "Simon & Garfunkel - The Sound of Silence"),
        new("Bruce Springsteen - Born in the U.S.A.", "Bruce Springsteen - Born in the U.S.A."),
        new("deadmau5 - Strobe (Original Mix)", "deadmau5 - Strobe (Original Mix)"),
        new("Daft Punk - Get Lucky (Radio Edit)", "Daft Punk - Get Lucky (Radio Edit)"),
        new("Johnny Cash - Hurt (Live)", "Johnny Cash - Hurt (Live)"),
        new("Eminem - Lose Yourself", "Eminem - Lose Yourself"),
    ];

    /// <summary>
    /// Every title in a real playlist, cleaned, side by side with what it was. Made-up cases
    /// only prove the rules do what they were written to do; a hundred real ones are the only
    /// way to find the titles nobody thought of.
    /// </summary>
    private static async Task<int> Live(string url)
    {
        var tool = YtDlpTool.Locate();
        if (tool is null) { Console.WriteLine("yt-dlp is not here."); return 1; }

        var list = await new YouTubeDownloader(tool).ProbePlaylistAsync(url);
        if (list is null) { Console.WriteLine("Could not read that playlist."); return 1; }

        var changed = 0;
        var kept = 0;

        foreach (var item in list.Items)
        {
            var cleaned = TrackNaming.Clean(item.Title);

            if (string.Equals(cleaned, item.Title, StringComparison.Ordinal))
            {
                kept++;
                Console.WriteLine($"  --  {item.Title}");
                continue;
            }

            changed++;
            Console.WriteLine($"  ->  {cleaned}");
            Console.WriteLine($"      was {item.Title}");
        }

        Console.WriteLine();
        Console.WriteLine($"  {changed} tidied, {kept} left as they were, of {list.Items.Count}.");
        return 0;
    }

    /// <summary>
    /// The Anime Op folder, which was already half tidy. These matter because they are the
    /// case where the song is what comes after the dash, not the band — the opposite of
    /// "Opening|The Rumbling - SiM", and told apart only by whether the theme already has a
    /// song beside it.
    /// </summary>
    private static readonly Case[] AnimeFolder =
    [
        new("Attack on Titan Opening 1", "Attack on Titan Opening"),
        new("Cyberpunk Edgerunners “I Really Want to Stay At Your House”",
            "Cyberpunk Edgerunners “I Really Want to Stay At Your House”"),
        new("Hunter X Hunter Opening 1 Departure!", "Hunter X Hunter Opening Departure!"),
        new("Kaiju No. 8 Opening", "Kaiju No. 8 Opening"),
        new("My Hero Academia Season 3 Opening 1", "My Hero Academia Opening"),
        new("Noragami Opening 2", "Noragami Opening"),
        new("Overlord Opening Clattanoia", "Overlord Opening Clattanoia"),
        new("The Seven Deadly Sins Opening 1 - Passionate Spectrum",
            "The Seven Deadly Sins Opening - Passionate Spectrum"),
        new("Tokyo Ghoul Opening Unravel", "Tokyo Ghoul Opening Unravel"),
    ];

    private static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;

        if (args.Length > 1 && args[0] == "--live") return Live(args[1]).GetAwaiter().GetResult();

        var (musicOk, musicTotal, musicKnown) = Run("music", Music);
        var (animeOk, animeTotal, animeKnown) = Run("anime themes", Anime);
        var (keepOk, keepTotal, keepKnown) = Run("names that must survive untouched", LeaveAlone);
        var (animeOk2, animeTotal2, animeKnown2) = Run("the anime op folder", AnimeFolder);

        var ok = musicOk + animeOk + keepOk + animeOk2;
        var total = musicTotal + animeTotal + keepTotal + animeTotal2;
        var known = musicKnown + animeKnown + keepKnown + animeKnown2;

        Console.WriteLine();
        Console.WriteLine($"  {ok} of {total} exact; {known} known to be out of reach.");

        var missed = total - ok - known;

        Console.WriteLine();
        Console.WriteLine(missed == 0
            ? "ALL PASS - everything that can be worked out from the title is."
            : $"{missed} UNEXPECTED");

        return missed == 0 ? 0 : 1;
    }

    private static (int ok, int total, int known) Run(string heading, Case[] cases)
    {
        Console.WriteLine($"=== {heading} ===");
        Console.WriteLine();

        var ok = 0;
        var known = 0;

        foreach (var one in cases)
        {
            var actual = TrackNaming.Clean(one.From);
            var matched = string.Equals(actual, one.To, StringComparison.Ordinal);

            if (matched) ok++;
            else if (one.Impossible is not null) known++;

            var mark = matched ? "PASS" : one.Impossible is not null ? "KNOWN" : "FAIL";

            Console.WriteLine($"  {mark,-5} {Shorten(one.From)}");
            Console.WriteLine($"        -> {actual}");

            if (matched) { Console.WriteLine(); continue; }

            Console.WriteLine($"        want {one.To}");
            if (one.Impossible is not null) Console.WriteLine($"        why  {one.Impossible}");

            Console.WriteLine();
        }

        return (ok, cases.Length, known);
    }

    private static string Shorten(string value) =>
        value.Length <= 72 ? value : value[..71] + "…";
}
