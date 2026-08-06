using ProjectSoundboard.Core.Models;
using ProjectSoundboard.Core.Storage;

namespace ProjectSoundboard.Core.Library;

/// <summary>What the library view is currently filtered to.</summary>
public sealed class SearchQuery
{
    public string Text { get; set; } = string.Empty;

    /// <summary>Restrict to this group and (optionally) its children. Null = all sounds.</summary>
    public string? GroupId { get; set; }
    public bool IncludeSubGroups { get; set; } = true;

    public bool FavoritesOnly { get; set; }
    public bool MissingOnly { get; set; }
    public string? Tag { get; set; }
    public SortMode Sort { get; set; } = SortMode.Name;
    public bool Descending { get; set; }
}

public sealed class SearchService
{
    private readonly SettingsService _settings;
    private readonly LibraryService _library;
    private readonly Random _random = new();

    public SearchService(SettingsService settings, LibraryService library)
    {
        _settings = settings;
        _library = library;
    }

    public IReadOnlyList<SoundEntry> Execute(SearchQuery query)
    {
        var options = _settings.Settings.Search;
        var source = (IEnumerable<SoundEntry>)_library.Sounds;

        // ---- structural filters -------------------------------------------
        if (query.GroupId is not null)
        {
            if (query.IncludeSubGroups)
            {
                var tree = _library.GetGroupTree(query.GroupId);
                source = source.Where(s => s.GroupId is not null && tree.Contains(s.GroupId));
            }
            else
            {
                source = source.Where(s => s.GroupId == query.GroupId);
            }
        }

        if (query.FavoritesOnly) source = source.Where(s => s.IsFavorite);
        if (query.MissingOnly) source = source.Where(s => s.IsMissing || s.IsBroken);

        if (!string.IsNullOrWhiteSpace(query.Tag))
            source = source.Where(s => s.Tags.Contains(query.Tag!, StringComparer.OrdinalIgnoreCase));

        // ---- text search ---------------------------------------------------
        var text = query.Text?.Trim() ?? string.Empty;
        if (text.Length == 0)
            return Sort(source, query).ToArray();

        var fuzzy = options.FuzzyMatching;
        var scored = new List<(SoundEntry Entry, int Score)>();

        foreach (var entry in source)
        {
            var best = FuzzyMatcher.NoMatch;

            if (options.SearchDisplayName)
                best = Math.Max(best, FuzzyMatcher.Score(entry.DisplayName, text, fuzzy));

            if (options.SearchFileName && best < 1000)
            {
                // Slight discount so a display-name hit outranks a raw filename hit.
                var s = FuzzyMatcher.Score(entry.OriginalNameWithoutExtension, text, fuzzy);
                if (s != FuzzyMatcher.NoMatch) best = Math.Max(best, s - 5);
            }

            if (options.SearchTags)
            {
                foreach (var tag in entry.Tags)
                {
                    var s = FuzzyMatcher.Score(tag, text, fuzzy);
                    if (s != FuzzyMatcher.NoMatch) best = Math.Max(best, s - 20);
                }
            }

            if (options.SearchGroup && entry.GroupId is not null)
            {
                var group = _library.GetGroup(entry.GroupId);
                if (group is not null)
                {
                    var s = FuzzyMatcher.Score(group.Name, text, fuzzy);
                    if (s != FuzzyMatcher.NoMatch) best = Math.Max(best, s - 30);
                }
            }

            if (best == FuzzyMatcher.NoMatch) continue;

            // Nudge favourites and frequently used sounds up when scores are close.
            if (entry.IsFavorite) best += 15;
            best += Math.Min(entry.PlayCount, 20);

            scored.Add((entry, best));
        }

        // Relevance wins unless the user picked an explicit sort other than Name.
        if (query.Sort is SortMode.Name)
            return scored.OrderByDescending(x => x.Score)
                         .ThenBy(x => x.Entry.DisplayName, StringComparer.OrdinalIgnoreCase)
                         .Select(x => x.Entry).ToArray();

        return Sort(scored.Select(x => x.Entry), query).ToArray();
    }

    private IEnumerable<SoundEntry> Sort(IEnumerable<SoundEntry> source, SearchQuery query)
    {
        IEnumerable<SoundEntry> sorted = query.Sort switch
        {
            SortMode.Name => source.OrderBy(s => s.DisplayName, StringComparer.OrdinalIgnoreCase),
            SortMode.RecentlyAdded => source.OrderByDescending(s => s.AddedUtc),
            SortMode.RecentlyPlayed => source.OrderByDescending(s => s.LastPlayedUtc ?? DateTime.MinValue),
            SortMode.MostPlayed => source.OrderByDescending(s => s.PlayCount),
            SortMode.Duration => source.OrderBy(s => s.DurationSeconds),
            SortMode.Random => source.OrderBy(_ => _random.Next()),
            _ => source
        };

        return query.Descending && query.Sort != SortMode.Random ? sorted.Reverse() : sorted;
    }

    public void RememberSearch(string text)
    {
        var options = _settings.Settings.Search;
        if (!options.RememberRecentSearches) return;

        text = text.Trim();
        if (text.Length < 2) return;

        options.RecentSearches.RemoveAll(s => string.Equals(s, text, StringComparison.OrdinalIgnoreCase));
        options.RecentSearches.Insert(0, text);

        var limit = Math.Max(1, options.RecentSearchLimit);
        if (options.RecentSearches.Count > limit)
            options.RecentSearches.RemoveRange(limit, options.RecentSearches.Count - limit);

        _settings.MarkDirty();
    }

    /// <summary>Every distinct tag in the library, ordered by how often it is used.</summary>
    public IReadOnlyList<string> AllTags()
    {
        return _library.Sounds
            .SelectMany(s => s.Tags)
            .GroupBy(t => t, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.Key)
            .ToArray();
    }
}
