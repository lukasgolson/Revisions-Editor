using System.Text;
using System.Text.RegularExpressions;

namespace RevisionsEditor.Core.Bibliography;

/// <summary>Helpers for creating citation keys and editing raw BibTeX entry text.</summary>
public static class BibTeXWriter
{
    private static readonly Regex EntryHead = new(@"@(?<type>[A-Za-z]+)(?<ws1>\s*)(?<open>[{(])(?<ws2>\s*)(?<key>[^,\s{}()=]*)", RegexOptions.Compiled);
    private static readonly Regex TitleWord = new(@"[\p{L}\p{Nd}]+", RegexOptions.Compiled);

    private static readonly HashSet<string> FunctionWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "a", "an", "the", "of", "on", "in", "into", "onto", "at", "to", "for", "from", "by", "with", "without", "within",
        "and", "or", "but", "nor", "as", "via", "vs", "versus", "is", "are", "be", "do", "does", "using", "towards", "toward",
        "about", "over", "under", "between", "among", "through", "after", "before", "its", "their", "new",
        "le", "la", "les", "un", "une", "des", "du", "de", "der", "die", "das", "el", "los",
    };

    /// <summary>Suggests a Better BibTeX-style key: first author's family name (lowercase, ASCII) + year + first significant
    /// title word, e.g. "white2016remote"; appends a, b, c… when the key already exists (case-insensitive).</summary>
    public static string SuggestKey(BibEntry e, IReadOnlySet<string> existingKeys)
    {
        var names = e.Authors.Count > 0 ? e.Authors : e.Editors;
        var first = names.FirstOrDefault(n => !n.IsOthers);
        string auth;
        if (first is null) auth = "anon";
        else if (first.IsInstitution) auth = FirstSignificantWord(first.Family) ?? "anon";
        else auth = Clean(first.Family);
        if (auth.Length == 0) auth = "anon";

        var year = e.Year is { } y ? new string(y.Where(char.IsAsciiDigit).Take(4).ToArray()) : "";
        var word = FirstSignificantWord(e.Title) ?? "";
        var baseKey = auth + year + word;

        var existing = new HashSet<string>(existingKeys, StringComparer.OrdinalIgnoreCase);
        if (!existing.Contains(baseKey)) return baseKey;
        for (var n = 0; ; n++)
        {
            var candidate = baseKey + Suffix(n);
            if (!existing.Contains(candidate)) return candidate;
        }
    }

    /// <summary>Rewrites the key of the first entry in raw BibTeX text (e.g. from doi.org content negotiation); other text is unchanged.</summary>
    public static string ReplaceKey(string bibtexEntryText, string newKey)
    {
        if (string.IsNullOrEmpty(bibtexEntryText)) return bibtexEntryText ?? "";
        foreach (Match m in EntryHead.Matches(bibtexEntryText))
        {
            var type = m.Groups["type"].Value.ToLowerInvariant();
            if (type is "comment" or "preamble" or "string") continue;
            var key = m.Groups["key"];
            return bibtexEntryText[..key.Index] + newKey + bibtexEntryText[(key.Index + key.Length)..];
        }
        return bibtexEntryText;
    }

    private static string Suffix(int n)
    {
        // 0 → a, 25 → z, 26 → aa …
        var sb = new StringBuilder();
        n++;
        while (n > 0)
        {
            n--;
            sb.Insert(0, (char)('a' + n % 26));
            n /= 26;
        }
        return sb.ToString();
    }

    private static string Clean(string s) =>
        new(BibText.AsciiFold(s).ToLowerInvariant().Where(char.IsAsciiLetterOrDigit).ToArray());

    private static string? FirstSignificantWord(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return null;
        var plain = title.Replace("*", "", StringComparison.Ordinal);
        foreach (Match m in TitleWord.Matches(plain))
        {
            if (FunctionWords.Contains(m.Value)) continue;
            var w = Clean(m.Value);
            if (w.Length > 0) return w;
        }
        return null;
    }
}
