using System.Text.RegularExpressions;
using ProjectSoundboard.Core.Models;

namespace ProjectSoundboard.App.Services;

/// <summary>A sound already in the library that a download looks like.</summary>
public sealed record AlreadyHave(string Name, string FilePath, TimeSpan Duration);

/// <summary>
/// Whether a download is something the library already has.
///
/// Checked before anything is fetched, so the only things to go on are the title and the
/// length — the file itself does not exist yet to be compared. That turns out to be enough:
/// the same song downloaded twice has the same length to within a second, and the titles
/// agree once the Official Video and Lyrics padding is off both of them, which is exactly
/// what the naming rules already do.
///
/// Being wrong in either direction is mild. A missed duplicate is what happens today, and a
/// false one is a warning next to a track that is still ticked and still downloads. So the
/// matching is deliberately strict — same name and same length — rather than clever.
/// </summary>
public sealed partial class DuplicateIndex
{
    private readonly Dictionary<string, List<AlreadyHave>> _byName = new(StringComparer.Ordinal);

    public DuplicateIndex(IEnumerable<SoundEntry> sounds)
    {
        foreach (var sound in sounds)
        {
            if (sound.IsMissing) continue;

            var key = Key(sound.DisplayName);
            if (key.Length == 0) continue;

            if (!_byName.TryGetValue(key, out var list)) _byName[key] = list = new List<AlreadyHave>();

            list.Add(new AlreadyHave(
                sound.DisplayName,
                sound.FilePath,
                TimeSpan.FromSeconds(sound.DurationSeconds)));
        }
    }

    public int Count => _byName.Count;

    /// <summary>
    /// The sound already in the library that this one looks like, or null.
    ///
    /// Lengths have to agree where both are known. Two different songs share a name often
    /// enough — covers, remakes, and every "Intro" ever recorded — and the length is what
    /// separates them.
    /// </summary>
    public AlreadyHave? Find(string? name, TimeSpan duration)
    {
        var key = Key(name);
        if (key.Length == 0) return null;

        if (!_byName.TryGetValue(key, out var candidates)) return null;

        foreach (var candidate in candidates)
        {
            if (duration <= TimeSpan.Zero || candidate.Duration <= TimeSpan.Zero) return candidate;

            if (Math.Abs((candidate.Duration - duration).TotalSeconds) <= 3) return candidate;
        }

        return null;
    }

    /// <summary>
    /// A name reduced to what two copies of the same song would have in common: the padding
    /// off, the punctuation and case gone.
    /// </summary>
    public static string Key(string? name)
    {
        var cleaned = TrackNaming.Clean(name);
        return NotLetters().Replace(cleaned, string.Empty).ToLowerInvariant();
    }

    [GeneratedRegex(@"[^a-zA-Z0-9]")]
    private static partial Regex NotLetters();
}
