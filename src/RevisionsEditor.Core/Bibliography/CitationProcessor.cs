using System.Globalization;
using System.Text;

namespace RevisionsEditor.Core.Bibliography;

/// <summary>A citation and the text that replaces it in the exported manuscript.</summary>
public sealed record RenderedCitation(Citation Citation, string Text);

/// <summary>One reference-list entry. Label is "1." / "[1]" for numeric styles and "" for author-year styles.</summary>
public sealed record FormattedReference(string Key, string Label, string Text)
{
    /// <summary>Label, a tab, and the text (or just the text when there is no label).</summary>
    public string FullText => Label.Length == 0 ? Text : Label + "\t" + Text;
}

/// <summary>Output of <see cref="CitationProcessor.Process"/>.</summary>
public sealed class CitationResult
{
    internal CitationResult(IReadOnlyList<IReadOnlyList<RenderedCitation>> perText, IReadOnlyList<FormattedReference> references,
        IReadOnlyList<string> missingKeys, IReadOnlyList<string> citedKeys)
    {
        PerText = perText;
        References = references;
        MissingKeys = missingKeys;
        CitedKeys = citedKeys;
    }

    /// <summary>For each input text index: the citations found and their formatted in-text replacement.</summary>
    public IReadOnlyList<IReadOnlyList<RenderedCitation>> PerText { get; }

    /// <summary>Reference list in the style's order (alphabetical or by number).</summary>
    public IReadOnlyList<FormattedReference> References { get; }

    /// <summary>Keys cited but not in the library, distinct, in first-seen order.</summary>
    public IReadOnlyList<string> MissingKeys { get; }

    /// <summary>All distinct cited keys in first-citation order (library spelling for found keys; includes missing keys).</summary>
    public IReadOnlyList<string> CitedKeys { get; }

    /// <summary>Applies the replacements to one text (right to left so indexes stay valid). The text must be the one processed at that index.</summary>
    public string Apply(int textIndex, string text)
    {
        if (textIndex < 0 || textIndex >= PerText.Count || string.IsNullOrEmpty(text)) return text;
        var sb = new StringBuilder(text);
        foreach (var r in PerText[textIndex].OrderByDescending(r => r.Citation.Start))
        {
            var c = r.Citation;
            if (c.Start < 0 || c.Start + c.Length > sb.Length) continue;
            sb.Remove(c.Start, c.Length).Insert(c.Start, r.Text);
        }
        return sb.ToString();
    }
}

/// <summary>Formats all citations of a manuscript and its reference list in one pass, so numbering and a/b year suffixes are consistent.</summary>
public sealed class CitationProcessor
{
    private readonly CitationStyle _style;
    private readonly IReadOnlyDictionary<string, BibEntry> _library;

    /// <summary>Creates a processor for a style and a library (key → entry; a case-insensitive dictionary is recommended).</summary>
    public CitationProcessor(CitationStyle style, IReadOnlyDictionary<string, BibEntry> library)
    {
        _style = style;
        _library = library;
    }

    /// <summary>Processes all citations of the manuscript (texts in document order) in one pass, so numbering and a/b suffixes are consistent.</summary>
    public CitationResult Process(IReadOnlyList<string> textsInDocumentOrder)
    {
        var found = textsInDocumentOrder.Select(t => CitationSyntax.Find(t ?? "")).ToList();

        var order = new List<BibEntry>();
        var orderSet = new HashSet<BibEntry>(ReferenceEqualityComparer.Instance);
        var missing = new List<string>();
        var missingSet = new HashSet<string>(StringComparer.Ordinal);
        var cited = new List<string>();
        foreach (var item in found.SelectMany(list => list).SelectMany(c => c.Items))
        {
            var e = Lookup(item.Key);
            if (e is null)
            {
                if (missingSet.Add(item.Key))
                {
                    missing.Add(item.Key);
                    cited.Add(item.Key);
                }
            }
            else if (orderSet.Add(e))
            {
                order.Add(e);
                cited.Add(e.Key);
            }
        }

        var numbers = new Dictionary<BibEntry, int>(ReferenceEqualityComparer.Instance);
        for (var i = 0; i < order.Count; i++) numbers[order[i]] = i + 1;
        var yearLabels = ComputeYearLabels(order);

        var ctx = new RenderContext(this, numbers, yearLabels);
        var perText = found.Select(list => (IReadOnlyList<RenderedCitation>)list.Select(c => new RenderedCitation(c, ctx.Render(c))).ToList()).ToList();

        List<FormattedReference> refs;
        if (_style.Mode == CitationMode.Numeric)
        {
            refs = order.Select(e => new FormattedReference(e.Key, _style.FormatLabel(numbers[e]), _style.FormatReference(e, yearLabels[e]))).ToList();
        }
        else
        {
            refs = order.OrderBy(e => e, Comparer<BibEntry>.Create((a, b) => CompareForList(a, b, yearLabels)))
                .Select(e => new FormattedReference(e.Key, "", _style.FormatReference(e, yearLabels[e]))).ToList();
        }
        return new CitationResult(perText, refs, missing, cited);
    }

    private BibEntry? Lookup(string key) => _library.TryGetValue(key, out var e) ? e : null;

    private Dictionary<BibEntry, string> ComputeYearLabels(List<BibEntry> entries)
    {
        var labels = new Dictionary<BibEntry, string>(ReferenceEqualityComparer.Instance);
        foreach (var e in entries) labels[e] = e.Year ?? _style.NoDateText;
        if (_style.Mode == CitationMode.Numeric) return labels;
        var groups = entries.GroupBy(e => (BibText.SortFold(_style.FormatAuthorsShort(e, false)), BibText.SortFold(e.Year ?? "")));
        foreach (var g in groups)
        {
            var list = g.OrderBy(e => BibText.SortFold(e.Title), StringComparer.Ordinal).ThenBy(e => e.Key, StringComparer.Ordinal).ToList();
            if (list.Count < 2) continue;
            for (var i = 0; i < list.Count; i++)
            {
                var y = list[i].Year;
                labels[list[i]] = (y ?? _style.NoDateText + "-") + Letter(i);
            }
        }
        return labels;
    }

    private static string Letter(int i)
    {
        var sb = new StringBuilder();
        i++;
        while (i > 0)
        {
            i--;
            sb.Insert(0, (char)('a' + i % 26));
            i /= 26;
        }
        return sb.ToString();
    }

    /// <summary>Sort key for names: family, particle, given of every author (or editors, or the title), accent- and case-insensitive.</summary>
    private static string NameSortKey(BibEntry e)
    {
        var names = (e.Authors.Count > 0 ? e.Authors : e.Editors).Where(n => !n.IsOthers).ToList();
        if (names.Count == 0) return BibText.SortFold(e.Title ?? e.Key);
        return string.Join("\u0002", names.Select(n =>
            BibText.SortFold(n.Family) + "\u0001" + BibText.SortFold(n.Particle) + "\u0001" + BibText.SortFold(n.InitialsWithPeriods(false))
            + "\u0001" + BibText.SortFold(n.Given)));
    }

    private static int YearValue(BibEntry e)
    {
        var y = e.Year;
        if (y is null) return -1;
        var digits = new string(y.Where(char.IsAsciiDigit).Take(4).ToArray());
        return digits.Length == 4 ? int.Parse(digits, CultureInfo.InvariantCulture) : int.MaxValue; // "in press" sorts last
    }

    private static int CompareForList(BibEntry a, BibEntry b, Dictionary<BibEntry, string> labels)
    {
        var c = string.CompareOrdinal(NameSortKey(a), NameSortKey(b));
        if (c != 0) return c;
        c = YearValue(a).CompareTo(YearValue(b));
        if (c != 0) return c;
        c = string.CompareOrdinal(labels[a], labels[b]);
        if (c != 0) return c;
        c = string.CompareOrdinal(BibText.SortFold(a.Title), BibText.SortFold(b.Title));
        return c != 0 ? c : string.CompareOrdinal(a.Key, b.Key);
    }

    private sealed class RenderContext(CitationProcessor p, Dictionary<BibEntry, int> numbers, Dictionary<BibEntry, string> years)
    {
        private CitationStyle S => p._style;

        private static string Missing(string key, bool numeric) => numeric ? "**?" + key + "**" : "**??" + key + "**";

        public string Render(Citation c)
        {
            if (c.Items.Count == 0) return "";
            return S.Mode == CitationMode.Numeric ? RenderNumeric(c) : RenderAuthorYear(c);
        }

        private string RenderAuthorYear(Citation c)
        {
            if (c.Narrative)
            {
                var it = c.Items[0];
                var e = p.Lookup(it.Key);
                if (e is null) return Missing(it.Key, false);
                var loc = it.Locator is null ? "" : ", " + FormatLocator(it.Locator);
                return S.FormatAuthorsShort(e, true) + " (" + years[e] + loc + ")";
            }

            var items = c.Items.Select(i => (item: i, entry: p.Lookup(i.Key))).ToList();
            if (S.ItemOrder != CitationItemOrder.AsWritten)
            {
                var keyed = items.Select((x, idx) => (x, idx)).ToList();
                keyed.Sort((a, b) =>
                {
                    if (a.x.entry is null || b.x.entry is null)
                        return a.x.entry is null == b.x.entry is null ? a.idx.CompareTo(b.idx) : a.x.entry is null ? 1 : -1;
                    var r = S.ItemOrder == CitationItemOrder.Chronological
                        ? YearValue(a.x.entry).CompareTo(YearValue(b.x.entry))
                        : string.CompareOrdinal(NameSortKey(a.x.entry), NameSortKey(b.x.entry));
                    if (r == 0) r = S.ItemOrder == CitationItemOrder.Chronological
                        ? string.CompareOrdinal(NameSortKey(a.x.entry), NameSortKey(b.x.entry))
                        : YearValue(a.x.entry).CompareTo(YearValue(b.x.entry));
                    if (r == 0) r = string.CompareOrdinal(years[a.x.entry], years[b.x.entry]);
                    return r != 0 ? r : a.idx.CompareTo(b.idx);
                });
                items = keyed.Select(k => k.x).ToList();
            }

            var chunks = new List<(string? author, StringBuilder text, bool closed)>();
            foreach (var (item, entry) in items)
            {
                if (entry is null)
                {
                    var t = new StringBuilder();
                    if (item.Prefix is not null) t.Append(item.Prefix).Append(' ');
                    t.Append(Missing(item.Key, false));
                    chunks.Add((null, t, true));
                    continue;
                }
                var author = item.SuppressAuthor ? null : S.FormatAuthorsShort(entry, false);
                var plain = item.Prefix is null && item.Locator is null && author is not null;
                if (plain && chunks.Count > 0 && !chunks[^1].closed && chunks[^1].author == author)
                {
                    chunks[^1].text.Append(S.SameAuthorYearSeparator).Append(years[entry]);
                    continue;
                }
                var sb = new StringBuilder();
                if (item.Prefix is not null) sb.Append(item.Prefix).Append(' ');
                if (author is not null) sb.Append(author).Append(S.AuthorYearSeparator);
                sb.Append(years[entry]);
                if (item.Locator is not null) sb.Append(", ").Append(FormatLocator(item.Locator));
                chunks.Add((author, sb, !plain));
            }
            return "(" + string.Join(S.ItemSeparator, chunks.Select(ch => ch.text.ToString())) + ")";
        }

        private string RenderNumeric(Citation c)
        {
            if (c.Narrative)
            {
                var it = c.Items[0];
                var e = p.Lookup(it.Key);
                if (e is null) return Missing(it.Key, false);
                return S.FormatAuthorsShort(e, true) + " [" + numbers[e].ToString(CultureInfo.InvariantCulture) + "]";
            }

            var outerPrefix = c.Items[0].Prefix;
            var entries = new List<(int? n, CitationItem item)>();
            var seen = new HashSet<int>();
            for (var i = 0; i < c.Items.Count; i++)
            {
                var it = i == 0 && outerPrefix is not null ? c.Items[0] with { Prefix = null } : c.Items[i];
                var e = p.Lookup(it.Key);
                if (e is null) entries.Add((null, it));
                else if (seen.Add(numbers[e])) entries.Add((numbers[e], it));
            }
            entries = entries.OrderBy(x => x.n ?? int.MaxValue).ToList();

            var pieces = new List<string>();
            var run = new List<int>();
            void FlushRun()
            {
                if (run.Count == 0) return;
                var start = 0;
                while (start < run.Count)
                {
                    var end = start;
                    while (end + 1 < run.Count && run[end + 1] == run[end] + 1) end++;
                    if (end - start >= 2)
                    {
                        pieces.Add(S.BracketPerItem ? $"[{run[start]}]–[{run[end]}]" : $"{run[start]}–{run[end]}");
                    }
                    else
                    {
                        for (var k = start; k <= end; k++) pieces.Add(S.BracketPerItem ? $"[{run[k]}]" : run[k].ToString(CultureInfo.InvariantCulture));
                    }
                    start = end + 1;
                }
                run.Clear();
            }

            var singleWithLocator = entries.Count == 1 && entries[0].item.Locator is not null && !S.BracketPerItem;
            foreach (var (n, item) in entries)
            {
                if (n is null)
                {
                    FlushRun();
                    var m = Missing(item.Key, true);
                    pieces.Add((item.Prefix is null ? "" : item.Prefix + " ") + (S.BracketPerItem ? "[" + m + "]" : m));
                    continue;
                }
                if (item.Prefix is null && item.Locator is null)
                {
                    run.Add(n.Value);
                    continue;
                }
                FlushRun();
                var num = n.Value.ToString(CultureInfo.InvariantCulture);
                var loc = item.Locator is null ? null : FormatLocator(item.Locator);
                var pre = item.Prefix is null ? "" : item.Prefix + " ";
                if (S.BracketPerItem) pieces.Add(pre + "[" + num + (loc is null ? "" : ", " + loc) + "]");
                else if (singleWithLocator) pieces.Add(pre + num);
                else pieces.Add(pre + num + (loc is null ? "" : " (" + loc + ")"));
            }
            FlushRun();

            var body = S.BracketPerItem ? string.Join(", ", pieces) : "[" + string.Join(",", pieces) + "]";
            if (singleWithLocator) body += " (" + FormatLocator(entries[0].item.Locator!) + ")";
            return outerPrefix is null ? body : outerPrefix + " " + body;
        }

        /// <summary>"12" → "p. 12", "3-5" → "pp. 3–5"; anything else (e.g. "chap. 2", "Table 3") is kept as typed.</summary>
        private static string FormatLocator(string loc)
        {
            loc = loc.Trim();
            if (loc.Length == 0 || !char.IsAsciiDigit(loc[0])) return loc;
            var norm = loc.Replace("--", "–", StringComparison.Ordinal).Replace('-', '–');
            return (norm.Contains('–', StringComparison.Ordinal) || norm.Contains(',', StringComparison.Ordinal) ? "pp. " : "p. ") + norm;
        }
    }
}
