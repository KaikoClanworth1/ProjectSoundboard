using System.Text;
using System.Text.RegularExpressions;

namespace ProjectSoundboard.App.Services;

/// <summary>
/// Turns a YouTube title into the name somebody would have typed themselves.
///
/// Uploaders pad titles with things that describe the upload rather than the song — Official
/// Video, Lyrics, 4K, the featured artist, the season a theme came from. Stripping that back
/// is mechanical enough to do for a hundred tracks at once, which is the point: nobody is
/// going to hand-edit a hundred names, so they get downloaded with the padding still on.
///
/// It is deliberately conservative about anything that changes which recording a name refers
/// to. "(KRONO Remix)" stays where "(Official Video)" goes, because one of those is a
/// different piece of music and the other is a label on the same one. Everything it produces
/// is still editable afterwards, so the bar is "usually right and never destructive of
/// meaning", not "always right".
/// </summary>
public static partial class TrackNaming
{
    /// <summary>
    /// Bracketed parts that say which recording this is, and so survive. Everything else in
    /// brackets is packaging: an alternate title, a featured artist, a soundtrack credit.
    /// </summary>
    private static readonly string[] VersionWords =
    [
        "remix", "mix", "edit", "version", "acoustic", "live", "cover", "instrumental",
        "extended", "remaster", "bootleg", "reprise", "demo", "vip"
    ];

    /// <summary>
    /// Words that mark a trailing "- something" as a note about the upload rather than part
    /// of the name. Only ever used to drop a short tail, never the whole title.
    /// </summary>
    private static readonly string[] NoiseWords =
    [
        "official", "video", "audio", "music", "lyric", "lyrics", "visualiser", "visualizer",
        "hd", "hq", "4k", "8k", "uhd", "mv", "full", "clip", "teaser", "trailer",
        "promo", "nightcore", "topic", "creditless", "noncredit", "non-credit", "nontelop",
        "vostfr", "sub", "subbed", "dub", "dubbed", "raw", "clean", "performance"
    ];

    /// <summary>
    /// The cleaned-up name for a title, or the title itself when there is nothing to do.
    /// Never returns empty: a title that cleans away to nothing is handed back untouched,
    /// because a blank name is worse than a padded one.
    /// </summary>
    public static string Clean(string? title)
    {
        var text = (title ?? string.Empty).Trim();
        if (text.Length == 0) return string.Empty;

        text = StripBrackets(text);

        // A title still carrying a lot of Japanese after the brackets have gone is one where
        // the English has to be found rather than uncovered, and the two are not the same
        // job. See CondenseLatin.
        text = Cjk().Matches(text).Count >= 4 ? CondenseLatin(text) : StripCjk(text);
        if (text.Length == 0) return (title ?? string.Empty).Trim();

        text = StripSymbols(text);

        // From here on the rules split on " - ", so the gaps left behind have to close first.
        text = Spaces().Replace(text, " ").Trim();

        text = SeasonPart().Replace(text, " ");
        text = ThemeNumber().Replace(text, "${word}");
        text = StripFeatured(text);
        text = StripByArtist(text);

        text = LabelTail().Replace(text, string.Empty);
        text = StripNoiseTail(text);
        text = StripTrailingNoise(text);
        text = StripThemeArtist(text);

        // "Guilty Crown - Opening" reads as a dash between artist and song, which is not what
        // it is. The theme belongs to the show's name, not opposite it.
        text = ThemeAfterDash().Replace(text, " $1");

        text = TidyArtistCommas(text);
        text = Tidy(text);

        // A title that cleans away to nothing is handed back as it was. Fragments are caught
        // earlier, by CondenseLatin, which is the only place they can be told apart from a
        // short name: "Moves Like Jagger - Maroon 5" is two thirds separator and numeral, and
        // counting those at the end of the run condemns it along with the real debris.
        return text.Length == 0 ? (title ?? string.Empty).Trim() : text;
    }

    /// <summary>Whether cleaning would actually change anything, so the button can say so.</summary>
    public static bool WouldChange(string? title) =>
        !string.Equals(Clean(title), (title ?? string.Empty).Trim(), StringComparison.Ordinal);

    // -----------------------------------------------------------------------

    /// <summary>
    /// Brackets of every kind, including the Japanese ones, since anime themes are titled
    /// with them. A bracket naming the opening becomes the word instead of vanishing.
    /// </summary>
    private static string StripBrackets(string text) =>
        BracketGroup().Replace(text, match =>
        {
            var inner = match.Groups["inner"].Value.Trim();
            if (inner.Length == 0) return " ";

            if (ThemeMark().IsMatch(inner)) return " Opening ";

            // A version word only earns its keep in a bracket that is about the recording.
            // "(Performance Edit)" has one, but "performance" is a word about the video, and
            // the song is the same song — where "(Radio Edit)" really is a different cut.
            if (!HasNoiseWord(inner) &&
                VersionWords.Any(w => inner.Contains(w, StringComparison.OrdinalIgnoreCase)))
            {
                return $" ({inner}) ";
            }

            // Round brackets after a song hold an alternate title or a credit, and go. The
            // square and Japanese ones are used the other way round — they hold the name of
            // the show, as in 【OSHI NO KO】 or [One Piece] — so what is inside them is kept
            // and the brackets themselves dropped, unless it is only a note about the upload.
            //
            // 「」 is the exception among the Japanese ones: it quotes the song a theme is,
            // not the show it belongs to, and repeating it makes the name longer, not clearer.
            var open = match.Groups["open"].Value[0];

            if (open is '(' or '{' or '「') return " ";
            if (IsNoiseOnly(inner)) return " ";

            return inner.Any(char.IsLetter) && !Cjk().IsMatch(inner) ? $" {inner} " : " ";
        });

    /// <summary>Whether a bracket mentions the upload at all, rather than only the song.</summary>
    private static bool HasNoiseWord(string inner) =>
        inner.Split([' ', ',', '·', '/', '+'], StringSplitOptions.RemoveEmptyEntries)
             .Any(word => NoiseWords.Contains(
                 word.Trim('.', '!', '?', '-', '–', '—').ToLowerInvariant()));

    /// <summary>Whether a bracket holds nothing but notes about the upload.</summary>
    private static bool IsNoiseOnly(string inner)
    {
        var words = inner.Split([' ', ',', '·', '/', '+'], StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return true;

        return words.All(word =>
        {
            var bare = word.Trim('.', '!', '?', '-', '–', '—').ToLowerInvariant();
            return bare.Length == 0 || NoiseWords.Contains(bare) || Resolution().IsMatch(bare);
        });
    }

    /// <summary>
    /// Japanese text, once the bracketed parts are gone. What is left of these titles is the
    /// English half the uploader put there for exactly this reason.
    /// </summary>
    private static string StripCjk(string text) => Cjk().Replace(text, " ");

    /// <summary>
    /// The English inside a Japanese title, where there is any.
    ///
    /// Deleting the Japanese and keeping whatever letters are left over does not work: these
    /// titles are built as 「TV anime 『Show』 non-credit OP movie ／ OP theme: Artist「Song」」,
    /// and the Latin scattered through that is the artist's name and the letters "TV" and
    /// "OP". Swept together they read "TV OP OP Eve", which is not the name of anything.
    ///
    /// What does work is looking for an unbroken run of English, because an uploader who
    /// wrote one wrote it on purpose — "CHAINSAW MAN Opening", "Undead Unluck Noncredit
    /// Opening Movie". Runs of a single word are the debris, so they go; if what is left is
    /// too short to be a name, nothing is returned and the title is kept as it was.
    /// </summary>
    private static string CondenseLatin(string text)
    {
        var runs = Cjk().Split(text)
            .Select(run => run.Trim())
            .Where(run => run.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                             .Count(word => word.Any(char.IsLetter)) >= 2)
            .ToList();

        if (runs.Count == 0) return string.Empty;

        var joined = string.Join(" ", runs);

        var words = joined.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Count(word => word.Any(char.IsLetter));

        return words >= 3 ? joined : string.Empty;
    }

    /// <summary>Emoji and the decorative marks that come with them.</summary>
    private static string StripSymbols(string text)
    {
        var kept = new StringBuilder(text.Length);

        foreach (var rune in text.EnumerateRunes())
        {
            var category = System.Globalization.CharUnicodeInfo.GetUnicodeCategory(rune.Value);

            if (!rune.IsAscii &&
                category is System.Globalization.UnicodeCategory.OtherSymbol
                          or System.Globalization.UnicodeCategory.Format
                          or System.Globalization.UnicodeCategory.PrivateUse)
            {
                kept.Append(' ');
                continue;
            }

            kept.Append(rune);
        }

        return kept.ToString();
    }

    /// <summary>"ft. Somebody", to the end. The song is the same song without them.</summary>
    /// <summary>
    /// Each side of the "artist - title" dash separately. Cutting from the first "ft." to the
    /// end of the whole string turns "Maroon 5 Ft. Wiz Khalifa - Payphone" into "Maroon 5":
    /// the guest was named in the artist, and the song went with them.
    /// </summary>
    private static string StripFeatured(string text)
    {
        var at = text.IndexOf(" - ", StringComparison.Ordinal);
        if (at < 0) return Featured().Replace(text, string.Empty);

        var artist = Featured().Replace(text[..at], string.Empty).Trim();
        var rest = Featured().Replace(text[(at + 3)..], string.Empty).Trim();

        if (artist.Length == 0 || rest.Length == 0) return text;

        return $"{artist} - {rest}";
    }

    /// <summary>
    /// "Silhouette by KANA-BOON" — the performer, tacked on. Only where the title has no
    /// " - " of its own, because there "by" is far more likely to be part of the song.
    /// </summary>
    private static string StripByArtist(string text) =>
        text.Contains(" - ", StringComparison.Ordinal) ? text : ByArtist().Replace(text, string.Empty);

    /// <summary>
    /// A short trailing "- Lyrics" or "- Extreme HD". Short on purpose: a long tail is more
    /// likely to be part of the name than a note about the upload.
    /// </summary>
    private static string StripNoiseTail(string text)
    {
        for (var pass = 0; pass < 3; pass++)
        {
            var parts = text.Split(" - ", StringSplitOptions.None);

            // Three parts at least. With only two, the second one is the song — "Lana Del
            // Rey - Video Games" is not an artist with a note about the upload after it.
            if (parts.Length < 3) return text;

            var tail = parts[^1].Trim();
            var words = tail.Split(' ', StringSplitOptions.RemoveEmptyEntries);

            if (words.Length > 3) return text;
            if (!words.Any(w => NoiseWords.Contains(w.Trim(',', '.', '!', '?').ToLowerInvariant()))) return text;

            text = string.Join(" - ", parts[..^1]);
        }

        return text;
    }

    /// <summary>
    /// Tags left hanging on the end without a dash to hold them: "One Piece opening HD 1080p",
    /// "We Are! |Creditless|HD". Taken one word at a time, and never down to a single word,
    /// since something like "[Deleted video]" is the whole name rather than a tag on one.
    /// </summary>
    private static string StripTrailingNoise(string text)
    {
        char[] breaks = [' ', '|', '│', '｜', '/'];

        for (var pass = 0; pass < 4; pass++)
        {
            var trimmed = text.TrimEnd(' ', '|', '│', '｜', '/', '-', '–', '—');

            var at = trimmed.LastIndexOfAny(breaks);
            if (at <= 0) return text;

            var last = trimmed[(at + 1)..]
                .Trim('.', '!', '?', '"', '\'', '“', '”', '*', '~', '_', '＊')
                .ToLowerInvariant();
            if (last.Length == 0) return text;
            if (!NoiseWords.Contains(last) && !Resolution().IsMatch(last)) return text;

            var rest = trimmed[..at];
            if (rest.Split(breaks, StringSplitOptions.RemoveEmptyEntries).Length < 2) return text;

            text = rest;
        }

        return text;
    }

    /// <summary>
    /// The band that performed a theme, left on the end: "Opening|The Rumbling - SiM". Only
    /// for titles that are plainly a theme, where a trailing name is the performer rather
    /// than half of an "artist - song" title.
    /// </summary>
    private static string StripThemeArtist(string text)
    {
        if (!ThemeWord().IsMatch(text)) return text;

        var parts = text.Split(" - ", StringSplitOptions.None);
        if (parts.Length < 2) return text;

        var tail = parts[^1].Trim();
        if (ThemeWord().IsMatch(tail)) return text;
        if (tail.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length > 3) return text;

        // Only when the theme is already named. "Opening|The Rumbling - SiM" has its song,
        // so what follows is the band; "The Seven Deadly Sins Opening - Passionate Spectrum"
        // has not, so what follows is the song and dropping it throws away the name.
        var head = string.Join(" - ", parts[..^1]);
        var theme = ThemeWord().Matches(head).LastOrDefault();

        if (theme is null) return text;
        if (head[(theme.Index + theme.Length)..].Trim(' ', '-', '–', '—', ':', '|', '｜', '│').Length == 0)
        {
            return text;
        }

        return head;
    }

    /// <summary>
    /// "Pitbull, Ne-Yo - Time of Our Lives". Two artists is one artist field; the comma is
    /// punctuation the file name does not need. Titles keep their commas.
    /// </summary>
    private static string TidyArtistCommas(string text)
    {
        var at = text.IndexOf(" - ", StringComparison.Ordinal);
        if (at < 0) return text;

        var artist = text[..at].Replace(",", " ");
        return artist + text[at..];
    }

    /// <summary>Whatever the rules left behind: doubled spaces, dangling separators.</summary>
    private static string Tidy(string text)
    {
        text = Spaces().Replace(text, " ");
        text = DanglingDash().Replace(text, " - ");
        // Not the full stop: "Born in the U.S.A." ends in one and means it. Whether a name
        // is legal as a file name is settled later, by the part that knows about that.
        text = text.Trim(' ', '-', '–', '—', '│', '｜', ':', ',', '/', '|');

        return Spaces().Replace(text, " ").Trim();
    }

    // -----------------------------------------------------------------------

    [GeneratedRegex(@"(?<open>[\(\[\{【「『〈])(?<inner>[^\)\]\}】」』〉]*)[\)\]\}】」』〉]")]
    private static partial Regex BracketGroup();

    // Japanese and Chinese text, plus the fullwidth punctuation that travels with it. The
    // fullwidth bar is left alone: titles use it as a separator and one of them keeps it.
    [GeneratedRegex(@"[぀-ゟ゠-ヿ㐀-䶿一-鿿　-〿！-｛｝-ﾟ]")]
    private static partial Regex Cjk();

    // "with" is not on this list. "Stuck with U" is a song title, and cutting at the word
    // would leave "Stuck".
    [GeneratedRegex(@"\s*[\(\[]?\s*\b(?:feat|ft|featuring)\b\.?\s+.*$", RegexOptions.IgnoreCase)]
    private static partial Regex Featured();

    [GeneratedRegex(@"\s+by\s+\S.*$", RegexOptions.IgnoreCase)]
    private static partial Regex ByArtist();

    [GeneratedRegex(@"\b(?:the\s+)?final\s+season\b|\bseason\s*\d+\b|\bpart\s*\d+\b|\bcour\s*\d+\b", RegexOptions.IgnoreCase)]
    private static partial Regex SeasonPart();

    // "Opening 20" -> "Opening". Which number it is belongs to the show, not the song, and
    // the numbering only makes sense next to the season that has just been dropped.
    [GeneratedRegex(@"(?<word>(?i:\b(?:opening|ending)\b)|\bOP\b|\bED\b)\.?\s*\d+\b")]
    private static partial Regex ThemeNumber();

    // Spelt out, and only spelt out. "OP" and "ED" are two of the commonest initials there
    // are — Ed Sheeran is on the very playlist this was written for — so they count only
    // inside a bracket, in capitals, where they can hardly be anything else.
    [GeneratedRegex(@"\b(?:opening|ending|theme)\b", RegexOptions.IgnoreCase)]
    private static partial Regex ThemeWord();

    [GeneratedRegex(@"\bOP\b|\bED\b|(?i:\b(?:opening|ending)\b)")]
    private static partial Regex ThemeMark();

    [GeneratedRegex(@"\s+-\s+(Opening|Ending)\b", RegexOptions.IgnoreCase)]
    private static partial Regex ThemeAfterDash();

    [GeneratedRegex(@"^\d{3,4}p?$|^\d+kbps$")]
    private static partial Regex Resolution();

    // The label, signed off on the end: "Cheerleader (Felix Jaehn Remix) Ultra Records". Two
    // words, because "Records" on its own is a word a song could end on.
    [GeneratedRegex(@"\s+[\w&'’.-]+\s+(?:Records|Recordings|Music\s+Group|Entertainment)\s*$",
                    RegexOptions.IgnoreCase)]
    private static partial Regex LabelTail();

    [GeneratedRegex(@"\s{2,}")]
    private static partial Regex Spaces();

    [GeneratedRegex(@"\s+-\s+-\s+")]
    private static partial Regex DanglingDash();
}
