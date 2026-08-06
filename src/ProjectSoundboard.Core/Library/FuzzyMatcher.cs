namespace ProjectSoundboard.Core.Library;

/// <summary>
/// Subsequence scorer in the spirit of Sublime Text's "goto anything": every query
/// character must appear in order, and matches score higher when they are consecutive,
/// at a word boundary, or at the very start of the candidate.
/// </summary>
public static class FuzzyMatcher
{
    public const int NoMatch = int.MinValue;

    private const int ScoreExactMatch = 1000;
    private const int ScorePrefix = 400;
    private const int ScoreContains = 250;
    private const int ScoreCharMatch = 12;
    private const int ScoreConsecutive = 18;
    private const int ScoreWordBoundary = 30;
    private const int PenaltyLeadingChar = -3;
    private const int PenaltyUnmatchedTail = -1;

    /// <summary>
    /// Returns a relevance score, or <see cref="NoMatch"/> when the query does not match.
    /// Both arguments are compared case-insensitively.
    /// </summary>
    public static int Score(string candidate, string query, bool fuzzy = true)
    {
        if (string.IsNullOrEmpty(query)) return 0;
        if (string.IsNullOrEmpty(candidate)) return NoMatch;

        if (candidate.Equals(query, StringComparison.OrdinalIgnoreCase))
            return ScoreExactMatch;

        if (candidate.StartsWith(query, StringComparison.OrdinalIgnoreCase))
            return ScorePrefix + Math.Max(0, 100 - candidate.Length);

        var containsIndex = candidate.IndexOf(query, StringComparison.OrdinalIgnoreCase);
        if (containsIndex >= 0)
            return ScoreContains - containsIndex + Math.Max(0, 60 - candidate.Length);

        return fuzzy ? Subsequence(candidate, query) : NoMatch;
    }

    private static int Subsequence(string candidate, string query)
    {
        var score = 0;
        var ci = 0;
        var consecutive = 0;

        for (var qi = 0; qi < query.Length; qi++)
        {
            var qc = char.ToLowerInvariant(query[qi]);
            if (char.IsWhiteSpace(qc)) continue;

            var found = false;
            while (ci < candidate.Length)
            {
                var cc = char.ToLowerInvariant(candidate[ci]);
                if (cc == qc)
                {
                    score += ScoreCharMatch;

                    if (consecutive > 0) score += ScoreConsecutive * Math.Min(consecutive, 4);
                    if (ci == 0 || IsBoundary(candidate, ci)) score += ScoreWordBoundary;
                    if (qi == 0) score += PenaltyLeadingChar * ci;

                    consecutive++;
                    ci++;
                    found = true;
                    break;
                }

                consecutive = 0;
                ci++;
            }

            if (!found) return NoMatch;
        }

        score += PenaltyUnmatchedTail * (candidate.Length - ci);
        return score;
    }

    private static bool IsBoundary(string text, int index)
    {
        var prev = text[index - 1];
        if (prev is ' ' or '_' or '-' or '.' or '/' or '\\' or '(' or '[') return true;
        // camelCase / PascalCase transition
        return char.IsLower(prev) && char.IsUpper(text[index]);
    }
}
