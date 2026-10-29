namespace RevisionsEditor.Core.Checks;

/// <summary>
/// Finds text for the editor's search: optionally matching case and whole words. Curly and straight quotes,
/// apostrophes and the various dashes are treated as the same character, and a run of spaces matches any
/// spacing, so "don't" finds "don’t" and "10-20" finds "10–20".
/// </summary>
public static class TextFinder
{
    /// <summary>The matches of <paramref name="query"/> in <paramref name="text"/>, as (start, length), in order and not overlapping.</summary>
    public static List<(int Start, int Length)> FindAll(string text, string query, bool matchCase = false, bool wholeWord = false)
    {
        var matches = new List<(int, int)>();
        if (string.IsNullOrEmpty(text) || string.IsNullOrWhiteSpace(query))
        {
            return matches;
        }
        string pattern = Fold(query.Trim(), matchCase);
        string haystack = Fold(text, matchCase);
        for (int i = 0; i <= haystack.Length - 1;)
        {
            int length = MatchAt(haystack, i, pattern);
            if (length > 0 && (!wholeWord || IsWordBoundary(text, i - 1, i) && IsWordBoundary(text, i + length - 1, i + length)))
            {
                matches.Add((i, length));
                i += length;
            }
            else
            {
                i++;
            }
        }
        return matches;
    }

    /// <summary>The length matched at <paramref name="start"/> (spaces in the pattern match one or more spaces), or 0.</summary>
    private static int MatchAt(string text, int start, string pattern)
    {
        int t = start;
        for (int p = 0; p < pattern.Length; p++)
        {
            if (pattern[p] == ' ')
            {
                while (p + 1 < pattern.Length && pattern[p + 1] == ' ')
                {
                    p++;
                }
                if (t >= text.Length || text[t] != ' ')
                {
                    return 0;
                }
                while (t < text.Length && text[t] == ' ')
                {
                    t++;
                }
                continue;
            }
            if (t >= text.Length || text[t] != pattern[p])
            {
                return 0;
            }
            t++;
        }
        return t - start;
    }

    private static bool IsWordBoundary(string text, int before, int after)
        => before < 0 || after >= text.Length || !(IsWordChar(text[before]) && IsWordChar(text[after]));

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_';

    /// <summary>Same length as the input: typographic variants folded to one character, case folded unless matching it.</summary>
    private static string Fold(string text, bool matchCase)
    {
        var chars = text.ToCharArray();
        for (int i = 0; i < chars.Length; i++)
        {
            char c = chars[i] switch
            {
                '‘' or '’' or '‚' or '′' or '`' => '\'',
                '“' or '”' or '„' or '″' => '"',
                '‐' or '‑' or '‒' or '–' or '—' or '−' => '-',
                ' ' or ' ' or ' ' or '\t' or '\n' or '\r' => ' ',
                var other => other
            };
            chars[i] = matchCase ? c : char.ToLowerInvariant(c);
        }
        return new string(chars);
    }
}
