using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace RevisionsEditor.Core.Bibliography;

/// <summary>How in-text citations identify a work: by author and year, or by a number in order of first citation.</summary>
public enum CitationMode
{
    /// <summary>"(White et al., 2016)"; the reference list is alphabetical.</summary>
    AuthorYear,

    /// <summary>"[1]"; the reference list is numbered in order of first citation.</summary>
    Numeric,
}

/// <summary>Order of the works inside one parenthetical author-year citation.</summary>
public enum CitationItemOrder
{
    /// <summary>Keep the order the author typed.</summary>
    AsWritten,

    /// <summary>Alphabetical by author, then year (APA rule).</summary>
    Alphabetical,

    /// <summary>Oldest first, then by author.</summary>
    Chronological,
}

/// <summary>A built-in citation style: in-text settings plus a reference-list formatter. Output uses *italic* and **bold** Markdown.</summary>
public sealed class CitationStyle
{
    private readonly Func<BibEntry, string, CitationStyle, string> _formatter;

    internal CitationStyle(string id, string name, CitationMode mode, string description, Func<BibEntry, string, CitationStyle, string> formatter)
    {
        Id = id;
        Name = name;
        Mode = mode;
        Description = description;
        _formatter = formatter;
    }

    /// <summary>Stable identifier, e.g. "elsevier-harvard".</summary>
    public string Id { get; }

    /// <summary>Display name.</summary>
    public string Name { get; }

    /// <summary>Author-year or numeric.</summary>
    public CitationMode Mode { get; }

    /// <summary>Which journals use the style and an example citation.</summary>
    public string Description { get; }

    /// <summary>In-text citations use "First et al." from this many authors.</summary>
    public int InTextEtAlMin { get; init; } = 3;

    /// <summary>Word joining two authors inside parentheses ("and", or "&amp;" for APA).</summary>
    public string ParentheticalAnd { get; init; } = "and";

    /// <summary>Word joining two authors in a narrative citation.</summary>
    public string NarrativeAnd { get; init; } = "and";

    /// <summary>Between author and year inside parentheses: ", " (Elsevier, APA) or " " (CSP).</summary>
    public string AuthorYearSeparator { get; init; } = ", ";

    /// <summary>Between works in one citation.</summary>
    public string ItemSeparator { get; init; } = "; ";

    /// <summary>Between years of the same author: "(White, 2016a, 2016b)".</summary>
    public string SameAuthorYearSeparator { get; init; } = ", ";

    /// <summary>Order of works inside one parenthetical citation.</summary>
    public CitationItemOrder ItemOrder { get; init; } = CitationItemOrder.AsWritten;

    /// <summary>Use the abbreviated journal name (ShortContainer) when the entry has one.</summary>
    public bool UseShortContainer { get; init; }

    /// <summary>Reference list label pattern, e.g. "{0}." or "[{0}]"; empty for author-year.</summary>
    public string LabelFormat { get; init; } = "";

    /// <summary>Numeric styles only: each number gets its own brackets ("[1], [3]", IEEE) instead of one group ("[1,3]").</summary>
    public bool BracketPerItem { get; init; }

    /// <summary>Text used when an entry has no year.</summary>
    public string NoDateText { get; init; } = "n.d.";

    /// <summary>Formats one reference-list entry (without its number label). <paramref name="yearLabel"/> is the year with any a/b suffix, or "n.d.".</summary>
    public string FormatReference(BibEntry entry, string yearLabel) => _formatter(entry, yearLabel, this);

    /// <summary>Label for reference number <paramref name="number"/>, e.g. "1." or "[1]"; empty for author-year styles.</summary>
    public string FormatLabel(int number) =>
        LabelFormat.Length == 0 ? "" : string.Format(CultureInfo.InvariantCulture, LabelFormat, number);

    /// <summary>Author part of an in-text citation: "White", "White and Coops" ("White &amp; Coops"), "White et al.", or an institution.
    /// Falls back to editors, then the title.</summary>
    public string FormatAuthorsShort(BibEntry entry, bool narrative)
    {
        var names = entry.Authors.Count > 0 ? entry.Authors : entry.Editors;
        var real = names.Where(n => !n.IsOthers).ToList();
        var others = names.Any(n => n.IsOthers);
        if (real.Count == 0) return StyleText.TitleFallback(entry);
        static string Short(PersonName p) => p.IsInstitution ? p.Family : p.FamilyWithParticle;
        if (others || real.Count >= InTextEtAlMin) return Short(real[0]) + " et al.";
        if (real.Count == 1) return Short(real[0]);
        var and = narrative ? NarrativeAnd : ParentheticalAnd;
        if (real.Count == 2) return Short(real[0]) + " " + and + " " + Short(real[1]);
        return string.Join(", ", real.Take(real.Count - 1).Select(Short)) + ", " + and + " " + Short(real[^1]);
    }

    /// <summary>Returns the style name.</summary>
    public override string ToString() => Name;
}

/// <summary>The built-in styles.</summary>
public static class CitationStyles
{
    /// <summary>Elsevier Harvard (with titles): Remote Sensing of Environment, ISPRS J. Photogramm., JAG, Forest Ecology and Management.</summary>
    public static CitationStyle ElsevierHarvard { get; } = new("elsevier-harvard", "Elsevier Harvard (with titles)", CitationMode.AuthorYear,
        "Remote Sensing of Environment, ISPRS Journal, JAG, Forest Ecology and Management. (White et al., 2016); list alphabetical.",
        ReferenceFormatters.Elsevier)
    {
        UseShortContainer = true,
    };

    /// <summary>APA 7th edition.</summary>
    public static CitationStyle Apa { get; } = new("apa", "APA 7th edition", CitationMode.AuthorYear,
        "American Psychological Association 7th edition. (White & Coops, 2016); narrative White and Coops (2016).",
        ReferenceFormatters.Apa)
    {
        ParentheticalAnd = "&",
        ItemOrder = CitationItemOrder.Alphabetical,
    };

    /// <summary>MDPI (Remote Sensing, Forests, Drones…).</summary>
    public static CitationStyle Mdpi { get; } = new("mdpi", "MDPI", CitationMode.Numeric,
        "MDPI journals (Remote Sensing, Forests, Drones). Numbered in order of citation: [1], [1,3], [1–3].",
        ReferenceFormatters.Mdpi)
    {
        UseShortContainer = true,
        LabelFormat = "{0}.",
    };

    /// <summary>IEEE (IEEE TGRS, JSTARS, GRSL).</summary>
    public static CitationStyle Ieee { get; } = new("ieee", "IEEE", CitationMode.Numeric,
        "IEEE Transactions and Letters (TGRS, JSTARS, GRSL). Numbered: [1], [1], [3], [1]–[3].",
        ReferenceFormatters.Ieee)
    {
        UseShortContainer = true,
        LabelFormat = "[{0}]",
        BracketPerItem = true,
    };

    /// <summary>Canadian Science Publishing (Canadian Journal of Forest Research and other CSP journals).</summary>
    public static CitationStyle Csp { get; } = new("csp", "Canadian Science Publishing", CitationMode.AuthorYear,
        "Canadian Journal of Forest Research and other CSP journals. (White et al. 2016); list alphabetical.",
        ReferenceFormatters.Csp)
    {
        AuthorYearSeparator = " ",
        UseShortContainer = true,
    };

    /// <summary>All built-in styles.</summary>
    public static IReadOnlyList<CitationStyle> All { get; } = [ElsevierHarvard, Apa, Mdpi, Ieee, Csp];

    /// <summary>Finds a style by id (case-insensitive); null when unknown.</summary>
    public static CitationStyle? Find(string id) =>
        All.FirstOrDefault(s => string.Equals(s.Id, id?.Trim(), StringComparison.OrdinalIgnoreCase));
}

/// <summary>Broad entry categories used by the formatters.</summary>
internal enum EntryKind { Article, Book, Chapter, Conference, Thesis, Report, Web, Dataset, Software, Misc }

internal enum ThesisLevel { Doctoral, Masters, Other }

internal enum LastDelimiter { Never, Always, Contextual }

/// <summary>Formatting helpers shared by the reference formatters.</summary>
internal static class StyleText
{
    private static readonly Regex SingleEmphasis = new(@"(?<!\*)\*(?!\*)(?<t>[^*]+?)(?<!\*)\*(?!\*)", RegexOptions.Compiled);
    private static readonly Regex IsoDate = new(@"^(?<y>\d{4})(?:-(?<m>\d{1,2})(?:-(?<d>\d{1,2}))?)?", RegexOptions.Compiled);

    public static EntryKind KindOf(BibEntry e) => e.Type switch
    {
        "article" or "periodical" or "review" or "letter" => e.Container is null ? EntryKind.Misc : EntryKind.Article,
        "book" or "mvbook" or "booklet" or "collection" or "mvcollection" or "proceedings" or "mvproceedings" or "manual" or "reference" => EntryKind.Book,
        "incollection" or "inbook" or "bookinbook" or "suppbook" or "suppcollection" or "inreference" => EntryKind.Chapter,
        "inproceedings" or "conference" => EntryKind.Conference,
        "thesis" or "phdthesis" or "mastersthesis" => EntryKind.Thesis,
        "report" or "techreport" => EntryKind.Report,
        "online" or "www" or "electronic" or "webpage" => EntryKind.Web,
        "dataset" or "data" => EntryKind.Dataset,
        "software" => EntryKind.Software,
        _ => EntryKind.Misc,
    };

    /// <summary>Thesis level and, for unrecognised "type" fields, the text to print verbatim.</summary>
    public static (ThesisLevel level, string? verbatim) Thesis(BibEntry e)
    {
        var g = e.Genre;
        if (g is not null)
        {
            var f = BibText.SortFold(g).Replace(".", "", StringComparison.Ordinal).Replace(" ", "", StringComparison.Ordinal);
            if (f is "phdthesis" or "phd" or "doctoralthesis" or "phddissertation") return (ThesisLevel.Doctoral, null);
            if (f is "mathesis" or "mastersthesis" or "masterthesis" or "mscthesis" or "msthesis") return (ThesisLevel.Masters, null);
            if (f is "thesis") return (ThesisLevel.Other, null);
            return (ThesisLevel.Other, g);
        }
        return e.Type switch
        {
            "phdthesis" => (ThesisLevel.Doctoral, null),
            "mastersthesis" => (ThesisLevel.Masters, null),
            _ => (ThesisLevel.Other, null),
        };
    }

    public static string? ThesisGenre(BibEntry e, string doctoral, string masters, string? other)
    {
        var (level, verbatim) = Thesis(e);
        return verbatim ?? level switch
        {
            ThesisLevel.Doctoral => doctoral,
            ThesisLevel.Masters => masters,
            _ => other,
        };
    }

    public static string? Container(BibEntry e, CitationStyle s) => s.UseShortContainer ? e.ShortContainer ?? e.Container : e.Container;

    public static string? UrlOf(BibEntry e)
    {
        if (e.Url is not null) return e.Url;
        var h = e.HowPublished;
        return h is not null && h.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? h : null;
    }

    /// <summary>Publisher-like source for web pages, datasets and software.</summary>
    public static string? PublisherLike(BibEntry e)
    {
        foreach (var v in new[] { e.Publisher, e.HowPublished, e.Organization, e.Institution, StyleText.KindOf(e) is EntryKind.Web or EntryKind.Misc ? e.Container : null })
        {
            if (v is not null && !v.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return v;
        }
        return null;
    }

    public static string Italic(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return s ?? "";
        if (!s.Contains('*', StringComparison.Ordinal)) return "*" + s + "*";
        // Emphasis inside an italic title is set roman (CSL "flip-flop").
        var sb = new StringBuilder();
        var last = 0;
        foreach (Match m in SingleEmphasis.Matches(s))
        {
            sb.Append(WrapItalic(s[last..m.Index]));
            sb.Append(m.Groups["t"].Value);
            last = m.Index + m.Length;
        }
        sb.Append(WrapItalic(s[last..]));
        return sb.ToString();
    }

    private static string WrapItalic(string seg)
    {
        if (seg.Trim().Length == 0) return seg;
        var lead = seg.Length - seg.TrimStart().Length;
        var trail = seg.Length - seg.TrimEnd().Length;
        return seg[..lead] + "*" + seg.Trim() + "*" + seg[(seg.Length - trail)..];
    }

    public static string Bold(string s) => "**" + s + "**";

    /// <summary>True when the text ends with . ? or ! (ignoring closing emphasis markers and quotes).</summary>
    public static bool EndsTerminal(string s) => EndsTerminal(s.AsSpan());

    public static bool EndsTerminal(StringBuilder sb) => sb.Length > 0 && EndsTerminal(sb.ToString());

    private static bool EndsTerminal(ReadOnlySpan<char> s)
    {
        var i = s.Length - 1;
        while (i >= 0 && s[i] is '*' or '”' or '’' or '"') i--;
        return i >= 0 && s[i] is '.' or '?' or '!';
    }

    /// <summary>Appends a new sentence: ". " before it unless the text already ends with terminal punctuation.</summary>
    public static void Sentence(StringBuilder sb, string? part)
    {
        if (string.IsNullOrWhiteSpace(part)) return;
        if (sb.Length > 0)
        {
            if (EndsTerminal(sb)) sb.Append(' ');
            else if (sb[^1] == ' ') { }
            else sb.Append(". ");
        }
        sb.Append(part);
    }

    /// <summary>Ends the text with a period unless it already ends with terminal punctuation.</summary>
    public static void Terminate(StringBuilder sb)
    {
        if (sb.Length > 0 && !EndsTerminal(sb)) sb.Append('.');
    }

    public static string? Join(string sep, params string?[] parts)
    {
        var list = parts.Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
        return list.Count == 0 ? null : string.Join(sep, list);
    }

    public static string? Prefixed(string prefix, string? value) => string.IsNullOrWhiteSpace(value) ? null : prefix + value;

    public static bool IsRange(string pages) => pages.Contains('–', StringComparison.Ordinal) || pages.Contains(',', StringComparison.Ordinal);

    public static string PagesWithLabel(string pages) => (IsRange(pages) ? "pp. " : "p. ") + pages;

    /// <summary>"2" → "2nd"; non-numeric editions are kept ("Second").</summary>
    public static string Edition(string ed)
    {
        if (!int.TryParse(ed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)) return ed;
        var suffix = (n % 100) is 11 or 12 or 13 ? "th" : (n % 10) switch { 1 => "st", 2 => "nd", 3 => "rd", _ => "th" };
        return n.ToString(CultureInfo.InvariantCulture) + suffix;
    }

    private static readonly string[] MonthNames = CultureInfo.InvariantCulture.DateTimeFormat.MonthNames;

    public static string MonthName(int m) => MonthNames[m - 1];

    /// <summary>IEEE month abbreviation: "Jan.", "May", "Jun.", "Sep."…</summary>
    public static string IeeeMonth(int m) => m switch
    {
        5 => "May",
        6 => "Jun.",
        7 => "Jul.",
        9 => "Sep.",
        _ => MonthNames[m - 1][..3] + ".",
    };

    public static (int y, int? m, int? d)? ParseDate(string? s)
    {
        if (s is null) return null;
        var m = IsoDate.Match(s.Trim());
        if (!m.Success) return null;
        var y = int.Parse(m.Groups["y"].Value, CultureInfo.InvariantCulture);
        int? mo = m.Groups["m"].Success ? int.Parse(m.Groups["m"].Value, CultureInfo.InvariantCulture) : null;
        int? d = m.Groups["d"].Success ? int.Parse(m.Groups["d"].Value, CultureInfo.InvariantCulture) : null;
        if (mo is < 1 or > 12) { mo = null; d = null; }
        if (d is < 1 or > 31) d = null;
        return (y, mo, d);
    }

    /// <summary>"13 March 2023".</summary>
    public static string DayMonthYear(string raw)
    {
        var p = ParseDate(raw);
        if (p is null) return raw;
        var (y, m, d) = p.Value;
        return Join(" ", d?.ToString(CultureInfo.InvariantCulture), m is null ? null : MonthName(m.Value), y.ToString(CultureInfo.InvariantCulture))!;
    }

    /// <summary>"March 13, 2023".</summary>
    public static string MonthDayYear(string raw)
    {
        var p = ParseDate(raw);
        if (p is null) return raw;
        var (y, m, d) = p.Value;
        if (m is null) return y.ToString(CultureInfo.InvariantCulture);
        return MonthName(m.Value) + (d is null ? "" : " " + d.Value.ToString(CultureInfo.InvariantCulture)) + ", " + y.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>"Mar. 13, 2023".</summary>
    public static string IeeeDate(string raw)
    {
        var p = ParseDate(raw);
        if (p is null) return raw;
        var (y, m, d) = p.Value;
        if (m is null) return y.ToString(CultureInfo.InvariantCulture);
        return IeeeMonth(m.Value) + (d is null ? "" : " " + d.Value.ToString(CultureInfo.InvariantCulture)) + ", " + y.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>In-text fallback when there are no authors or editors: the title in quotes (articles) or italics (other works).</summary>
    public static string TitleFallback(BibEntry e)
    {
        var t = e.Title;
        if (t is null) return e.Key;
        return KindOf(e) is EntryKind.Article or EntryKind.Chapter or EntryKind.Conference ? "“" + t + "”" : Italic(t);
    }

    // ------------------------------------------------------------------ names

    /// <summary>"White, J.C." (or "White, J. C."); suffix after the initials ("Strunk, W., Jr.") or after the family name ("Strunk Jr., W.").</summary>
    public static string Inverted(PersonName p, bool spaced, bool suffixAfterInitials)
    {
        if (p.IsInstitution) return p.Family;
        var initials = p.InitialsWithPeriods(spaced);
        var fam = p.FamilyWithParticle;
        if (initials.Length == 0) return p.Suffix is null ? fam : fam + " " + p.Suffix;
        if (p.Suffix is null) return fam + ", " + initials;
        return suffixAfterInitials ? fam + ", " + initials + ", " + p.Suffix : fam + " " + p.Suffix + ", " + initials;
    }

    /// <summary>"J.C. White" / "J. C. White Jr.".</summary>
    public static string Forward(PersonName p, bool spaced)
    {
        if (p.IsInstitution) return p.Family;
        var initials = p.InitialsWithPeriods(spaced);
        var s = initials.Length == 0 ? p.FamilyWithParticle : initials + " " + p.FamilyWithParticle;
        return p.Suffix is null ? s : s + " " + p.Suffix;
    }

    /// <summary>Joins rendered names with et-al truncation. A trailing "others" always produces the et-al text.</summary>
    public static string? NameList(IReadOnlyList<PersonName> names, Func<PersonName, string> render, string delimiter, string? and,
        LastDelimiter lastDelimiter, int etAlMin = int.MaxValue, int etAlUseFirst = 1, string etAl = "et al.", string etAlDelimiter = ", ")
    {
        var real = names.Where(n => !n.IsOthers).ToList();
        if (real.Count == 0) return null;
        var others = names.Any(n => n.IsOthers);
        if (real.Count >= etAlMin || others)
        {
            var take = real.Count >= etAlMin ? Math.Max(1, etAlUseFirst) : real.Count;
            return string.Join(delimiter, real.Take(take).Select(render)) + etAlDelimiter + etAl;
        }
        if (real.Count == 1) return render(real[0]);
        var rendered = real.Select(render).ToList();
        if (and is null) return string.Join(delimiter, rendered);
        var useDelim = lastDelimiter == LastDelimiter.Always || (lastDelimiter == LastDelimiter.Contextual && real.Count > 2);
        var head = string.Join(delimiter, rendered.Take(rendered.Count - 1));
        return head + (useDelim ? delimiter : " ") + and + " " + rendered[^1];
    }

    public static IReadOnlyList<PersonName> RealNames(IReadOnlyList<PersonName> names) => names.Where(n => !n.IsOthers).ToList();
}

/// <summary>Reference-list formatters, one per built-in style.</summary>
internal static class ReferenceFormatters
{
    // =============================================================== Elsevier Harvard
    // White, J.C., Coops, N.C., 2016. Title. Can. J. Remote Sens. 42, 619–641. https://doi.org/10.1080/...
    public static string Elsevier(BibEntry e, string year, CitationStyle s)
    {
        var kind = StyleText.KindOf(e);
        var sb = new StringBuilder();
        if (kind == EntryKind.Dataset) sb.Append("[dataset] ");
        string Names(IReadOnlyList<PersonName> list) =>
            StyleText.NameList(list, n => StyleText.Inverted(n, false, false), ", ", null, LastDelimiter.Never, etAlDelimiter: ", ") ?? "";
        var titleUsedAsAuthor = false;
        if (e.Authors.Count > 0) sb.Append(Names(e.Authors));
        else if (e.Editors.Count > 0 && kind is EntryKind.Book or EntryKind.Report or EntryKind.Misc)
            sb.Append(Names(e.Editors)).Append(StyleText.RealNames(e.Editors).Count > 1 ? " (Eds.)" : " (Ed.)");
        else
        {
            sb.Append(e.Title ?? e.Key);
            titleUsedAsAuthor = true;
        }
        sb.Append(", ").Append(year);

        var title = titleUsedAsAuthor ? null : e.Title;
        switch (kind)
        {
            case EntryKind.Article:
            {
                StyleText.Sentence(sb, title);
                var loc = StyleText.Join(", ", e.Volume, e.Pages ?? e.ArticleNumber);
                StyleText.Sentence(sb, StyleText.Join(" ", StyleText.Container(e, s), loc));
                break;
            }
            case EntryKind.Book:
                StyleText.Sentence(sb, StyleText.Join(", ", title, StyleText.Prefixed("", e.Edition is null ? null : StyleText.Edition(e.Edition) + " ed")));
                StyleText.Sentence(sb, StyleText.Join(", ", e.Publisher, e.Address));
                break;
            case EntryKind.Chapter or EntryKind.Conference:
            {
                var eds = e.Editors.Count > 0 ? Names(e.Editors) + (StyleText.RealNames(e.Editors).Count > 1 ? " (Eds.)" : " (Ed.)") : null;
                var container = e.Container is null ? null : "in: " + StyleText.Join(", ", eds, e.Container);
                StyleText.Sentence(sb, StyleText.Join(", ", title, container));
                StyleText.Sentence(sb, StyleText.Join(", ", e.Publisher ?? (kind == EntryKind.Conference ? e.Organization : null), e.Address,
                    e.Pages is null ? null : StyleText.PagesWithLabel(e.Pages)));
                break;
            }
            case EntryKind.Thesis:
            {
                var genre = StyleText.ThesisGenre(e, "Ph.D. thesis", "Master's thesis", "Thesis");
                StyleText.Sentence(sb, StyleText.Join(" ", title, genre is null ? null : "(" + genre + ")"));
                StyleText.Sentence(sb, StyleText.Join(", ", e.School, e.Address));
                break;
            }
            case EntryKind.Report:
            {
                var genre = StyleText.Join(" ", e.Genre ?? (e.Type == "techreport" ? "Technical Report" : null), StyleText.Prefixed("No. ", e.Number));
                StyleText.Sentence(sb, StyleText.Join(" ", title, genre is null ? null : "(" + genre + ")"));
                StyleText.Sentence(sb, StyleText.Join(", ", e.Institution ?? e.Publisher, e.Address));
                break;
            }
            default:
            {
                StyleText.Sentence(sb, title);
                var version = e.Version is null ? null : char.IsDigit(e.Version[0]) ? "v" + e.Version : e.Version;
                StyleText.Sentence(sb, StyleText.Join(", ", StyleText.PublisherLike(e), version));
                break;
            }
        }
        StyleText.Terminate(sb);

        var url = StyleText.UrlOf(e);
        if (e.Doi is not null) sb.Append(" https://doi.org/").Append(e.Doi);
        else if (url is not null)
        {
            sb.Append(' ').Append(url);
            if (e.UrlDate is not null) sb.Append(" (accessed ").Append(StyleText.DayMonthYear(e.UrlDate)).Append(").");
        }
        return sb.ToString();
    }

    // =============================================================== APA 7
    // White, J. C., Coops, N. C., & Wulder, M. A. (2016). Title. *Journal*, *42*(5), 619–641. https://doi.org/...
    public static string Apa(BibEntry e, string year, CitationStyle s)
    {
        var kind = StyleText.KindOf(e);
        var sb = new StringBuilder();

        static string InvertedList(IReadOnlyList<PersonName> list)
        {
            var real = StyleText.RealNames(list);
            if (real.Count >= 21)
            {
                return string.Join(", ", real.Take(19).Select(n => StyleText.Inverted(n, true, true))) + ", . . . " +
                       StyleText.Inverted(real[^1], true, true);
            }
            return StyleText.NameList(list, n => StyleText.Inverted(n, true, true), ", ", "&", LastDelimiter.Always) ?? "";
        }

        static string ForwardList(IReadOnlyList<PersonName> list) =>
            StyleText.NameList(list, n => StyleText.Forward(n, true), ", ", "&", LastDelimiter.Contextual) ?? "";

        string? authorText = null;
        var titleUsedAsAuthor = false;
        var bookLike = kind is not (EntryKind.Article or EntryKind.Chapter or EntryKind.Conference);
        if (e.Authors.Count > 0) authorText = InvertedList(e.Authors);
        else if (e.Editors.Count > 0 && kind is EntryKind.Book or EntryKind.Report or EntryKind.Misc)
            authorText = InvertedList(e.Editors) + (StyleText.RealNames(e.Editors).Count > 1 ? " (Eds.)" : " (Ed.)");

        var dateLabel = year;
        if (kind == EntryKind.Web && year.Length == 4 && StyleText.ParseDate(e.Get("date")) is { m: not null } d)
        {
            dateLabel = year + ", " + StyleText.MonthName(d.m!.Value) + (d.d is null ? "" : " " + d.d.Value.ToString(CultureInfo.InvariantCulture));
        }

        if (authorText is not null)
        {
            sb.Append(authorText);
            if (!StyleText.EndsTerminal(authorText)) sb.Append('.');
        }
        else
        {
            var t = e.Title ?? e.Key;
            sb.Append(bookLike ? StyleText.Italic(t) : t);
            if (!StyleText.EndsTerminal(sb)) sb.Append('.');
            titleUsedAsAuthor = true;
        }
        sb.Append(" (").Append(dateLabel).Append(").");

        var title = titleUsedAsAuthor ? null : e.Title;
        string? SourceUnlessAuthor(string? source)
        {
            if (source is null) return null;
            var a = StyleText.RealNames(e.Authors);
            if (a.Count == 1 && a[0].IsInstitution && BibText.SortFold(a[0].Family) == BibText.SortFold(source)) return null;
            return source;
        }

        switch (kind)
        {
            case EntryKind.Article:
            {
                StyleText.Sentence(sb, title);
                var c = StyleText.Container(e, s);
                var part = new StringBuilder();
                if (c is not null) part.Append(StyleText.Italic(c));
                if (e.Volume is not null)
                {
                    if (part.Length > 0) part.Append(", ");
                    part.Append(StyleText.Italic(e.Volume));
                    if (e.Issue is not null) part.Append('(').Append(e.Issue).Append(')');
                }
                if (e.Pages is not null) part.Append(part.Length > 0 ? ", " : "").Append(e.Pages);
                else if (e.ArticleNumber is not null) part.Append(part.Length > 0 ? ", " : "").Append("Article ").Append(e.ArticleNumber);
                StyleText.Sentence(sb, part.ToString());
                break;
            }
            case EntryKind.Book:
                StyleText.Sentence(sb, title is null ? null : StyleText.Italic(title) + (e.Edition is null ? "" : " (" + StyleText.Edition(e.Edition) + " ed.)"));
                StyleText.Sentence(sb, SourceUnlessAuthor(e.Publisher));
                break;
            case EntryKind.Chapter or EntryKind.Conference:
            {
                StyleText.Sentence(sb, title);
                if (e.Container is not null)
                {
                    var eds = e.Editors.Count > 0
                        ? ForwardList(e.Editors) + (StyleText.RealNames(e.Editors).Count > 1 ? " (Eds.), " : " (Ed.), ")
                        : "";
                    var pages = e.Pages is null ? "" : " (" + StyleText.PagesWithLabel(e.Pages) + ")";
                    StyleText.Sentence(sb, "In " + eds + StyleText.Italic(e.Container) + pages);
                }
                StyleText.Sentence(sb, SourceUnlessAuthor(e.Publisher ?? (kind == EntryKind.Conference ? e.Organization : null)));
                break;
            }
            case EntryKind.Thesis:
            {
                var genre = StyleText.ThesisGenre(e, "Doctoral dissertation", "Master's thesis", "Thesis");
                var bracket = StyleText.Join(", ", genre, e.School);
                StyleText.Sentence(sb, StyleText.Join(" ", title is null ? null : StyleText.Italic(title), bracket is null ? null : "[" + bracket + "]"));
                break;
            }
            case EntryKind.Report:
            {
                var number = e.Number is null ? null : e.Genre is not null ? e.Genre + " " + e.Number : "Report No. " + e.Number;
                StyleText.Sentence(sb, StyleText.Join(" ", title is null ? null : StyleText.Italic(title), number is null ? null : "(" + number + ")"));
                StyleText.Sentence(sb, SourceUnlessAuthor(e.Institution ?? e.Publisher));
                break;
            }
            default:
            {
                var label = kind switch { EntryKind.Dataset => " [Data set]", EntryKind.Software => " [Computer software]", _ => "" };
                var version = e.Version is null ? "" : " (Version " + e.Version + ")";
                StyleText.Sentence(sb, title is null ? (label.Length > 0 ? label.Trim() : null) : StyleText.Italic(title) + version + label);
                StyleText.Sentence(sb, SourceUnlessAuthor(StyleText.PublisherLike(e)));
                break;
            }
        }
        StyleText.Terminate(sb);

        var url = StyleText.UrlOf(e);
        if (e.Doi is not null) sb.Append(" https://doi.org/").Append(e.Doi);
        else if (url is not null)
        {
            if (kind is EntryKind.Web or EntryKind.Misc && e.UrlDate is not null)
                sb.Append(" Retrieved ").Append(StyleText.MonthDayYear(e.UrlDate)).Append(", from ").Append(url);
            else sb.Append(' ').Append(url);
        }
        return sb.ToString();
    }

    // =============================================================== MDPI
    // White, J.C.; Coops, N.C. Title. *Can. J. Remote Sens.* **2016**, *42*, 619–641. https://doi.org/...
    public static string Mdpi(BibEntry e, string year, CitationStyle s)
    {
        var kind = StyleText.KindOf(e);
        var sb = new StringBuilder();
        static string Names(IReadOnlyList<PersonName> list) =>
            StyleText.NameList(list, n => StyleText.Inverted(n, false, true), "; ", null, LastDelimiter.Never, 11, 10, "et al.", "; ") ?? "";
        static string EditorNames(IReadOnlyList<PersonName> list) =>
            (StyleText.NameList(list, n => StyleText.Inverted(n, false, true), ", ", null, LastDelimiter.Never, 11, 10) ?? "")
            + (StyleText.RealNames(list).Count > 1 ? ", Eds." : ", Ed.");

        var titleUsedAsAuthor = false;
        if (e.Authors.Count > 0) sb.Append(Names(e.Authors));
        else if (e.Editors.Count > 0 && kind is EntryKind.Book or EntryKind.Report) sb.Append(EditorNames(e.Editors));
        var title = e.Title;
        if (sb.Length == 0 && title is not null && kind is not (EntryKind.Book or EntryKind.Report))
        {
            sb.Append(title);
            titleUsedAsAuthor = true;
        }
        if (titleUsedAsAuthor) title = null;
        var yearOrNull = year;
        var pubLoc = StyleText.Join(": ", e.Publisher, e.Address);

        switch (kind)
        {
            case EntryKind.Article:
            {
                StyleText.Sentence(sb, title);
                var c = StyleText.Container(e, s);
                var tail = StyleText.Join(", ", StyleText.Bold(yearOrNull), e.Volume is null ? null : StyleText.Italic(e.Volume), e.Pages ?? e.ArticleNumber);
                StyleText.Sentence(sb, StyleText.Join(" ", c is null ? null : StyleText.Italic(c), tail));
                break;
            }
            case EntryKind.Book or EntryKind.Report:
            {
                var t = title is null ? null : StyleText.Italic(title) + (e.Edition is null ? "" : ", " + StyleText.Edition(e.Edition) + " ed.");
                var eds = e.Authors.Count > 0 && e.Editors.Count > 0 ? EditorNames(e.Editors) : null;
                var number = kind == EntryKind.Report && e.Number is not null ? StyleText.Join(" ", e.Genre ?? "Report", e.Number) : null;
                var publisher = kind == EntryKind.Report ? StyleText.Join(": ", e.Institution ?? e.Publisher, e.Address) : pubLoc;
                StyleText.Sentence(sb, StyleText.Join("; ", t, eds, number, StyleText.Join(", ", publisher, yearOrNull),
                    e.Pages is null ? null : StyleText.PagesWithLabel(e.Pages)));
                break;
            }
            case EntryKind.Chapter:
            {
                StyleText.Sentence(sb, title);
                var book = e.Container is null ? null : "In " + StyleText.Italic(e.Container) + (e.Edition is null ? "" : ", " + StyleText.Edition(e.Edition) + " ed.");
                var eds = e.Editors.Count > 0 ? EditorNames(e.Editors) : null;
                StyleText.Sentence(sb, StyleText.Join("; ", book, eds, StyleText.Join(", ", pubLoc, yearOrNull),
                    e.Pages is null ? null : StyleText.PagesWithLabel(e.Pages)));
                break;
            }
            case EntryKind.Conference:
            {
                StyleText.Sentence(sb, title);
                string? proc = null;
                if (e.Container is not null)
                {
                    proc = e.Container.StartsWith("Proc", StringComparison.OrdinalIgnoreCase) ? "In " + e.Container : "In Proceedings of the " + e.Container;
                }
                var when = e.Month is { } m ? StyleText.MonthName(m) + " " + yearOrNull : yearOrNull;
                StyleText.Sentence(sb, StyleText.Join("; ", StyleText.Join(", ", proc, e.Address, when), e.Publisher,
                    e.Pages is null ? null : StyleText.PagesWithLabel(e.Pages)));
                break;
            }
            case EntryKind.Thesis:
                StyleText.Sentence(sb, title);
                StyleText.Sentence(sb, StyleText.Join(", ", StyleText.ThesisGenre(e, "Ph.D. Thesis", "Master's Thesis", "Thesis"), e.School, e.Address, yearOrNull));
                break;
            default:
            {
                StyleText.Sentence(sb, title);
                StyleText.Sentence(sb, StyleText.Join(", ", StyleText.PublisherLike(e), StyleText.Prefixed("Version ", e.Version), yearOrNull));
                break;
            }
        }
        StyleText.Terminate(sb);

        var url = StyleText.UrlOf(e);
        if (e.Doi is not null) sb.Append(" https://doi.org/").Append(e.Doi).Append('.');
        else if (url is not null)
        {
            sb.Append(" Available online: ").Append(url);
            if (e.UrlDate is not null) sb.Append(" (accessed on ").Append(StyleText.DayMonthYear(e.UrlDate)).Append(')');
            sb.Append('.');
        }
        return sb.ToString();
    }

    // =============================================================== IEEE
    // J. C. White, N. C. Coops, and M. A. Wulder, “Title,” *Can. J. Remote Sens.*, vol. 42, no. 5, pp. 619–641, 2016, doi: 10.1080/....
    public static string Ieee(BibEntry e, string year, CitationStyle s)
    {
        var kind = StyleText.KindOf(e);
        var sb = new StringBuilder();
        static string Names(IReadOnlyList<PersonName> list) =>
            StyleText.NameList(list, n => StyleText.Forward(n, true), ", ", "and", LastDelimiter.Contextual, 7, 1, "*et al.*", " ") ?? "";
        static string EditorNames(IReadOnlyList<PersonName> list) =>
            Names(list) + (StyleText.RealNames(list).Count > 1 ? ", Eds." : ", Ed.");

        if (e.Authors.Count > 0) sb.Append(Names(e.Authors)).Append(", ");
        else if (e.Editors.Count > 0 && kind is EntryKind.Book or EntryKind.Report) sb.Append(EditorNames(e.Editors)).Append(", ");

        var title = e.Title ?? e.Key;
        var monthYear = e.Month is { } mo && kind is EntryKind.Article or EntryKind.Conference or EntryKind.Report
            ? StyleText.IeeeMonth(mo) + " " + year
            : year;
        string Quoted(string punct) =>
            "“" + title + (title.EndsWith('?') || title.EndsWith('!') ? "" : punct) + "”";
        var doi = e.Doi;
        var url = StyleText.UrlOf(e);

        // Appends "rest" after the quoted title and closes with ", doi: X." or ".".
        void QuotedWithRest(params string?[] rest)
        {
            var r = StyleText.Join(", ", rest);
            if (r is null)
            {
                sb.Append(Quoted(doi is null ? "." : ","));
                if (doi is not null) sb.Append(" doi: ").Append(doi).Append('.');
                return;
            }
            sb.Append(Quoted(",")).Append(' ').Append(r);
            if (doi is not null) sb.Append(", doi: ").Append(doi).Append('.');
            else StyleText.Terminate(sb);
        }

        switch (kind)
        {
            case EntryKind.Article:
            {
                var c = StyleText.Container(e, s);
                var pages = e.Pages is not null ? StyleText.PagesWithLabel(e.Pages) : StyleText.Prefixed("Art. no. ", e.ArticleNumber);
                QuotedWithRest(c is null ? null : StyleText.Italic(c), StyleText.Prefixed("vol. ", e.Volume), StyleText.Prefixed("no. ", e.Issue), pages, monthYear);
                break;
            }
            case EntryKind.Conference:
            {
                var eds = e.Editors.Count > 0 ? EditorNames(e.Editors) : null;
                var inPart = e.Container is null ? null : "in " + StyleText.Italic(e.Container);
                var rest = StyleText.Join(", ", eds, e.Address, monthYear, e.Pages is null ? null : StyleText.PagesWithLabel(e.Pages));
                sb.Append(Quoted(",")).Append(' ').Append(StyleText.Join(", ", inPart, rest) ?? "");
                if (doi is not null) sb.Append(", doi: ").Append(doi).Append('.');
                else StyleText.Terminate(sb);
                break;
            }
            case EntryKind.Chapter:
            {
                var eds = e.Editors.Count > 0 ? EditorNames(e.Editors) : null;
                var ed = e.Edition is null ? null : StyleText.Edition(e.Edition) + " ed.";
                sb.Append(Quoted(",")).Append(' ');
                sb.Append(StyleText.Join(", ", e.Container is null ? null : "in " + StyleText.Italic(e.Container), ed, eds) ?? "");
                StyleText.Sentence(sb, StyleText.Join(", ", StyleText.Join(": ", e.Address, e.Publisher), year,
                    e.Pages is null ? null : StyleText.PagesWithLabel(e.Pages)));
                StyleText.Terminate(sb);
                if (doi is not null) sb.Append(" doi: ").Append(doi).Append('.');
                break;
            }
            case EntryKind.Book:
            {
                sb.Append(StyleText.Italic(title));
                if (e.Edition is not null) sb.Append(", ").Append(StyleText.Edition(e.Edition)).Append(" ed");
                var pub = StyleText.Join(": ", e.Address, e.Publisher);
                if (pub is null) sb.Append(", ").Append(year);
                else StyleText.Sentence(sb, pub + ", " + year);
                StyleText.Terminate(sb);
                if (doi is not null) sb.Append(" doi: ").Append(doi).Append('.');
                break;
            }
            case EntryKind.Thesis:
                QuotedWithRest(StyleText.ThesisGenre(e, "Ph.D. dissertation", "M.S. thesis", "Thesis"), e.School, e.Address, year);
                break;
            case EntryKind.Report:
            {
                var number = e.Number is null ? null : (e.Genre ?? "Tech. Rep.") + " " + e.Number;
                QuotedWithRest(e.Institution ?? e.Publisher, e.Address, number, monthYear);
                break;
            }
            default:
            {
                var rest = StyleText.Join(", ", StyleText.PublisherLike(e), StyleText.Prefixed("ver. ", e.Version), year);
                if (kind == EntryKind.Software)
                {
                    sb.Append(StyleText.Italic(title));
                    if (rest is not null) sb.Append(", ").Append(rest);
                    StyleText.Terminate(sb);
                }
                else
                {
                    sb.Append(Quoted(rest is null ? "." : ","));
                    if (rest is not null) sb.Append(' ').Append(rest).Append('.');
                }
                if (e.UrlDate is not null && url is not null) sb.Append(" Accessed: ").Append(StyleText.IeeeDate(e.UrlDate)).Append('.');
                if (doi is not null) sb.Append(" doi: ").Append(doi).Append('.');
                else if (url is not null) sb.Append(kind == EntryKind.Dataset ? " [Dataset]. Available: " : " [Online]. Available: ").Append(url);
                return sb.ToString();
            }
        }
        if (doi is null && url is not null)
        {
            sb.Append(" [Online]. Available: ").Append(url);
        }
        return sb.ToString();
    }

    // =============================================================== Canadian Science Publishing
    // White, J.C., Coops, N.C., and Wulder, M.A. 2016. Title. Can. J. Remote Sens. 42(5): 619–641. doi:10.1080/....
    public static string Csp(BibEntry e, string year, CitationStyle s)
    {
        var kind = StyleText.KindOf(e);
        var sb = new StringBuilder();
        static string Names(IReadOnlyList<PersonName> list) =>
            StyleText.NameList(list, n => StyleText.Inverted(n, false, true), ", ", "and", LastDelimiter.Always) ?? "";
        static string ForwardNames(IReadOnlyList<PersonName> list) =>
            StyleText.NameList(list, n => StyleText.Forward(n, false), ", ", "and", LastDelimiter.Contextual) ?? "";

        var titleUsedAsAuthor = false;
        if (e.Authors.Count > 0) sb.Append(Names(e.Authors));
        else if (e.Editors.Count > 0 && kind is EntryKind.Book or EntryKind.Report or EntryKind.Misc)
            sb.Append(Names(e.Editors)).Append(StyleText.RealNames(e.Editors).Count > 1 ? " (*Editors*)" : " (*Editor*)");
        else
        {
            sb.Append(e.Title ?? e.Key);
            titleUsedAsAuthor = true;
        }
        StyleText.Sentence(sb, year);
        var title = titleUsedAsAuthor ? null : e.Title;

        switch (kind)
        {
            case EntryKind.Article:
            {
                StyleText.Sentence(sb, title);
                var vol = e.Volume is null ? null : e.Volume + (e.Issue is null ? "" : "(" + e.Issue + ")");
                var src = StyleText.Join(" ", StyleText.Container(e, s), vol);
                var pages = e.Pages ?? e.ArticleNumber;
                StyleText.Sentence(sb, src is null ? pages : pages is null ? src : src + ": " + pages);
                break;
            }
            case EntryKind.Book:
                StyleText.Sentence(sb, title);
                StyleText.Sentence(sb, e.Edition is null ? null : StyleText.Edition(e.Edition) + " ed");
                StyleText.Sentence(sb, StyleText.Join(", ", e.Publisher, e.Address));
                break;
            case EntryKind.Chapter or EntryKind.Conference:
                StyleText.Sentence(sb, title);
                StyleText.Sentence(sb, e.Container is null ? null : "*In* " + e.Container + (e.Edition is null ? "" : ", " + StyleText.Edition(e.Edition) + " ed"));
                StyleText.Sentence(sb, e.Editors.Count > 0 ? "*Edited by* " + ForwardNames(e.Editors) : null);
                StyleText.Sentence(sb, StyleText.Join(", ", e.Publisher ?? (kind == EntryKind.Conference ? e.Organization : null), e.Address));
                StyleText.Sentence(sb, e.Pages is null ? null : StyleText.PagesWithLabel(e.Pages));
                break;
            case EntryKind.Thesis:
                StyleText.Sentence(sb, title);
                StyleText.Sentence(sb, StyleText.Join(", ", StyleText.ThesisGenre(e, "Ph.D. thesis", "M.Sc. thesis", "Thesis"), e.School, e.Address));
                break;
            case EntryKind.Report:
                StyleText.Sentence(sb, title);
                StyleText.Sentence(sb, StyleText.Join(", ", StyleText.Join(" ", e.Genre, e.Number), e.Institution ?? e.Publisher, e.Address));
                break;
            default:
                StyleText.Sentence(sb, title);
                StyleText.Sentence(sb, StyleText.Join(", ", StyleText.PublisherLike(e), StyleText.Prefixed("Version ", e.Version)));
                break;
        }

        var url = StyleText.UrlOf(e);
        if (e.Doi is not null) StyleText.Sentence(sb, "doi:" + e.Doi);
        else if (url is not null)
            StyleText.Sentence(sb, "Available from " + url + (e.UrlDate is null ? "" : " [accessed " + StyleText.DayMonthYear(e.UrlDate) + "]"));
        StyleText.Terminate(sb);
        return sb.ToString();
    }
}
