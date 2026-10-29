using System.Text.RegularExpressions;

namespace RevisionsEditor.Core.Bibliography;

/// <summary>One cited work inside a citation: key, optional prefix ("see"), optional locator ("p. 12") and the year-only flag (<c>-@key</c>).</summary>
public sealed record CitationItem(string Key, string? Prefix, string? Locator, bool SuppressAuthor);

/// <summary>A citation found in block text. Start/Length cover the whole <c>[...]</c> group, or the <c>@key</c> of a narrative citation.</summary>
public sealed record Citation(int Start, int Length, bool Narrative, IReadOnlyList<CitationItem> Items);

/// <summary>Finds Pandoc citations (<c>[@a; see @b, p. 3]</c>, <c>[-@a]</c>, narrative <c>@a</c>) in Markdown-like block text.</summary>
public static class CitationSyntax
{
    private const string KeyPattern = @"[\p{L}\p{Nd}_](?:[\p{L}\p{Nd}_:.#$%&+?<>~/-]*[\p{L}\p{Nd}])?";

    /// <summary>Innermost bracket group not escaped with a backslash.</summary>
    private static readonly Regex BracketGroup = new(@"(?<!\\)\[(?<body>[^\[\]]*)\]", RegexOptions.Compiled);

    private static readonly Regex ItemKey = new(@"(?<![\p{L}\p{Nd}_@\\])(?<dash>-)?@(?<key>" + KeyPattern + ")", RegexOptions.Compiled);

    private static readonly Regex NarrativeKey = new(@"(?<![\p{L}\p{Nd}_@\\])@(?<key>" + KeyPattern + ")", RegexOptions.Compiled);

    /// <summary>Link destinations "](...)" and bare URLs, where an @ is never a citation.</summary>
    private static readonly Regex UrlLike = new(@"\]\([^)\s]*\)|(?:https?|ftp)://[^\s<>\]\)]+|www\.[^\s<>\]\)]+", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex CrossRefPrefix = new(@"^(?:fig|tbl|eq|sec):", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>True when the key is a cross-reference label (fig:, tbl:, eq:, sec:) rather than a citation key.</summary>
    public static bool IsCrossReference(string key) => CrossRefPrefix.IsMatch(key);

    /// <summary>Returns citations in text order. A bracket group is a citation only when every <c>;</c>-separated part holds an
    /// <c>@key</c> and at least one key is a citekey; a group mixing cross-references and citekeys (<c>[@fig:a; @white2016]</c>)
    /// returns only the citekey items, but its Start/Length still cover the whole group (so replacing it also replaces the
    /// cross-references). Emails, <c>\@</c>, links <c>[..](url)</c> and cross-references are ignored.</summary>
    public static IReadOnlyList<Citation> Find(string text)
    {
        var result = new List<Citation>();
        if (string.IsNullOrEmpty(text) || !text.Contains('@', StringComparison.Ordinal)) return result;
        var consumed = new List<(int start, int end)>();
        foreach (Match u in UrlLike.Matches(text)) consumed.Add((u.Index, u.Index + u.Length));

        foreach (Match g in BracketGroup.Matches(text))
        {
            var afterIdx = g.Index + g.Length;
            if (afterIdx < text.Length && text[afterIdx] == '(') continue; // Markdown link [text](url)
            var body = g.Groups["body"];
            if (!body.Value.Contains('@', StringComparison.Ordinal)) continue;

            var items = new List<CitationItem>();
            var allPartsHaveKeys = true;
            var anyCrossRef = false;
            foreach (var part in body.Value.Split(';'))
            {
                var m = ItemKey.Match(part);
                if (!m.Success)
                {
                    allPartsHaveKeys = false;
                    break;
                }
                var key = m.Groups["key"].Value;
                if (IsCrossReference(key))
                {
                    anyCrossRef = true;
                    continue;
                }
                var prefix = part[..m.Index].Trim();
                var rest = part[(m.Index + m.Length)..].Trim();
                if (rest.StartsWith(',')) rest = rest[1..].Trim();
                items.Add(new CitationItem(key, prefix.Length == 0 ? null : prefix, rest.Length == 0 ? null : rest,
                    m.Groups["dash"].Success));
            }
            if (!allPartsHaveKeys) continue;
            if (items.Count == 0)
            {
                if (anyCrossRef) consumed.Add((g.Index, afterIdx));
                continue;
            }
            result.Add(new Citation(g.Index, g.Length, false, items));
            consumed.Add((g.Index, afterIdx));
        }

        foreach (Match m in NarrativeKey.Matches(text))
        {
            if (consumed.Any(r => m.Index >= r.start && m.Index < r.end)) continue;
            var key = m.Groups["key"].Value;
            if (IsCrossReference(key)) continue;
            result.Add(new Citation(m.Index, m.Length, true, [new CitationItem(key, null, null, false)]));
        }

        result.Sort((a, b) => a.Start.CompareTo(b.Start));
        return result;
    }
}
