using System.Text;
using System.Text.RegularExpressions;
using RevisionsEditor.Core.Models;

namespace RevisionsEditor.Core.Checks;

/// <summary>Settings for <see cref="StyleChecker"/>: which rule families run and which abbreviations need no definition.</summary>
public sealed class StyleCheckOptions
{
    /// <summary>Creates options with the default known abbreviations and every rule family switched on.</summary>
    public StyleCheckOptions()
    {
        KnownAbbreviations = new HashSet<string>(DefaultKnownAbbreviations, StringComparer.Ordinal);
    }

    /// <summary>The abbreviations that a new <see cref="KnownAbbreviations"/> set starts with (case-sensitive).</summary>
    public static IReadOnlyList<string> DefaultKnownAbbreviations { get; } =
    [
        // Latin and general
        "e.g.", "i.e.", "et al.", "etc.", "cf.", "vs.", "USA", "US", "UK", "EU", "UN", "SI", "DOI", "ID", "OK", "AM", "PM",
        "PhD", "MSc", "BSc", "ISO", "IEEE", "NASA", "ESA", "AI", "TV", "NE", "NW", "SE", "SW", "CO", "CO2", "CH4", "DNA",
        // Computing, imaging and file formats
        "2D", "3D", "4D", "RGB", "CPU", "GPU", "UTC", "GPS", "RAM", "SSD", "HDD", "USB", "URL", "API", "CSV", "PDF", "PNG",
        "JPEG", "JPG", "TIFF", "GeoTIFF", "LAS", "LAZ", "XML", "JSON", "HTML", "HD", "UHD",
        // Units that look like abbreviations
        "MHz", "GHz", "MB", "GB", "TB", "KB", "MP", "MPa", "kPa", "hPa",
        // Registries, datum suffixes, organisations
        "EPSG", "CSRS", "ASPRS", "CRAN", "PyPI",
        // Software, manufacturers and instruments whose names look like abbreviations
        "COLMAP", "MATLAB", "QGIS", "ArcGIS", "ArcMap", "PDAL", "GDAL", "RiSCAN", "PRO", "FUSION", "LDV", "ENVI", "SNAP",
        "DJI", "RIEGL", "VUX", "VZ", "HERO", "ICESat", "LiDAR360", "SciPy",
    ];

    /// <summary>Abbreviations never required to be defined (case-sensitive; add your own).</summary>
    public ISet<string> KnownAbbreviations { get; }

    /// <summary>Check that abbreviations are defined once, before use, and used after their definition.</summary>
    public bool CheckAbbreviations { get; set; } = true;

    /// <summary>Check that variant spellings of the same term (lidar/LiDAR, dataset/data set, …) are not mixed.</summary>
    public bool CheckTerminology { get; set; } = true;

    /// <summary>Check unit formatting (spacing, exponents, multiplication sign, minus sign, en-dash ranges, spelling).</summary>
    public bool CheckUnits { get; set; } = true;

    /// <summary>Check that species binomials are italic and correctly capitalised.</summary>
    public bool CheckSpecies { get; set; } = true;

    /// <summary>Check that software is cited with a version and satellite sensors with their instrument.</summary>
    public bool CheckSoftwareVersions { get; set; } = true;

    /// <summary>Check number style: precision, digit grouping, numerals at sentence start, “e.g.”, “et al.”, “etc.”.</summary>
    public bool CheckNumbers { get; set; } = true;

    /// <summary>Check general prose: repeated words, “data is”, unmatched parentheses and brackets.</summary>
    public bool CheckGeneral { get; set; } = true;

    /// <summary>When true, derived units should be written with negative exponents (“Mg ha^-1^”); when false, with a slash (“Mg/ha”).</summary>
    public bool PreferNegativeExponents { get; set; } = true;

    /// <summary>When true, an abbreviation used in a caption must be defined in that caption (reported as Info).</summary>
    public bool RequireSelfContainedCaptions { get; set; } = true;

    /// <summary>When true, “data is/was/has” is reported with a suggestion to treat “data” as plural.</summary>
    public bool PreferPluralData { get; set; } = true;
}

/// <summary>Reports style issues an editor or reviewer of a forest remote sensing article would flag.</summary>
public static class StyleChecker
{
    internal const string Category = "Style";

    /// <summary>Built-in expansions of common forest remote sensing, photogrammetry and statistics abbreviations.</summary>
    public static IReadOnlyDictionary<string, string> AbbreviationExpansions => StyleAbbreviationRule.Expansions;

    /// <summary>Checks the prose of the manuscript and returns the style issues found, in document order.</summary>
    public static List<ManuscriptIssue> Check(IReadOnlyList<DocumentBlock> blocks, StyleCheckOptions? options = null)
    {
        options ??= new StyleCheckOptions();
        var sink = new StyleIssueSink();
        if (blocks is null || blocks.Count == 0)
        {
            return [];
        }

        var segments = new List<StyleSegment>();
        for (var i = 0; i < blocks.Count; i++)
        {
            var block = blocks[i];
            if (block is null)
            {
                continue;
            }

            try
            {
                var segment = StyleTextMask.Build(block, i);
                if (segment is not null)
                {
                    segments.Add(segment);
                }
            }
            catch (Exception)
            {
                sink.AddParagraph(block, i, IssueSeverity.Warning, "This paragraph could not be checked for style because its markup could not be read.");
            }
        }

        Run(options.CheckAbbreviations, "abbreviation", () => StyleAbbreviationRule.Run(segments, options, sink), sink);
        Run(options.CheckTerminology, "terminology", () => StyleTerminologyRule.Run(segments, sink), sink);
        Run(options.CheckUnits, "unit", () => StyleUnitRule.Run(segments, options, sink), sink);
        Run(options.CheckSpecies, "species name", () => StyleSpeciesRule.Run(segments, sink), sink);
        Run(options.CheckSoftwareVersions, "software version", () => StyleSoftwareRule.Run(segments, sink), sink);
        Run(options.CheckNumbers, "number", () => StyleNumberRule.Run(segments, sink), sink);
        Run(options.CheckGeneral, "general", () => StyleGeneralRule.Run(segments, options, sink), sink);
        return sink.ToList();
    }

    private static void Run(bool enabled, string name, Action rule, StyleIssueSink sink)
    {
        if (!enabled)
        {
            return;
        }

        try
        {
            rule();
        }
        catch (Exception)
        {
            sink.AddGeneral(IssueSeverity.Warning, $"The {name} style check could not be completed for this manuscript.");
        }
    }
}

// ---------------------------------------------------------------------------------------------------------------------
// Shared infrastructure: regex factory, masked text segments and the issue sink.
// ---------------------------------------------------------------------------------------------------------------------

/// <summary>Creates the compiled, time-limited regular expressions used by the style rules.</summary>
internal static class StyleRegex
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(2);

    public static Regex Create(string pattern, RegexOptions options = RegexOptions.None)
        => new(pattern, options | RegexOptions.Compiled | RegexOptions.CultureInvariant, Timeout);
}

/// <summary>Small string helpers shared by the style rules.</summary>
internal static class StyleText
{
    public static string LowerFirst(string s) => s.Length == 0 ? s : char.ToLowerInvariant(s[0]) + s[1..];

    public static string UpperFirst(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

    public static string Q(string s) => $"“{s}”";

    public static string Times(int n) => n == 1 ? "once" : $"{n} times";

    private static readonly Regex WhitespaceRegex = StyleRegex.Create(@"\s+");

    public static string NormalizeSpaces(string s) => WhitespaceRegex.Replace(s, " ");

    /// <summary>Joins quoted items as “a”, “b” and “c”.</summary>
    public static string JoinAnd(IReadOnlyList<string> items)
    {
        if (items.Count == 0)
        {
            return string.Empty;
        }

        if (items.Count == 1)
        {
            return items[0];
        }

        return string.Join(", ", items.Take(items.Count - 1)) + " and " + items[^1];
    }
}

/// <summary>The kind of manuscript part a segment comes from; it decides which rules apply.</summary>
internal enum StyleSegmentKind { Title, Heading, Abstract, Body, Caption }

/// <summary>
/// The analysable prose of one block: markup that is never analysed is removed (or replaced by one opaque character),
/// and every remaining character maps back to its index in the original block text.
/// </summary>
internal sealed class StyleSegment
{
    public const char OpaqueChar = '￼';

    private static readonly HashSet<string> NonTerminalAbbreviations = new(StringComparer.Ordinal)
    {
        "e.g.", "i.e.", "al.", "cf.", "vs.", "viz.", "Fig.", "Figs.", "fig.", "figs.", "Eq.", "Eqs.", "eq.", "Ref.", "Refs.",
        "Tab.", "Sect.", "Sec.", "Ch.", "approx.", "ca.", "c.", "no.", "No.", "Nos.", "nos.", "p.", "pp.", "vol.", "Vol.",
        "var.", "subsp.", "ssp.", "sp.", "spp.", "Dr.", "Mr.", "Mrs.", "Ms.", "Prof.", "St.", "Mt.", "Inc.", "Ltd.", "Co.",
        "Corp.", "resp.", "incl.", "esp.", "min.", "max.", "U.S.", "U.K.", "e.", "i.", "et.",
    };

    public StyleSegment(DocumentBlock block, int order, StyleSegmentKind kind, string text, int[] map, bool[] italic)
    {
        Block = block;
        Order = order;
        Kind = kind;
        Text = text;
        Map = map;
        Italic = italic;
    }

    public DocumentBlock Block { get; }

    public int Order { get; }

    public StyleSegmentKind Kind { get; }

    /// <summary>The analysed text (markup removed, opaque elements replaced by <see cref="OpaqueChar"/>).</summary>
    public string Text { get; }

    /// <summary>For every character of <see cref="Text"/>, its index in the original block text.</summary>
    public int[] Map { get; }

    /// <summary>For every character of <see cref="Text"/>, whether it was inside single or triple asterisks.</summary>
    public bool[] Italic { get; }

    public bool IsProse => Kind is StyleSegmentKind.Abstract or StyleSegmentKind.Body or StyleSegmentKind.Caption;

    /// <summary>True when every non-space character in the range is italic.</summary>
    public bool AllItalic(int start, int length)
    {
        if (length <= 0)
        {
            return false;
        }

        for (var i = start; i < start + length && i < Text.Length; i++)
        {
            if (!Italic[i] && !char.IsWhiteSpace(Text[i]))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>True when any character in the range is italic.</summary>
    public bool AnyItalic(int start, int length)
    {
        for (var i = start; i < start + length && i < Text.Length; i++)
        {
            if (Italic[i])
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Maps a range of <see cref="Text"/> to the original block text; contiguous means no markup lies inside it.</summary>
    public bool TryMapSpan(int start, int length, out int originalStart, out int originalLength, out bool contiguous)
    {
        originalStart = -1;
        originalLength = 0;
        contiguous = false;
        if (start < 0 || length <= 0 || start + length > Text.Length)
        {
            return false;
        }

        originalStart = Map[start];
        var end = Map[start + length - 1] + 1;
        originalLength = end - originalStart;
        contiguous = originalLength == length;
        if (contiguous)
        {
            var original = Block.Text ?? string.Empty;
            for (var k = 0; k < length; k++)
            {
                var c = Text[start + k];
                if (c == OpaqueChar || originalStart + k >= original.Length || original[originalStart + k] != c)
                {
                    contiguous = false;
                    break;
                }
            }
        }

        return true;
    }

    /// <summary>True when the word at <paramref name="position"/> starts a sentence (or the segment).</summary>
    public bool IsSentenceStart(int position)
    {
        var i = position - 1;
        while (i >= 0 && (char.IsWhiteSpace(Text[i]) || Text[i] is '(' or '[' or '"' or '“' or '‘' or '\''))
        {
            i--;
        }

        if (i < 0)
        {
            return true;
        }

        var c = Text[i];
        if (c is '!' or '?')
        {
            return true;
        }

        if (c != '.')
        {
            return false;
        }

        var j = i - 1;
        while (j >= 0 && !char.IsWhiteSpace(Text[j]) && Text[j] is not ('(' or '[' or OpaqueChar))
        {
            j--;
        }

        var token = Text.Substring(j + 1, i - j);
        if (NonTerminalAbbreviations.Contains(token))
        {
            return false;
        }

        // A single capital initial ("J.") does not end a sentence.
        return !(token.Length == 2 && char.IsUpper(token[0]));
    }

    /// <summary>The first sentence boundary at or after <paramref name="start"/> within <paramref name="limit"/> characters.</summary>
    public int SentenceEnd(int start, int limit)
    {
        var end = Math.Min(Text.Length, start + limit);
        for (var i = start; i < end; i++)
        {
            if (Text[i] is '.' or '!' or '?' && i + 1 < Text.Length && char.IsWhiteSpace(Text[i + 1]) && IsSentenceStartAfter(i + 1))
            {
                return i;
            }
        }

        return end;
    }

    private bool IsSentenceStartAfter(int index)
    {
        var k = index;
        while (k < Text.Length && char.IsWhiteSpace(Text[k]))
        {
            k++;
        }

        return k < Text.Length && char.IsUpper(Text[k]) && IsSentenceStart(k);
    }
}

/// <summary>
/// Builds <see cref="StyleSegment"/>s: the single masking helper shared by every style rule. It restricts figure and table
/// blocks to their captions, drops CriticMarkup deletions, comments, attribute groups, link targets and emphasis markers,
/// and replaces citations, cross-references, placeholders, inline math, URLs and DOIs by one opaque character each.
/// </summary>
internal static class StyleTextMask
{
    private enum CharState : byte { Keep, Remove, Opaque }

    private static readonly Regex CriticRegex = StyleRegex.Create(
        @"(\{~~(?<repOld>.*?)~>(?<repNew>.*?)~~\})|(\{\+\+(?<ins>.*?)\+\+\})|(\{--(?<del>.*?)--\})|(\{==(?<hl>.*?)==\}\{>>(?<hlComment>.*?)<<\})|(\{>>(?<comment>.*?)<<\})");

    private static readonly Regex LinkRegex = StyleRegex.Create(@"(?<!!)\[(?<label>[^\[\]\0\n]*)\]\((?<target>[^()\s\0]*)\)");
    private static readonly Regex BareLinkTargetRegex = StyleRegex.Create(@"\]\([^()\s\0]*\)");
    private static readonly Regex AttributeRegex = StyleRegex.Create(@"\{#[^{}\0\n]*\}");
    private static readonly Regex PlaceholderRegex = StyleRegex.Create(@"\{\{[^{}\0\n]*\}\}");
    private static readonly Regex UrlRegex = StyleRegex.Create(@"(?:https?://|ftp://|www\.)[^\s\0<>""]+", RegexOptions.IgnoreCase);
    private static readonly Regex DoiRegex = StyleRegex.Create(@"(?<![\w./])(?:doi:\s?)?10\.\d{4,9}/[^\s\0<>""]+", RegexOptions.IgnoreCase);
    private static readonly Regex MathRegex = StyleRegex.Create(@"(?<!\\)\$(?![\s$])[^$\0\n]*?(?<![\s\\])\$(?!\d)");
    private static readonly Regex BracketCitationRegex = StyleRegex.Create(@"\[[^\[\]\0\n]*?(?<![A-Za-z0-9_@\\])-?@[A-Za-z0-9_][^\[\]\0\n]*\]");
    private static readonly Regex NarrativeCitationRegex = StyleRegex.Create(@"(?<![A-Za-z0-9_@\\])@[A-Za-z0-9_](?:[A-Za-z0-9_:.#$%&+?<>~/\-]*[A-Za-z0-9])?");
    private static readonly Regex EmphasisRegex = StyleRegex.Create(@"(\*{1,3})([^*\n]+?)\1");

    public static StyleSegment? Build(DocumentBlock block, int order)
    {
        var text = block.Text ?? string.Empty;
        StyleSegmentKind kind;
        switch (block.Role)
        {
            case BlockStyleRole.Title: kind = StyleSegmentKind.Title; break;
            case BlockStyleRole.Heading1:
            case BlockStyleRole.Heading2:
            case BlockStyleRole.Heading3: kind = StyleSegmentKind.Heading; break;
            case BlockStyleRole.Abstract: kind = StyleSegmentKind.Abstract; break;
            case BlockStyleRole.BodyParagraph:
            case BlockStyleRole.BlockQuote: kind = StyleSegmentKind.Body; break;
            case BlockStyleRole.Caption:
            case BlockStyleRole.Figure:
            case BlockStyleRole.Table: kind = StyleSegmentKind.Caption; break;
            default: return null; // ReferenceItem, Equation and unknown roles are not prose.
        }

        var regionStart = 0;
        var regionEnd = text.Length;
        if (block.Role == BlockStyleRole.Figure)
        {
            var open = text.IndexOf("![", StringComparison.Ordinal);
            var close = text.LastIndexOf("](", StringComparison.Ordinal);
            if (open < 0 || close < open + 2)
            {
                return null;
            }

            regionStart = open + 2;
            regionEnd = close;
        }
        else if (block.Role == BlockStyleRole.Table)
        {
            var label = text.IndexOf("Table:", StringComparison.Ordinal);
            if (label < 0 || !string.IsNullOrWhiteSpace(text[..label]))
            {
                return null;
            }

            regionStart = label + "Table:".Length;
            var attr = text.IndexOf("{#", regionStart, StringComparison.Ordinal);
            regionEnd = attr >= 0 ? attr : text.Length;
        }

        var state = new CharState[text.Length];
        var italic = new bool[text.Length];
        var work = text.ToCharArray();

        void Remove(int start, int length)
        {
            for (var k = Math.Max(0, start); k < start + length && k < text.Length; k++)
            {
                state[k] = CharState.Remove;
                work[k] = '\0';
            }
        }

        void MakeOpaque(int start, int length)
        {
            var placed = false;
            for (var k = Math.Max(0, start); k < start + length && k < text.Length; k++)
            {
                if (!placed && state[k] == CharState.Keep)
                {
                    state[k] = CharState.Opaque;
                    placed = true;
                }
                else
                {
                    state[k] = CharState.Remove;
                }

                work[k] = '\0';
            }
        }

        Remove(0, regionStart);
        Remove(regionEnd, text.Length - regionEnd);

        // CriticMarkup (repeated so that marks nested inside an insertion or highlight are also handled).
        for (var pass = 0; pass < 4; pass++)
        {
            var matches = CriticRegex.Matches(new string(work));
            if (matches.Count == 0)
            {
                break;
            }

            foreach (Match m in matches)
            {
                var end = m.Index + m.Length;
                if (m.Groups["repNew"].Success)
                {
                    var g = m.Groups["repNew"];
                    Remove(m.Index, g.Index - m.Index);
                    Remove(g.Index + g.Length, end - (g.Index + g.Length));
                }
                else if (m.Groups["ins"].Success)
                {
                    var g = m.Groups["ins"];
                    Remove(m.Index, g.Index - m.Index);
                    Remove(g.Index + g.Length, end - (g.Index + g.Length));
                }
                else if (m.Groups["hl"].Success)
                {
                    var g = m.Groups["hl"];
                    Remove(m.Index, g.Index - m.Index);
                    Remove(g.Index + g.Length, end - (g.Index + g.Length));
                }
                else
                {
                    Remove(m.Index, m.Length); // deletion or comment
                }
            }
        }

        foreach (Match m in LinkRegex.Matches(new string(work)))
        {
            var label = m.Groups["label"];
            Remove(m.Index, 1);
            var close = label.Index + label.Length;
            Remove(close, m.Index + m.Length - close);
        }

        ApplyAll(BareLinkTargetRegex, (s, l) => Remove(s, l));
        ApplyAll(AttributeRegex, (s, l) => Remove(s, l));
        ApplyAll(PlaceholderRegex, MakeOpaque);
        ApplyAll(UrlRegex, (s, l) => MakeOpaque(s, TrimUrl(work, s, l)));
        ApplyAll(DoiRegex, (s, l) => MakeOpaque(s, TrimUrl(work, s, l)));
        ApplyAll(MathRegex, MakeOpaque);
        ApplyAll(BracketCitationRegex, MakeOpaque);
        ApplyAll(NarrativeCitationRegex, MakeOpaque);

        foreach (Match m in EmphasisRegex.Matches(new string(work)))
        {
            var markerLength = m.Groups[1].Length;
            var inner = m.Groups[2];
            Remove(m.Index, markerLength);
            Remove(inner.Index + inner.Length, markerLength);
            if (markerLength != 2)
            {
                for (var k = inner.Index; k < inner.Index + inner.Length; k++)
                {
                    italic[k] = true;
                }
            }
        }

        var sb = new StringBuilder(text.Length);
        var map = new List<int>(text.Length);
        var ital = new List<bool>(text.Length);
        for (var k = 0; k < text.Length; k++)
        {
            if (state[k] == CharState.Keep)
            {
                sb.Append(text[k]);
                map.Add(k);
                ital.Add(italic[k]);
            }
            else if (state[k] == CharState.Opaque)
            {
                sb.Append(StyleSegment.OpaqueChar);
                map.Add(k);
                ital.Add(false);
            }
        }

        return new StyleSegment(block, order, kind, sb.ToString(), map.ToArray(), ital.ToArray());

        void ApplyAll(Regex regex, Action<int, int> apply)
        {
            foreach (Match m in regex.Matches(new string(work)))
            {
                apply(m.Index, m.Length);
            }
        }
    }

    private static int TrimUrl(char[] work, int start, int length)
    {
        while (length > 0 && work[start + length - 1] is '.' or ',' or ';' or ':' or '!' or '?' or ')' or ']' or '}' or '\'' or '"')
        {
            length--;
        }

        return length;
    }
}

/// <summary>Collects issues, maps segment positions to original text positions and orders the result.</summary>
internal sealed class StyleIssueSink
{
    private readonly List<(int Order, ManuscriptIssue Issue)> _items = [];
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);

    /// <summary>Adds an issue for a range of the segment text; the fix is dropped when markup lies inside the range.</summary>
    public void Add(StyleSegment segment, int start, int length, IssueSeverity severity, string message, string? fix = null, string? fixLabel = null)
    {
        if (!segment.TryMapSpan(start, length, out var originalStart, out var originalLength, out var contiguous))
        {
            return;
        }

        if (!contiguous)
        {
            fix = null;
        }

        AddOriginal(segment, originalStart, originalLength, severity, message, fix, fixLabel);
    }

    /// <summary>Adds an issue whose range is already given in original block text positions.</summary>
    public void AddOriginal(StyleSegment segment, int originalStart, int originalLength, IssueSeverity severity, string message, string? fix = null, string? fixLabel = null)
    {
        var key = $"{segment.Block.ParagraphId}\u0001{originalStart}\u0001{originalLength}\u0001{message}";
        if (!_seen.Add(key))
        {
            return;
        }

        _items.Add((segment.Order, new ManuscriptIssue(StyleChecker.Category, severity, message, segment.Block.ParagraphId,
            originalStart, originalLength, fix, fix is null ? null : fixLabel ?? $"Change to {StyleText.Q(fix)}")));
    }

    public void AddParagraph(DocumentBlock block, int order, IssueSeverity severity, string message)
        => _items.Add((order, new ManuscriptIssue(StyleChecker.Category, severity, message, block.ParagraphId)));

    public void AddGeneral(IssueSeverity severity, string message)
        => _items.Add((int.MaxValue, new ManuscriptIssue(StyleChecker.Category, severity, message)));

    public List<ManuscriptIssue> ToList()
        => _items
            .OrderBy(i => i.Order)
            .ThenBy(i => i.Issue.Start)
            .ThenBy(i => i.Issue.Length)
            .ThenBy(i => i.Issue.Message, StringComparer.Ordinal)
            .Select(i => i.Issue)
            .ToList();
}

/// <summary>A position in the manuscript (segment order, then character index).</summary>
internal readonly record struct StylePosition(int Order, int Index) : IComparable<StylePosition>
{
    public int CompareTo(StylePosition other) => Order != other.Order ? Order.CompareTo(other.Order) : Index.CompareTo(other.Index);

    public static bool operator <(StylePosition a, StylePosition b) => a.CompareTo(b) < 0;

    public static bool operator >(StylePosition a, StylePosition b) => a.CompareTo(b) > 0;
}

// ---------------------------------------------------------------------------------------------------------------------
// Rule 1: abbreviations.
// ---------------------------------------------------------------------------------------------------------------------

/// <summary>Abbreviation definitions and uses, with the Abstract treated as a separate scope from the main text.</summary>
internal static class StyleAbbreviationRule
{
    public static readonly IReadOnlyDictionary<string, string> Expansions = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["ALS"] = "airborne laser scanning", ["TLS"] = "terrestrial laser scanning", ["MLS"] = "mobile laser scanning",
        ["ULS"] = "UAV laser scanning", ["PLS"] = "personal laser scanning", ["HMLS"] = "hand-held mobile laser scanning",
        ["UAV"] = "uncrewed aerial vehicle", ["UAS"] = "uncrewed aircraft system", ["RPAS"] = "remotely piloted aircraft system",
        ["LiDAR"] = "light detection and ranging", ["LIDAR"] = "light detection and ranging", ["SfM"] = "structure from motion",
        ["MVS"] = "multi-view stereo", ["DAP"] = "digital aerial photogrammetry", ["CHM"] = "canopy height model",
        ["DTM"] = "digital terrain model", ["DSM"] = "digital surface model", ["DEM"] = "digital elevation model",
        ["nDSM"] = "normalised digital surface model", ["TIN"] = "triangulated irregular network",
        ["DBH"] = "diameter at breast height", ["dbh"] = "diameter at breast height", ["AGB"] = "aboveground biomass",
        ["LAI"] = "leaf area index", ["PAI"] = "plant area index", ["LAD"] = "leaf area density", ["CBH"] = "crown base height",
        ["NDVI"] = "normalised difference vegetation index", ["EVI"] = "enhanced vegetation index",
        ["NDRE"] = "normalised difference red edge index", ["GEDI"] = "Global Ecosystem Dynamics Investigation",
        ["SAR"] = "synthetic aperture radar", ["InSAR"] = "interferometric synthetic aperture radar",
        ["PALSAR"] = "Phased Array type L-band Synthetic Aperture Radar", ["MSI"] = "MultiSpectral Instrument",
        ["OLI"] = "Operational Land Imager", ["TIRS"] = "Thermal Infrared Sensor", ["ETM"] = "Enhanced Thematic Mapper",
        ["MODIS"] = "Moderate Resolution Imaging Spectroradiometer", ["NIR"] = "near-infrared", ["SWIR"] = "short-wave infrared",
        ["TIR"] = "thermal infrared", ["VNIR"] = "visible and near-infrared", ["RMSE"] = "root mean square error",
        ["rRMSE"] = "relative root mean square error", ["MAE"] = "mean absolute error", ["MAPE"] = "mean absolute percentage error",
        ["SD"] = "standard deviation", ["SE"] = "standard error", ["CV"] = "coefficient of variation", ["CI"] = "confidence interval",
        ["ANOVA"] = "analysis of variance", ["PCA"] = "principal component analysis", ["OLS"] = "ordinary least squares",
        ["GLM"] = "generalised linear model", ["LMM"] = "linear mixed-effects model", ["AIC"] = "Akaike information criterion",
        ["BIC"] = "Bayesian information criterion", ["ICC"] = "intraclass correlation coefficient", ["GCP"] = "ground control point",
        ["GNSS"] = "global navigation satellite system", ["RTK"] = "real-time kinematic", ["PPK"] = "post-processed kinematic",
        ["IMU"] = "inertial measurement unit", ["INS"] = "inertial navigation system", ["GSD"] = "ground sampling distance",
        ["FOV"] = "field of view", ["PRF"] = "pulse repetition frequency", ["ITD"] = "individual tree detection",
        ["ITS"] = "individual tree segmentation", ["ABA"] = "area-based approach", ["ICP"] = "iterative closest point",
        ["RANSAC"] = "random sample consensus", ["CNN"] = "convolutional neural network", ["RF"] = "random forest",
        ["SVM"] = "support vector machine", ["kNN"] = "k-nearest neighbours", ["LUT"] = "look-up table",
        ["IoU"] = "intersection over union", ["mAP"] = "mean average precision", ["ROI"] = "region of interest",
        ["AOI"] = "area of interest", ["CRS"] = "coordinate reference system", ["UTM"] = "Universal Transverse Mercator",
        ["NAD83"] = "North American Datum of 1983", ["WGS84"] = "World Geodetic System 1984", ["BC"] = "British Columbia",
        ["NFI"] = "national forest inventory", ["BEC"] = "biogeoclimatic ecosystem classification",
        ["CWH"] = "Coastal Western Hemlock zone", ["CDF"] = "Coastal Douglas-fir zone", ["USGS"] = "United States Geological Survey",
        ["NRCan"] = "Natural Resources Canada", ["GEE"] = "Google Earth Engine", ["ML"] = "machine learning", ["DL"] = "deep learning",
        ["LULC"] = "land use and land cover", ["TCH"] = "top-of-canopy height",
    };

    private static readonly Regex TokenRegex = StyleRegex.Create(@"(?<![A-Za-z0-9])[A-Za-z][A-Za-z0-9]*(?![A-Za-z0-9])");
    private static readonly Regex ParenRegex = StyleRegex.Create(@"\(\s*(?<abbr>[A-Za-z][A-Za-z0-9]{1,8})\s*(?<rest>[;,][^()]*)?\)");
    private static readonly Regex AbbrFirstRegex = StyleRegex.Create(@"(?<![A-Za-z0-9])(?<abbr>[A-Za-z][A-Za-z0-9]{1,8})[ \t]+\((?<long>[A-Za-z][^()\[\];:￼]{2,80}?)\)");
    private static readonly Regex RomanRegex = StyleRegex.Create(@"^(?:I{1,3}|IV|VI{0,3}|IX|XI{0,3}|XIV|XV|XX)$");
    private static readonly Regex ParenGroupBeforeRegex = StyleRegex.Create(@"\([A-Za-z][A-Za-z0-9]{1,8}s?\)$");

    private sealed record Use(StyleSegment Segment, int Start, int Length, string Key)
    {
        public StylePosition Position => new(Segment.Order, Start);
    }

    private sealed record Definition(StyleSegment Segment, string Key, string Token, int TokenStart, int ExprStart, int ExprEnd,
        string LongForm, int LongStart, bool LongFirst, int RemoveStart, bool SimpleParen)
    {
        public StylePosition Position => new(Segment.Order, ExprStart);

        public StylePosition EndPosition => new(Segment.Order, ExprEnd);
    }

    /// <summary>The abbreviation key of a token (plural “CHMs” gives “CHM”), or null when it does not look like an abbreviation.</summary>
    public static string? KeyOf(string token)
    {
        if (token.Length >= 3 && token[^1] == 's')
        {
            var stem = token[..^1];
            if ((char.IsUpper(stem[^1]) || char.IsDigit(stem[^1])) && IsCandidate(stem))
            {
                return stem;
            }
        }

        return IsCandidate(token) ? token : null;
    }

    private static bool IsCandidate(string token)
    {
        if (token.Length is < 2 or > 8)
        {
            return false;
        }

        if (Expansions.ContainsKey(token))
        {
            return true;
        }

        if (!char.IsLetter(token[0]) || RomanRegex.IsMatch(token))
        {
            return false;
        }

        int upper = 0, lower = 0, run = 0, maxRun = 0;
        var digitSeen = false;
        foreach (var c in token)
        {
            if (char.IsDigit(c))
            {
                digitSeen = true;
                continue;
            }

            if (digitSeen || !char.IsAsciiLetter(c))
            {
                return false; // letters after digits ("YOLOv8" is fine, "L1C" is not an abbreviation pattern we judge)
            }

            if (char.IsUpper(c))
            {
                upper++;
                run = 0;
            }
            else
            {
                lower++;
                run++;
                maxRun = Math.Max(maxRun, run);
            }
        }

        return upper >= 2 && maxRun < 3 && upper * 2 >= upper + lower;
    }

    /// <summary>Schwartz–Hearst matching: the start of the long form for <paramref name="shortForm"/> at the end of <paramref name="window"/>, or -1.</summary>
    private static int FindLongFormStart(string shortForm, string window)
    {
        var s = shortForm.Length - 1;
        var l = window.Length - 1;
        while (s >= 0)
        {
            var c = char.ToLowerInvariant(shortForm[s]);
            if (!char.IsLetterOrDigit(c))
            {
                s--;
                continue;
            }

            while ((l >= 0 && char.ToLowerInvariant(window[l]) != c) || (s == 0 && l > 0 && char.IsLetterOrDigit(window[l - 1])))
            {
                l--;
            }

            if (l < 0)
            {
                return -1;
            }

            l--;
            s--;
        }

        var start = l + 1;
        while (start > 0 && !char.IsWhiteSpace(window[start - 1]))
        {
            start--;
        }

        return start;
    }

    private static bool IsWindowStop(char c) => c is '(' or ')' or '[' or ']' or ';' or ':' or ',' or '.' or '!' or '?' or '"' or '“' or '”' or StyleSegment.OpaqueChar;

    public static void Run(List<StyleSegment> segments, StyleCheckOptions options, StyleIssueSink sink)
    {
        var defs = new List<Definition>();
        var uses = new List<Use>();
        foreach (var segment in segments.Where(s => s.IsProse))
        {
            var segmentDefs = FindDefinitions(segment);
            defs.AddRange(segmentDefs);
            var defTokenStarts = segmentDefs.Select(d => d.TokenStart).ToHashSet();
            foreach (Match m in TokenRegex.Matches(segment.Text))
            {
                var key = KeyOf(m.Value);
                if (key is null || defTokenStarts.Contains(m.Index))
                {
                    continue;
                }

                // Instrument model codes such as "VQ-1560i" or "VUX-1" are names, not abbreviations.
                var after = m.Index + m.Length;
                if (after + 1 < segment.Text.Length && segment.Text[after] == '-' && char.IsDigit(segment.Text[after + 1]))
                {
                    continue;
                }

                // Product suffixes such as "RedEdge-MX" belong to the preceding name ("UAV-LiDAR" stays a use).
                if (m.Index >= 2 && segment.Text[m.Index - 1] == '-')
                {
                    var q = m.Index - 1;
                    while (q > 0 && char.IsLetterOrDigit(segment.Text[q - 1]))
                    {
                        q--;
                    }

                    var previous = segment.Text[q..(m.Index - 1)];
                    if (previous.Length > 0 && char.IsUpper(previous[0]) && KeyOf(previous) is null)
                    {
                        continue;
                    }
                }

                uses.Add(new Use(segment, m.Index, m.Length, key));
            }
        }

        var known = options.KnownAbbreviations;
        foreach (var key in defs.Select(d => d.Key).Concat(uses.Select(u => u.Key)).Distinct(StringComparer.Ordinal))
        {
            var isKnown = known.Contains(key);
            var keyDefs = defs.Where(d => d.Key == key).ToList();
            var keyUses = uses.Where(u => u.Key == key).ToList();

            var abstractDefs = keyDefs.Where(d => d.Segment.Kind == StyleSegmentKind.Abstract).ToList();
            var abstractUses = keyUses.Where(u => u.Segment.Kind == StyleSegmentKind.Abstract).ToList();
            CheckScope(key, isKnown, abstractDefs, abstractUses, abstractUses, true, false, defs, sink);

            var bodyDefs = keyDefs.Where(d => d.Segment.Kind == StyleSegmentKind.Body).ToList();
            var bodyUses = keyUses.Where(u => u.Segment.Kind == StyleSegmentKind.Body).ToList();
            var laterUses = keyUses.Where(u => u.Segment.Kind is StyleSegmentKind.Body or StyleSegmentKind.Caption).ToList();
            CheckScope(key, isKnown, bodyDefs, bodyUses, laterUses, false, abstractDefs.Count > 0, defs, sink);

            if (options.RequireSelfContainedCaptions && !isKnown)
            {
                foreach (var group in keyUses.Where(u => u.Segment.Kind == StyleSegmentKind.Caption).GroupBy(u => u.Segment))
                {
                    var first = group.OrderBy(u => u.Start).First();
                    if (keyDefs.Any(d => d.Segment == first.Segment && d.ExprStart < first.Start))
                    {
                        continue;
                    }

                    var hint = Expansions.TryGetValue(key, out var exp) ? $" (e.g. {StyleText.Q($"{exp} ({key})")})" : string.Empty;
                    sink.Add(first.Segment, first.Start, first.Length, IssueSeverity.Info,
                        $"Captions should be self-contained: define {key} in this caption{hint} or write it out.");
                }
            }
        }
    }

    private static void CheckScope(string key, bool isKnown, List<Definition> defs, List<Use> uses, List<Use> laterUses,
        bool isAbstract, bool definedInAbstract, List<Definition> allDefs, StyleIssueSink sink)
    {
        var expansion = Expansions.TryGetValue(key, out var exp) ? exp : null;
        if (defs.Count == 0)
        {
            if (uses.Count == 0 || isKnown)
            {
                return;
            }

            var u = uses[0];
            string message;
            if (!isAbstract && definedInAbstract)
            {
                message = $"{StyleText.Q(key)} is defined in the Abstract but not in the main text; define it again at its first use here.";
            }
            else if (expansion is not null)
            {
                message = isAbstract
                    ? $"Define {key} at first use in the Abstract, e.g. {StyleText.Q($"{expansion} ({key})")}."
                    : $"Define {key} at first use, e.g. {StyleText.Q($"{expansion} ({key})")}.";
            }
            else
            {
                message = isAbstract
                    ? $"{StyleText.Q(key)} is not defined in the Abstract; define it at first use, e.g. {StyleText.Q($"full term ({key})")}."
                    : $"{StyleText.Q(key)} is never defined; define it at first use, e.g. {StyleText.Q($"full term ({key})")}.";
            }

            sink.Add(u.Segment, u.Start, u.Length, IssueSeverity.Warning, message);
            return;
        }

        var first = defs[0];
        if (!isKnown)
        {
            var before = uses.FirstOrDefault(u => u.Position < first.Position);
            if (before is not null)
            {
                var where = first.Segment == before.Segment ? "later in this paragraph" : "later";
                sink.Add(before.Segment, before.Start, before.Length, IssueSeverity.Warning,
                    $"{StyleText.Q(key)} is used before it is defined {where}; define it here at its first use ({StyleText.Q($"{first.LongForm} ({key})")}).");
            }
        }

        foreach (var duplicate in defs.Skip(1))
        {
            var scope = isAbstract ? " in the Abstract" : string.Empty;
            var fix = duplicate.Token;
            sink.Add(duplicate.Segment, duplicate.ExprStart, duplicate.ExprEnd - duplicate.ExprStart, IssueSeverity.Info,
                $"{StyleText.Q(key)} is already defined earlier{scope}; use the abbreviation alone here.",
                fix, $"Use {StyleText.Q(fix)}");
        }

        var usedLater = laterUses.Any(u => u.Position > first.EndPosition);
        var nested = allDefs.Any(d => d != first && d.Segment == first.Segment && d.LongStart <= first.ExprStart && d.TokenStart >= first.ExprEnd);
        if (!usedLater && !nested)
        {
            var scope = isAbstract ? " in the Abstract" : string.Empty;
            var message = $"{StyleText.Q(key)} is defined but not used again{scope}; consider writing out {StyleText.Q(first.LongForm)} without the abbreviation.";
            if (first.LongFirst)
            {
                var removeLength = first.ExprEnd - first.RemoveStart;
                var removed = first.Segment.Text.Substring(first.RemoveStart, removeLength);
                sink.Add(first.Segment, first.RemoveStart, removeLength, IssueSeverity.Info, message,
                    first.SimpleParen ? string.Empty : null, $"Remove {StyleText.Q(removed.Trim())}");
            }
            else
            {
                sink.Add(first.Segment, first.ExprStart, first.ExprEnd - first.ExprStart, IssueSeverity.Info, message,
                    first.LongForm, $"Change to {StyleText.Q(first.LongForm)}");
            }
        }
    }

    private static List<Definition> FindDefinitions(StyleSegment segment)
    {
        var text = segment.Text;
        var result = new List<Definition>();

        // "full words (ABBR)"
        foreach (Match m in ParenRegex.Matches(text))
        {
            var abbr = m.Groups["abbr"];
            var key = KeyOf(abbr.Value);
            if (key is null)
            {
                continue;
            }

            var end = m.Index;
            while (end > 0 && char.IsWhiteSpace(text[end - 1]))
            {
                end--;
            }

            var letters = key.Count(char.IsLetterOrDigit);
            var maxWords = Math.Min(letters + 5, letters * 2);
            var pos = end;
            var words = 0;
            var skipped = new List<(int Start, int End)>();
            while (pos > 0 && words < maxWords)
            {
                var p = pos;
                while (p > 0 && char.IsWhiteSpace(text[p - 1]))
                {
                    p--;
                }

                if (p == 0)
                {
                    break;
                }

                if (text[p - 1] == ')')
                {
                    // An earlier "(ABBR)" inside the long form, as in "uncrewed aerial vehicle (UAV) laser scanning (ULS)".
                    var group = ParenGroupBeforeRegex.Match(text[..p]);
                    if (group.Success)
                    {
                        skipped.Add((group.Index, p));
                        pos = group.Index;
                        continue;
                    }

                    break;
                }

                if (IsWindowStop(text[p - 1]))
                {
                    break;
                }

                var q = p;
                while (q > 0 && !char.IsWhiteSpace(text[q - 1]) && !IsWindowStop(text[q - 1]))
                {
                    q--;
                }

                pos = q;
                words++;
            }

            if (pos >= end)
            {
                continue;
            }

            var windowChars = text[pos..end].ToCharArray();
            foreach (var (s, e) in skipped)
            {
                for (var k = s; k < e; k++)
                {
                    windowChars[k - pos] = ' ';
                }
            }

            var window = new string(windowChars);
            var startInWindow = FindLongFormStart(key, window);
            if (startInWindow < 0)
            {
                continue;
            }

            var longStart = pos + startInWindow;
            var longForm = text[longStart..end];
            if (longForm.Length <= key.Length || !longForm.Any(char.IsLower) || KeyOf(longForm.Split(' ')[0]) == key)
            {
                continue;
            }

            result.Add(new Definition(segment, key, abbr.Value, abbr.Index, longStart, m.Index + m.Length, longForm, longStart,
                true, end, !m.Groups["rest"].Success && m.Length == abbr.Length + 2));
        }

        // "ABBR (full words)"
        foreach (Match m in AbbrFirstRegex.Matches(text))
        {
            var abbr = m.Groups["abbr"];
            var key = KeyOf(abbr.Value);
            if (key is null || result.Any(d => d.TokenStart == abbr.Index || (abbr.Index >= d.ExprStart && abbr.Index < d.ExprEnd)))
            {
                continue;
            }

            var longForm = m.Groups["long"].Value.Trim();
            if ((!longForm.Contains(' ') && !longForm.Contains('-')) || !longForm.Any(char.IsLower) || FindLongFormStart(key, longForm) != 0)
            {
                continue;
            }

            var longStart = m.Groups["long"].Index + (m.Groups["long"].Value.Length - m.Groups["long"].Value.TrimStart().Length);
            result.Add(new Definition(segment, key, abbr.Value, abbr.Index, abbr.Index, m.Index + m.Length, longForm, longStart,
                false, abbr.Index + abbr.Length, false));
        }

        return result.OrderBy(d => d.ExprStart).ToList();
    }
}

// ---------------------------------------------------------------------------------------------------------------------
// Rule 2: terminology consistency.
// ---------------------------------------------------------------------------------------------------------------------

/// <summary>One family of variant spellings; the minority variants are reported with a fix to the dominant one.</summary>
internal sealed class StyleTermGroup
{
    public StyleTermGroup(Regex pattern, Func<StyleSegment, Match, string?> classify, Func<StyleSegment, Match, string, string?> rewrite, bool displayKey)
    {
        Pattern = pattern;
        Classify = classify;
        Rewrite = rewrite;
        DisplayKey = displayKey;
    }

    public Regex Pattern { get; }

    /// <summary>Returns the variant key of a match, or null when the match does not count.</summary>
    public Func<StyleSegment, Match, string?> Classify { get; }

    /// <summary>Returns the replacement text that turns the match into the target variant, or null when there is none.</summary>
    public Func<StyleSegment, Match, string, string?> Rewrite { get; }

    /// <summary>True when the key itself is the display text (otherwise the first occurrence is shown).</summary>
    public bool DisplayKey { get; }
}

/// <summary>Terminology consistency and the R² notation.</summary>
internal static class StyleTerminologyRule
{
    private static readonly Regex NounFollowRegex = StyleRegex.Create(@"\G(?:\s*(?:[,.;:)!?\]￼]|$)|\s+(?:is|are|was|were|of|from|with|in|to|for|that|which|by|as|at|on|into|has|have|had)\b)");
    private static readonly Regex LeafContextRegex = StyleRegex.Create(@"\G(?:\s*(?:[,.;:)/\]]|$)|[ \t]+(?:and|or|vs\.?|versus|conditions?|seasons?|period|data|datasets?|acquisitions?|surveys?|scans?|scanning|imagery|images?|flights?|point|states?|canopy|canopies|stands?|trees?|videos?|photos?|photographs?|reconstructions?|models?)\b)");
    private static readonly Regex RSquaredRegex = StyleRegex.Create(@"(?<![A-Za-z0-9_^])(?<r>[Rr])(?<sup>\^2\^|²|\^2(?![\^\d])|2)(?![A-Za-z0-9_^])");
    private static readonly Regex RTwoContextAfterRegex = StyleRegex.Create(@"\G(?:\s*[=<>≤≥≈]|[ \t]+(?:of|values?|ranged|between|scores?)\b)");
    private static readonly Regex RTwoContextBeforeRegex = StyleRegex.Create(@"(?:^|[^A-Za-z])(?:adjusted|adj\.|an|the|of|and|high|low|higher|lower|mean|coefficient of determination)[ \t]*\(?[ \t]*$", RegexOptions.IgnoreCase);

    private static readonly IReadOnlyList<StyleTermGroup> Groups = BuildGroups();

    private static IReadOnlyList<StyleTermGroup> BuildGroups()
    {
        bool NounPosition(StyleSegment s, Match m, string raw)
            => !raw.Contains('-') || m.Groups["pl"].Success || NounFollowRegex.IsMatch(s.Text, m.Index + m.Length);

        bool LeafContext(StyleSegment s, Match m, string raw)
            => raw.Contains('-') || LeafContextRegex.IsMatch(s.Text, m.Index + m.Length);

        return
        [
            Simple(["lidar", "LiDAR", "LIDAR", "Lidar"]),
            Simple(["point cloud", "point-cloud"], accept: NounPosition),
            Simple(["leaf-on", "leaf on"], plural: false, accept: LeafContext),
            Simple(["leaf-off", "leaf off"], plural: false, accept: LeafContext),
            Simple(["UAV", "UAS", "RPAS"], ignoreCase: false),
            Simple(["structure from motion", "structure-from-motion", "Structure from Motion", "Structure-from-Motion", "Structure from motion", "Structure-from-motion"], plural: false),
            Simple(["DBH", "dbh"]),
            Simple(["diameter at breast height", "diameter-at-breast-height", "breast height diameter", "breast-height diameter"]),
            Simple(["canopy height model", "Canopy Height Model", "Canopy height model", "canopy-height model"]),
            Simple(["ground truth", "ground-truth", "groundtruth"], accept: NounPosition),
            Simple(["dataset", "data set"]),
            Simple(["multispectral", "multi-spectral"], plural: false),
            Simple(["hyperspectral", "hyper-spectral"], plural: false),
            Simple(["photogrammetry", "photo-grammetry"], plural: false),
            Simple(["photogrammetric", "photo-grammetric"], plural: false),
            Sentinel(),
            Landsat(),
        ];
    }

    private static StyleTermGroup Simple(string[] variants, bool ignoreCase = true, bool plural = true, Func<StyleSegment, Match, string, bool>? accept = null)
    {
        var set = new HashSet<string>(variants, StringComparer.Ordinal);
        var alternatives = variants
            .Select(v => ignoreCase ? v.ToLowerInvariant() : v)
            .Distinct(StringComparer.Ordinal)
            .OrderByDescending(v => v.Length)
            .Select(v => Regex.Escape(v).Replace("\\ ", "[ \\t]+", StringComparison.Ordinal));
        var pattern = $@"(?<![A-Za-z0-9])(?<base>{string.Join("|", alternatives)}){(plural ? "(?<pl>s)?" : string.Empty)}(?![A-Za-z0-9])";
        var regex = StyleRegex.Create(pattern, ignoreCase ? RegexOptions.IgnoreCase : RegexOptions.None);

        string? Classify(StyleSegment s, Match m)
        {
            var raw = StyleText.NormalizeSpaces(m.Groups["base"].Value);
            if (accept is not null && !accept(s, m, raw))
            {
                return null;
            }

            if (char.IsUpper(raw[0]) && s.IsSentenceStart(m.Index))
            {
                var lowered = StyleText.LowerFirst(raw);
                if (set.Contains(lowered))
                {
                    return lowered;
                }
            }

            return set.Contains(raw) ? raw : null;
        }

        string? Rewrite(StyleSegment s, Match m, string target)
        {
            var raw = m.Groups["base"].Value;
            var result = char.IsUpper(raw[0]) && char.IsLower(target[0]) && s.IsSentenceStart(m.Index) ? StyleText.UpperFirst(target) : target;
            return result + (m.Groups["pl"].Success ? m.Groups["pl"].Value : string.Empty);
        }

        return new StyleTermGroup(regex, Classify, Rewrite, true);
    }

    private static StyleTermGroup Sentinel()
    {
        var regex = StyleRegex.Create(@"(?<![A-Za-z0-9])(?:Sentinel(?<sep>[- ]?)(?<n>[1-6][AB]?)|(?<s2>S2)(?=[ \t]+(?:MSI|imagery|images?|data|bands?|L1C|L2A|scenes?|tiles?|reflectance|surface)\b))(?![A-Za-z0-9])");
        return new StyleTermGroup(regex,
            (_, m) => m.Groups["s2"].Success ? "S2" : "Sentinel" + m.Groups["sep"].Value,
            (_, m, target) =>
            {
                var n = m.Groups["n"].Success ? m.Groups["n"].Value : "2";
                if (target == "S2")
                {
                    return n == "2" ? "S2" : null;
                }

                return target + n;
            },
            false);
    }

    private static StyleTermGroup Landsat()
    {
        var regex = StyleRegex.Create(@"(?<![A-Za-z0-9])Landsat(?<sep>[- ]?)(?<n>[1-9])(?![0-9A-Za-z])");
        return new StyleTermGroup(regex, (_, m) => "Landsat" + m.Groups["sep"].Value, (_, m, target) => target + m.Groups["n"].Value, false);
    }

    public static void Run(List<StyleSegment> segments, StyleIssueSink sink)
    {
        var prose = segments.Where(s => s.IsProse).ToList();
        foreach (var group in Groups)
        {
            var occurrences = new List<(StyleSegment Segment, Match Match, string Key)>();
            foreach (var segment in prose)
            {
                foreach (Match m in group.Pattern.Matches(segment.Text))
                {
                    var key = group.Classify(segment, m);
                    if (key is not null)
                    {
                        occurrences.Add((segment, m, key));
                    }
                }
            }

            var counts = occurrences.GroupBy(o => o.Key, StringComparer.Ordinal)
                .Select(g => (Key: g.Key, Count: g.Count(), First: g.First()))
                .ToList();
            if (counts.Count < 2)
            {
                continue;
            }

            var dominant = counts.OrderByDescending(c => c.Count).ThenBy(c => occurrences.FindIndex(o => o.Key == c.Key)).First();
            string Display((string Key, int Count, (StyleSegment Segment, Match Match, string Key) First) c)
            {
                var text = group.DisplayKey ? c.Key : StyleText.NormalizeSpaces(c.First.Match.Value);
                if (group.DisplayKey && c.First.Match.Groups["pl"].Success)
                {
                    text = c.Key; // show the singular form
                }

                return StyleText.Q(text);
            }

            var summary = StyleText.JoinAnd(counts.Select(c => $"{Display(c)} ({StyleText.Times(c.Count)})").ToList());
            var dominantDisplay = Display(dominant);
            foreach (var (segment, match, key) in occurrences)
            {
                if (key == dominant.Key)
                {
                    continue;
                }

                var fix = group.Rewrite(segment, match, dominant.Key);
                sink.Add(segment, match.Index, match.Length, IssueSeverity.Info,
                    $"Inconsistent terminology: the manuscript uses {summary}; use {dominantDisplay} throughout.", fix);
            }
        }

        RunRSquared(prose, sink);
    }

    private static void RunRSquared(List<StyleSegment> prose, StyleIssueSink sink)
    {
        var caretUsed = false;
        var unicode = new List<(StyleSegment Segment, Match Match)>();
        foreach (var segment in prose)
        {
            foreach (Match m in RSquaredRegex.Matches(segment.Text))
            {
                var r = m.Groups["r"].Value;
                var sup = m.Groups["sup"].Value;
                switch (sup)
                {
                    case "^2^":
                        caretUsed = true;
                        break;
                    case "²":
                        if (r == "R")
                        {
                            unicode.Add((segment, m));
                        }

                        break;
                    case "^2":
                        sink.Add(segment, m.Index, m.Length, IssueSeverity.Info,
                            $"The superscript is not closed; write {StyleText.Q($"{r}^2^")}.", $"{r}^2^");
                        break;
                    default:
                        var before = segment.Text[Math.Max(0, m.Index - 40)..m.Index];
                        if (RTwoContextAfterRegex.IsMatch(segment.Text, m.Index + m.Length) || RTwoContextBeforeRegex.IsMatch(before))
                        {
                            sink.Add(segment, m.Index, m.Length, IssueSeverity.Info,
                                $"Write the exponent as a superscript ({StyleText.Q($"{r}^2^")}).", $"{r}^2^");
                        }

                        break;
                }
            }
        }

        if (caretUsed)
        {
            foreach (var (segment, m) in unicode)
            {
                sink.Add(segment, m.Index, m.Length, IssueSeverity.Info,
                    "Both “R²” and “R^2^” are used; write R² consistently as “R^2^”.", "R^2^");
            }
        }
    }
}

// ---------------------------------------------------------------------------------------------------------------------
// Rule 3: units.
// ---------------------------------------------------------------------------------------------------------------------

/// <summary>SI and journal unit style.</summary>
internal static class StyleUnitRule
{
    private const string GluedUnits = "km|cm|mm|µm|μm|nm|m|ha|kg|Mg|kHz|MHz|GHz|Hz|ms|min|kW|GB|MB|TB|MP";

    private static readonly Regex GluedRegex = StyleRegex.Create($@"(?<![A-Za-z0-9_.,^~\-−/])(?<num>\d+(?:\.\d+)?)(?<unit>{GluedUnits})(?<exp>\^-?[23]\^|[23])?(?![A-Za-z0-9_µμ])");
    private static readonly Regex DegreeRegex = StyleRegex.Create(@"(?<![\d.,])(?<num>\d+(?:\.\d+)?)(?<unit>°[CF]|℃)(?![A-Za-z])");
    private static readonly Regex SlashRegex = StyleRegex.Create(@"(?<![A-Za-z0-9_/^])(?<num>stems|trees|points|pulses|returns|pts|Mg|kg|km|cm|mm|m\^[23]\^|m[23]|m|t|g)/(?<den>ha|km\^2\^|km2|m\^[23]\^|m[23]|km|m|s|h|yr)(?![A-Za-z0-9_^/])");
    private static readonly Regex NegativeExponentRegex = StyleRegex.Create(@"(?<![A-Za-z0-9_/^])(?<num>stems|trees|points|pulses|returns|pts|Mg|kg|km|cm|mm|m\^[23]\^|m|t|g)[ \t](?<den>ha|km|m|s|h|yr)\^[-−](?<e>[1-3])\^");
    private static readonly Regex OpenCaretRegex = StyleRegex.Create(@"(?<![A-Za-z0-9_])(?<u>km|cm|mm|ha|yr|m|s)\^(?<e>[-−]?[1-3])(?![\^\d])");
    private static readonly Regex PlainExponentRegex = StyleRegex.Create(@"(?<=\d[ \t]|\bper[ \t])(?<u>km|cm|mm|m)(?<e>[23])(?![A-Za-z0-9_^])");
    private static readonly Regex SquareRegex = StyleRegex.Create(@"(?<![A-Za-z])sq\.?[ \t]?(?<u>km|cm|mm|m)(?![A-Za-z0-9])");
    private static readonly Regex TimesRegex = StyleRegex.Create(@"(?<=\d(?:[ \t]?(?:km|cm|mm|m))?[ \t]?)x(?=[ \t]?\d)");
    private static readonly Regex MinusRegex = StyleRegex.Create(@"(?<=(?:^|[\s(\[=<>≈:;,]))(?<!\d\s)-(?=\d)");
    private static readonly Regex RangeRegex = StyleRegex.Create(@"(?<![A-Za-z0-9_.,^~/:\-−–])(?<a>\d+(?:\.\d+)?)-(?<b>\d+(?:\.\d+)?)(?=[ \t]?(?:km|cm|mm|m|ha|kg|Mg|kHz|MHz|GHz|Hz|ms|s|min|h|yr|years|°C|°|%|trees|stems|points|pts)(?![A-Za-z0-9]))");
    private static readonly Regex SpellingRegex = StyleRegex.Create(@"(?<![A-Za-z])(?<pre>kilo|centi|milli|micro)?(?<root>met|lit)(?<end>er|re)(?<pl>s)?(?![A-Za-z])", RegexOptions.IgnoreCase);

    public static void Run(List<StyleSegment> segments, StyleCheckOptions options, StyleIssueSink sink)
    {
        var spelling = new List<(StyleSegment Segment, Match Match, bool Us)>();
        foreach (var s in segments)
        {
            var text = s.Text;
            foreach (Match m in GluedRegex.Matches(text))
            {
                var exp = m.Groups["exp"].Value;
                if (exp.Length == 1)
                {
                    exp = $"^{exp}^";
                }

                var fix = $"{m.Groups["num"].Value} {m.Groups["unit"].Value}{exp}";
                sink.Add(s, m.Index, m.Length, IssueSeverity.Info, $"Separate the number from the unit with a space ({StyleText.Q(fix)}).", fix);
            }

            foreach (Match m in DegreeRegex.Matches(text))
            {
                var fix = $"{m.Groups["num"].Value} {(m.Groups["unit"].Value == "℃" ? "°C" : m.Groups["unit"].Value)}";
                sink.Add(s, m.Index, m.Length, IssueSeverity.Info, $"SI style puts a space between the number and the temperature unit ({StyleText.Q(fix)}).", fix);
            }

            if (options.PreferNegativeExponents)
            {
                foreach (Match m in SlashRegex.Matches(text))
                {
                    var num = NormalizeExponent(m.Groups["num"].Value);
                    var den = m.Groups["den"].Value;
                    var baseUnit = den.TrimEnd('2', '3', '^');
                    var power = den.Contains('3') ? "3" : den.Contains('2') ? "2" : "1";
                    if (den.StartsWith("km", StringComparison.Ordinal))
                    {
                        baseUnit = "km";
                    }

                    var fix = $"{num} {baseUnit}^-{power}^";
                    sink.Add(s, m.Index, m.Length, IssueSeverity.Info, $"Write derived units with negative exponents ({StyleText.Q(fix)}).", fix);
                }
            }
            else
            {
                foreach (Match m in NegativeExponentRegex.Matches(text))
                {
                    var e = m.Groups["e"].Value;
                    var fix = $"{m.Groups["num"].Value}/{m.Groups["den"].Value}{(e == "1" ? string.Empty : $"^{e}^")}";
                    sink.Add(s, m.Index, m.Length, IssueSeverity.Info, $"Write derived units with a slash ({StyleText.Q(fix)}).", fix);
                }
            }

            foreach (Match m in OpenCaretRegex.Matches(text))
            {
                var fix = $"{m.Groups["u"].Value}^{m.Groups["e"].Value}^";
                sink.Add(s, m.Index, m.Length, IssueSeverity.Info, $"The superscript is not closed; write {StyleText.Q(fix)}.", fix);
            }

            foreach (Match m in PlainExponentRegex.Matches(text))
            {
                var fix = $"{m.Groups["u"].Value}^{m.Groups["e"].Value}^";
                sink.Add(s, m.Index, m.Length, IssueSeverity.Info, $"Write the exponent as a superscript ({StyleText.Q(fix)}).", fix);
            }

            foreach (Match m in SquareRegex.Matches(text))
            {
                var fix = $"{m.Groups["u"].Value}^2^";
                sink.Add(s, m.Index, m.Length, IssueSeverity.Info, $"Write square units with an exponent ({StyleText.Q(fix)}).", fix);
            }

            foreach (Match m in TimesRegex.Matches(text))
            {
                sink.Add(s, m.Index, m.Length, IssueSeverity.Info, "Use the multiplication sign “×” rather than the letter “x” between numbers.", "×", "Change to “×”");
            }

            foreach (Match m in MinusRegex.Matches(text))
            {
                sink.Add(s, m.Index, m.Length, IssueSeverity.Info, "Use a true minus sign “−” rather than a hyphen before a negative number.", "−", "Change to “−”");
            }

            foreach (Match m in RangeRegex.Matches(text))
            {
                var dash = m.Groups["a"].Index + m.Groups["a"].Length;
                sink.Add(s, dash, 1, IssueSeverity.Info,
                    $"Use an en dash for numeric ranges ({StyleText.Q($"{m.Groups["a"].Value}–{m.Groups["b"].Value}")}).", "–", "Change to “–”");
            }

            if (s.IsProse)
            {
                foreach (Match m in SpellingRegex.Matches(text))
                {
                    spelling.Add((s, m, m.Groups["end"].Value.Equals("er", StringComparison.OrdinalIgnoreCase)));
                }
            }
        }

        var us = spelling.Count(x => x.Us);
        var uk = spelling.Count - us;
        if (us > 0 && uk > 0)
        {
            var preferUs = us > uk || (us == uk && spelling[0].Us);
            var summary = $"US spelling (“-meter”, “-liter”) {StyleText.Times(us)} and British spelling (“-metre”, “-litre”) {StyleText.Times(uk)}";
            foreach (var (segment, m, isUs) in spelling.Where(x => x.Us != preferUs))
            {
                var end = m.Groups["end"].Value;
                var swapped = end switch { "er" => "re", "re" => "er", "ER" => "RE", "RE" => "ER", _ => preferUs ? "er" : "re" };
                var fix = m.Groups["pre"].Value + m.Groups["root"].Value + swapped + m.Groups["pl"].Value;
                sink.Add(segment, m.Index, m.Length, IssueSeverity.Info, $"Unit spelling is inconsistent: the manuscript uses {summary}; use one spelling.", fix);
            }
        }
    }

    private static string NormalizeExponent(string unit) => unit is "m2" or "m3" ? $"m^{unit[1]}^" : unit;
}

// ---------------------------------------------------------------------------------------------------------------------
// Rule 4: species names.
// ---------------------------------------------------------------------------------------------------------------------

/// <summary>Italic binomials with a roman “spp.”, and lower-case species epithets.</summary>
internal static class StyleSpeciesRule
{
    public static readonly IReadOnlyList<string> Genera =
    [
        "Pseudotsuga", "Thuja", "Tsuga", "Picea", "Pinus", "Abies", "Larix", "Acer", "Alnus", "Betula", "Populus", "Quercus",
        "Fagus", "Fraxinus", "Eucalyptus", "Cedrus", "Chamaecyparis", "Callitropsis", "Cupressus", "Arbutus", "Prunus", "Salix",
        "Sequoia", "Sequoiadendron", "Juniperus", "Taxus", "Ulmus", "Tilia", "Carpinus", "Castanea", "Liriodendron",
        "Liquidambar", "Platanus", "Magnolia", "Cornus", "Ilex", "Sorbus", "Malus", "Pyrus", "Robinia", "Aesculus", "Juglans",
        "Carya", "Nyssa", "Ostrya", "Corylus", "Crataegus", "Gleditsia", "Catalpa", "Paulownia", "Ailanthus", "Metasequoia",
        "Taxodium", "Cryptomeria", "Cunninghamia", "Calocedrus", "Araucaria", "Podocarpus", "Nothofagus", "Tectona", "Acacia",
        "Shorea", "Swietenia", "Hevea", "Rhizophora", "Ginkgo", "Frangula", "Amelanchier", "Sambucus", "Gaultheria", "Mahonia",
        "Rubus", "Vaccinium", "Oplopanax", "Polystichum", "Holodiscus", "Symphoricarpos", "Oemleria", "Rhamnus", "Picrasma",
    ];

    private static readonly HashSet<string> KnownEpithets = new(StringComparer.Ordinal)
    {
        "menziesii", "plicata", "heterophylla", "mertensiana", "sitchensis", "glauca", "mariana", "abies", "engelmannii",
        "contorta", "ponderosa", "sylvestris", "strobus", "banksiana", "resinosa", "monticola", "taeda", "radiata", "pinaster",
        "nigra", "grandis", "amabilis", "lasiocarpa", "balsamea", "alba", "occidentalis", "decidua", "laricina", "macrophyllum",
        "circinatum", "saccharum", "rubrum", "pseudoplatanus", "rubra", "glutinosa", "papyrifera", "pendula", "pubescens",
        "tremuloides", "trichocarpa", "tremula", "garryana", "robur", "petraea", "sylvatica", "grandifolia", "excelsior",
        "americana", "latifolia", "globulus", "deodara", "atlantica", "nootkatensis", "lawsoniana", "emarginata", "avium",
        "sempervirens", "giganteum", "communis", "scopulorum", "brevifolia", "baccata", "cordata", "betulus", "sativa",
        "tulipifera", "styraciflua", "grandiflora", "nuttallii", "aquifolium", "shallon", "spectabilis", "munitum", "decurrens",
        "purshiana", "nervosa", "horridus", "discolor", "albus", "parviflorum", "aucuparia", "glyptostroboides", "biloba",
    };

    private static readonly HashSet<string> NotEpithets = new(StringComparer.Ordinal)
    {
        "and", "or", "the", "of", "in", "on", "at", "by", "for", "with", "from", "to", "trees", "tree", "stands", "stand",
        "species", "forest", "forests", "plots", "plot", "was", "were", "is", "are", "has", "have", "had", "which", "that",
        "this", "these", "those", "thus", "plus", "versus", "via", "data", "area", "areas", "extra", "per", "over", "under",
        "after", "other", "either", "neither", "rather", "whether", "never", "ever", "together", "for", "nor", "our", "four",
        "your", "upon", "common", "region", "sites", "crowns", "needles", "leaves", "cones", "seedlings", "saplings", "stems",
        "canopy", "mixed", "mostly", "also", "but", "not", "can", "may", "while", "where", "within", "across", "between",
        "along", "genus", "genera", "individuals", "dominated", "dominant", "spp", "var", "subsp", "ssp", "similar", "major",
        "minor", "interior", "exterior", "focus", "bonus", "status", "apparatus", "corpus", "virus", "census", "consensus",
        "basis", "analysis", "axis", "emphasis", "hypothesis", "crisis", "series", "less", "unless", "across", "else",
        "plantation", "plantations", "cover", "litter", "water", "winter", "summer", "after", "power", "colour", "color",
        "error", "sensor", "factor", "vector", "sector", "author", "major", "prior", "under", "inner", "outer", "upper", "lower",
    };

    private static readonly Regex LatinEndingRegex = StyleRegex.Create(@"(?:a|ae|um|us|is|ii|i|es|er|or|ur|ix|ex|ens|ans|on|os|ys|x)$");

    private static readonly string GenusAlternation = string.Join("|", Genera.OrderByDescending(g => g.Length));

    private static readonly Regex BinomialRegex = StyleRegex.Create($@"(?<![A-Za-z])(?<genus>{GenusAlternation})(?<s1>[ \t]+)(?<ep>[a-z]+(?:-[a-z]+)?)(?:(?<infra>[ \t]+(?:var|subsp|ssp|f)\.[ \t]+)(?<ep2>[a-z]+(?:-[a-z]+)?))?(?![A-Za-z])");
    private static readonly Regex GenusSppRegex = StyleRegex.Create($@"(?<![A-Za-z])(?<genus>{GenusAlternation})[ \t]+(?<spp>spp?\.)");
    private static readonly Regex CapitalEpithetRegex = StyleRegex.Create($@"(?<![A-Za-z])(?<genus>{GenusAlternation})(?<s1>[ \t]+)(?<ep>[A-Z][a-z]{{2,}}(?:-[a-z]+)?)(?![A-Za-z])");
    private static readonly Regex AbbreviatedRegex = StyleRegex.Create(@"(?<![A-Za-z.])(?<ini>[A-Z])\.(?<s1>[ \t]?)(?<ep>[a-z]{3,}(?:-[a-z]+)?)(?![A-Za-z])");

    private static bool IsEpithet(string word)
        => word.Length >= 3 && (KnownEpithets.Contains(word) || (!NotEpithets.Contains(word) && LatinEndingRegex.IsMatch(word)));

    public static void Run(List<StyleSegment> segments, StyleIssueSink sink)
    {
        var binomials = new HashSet<string>(StringComparer.Ordinal);
        foreach (var s in segments)
        {
            var text = s.Text;
            foreach (Match m in BinomialRegex.Matches(text))
            {
                var ep = m.Groups["ep"];
                if (!IsEpithet(ep.Value))
                {
                    continue;
                }

                var genus = m.Groups["genus"].Value;
                binomials.Add($"{genus[0]}|{ep.Value}");
                var hasInfra = m.Groups["infra"].Success && IsEpithet(m.Groups["ep2"].Value);
                var end = hasInfra ? m.Groups["ep2"].Index + m.Groups["ep2"].Length : ep.Index + ep.Length;
                var length = end - m.Index;
                var binomialLength = ep.Index + ep.Length - m.Index;
                var italicOk = s.AllItalic(m.Index, binomialLength) && (!hasInfra || s.AllItalic(m.Groups["ep2"].Index, m.Groups["ep2"].Length));
                if (italicOk)
                {
                    continue;
                }

                var fix = $"*{text.Substring(m.Index, binomialLength)}*";
                if (hasInfra)
                {
                    fix += $"{m.Groups["infra"].Value}*{m.Groups["ep2"].Value}*";
                }

                var anyItalic = s.AnyItalic(m.Index, length);
                sink.Add(s, m.Index, length, IssueSeverity.Warning,
                    $"Species names should be in italics ({StyleText.Q(fix)}).", anyItalic ? null : fix, $"Italicise {StyleText.Q(text.Substring(m.Index, length))}");
            }

            foreach (Match m in GenusSppRegex.Matches(text))
            {
                var genus = m.Groups["genus"];
                var spp = m.Groups["spp"];
                var genusItalic = s.AllItalic(genus.Index, genus.Length);
                var sppItalic = s.AnyItalic(spp.Index, spp.Length);
                var preferred = $"*{genus.Value}* {spp.Value}";
                if (!genusItalic && !sppItalic)
                {
                    sink.Add(s, genus.Index, genus.Length, IssueSeverity.Warning,
                        $"The genus name should be in italics while {StyleText.Q(spp.Value)} stays roman ({StyleText.Q(preferred)}).", $"*{genus.Value}*", $"Italicise {StyleText.Q(genus.Value)}");
                }
                else if (sppItalic)
                {
                    // Offer a fix only for the simple "*Genus spp.*" form.
                    string? fix = null;
                    var originalText = s.Block.Text ?? string.Empty;
                    if (s.TryMapSpan(genus.Index, spp.Index + spp.Length - genus.Index, out var os, out var ol, out var contiguous)
                        && contiguous && os > 0 && os + ol < originalText.Length && originalText[os - 1] == '*' && originalText[os + ol] == '*'
                        && (os < 2 || originalText[os - 2] != '*') && (os + ol + 1 >= originalText.Length || originalText[os + ol + 1] != '*'))
                    {
                        fix = $"*{genus.Value}*{text.Substring(genus.Index + genus.Length, spp.Index - genus.Index - genus.Length)}{spp.Value}";
                        sink.AddOriginal(s, os - 1, ol + 2, IssueSeverity.Info,
                            $"{StyleText.Q(spp.Value)} should not be in italics ({StyleText.Q(preferred)}).", fix);
                    }
                    else
                    {
                        sink.Add(s, spp.Index, spp.Length, IssueSeverity.Info,
                            $"{StyleText.Q(spp.Value)} should not be in italics ({StyleText.Q(preferred)}).");
                    }
                }
            }

            foreach (Match m in CapitalEpithetRegex.Matches(text))
            {
                var ep = m.Groups["ep"];
                var lowered = ep.Value.ToLowerInvariant();
                if (!KnownEpithets.Contains(lowered))
                {
                    continue;
                }

                var genus = m.Groups["genus"].Value;
                var correct = $"{genus}{m.Groups["s1"].Value}{lowered}";
                if (s.AllItalic(m.Index, m.Length))
                {
                    sink.Add(s, ep.Index, ep.Length, IssueSeverity.Info,
                        $"The species epithet is written in lower case ({StyleText.Q(correct)}).", lowered);
                }
                else
                {
                    sink.Add(s, m.Index, m.Length, IssueSeverity.Info,
                        $"The species epithet is written in lower case and the name in italics ({StyleText.Q($"*{correct}*")}).",
                        s.AnyItalic(m.Index, m.Length) ? null : $"*{correct}*");
                }
            }
        }

        // "P. menziesii" only when the full binomial appears somewhere in the manuscript.
        foreach (var s in segments)
        {
            foreach (Match m in AbbreviatedRegex.Matches(s.Text))
            {
                if (!binomials.Contains($"{m.Groups["ini"].Value}|{m.Groups["ep"].Value}") || s.AllItalic(m.Index, m.Length))
                {
                    continue;
                }

                var fix = $"*{m.Value}*";
                sink.Add(s, m.Index, m.Length, IssueSeverity.Warning, $"Species names should be in italics ({StyleText.Q(fix)}).",
                    s.AnyItalic(m.Index, m.Length) ? null : fix, $"Italicise {StyleText.Q(m.Value)}");
            }
        }
    }
}

// ---------------------------------------------------------------------------------------------------------------------
// Rule 5: software versions and satellite instruments.
// ---------------------------------------------------------------------------------------------------------------------

/// <summary>Software should be cited with a version; Sentinel-2 and Landsat 8/9 with their instrument.</summary>
internal static class StyleSoftwareRule
{
    private sealed record Software(string Name, string Example, Regex Pattern);

    private static Regex Word(string body) => StyleRegex.Create($@"(?<![A-Za-z0-9])(?:{body})(?![A-Za-z0-9])");

    private static readonly IReadOnlyList<Software> Catalogue =
    [
        new("COLMAP", "COLMAP 3.9", Word("COLMAP")),
        new("CloudCompare", "CloudCompare 2.13", Word("CloudCompare")),
        new("LAStools", "LAStools 2.0", Word("LAStools")),
        new("lidR", "lidR 4.1", Word("lidR")),
        new("Python", "Python 3.11", Word("Python")),
        new("MATLAB", "MATLAB R2023b", Word("MATLAB|Matlab")),
        new("Agisoft Metashape", "Agisoft Metashape 2.1", Word(@"(?:Agisoft[ \t]+)?(?:Metashape|PhotoScan)(?:[ \t]+Pro(?:fessional)?)?")),
        new("Pix4D", "Pix4Dmapper 4.8", Word("Pix4D(?:mapper|matic|fields|cloud)?")),
        new("RealityCapture", "RealityCapture 1.3", Word("RealityCapture")),
        new("OpenMVS", "OpenMVS 2.1", Word("OpenMVS")),
        new("Meshroom", "Meshroom 2023.3", Word("Meshroom")),
        new("QGIS", "QGIS 3.34", Word("QGIS")),
        new("ArcGIS", "ArcGIS Pro 3.2", Word(@"ArcGIS(?:[ \t]+(?:Pro|Desktop))?|ArcMap")),
        new("PyTorch", "PyTorch 2.2", Word("PyTorch")),
        new("TensorFlow", "TensorFlow 2.15", Word("TensorFlow")),
        new("scikit-learn", "scikit-learn 1.4", StyleRegex.Create(@"(?<![A-Za-z0-9\-])(?:scikit-learn|sklearn)(?![A-Za-z0-9\-])")),
        new("PDAL", "PDAL 2.6", Word("PDAL")),
        new("GDAL", "GDAL 3.8", Word("GDAL")),
        new("RiSCAN PRO", "RiSCAN PRO 2.17", Word(@"RiSCAN[ \t]?PRO")),
        new("FUSION", "FUSION 4.50", Word("FUSION(?:/LDV)?")),
        new("TreeLS", "TreeLS 2.0", Word("TreeLS")),
        new("3D Forest", "3D Forest 0.5", Word(@"3D[ \t]?Forest")),
        new("Open3D", "Open3D 0.18", Word("Open3D")),
        new("ENVI", "ENVI 5.7", Word("ENVI")),
        new("eCognition", "eCognition 10.3", Word("eCognition")),
        new("LiDAR360", "LiDAR360 7.0", Word("LiDAR360")),
    ];

    private static readonly Regex VersionRegex = StyleRegex.Create(
        @"(?:\b[vV](?:ersion|er\.?)?\.?[ \t]?\d+(?:\.\d+)*)|(?:\bR20\d\d[ab]\b)|(?:(?<![\d.,])\d+\.\d+(?:\.\d+)*(?![\d.]*\d)(?![ \t]?(?:cm|mm|km|m|ha|%|°|s|px|pixels?|points?|pts|GB|MB|images?|cameras?|frames?)(?![A-Za-z0-9])))");

    private static readonly Regex RTokenRegex = StyleRegex.Create(@"(?<![A-Za-z0-9_^~.\-@\\/])R(?![A-Za-z0-9_^~\-²])");
    private static readonly Regex RFollowRegex = StyleRegex.Create(@"\G(?:[ \t]+(?:software|version|package|packages|statistical|statistics|environment|programming|language|project|scripts?|code|Core[ \t]+Team|v\d|\d+\.\d+)\b|[ \t]*\([ \t]*(?:R[ \t]+Core|version|ver\.|v\d|\d+\.\d+))", RegexOptions.IgnoreCase);
    private static readonly Regex RPrecedeRegex = StyleRegex.Create(@"(?:^|[^A-Za-z])(?:in|using|with|within|via)[ \t]+$", RegexOptions.IgnoreCase);
    private static readonly Regex RAfterPrecedeRegex = StyleRegex.Create(@"\G(?:[ \t]*(?:[,.;:)￼]|$)|[ \t]+(?:and|or|to|for|with|using|v?\d|version|package|packages|software|scripts?|code)\b|[ \t]*\()", RegexOptions.IgnoreCase);

    private static readonly Regex SentinelRegex = StyleRegex.Create(@"(?<![A-Za-z0-9])Sentinel[- ]?2[AB]?(?![A-Za-z0-9])");
    private static readonly Regex MsiRegex = StyleRegex.Create(@"(?<![A-Za-z0-9])MSI(?![A-Za-z0-9])|multi-?spectral[ \t]+instrument", RegexOptions.IgnoreCase);
    private static readonly Regex LandsatRegex = StyleRegex.Create(@"(?<![A-Za-z0-9])Landsat[- ]?(?<n>[89])(?![0-9A-Za-z])");
    private static readonly Regex OliRegex = StyleRegex.Create(@"(?<![A-Za-z0-9])OLI(?:-2)?(?![A-Za-z0-9])|operational[ \t]+land[ \t]+imager", RegexOptions.IgnoreCase);

    public static void Run(List<StyleSegment> segments, StyleIssueSink sink)
    {
        var prose = segments.Where(s => s.IsProse).ToList();
        foreach (var software in Catalogue)
        {
            var mentions = new List<(StyleSegment Segment, int Start, int Length, bool Versioned)>();
            foreach (var s in prose)
            {
                foreach (Match m in software.Pattern.Matches(s.Text))
                {
                    mentions.Add((s, m.Index, m.Length, HasVersion(s, m.Index + m.Length)));
                }
            }

            Report(software.Name, software.Example, mentions, sink);
        }

        var rMentions = new List<(StyleSegment Segment, int Start, int Length, bool Versioned)>();
        foreach (var s in prose)
        {
            foreach (Match m in RTokenRegex.Matches(s.Text))
            {
                var after = m.Index + m.Length;
                var before = s.Text[Math.Max(0, m.Index - 12)..m.Index];
                var isSoftware = RFollowRegex.IsMatch(s.Text, after)
                    || (RPrecedeRegex.IsMatch(before) && RAfterPrecedeRegex.IsMatch(s.Text, after));
                if (isSoftware)
                {
                    rMentions.Add((s, m.Index, m.Length, HasVersion(s, after)));
                }
            }
        }

        Report("R", "R 4.3.1", rMentions, sink);

        var all = string.Join("\n", segments.Select(s => s.Text));
        if (!MsiRegex.IsMatch(all))
        {
            var first = prose.Select(s => (Segment: s, Match: SentinelRegex.Match(s.Text))).FirstOrDefault(x => x.Match.Success);
            if (first.Segment is not null)
            {
                sink.Add(first.Segment, first.Match.Index, first.Match.Length, IssueSeverity.Info,
                    "Name the instrument as well, e.g. “Sentinel-2 MultiSpectral Instrument (MSI)”.");
            }
        }

        if (!OliRegex.IsMatch(all))
        {
            var first = prose.Select(s => (Segment: s, Match: LandsatRegex.Match(s.Text))).FirstOrDefault(x => x.Match.Success);
            if (first.Segment is not null)
            {
                var n = first.Match.Groups["n"].Value;
                sink.Add(first.Segment, first.Match.Index, first.Match.Length, IssueSeverity.Info,
                    $"Name the instrument as well, e.g. “Landsat {n} Operational Land Imager (OLI)”.");
            }
        }
    }

    private static bool HasVersion(StyleSegment s, int after)
    {
        var end = s.SentenceEnd(after, 40);
        if (end <= after)
        {
            return false;
        }

        return VersionRegex.IsMatch(s.Text[after..end]);
    }

    private static void Report(string name, string example, List<(StyleSegment Segment, int Start, int Length, bool Versioned)> mentions, StyleIssueSink sink)
    {
        if (mentions.Count == 0 || mentions.Any(m => m.Versioned))
        {
            return;
        }

        // Abstracts do not give versions: report at the first mention in the main text or a caption.
        var first = mentions.FirstOrDefault(m => m.Segment.Kind != StyleSegmentKind.Abstract);
        if (first.Segment is null)
        {
            return;
        }

        sink.Add(first.Segment, first.Start, first.Length, IssueSeverity.Info, $"Give the version of {name} used (e.g. {StyleText.Q(example)}).");
    }
}

// ---------------------------------------------------------------------------------------------------------------------
// Rule 6: numbers and Latin abbreviations.
// ---------------------------------------------------------------------------------------------------------------------

/// <summary>Number precision and grouping, numerals at sentence start, and “e.g.”, “i.e.”, “et al.”, “etc.”.</summary>
internal static class StyleNumberRule
{
    private static readonly Regex QuantityRegex = StyleRegex.Create(
        @"(?<![A-Za-z0-9])(?<q>rRMSE|RMSE|MAE|MAPE|[Bb]ias|SD|CV|R\^2\^|R²)(?![A-Za-z0-9^])(?:[ \t]+(?:of|was|were|is|values?|ranged|from|between|reached|only|about|approximately|decreased|increased|improved|to))*[ \t]*[=:≈]?[ \t]*(?<num>[-−]?\d+(?:\.\d+)?)(?![\d.]*\d)(?<unit>[ \t]?(?:cm|mm|km|m|%|ha)(?![A-Za-z0-9]))?");
    private static readonly Regex CommaGroupRegex = StyleRegex.Create(@"(?<![\d.,  ])[1-9]\d{0,2}(?:,\d{3})+(?:\.\d+)?(?!\d|,\d)");
    private static readonly Regex ThinGroupRegex = StyleRegex.Create(@"(?<![\d.,  ])[1-9]\d{0,2}(?:[  ]\d{3})+(?:\.\d+)?(?!\d|[  ]\d)");
    private static readonly Regex PlainLargeRegex = StyleRegex.Create(@"(?<![\d.,  ^~\-−/:#A-Za-z_])(?<num>[1-9]\d{4,})(?![\d.,]\d|\d)(?=[ \t]+[a-z]|[ \t]?%)");
    private static readonly Regex PreviousWordRegex = StyleRegex.Create(@"(?<w>[A-Za-z.#]+)[ \t]*$");
    private static readonly Regex SentenceNumeralRegex = StyleRegex.Create(@"(?<![A-Za-z0-9_.,\-−^~])(?<num>\d+(?:\.\d+)?)(?=[ \t]+[a-z]|[ \t]?%)");
    private static readonly Regex YearRegex = StyleRegex.Create(@"^(?:1[5-9]\d\d|20\d\d)$");
    private static readonly Regex EgRegex = StyleRegex.Create(@"(?<![A-Za-z.])(?<x>e\.g|i\.e)(?![.A-Za-z])");
    private static readonly Regex EtAlRegex = StyleRegex.Create(@"(?<![A-Za-z])et(?<d1>\.)?[ \t]+al(?<d2>\.)?(?![A-Za-z])");
    private static readonly Regex EtcRegex = StyleRegex.Create(@"(?<![A-Za-z])etc(?![.A-Za-z])");

    private static readonly HashSet<string> IdentifierWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "plot", "tree", "site", "sample", "station", "tile", "zone", "id", "no.", "no", "number", "epsg", "code", "scene", "path",
        "row", "orbit", "image", "frame", "flight", "file", "version", "release", "build", "sheet", "grid", "lot", "unit", "block",
        "stand", "page", "line", "item", "#",
    };

    public static void Run(List<StyleSegment> segments, StyleIssueSink sink)
    {
        var prose = segments.Where(s => s.IsProse).ToList();
        foreach (var s in prose)
        {
            CheckPrecision(s, sink);
            CheckSentenceNumerals(s, sink);
            CheckLatin(s, sink);
        }

        CheckGrouping(prose, sink);
    }

    private static void CheckPrecision(StyleSegment s, StyleIssueSink sink)
    {
        var groups = new Dictionary<string, List<(int Start, int Length, int Decimals, string Text)>>(StringComparer.Ordinal);
        foreach (Match m in QuantityRegex.Matches(s.Text))
        {
            var q = m.Groups["q"].Value;
            q = q is "R^2^" or "R²" ? "R²" : q.Equals("bias", StringComparison.OrdinalIgnoreCase) ? "Bias" : q;
            var num = m.Groups["num"];
            var dot = num.Value.IndexOf('.', StringComparison.Ordinal);
            var decimals = dot < 0 ? 0 : num.Value.Length - dot - 1;
            var key = q + "|" + m.Groups["unit"].Value.Trim();
            if (!groups.TryGetValue(key, out var list))
            {
                groups[key] = list = [];
            }

            list.Add((num.Index, num.Length, decimals, num.Value));
        }

        foreach (var (key, list) in groups)
        {
            var first = list[0];
            var different = list.FirstOrDefault(x => x.Decimals != first.Decimals);
            if (different.Text is null)
            {
                continue;
            }

            var q = key[..key.IndexOf('|', StringComparison.Ordinal)];
            sink.Add(s, different.Start, different.Length, IssueSeverity.Info,
                $"{q} values in this paragraph are reported with different precision ({first.Text} and {different.Text}); use the same number of decimal places.");
        }
    }

    private static void CheckSentenceNumerals(StyleSegment s, StyleIssueSink sink)
    {
        if (s.Kind == StyleSegmentKind.Caption)
        {
            return;
        }

        foreach (Match m in SentenceNumeralRegex.Matches(s.Text))
        {
            var num = m.Groups["num"].Value;
            if (YearRegex.IsMatch(num) || !s.IsSentenceStart(m.Index))
            {
                continue;
            }

            sink.Add(s, m.Index, m.Length, IssueSeverity.Info, "Avoid starting a sentence with a numeral; spell the number out or rephrase the sentence.");
        }
    }

    private static void CheckLatin(StyleSegment s, StyleIssueSink sink)
    {
        foreach (Match m in EgRegex.Matches(s.Text))
        {
            var fix = m.Value + ".";
            sink.Add(s, m.Index, m.Length, IssueSeverity.Info, $"Add the final full stop ({StyleText.Q(fix)}).", fix);
        }

        foreach (Match m in EtAlRegex.Matches(s.Text))
        {
            if (m.Groups["d1"].Success)
            {
                sink.Add(s, m.Index, m.Length, IssueSeverity.Info, "Write “et al.” without a full stop after “et”.", "et al.");
            }
            else if (!m.Groups["d2"].Success)
            {
                sink.Add(s, m.Index, m.Length, IssueSeverity.Info, "Add the full stop after “al” (“et al.”).", "et al.");
            }
        }

        foreach (Match m in EtcRegex.Matches(s.Text))
        {
            sink.Add(s, m.Index, m.Length, IssueSeverity.Info, "Add the full stop (“etc.”).", "etc.");
        }
    }

    private static void CheckGrouping(List<StyleSegment> prose, StyleIssueSink sink)
    {
        var comma = new List<(StyleSegment Segment, Match Match)>();
        var thin = new List<(StyleSegment Segment, Match Match)>();
        var plain = new List<(StyleSegment Segment, Match Match)>();
        foreach (var s in prose)
        {
            comma.AddRange(CommaGroupRegex.Matches(s.Text).Select(m => (s, m)));
            thin.AddRange(ThinGroupRegex.Matches(s.Text).Select(m => (s, m)));
            foreach (Match m in PlainLargeRegex.Matches(s.Text))
            {
                var previous = PreviousWordRegex.Match(s.Text[Math.Max(0, m.Index - 20)..m.Index]);
                if (previous.Success)
                {
                    var word = previous.Groups["w"].Value;
                    var wordStart = m.Index - (s.Text[Math.Max(0, m.Index - 20)..m.Index].Length - previous.Index);
                    if (IdentifierWords.Contains(word) || IdentifierWords.Contains(word.TrimEnd('.'))
                        || (char.IsUpper(word[0]) && !s.IsSentenceStart(wordStart)))
                    {
                        continue;
                    }
                }

                plain.Add((s, m));
            }
        }

        var thinDominant = thin.Count > comma.Count;
        if (comma.Count > 0 && thin.Count > 0)
        {
            var minority = thinDominant ? comma : thin;
            var summary = $"commas {StyleText.Times(comma.Count)} and thin spaces {StyleText.Times(thin.Count)}";
            foreach (var (s, m) in minority)
            {
                var digits = m.Value.Replace(",", string.Empty, StringComparison.Ordinal).Replace(" ", string.Empty, StringComparison.Ordinal).Replace(" ", string.Empty, StringComparison.Ordinal);
                var fix = Group(digits, thinDominant ? " " : ",");
                sink.Add(s, m.Index, m.Length, IssueSeverity.Info,
                    $"Digit grouping is inconsistent: the manuscript separates thousands with {summary}; use one style.", fix,
                    thinDominant ? "Use a thin space" : $"Change to {StyleText.Q(fix)}");
            }
        }

        foreach (var (s, m) in plain)
        {
            var fix = Group(m.Groups["num"].Value, thinDominant ? " " : ",");
            sink.Add(s, m.Index, m.Length, IssueSeverity.Info,
                $"Group the digits of numbers of 10,000 or more ({StyleText.Q(fix)}).", fix);
        }
    }

    private static string Group(string number, string separator)
    {
        var dot = number.IndexOf('.', StringComparison.Ordinal);
        var integer = dot < 0 ? number : number[..dot];
        var fraction = dot < 0 ? string.Empty : number[dot..];
        var sb = new StringBuilder();
        for (var i = 0; i < integer.Length; i++)
        {
            if (i > 0 && (integer.Length - i) % 3 == 0)
            {
                sb.Append(separator);
            }

            sb.Append(integer[i]);
        }

        return sb + fraction;
    }
}

// ---------------------------------------------------------------------------------------------------------------------
// Rule 7: general prose.
// ---------------------------------------------------------------------------------------------------------------------

/// <summary>Repeated words, singular “data”, and unmatched parentheses or brackets.</summary>
internal static class StyleGeneralRule
{
    private static readonly Regex DoubledRegex = StyleRegex.Create(@"(?<![A-Za-z])(?<w>[A-Za-z]+)(?<gap>\s+)\k<w>(?![A-Za-z])", RegexOptions.IgnoreCase);
    private static readonly Regex DataRegex = StyleRegex.Create(@"(?<![A-Za-z])(?<d>[Dd]ata)[ \t]+(?<v>is|was|has)(?![A-Za-z])");
    private static readonly Regex EnumeratorRegex = StyleRegex.Create(@"(?:^|[\s(])[A-Za-z0-9]{1,3}$");

    private static readonly HashSet<string> AllowedDoubles = new(StringComparer.OrdinalIgnoreCase) { "that", "had", "is", "do", "does", "can", "bye" };

    public static void Run(List<StyleSegment> segments, StyleCheckOptions options, StyleIssueSink sink)
    {
        foreach (var s in segments)
        {
            foreach (Match m in DoubledRegex.Matches(s.Text))
            {
                var word = m.Groups["w"].Value;
                if (AllowedDoubles.Contains(word))
                {
                    continue;
                }

                sink.Add(s, m.Index, m.Length, IssueSeverity.Warning, $"The word {StyleText.Q(word)} is repeated.", word, "Remove the repeated word");
            }

            if (options.PreferPluralData && s.IsProse)
            {
                foreach (Match m in DataRegex.Matches(s.Text))
                {
                    var verb = m.Groups["v"].Value switch { "is" => "are", "was" => "were", _ => "have" };
                    var fix = $"{m.Groups["d"].Value} {verb}";
                    sink.Add(s, m.Index, m.Length, IssueSeverity.Info,
                        $"“Data” is usually treated as plural in scientific writing ({StyleText.Q(fix)}).", fix);
                }
            }

            CheckBrackets(s, sink);
        }
    }

    private static void CheckBrackets(StyleSegment s, StyleIssueSink sink)
    {
        var text = s.Text;
        var stack = new Stack<(char Open, int Position)>();
        var reported = 0;
        for (var i = 0; i < text.Length && reported < 3; i++)
        {
            var c = text[i];
            if (c is '(' or '[')
            {
                stack.Push((c, i));
                continue;
            }

            if (c is not (')' or ']'))
            {
                continue;
            }

            var open = c == ')' ? '(' : '[';
            if (stack.Count > 0 && stack.Peek().Open == open)
            {
                stack.Pop();
                continue;
            }

            if (c == ')' && stack.Count == 0 && (EnumeratorRegex.IsMatch(text[..i]) || (i > 0 && text[i - 1] is ':' or ';')))
            {
                continue; // "a)" list markers and emoticons
            }

            sink.Add(s, i, 1, IssueSeverity.Warning, c == ')'
                ? "This closing parenthesis has no matching opening parenthesis in the paragraph."
                : "This closing bracket has no matching opening bracket in the paragraph.");
            reported++;
        }

        foreach (var (open, position) in stack.Reverse())
        {
            if (reported++ >= 3)
            {
                break;
            }

            sink.Add(s, position, 1, IssueSeverity.Warning, open == '('
                ? "This opening parenthesis is not closed in the paragraph."
                : "This opening bracket is not closed in the paragraph.");
        }
    }
}
