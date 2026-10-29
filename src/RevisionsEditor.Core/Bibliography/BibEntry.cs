using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace RevisionsEditor.Core.Bibliography;

/// <summary>One parsed author or editor name. Institutional names (written in braces in BibTeX) keep the whole name in <see cref="Family"/>.</summary>
public sealed record PersonName
{
    /// <summary>Family (last) name without particle, or the whole institution name.</summary>
    public string Family { get; init; } = "";

    /// <summary>Given (first) names, e.g. "Joanne C." or "Jean-Pierre"; null when absent.</summary>
    public string? Given { get; init; }

    /// <summary>Lowercase particle ("von part"), e.g. "van der"; null when absent.</summary>
    public string? Particle { get; init; }

    /// <summary>Suffix ("Jr part"), e.g. "Jr."; null when absent.</summary>
    public string? Suffix { get; init; }

    /// <summary>True when the name was wrapped in braces, e.g. {Natural Resources Canada}; it is never inverted or abbreviated.</summary>
    public bool IsInstitution { get; init; }

    /// <summary>True for the BibTeX "others" marker, which means the list continues ("et al.").</summary>
    public bool IsOthers { get; init; }

    /// <summary>The shared "others" (et al.) marker.</summary>
    public static PersonName Others { get; } = new() { Family = "others", IsOthers = true };

    /// <summary>Family name preceded by its particle, e.g. "van der Meer".</summary>
    public string FamilyWithParticle => string.IsNullOrEmpty(Particle) ? Family : Particle + " " + Family;

    /// <summary>Initials of the given names with periods, e.g. "J.C." (or "J. C." when <paramref name="spaced"/>); "Jean-Pierre" gives "J.-P.".</summary>
    public string InitialsWithPeriods(bool spaced)
    {
        if (string.IsNullOrWhiteSpace(Given) || IsInstitution) return "";
        var units = new List<string>();
        foreach (var token in Given.Split([' ', ' ', '\t', ' '], StringSplitOptions.RemoveEmptyEntries))
        {
            var hyphenParts = token.Split('-', StringSplitOptions.RemoveEmptyEntries);
            if (hyphenParts.Length > 1)
            {
                var initials = hyphenParts.Select(FirstLetter).Where(c => c is not null).Select(c => c + ".").ToList();
                if (initials.Count > 0) units.Add(string.Join("-", initials));
                continue;
            }
            // "J.C." packs several initials into one token.
            foreach (var piece in token.Split('.', StringSplitOptions.RemoveEmptyEntries))
            {
                var c = FirstLetter(piece);
                if (c is not null) units.Add(c + ".");
            }
        }
        return string.Join(spaced ? " " : "", units);
    }

    private static string? FirstLetter(string s)
    {
        foreach (var ch in s)
        {
            if (char.IsLetter(ch)) return char.ToUpperInvariant(ch).ToString();
        }
        return null;
    }

    /// <summary>Readable form for diagnostics, e.g. "White, Joanne C.".</summary>
    public override string ToString() => IsOthers ? "others"
        : IsInstitution || string.IsNullOrEmpty(Given) ? FamilyWithParticle
        : FamilyWithParticle + ", " + Given + (Suffix is null ? "" : ", " + Suffix);
}

/// <summary>One BibTeX/BibLaTeX entry with LaTeX-decoded fields and convenience accessors for the fields citation styles use.</summary>
public sealed class BibEntry
{
    private static readonly Regex PageDash = new(@"\s*(?:-{1,3}|[–—])\s*", RegexOptions.Compiled);
    private static readonly Regex FourDigits = new(@"\d{4}", RegexOptions.Compiled);
    private static readonly Regex DoiPrefix = new(@"^(?:https?://(?:dx\.)?doi\.org/|doi:\s*)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>Creates an entry; field names must be lowercase and values already decoded.</summary>
    public BibEntry(string key, string type, IReadOnlyDictionary<string, string> fields,
        IReadOnlyList<PersonName>? authors = null, IReadOnlyList<PersonName>? editors = null, int line = 0)
    {
        Key = key;
        Type = type.ToLowerInvariant();
        Fields = fields;
        Authors = authors ?? [];
        Editors = editors ?? [];
        Line = line;
    }

    /// <summary>Citation key, e.g. "white2016remote".</summary>
    public string Key { get; }

    /// <summary>Lowercase entry type, e.g. "article", "inproceedings", "online".</summary>
    public string Type { get; }

    /// <summary>All fields by lowercase name, LaTeX-decoded (url/doi/file are kept verbatim apart from escapes).</summary>
    public IReadOnlyDictionary<string, string> Fields { get; }

    /// <summary>Parsed author list (may end with <see cref="PersonName.Others"/>).</summary>
    public IReadOnlyList<PersonName> Authors { get; }

    /// <summary>Parsed editor list.</summary>
    public IReadOnlyList<PersonName> Editors { get; }

    /// <summary>1-based line of the entry in the source file (0 when unknown).</summary>
    public int Line { get; }

    /// <summary>Returns the field value, or null when it is absent or empty.</summary>
    public string? Get(string field) => Fields.TryGetValue(field, out var v) && !string.IsNullOrWhiteSpace(v) ? v : null;

    private string? First(params string[] names)
    {
        foreach (var n in names)
        {
            var v = Get(n);
            if (v is not null) return v;
        }
        return null;
    }

    /// <summary>Year from the year field, or the first four digits of the date field; null when neither is present.</summary>
    public string? Year
    {
        get
        {
            var y = Get("year");
            if (y is not null) return y;
            var d = Get("date");
            if (d is null) return null;
            var m = FourDigits.Match(d);
            return m.Success ? m.Value : null;
        }
    }

    /// <summary>Title field.</summary>
    public string? Title => Get("title");

    /// <summary>Journal, journaltitle or booktitle.</summary>
    public string? Container => First("journal", "journaltitle", "booktitle");

    /// <summary>Abbreviated journal name (shortjournal, journalabbreviation, journal_abbrev); null when absent.</summary>
    public string? ShortContainer => First("shortjournal", "journalabbreviation", "journal_abbrev", "journalabbrev");

    /// <summary>Volume field.</summary>
    public string? Volume => Get("volume");

    /// <summary>Issue: the number field, else the issue field.</summary>
    public string? Issue => First("number", "issue");

    /// <summary>Report or series number (the raw number field).</summary>
    public string? Number => Get("number");

    /// <summary>Pages with "-" / "--" normalised to an en dash, e.g. "619–641".</summary>
    public string? Pages
    {
        get
        {
            var p = Get("pages");
            return p is null ? null : PageDash.Replace(p.Trim(), "–");
        }
    }

    /// <summary>DOI without "https://doi.org/" or "doi:" prefixes.</summary>
    public string? Doi
    {
        get
        {
            var d = Get("doi");
            if (d is null) return null;
            d = DoiPrefix.Replace(d.Trim(), "").Trim();
            return d.Length == 0 ? null : d;
        }
    }

    /// <summary>URL field.</summary>
    public string? Url => Get("url");

    /// <summary>Publisher field.</summary>
    public string? Publisher => Get("publisher");

    /// <summary>Institution, else school (thesis-granting university).</summary>
    public string? Institution => First("institution", "school");

    /// <summary>School, else institution.</summary>
    public string? School => First("school", "institution");

    /// <summary>Address, else location (BibLaTeX).</summary>
    public string? Address => First("address", "location");

    /// <summary>Edition field, e.g. "2" or "Second".</summary>
    public string? Edition => Get("edition");

    /// <summary>The BibTeX "type" field (thesis/report genre such as "Information Report").</summary>
    public string? Genre => Get("type");

    /// <summary>Organization field.</summary>
    public string? Organization => Get("organization");

    /// <summary>Howpublished field.</summary>
    public string? HowPublished => Get("howpublished");

    /// <summary>Version field (datasets, software).</summary>
    public string? Version => Get("version");

    /// <summary>Electronic article number (eid / BibLaTeX eid), e.g. "112027".</summary>
    public string? ArticleNumber => First("eid", "articleno");

    /// <summary>Access date (urldate), as written, e.g. "2024-03-12".</summary>
    public string? UrlDate => Get("urldate");

    /// <summary>Month number 1–12 from the month field or the date field; null when unknown.</summary>
    public int? Month
    {
        get
        {
            var m = Get("month");
            if (m is not null)
            {
                if (int.TryParse(m, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n is >= 1 and <= 12) return n;
                var t = m.Trim().ToLowerInvariant();
                for (var i = 0; i < 12; i++)
                {
                    var name = CultureInfo.InvariantCulture.DateTimeFormat.MonthNames[i].ToLowerInvariant();
                    if (t.Length >= 3 && name.StartsWith(t[..3], StringComparison.Ordinal)) return i + 1;
                }
            }
            var d = Get("date");
            if (d is not null && d.Length >= 7 && d[4] == '-' && int.TryParse(d.AsSpan(5, 2), NumberStyles.Integer, CultureInfo.InvariantCulture, out var dm)
                && dm is >= 1 and <= 12) return dm;
            return null;
        }
    }

    /// <summary>Short description for diagnostics.</summary>
    public override string ToString() => "@" + Type + "{" + Key + "}";
}

/// <summary>Text folding helpers shared by sorting and key generation.</summary>
internal static class BibText
{
    /// <summary>Removes diacritics and maps special letters (ø, æ, ß, ł…) to ASCII; keeps other characters.</summary>
    public static string AsciiFold(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark) continue;
            switch (ch)
            {
                case 'ø': sb.Append('o'); break;
                case 'Ø': sb.Append('O'); break;
                case 'æ': sb.Append("ae"); break;
                case 'Æ': sb.Append("AE"); break;
                case 'œ': sb.Append("oe"); break;
                case 'Œ': sb.Append("OE"); break;
                case 'ß': sb.Append("ss"); break;
                case 'ł': sb.Append('l'); break;
                case 'Ł': sb.Append('L'); break;
                case 'đ': sb.Append('d'); break;
                case 'Đ': sb.Append('D'); break;
                case 'ð': sb.Append('d'); break;
                case 'Ð': sb.Append('D'); break;
                case 'þ': sb.Append("th"); break;
                case 'Þ': sb.Append("Th"); break;
                case 'ı': sb.Append('i'); break;
                case 'ȷ': sb.Append('j'); break;
                default: sb.Append(ch); break;
            }
        }
        return sb.ToString();
    }

    /// <summary>Sort key: accent-folded, lowercase, without Markdown emphasis markers.</summary>
    public static string SortFold(string? s) =>
        s is null ? "" : AsciiFold(s.Replace("*", "", StringComparison.Ordinal)).ToLowerInvariant().Trim();
}
