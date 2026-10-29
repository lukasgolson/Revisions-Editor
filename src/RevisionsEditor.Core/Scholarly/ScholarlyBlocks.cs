using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using RevisionsEditor.Core.Models;

namespace RevisionsEditor.Core.Scholarly;

/// <summary>A figure block: <c>![Caption](figures/file.png){#fig:label width=80%}</c>.</summary>
public sealed record FigureBlock(string Caption, string ImagePath, string? Label, IReadOnlyDictionary<string, string> Attributes)
{
    /// <summary>Width as a fraction of the text width (from <c>width=80%</c>), or null for the image's natural size.</summary>
    public double? WidthFraction => Attributes.TryGetValue("width", out var w) ? ScholarlyBlocks.ParsePercent(w) : null;
}

/// <summary>A table block: <c>Table: Caption {#tbl:label file=tables/file.csv}</c>; the cells come from the CSV file.</summary>
public sealed record TableBlock(string Caption, string? Label, string? CsvPath, IReadOnlyDictionary<string, string> Attributes);

/// <summary>A display equation: <c>$$ LaTeX $$ {#eq:label}</c>.</summary>
public sealed record EquationBlock(string Latex, string? Label, IReadOnlyDictionary<string, string> Attributes);

/// <summary>
/// Parses and writes the whole-paragraph syntax of figures, tables and equations, and section labels on
/// headings (<c>Study area {#sec:study-area}</c>). The syntax follows Pandoc and pandoc-crossref.
/// </summary>
public static class ScholarlyBlocks
{
    // An attribute group starts with an id (#fig:a) or a key=value pair, so a CriticMarkup mark such as
    // {++text++} at the end of a caption is not mistaken for one.
    private const string AttributeGroup = @"\{(?<attrs>\s*(?:#[^{}]*|[A-Za-z][\w-]*\s*=[^{}]*))\}";

    private static readonly Regex FigureRegex = new(
        @"^!\[(?<cap>.*)\]\((?<path>[^()\s]+)\)\s*(?:" + AttributeGroup + @")?\s*$",
        RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex TableRegex = new(
        @"^Table:\s*(?<cap>.*?)\s*(?:" + AttributeGroup + @")?\s*$",
        RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex EquationRegex = new(
        @"^\$\$(?<tex>.*?)\$\$\s*(?:" + AttributeGroup + @")?\s*$",
        RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex HeadingLabelRegex = new(@"\s*\{#(?<label>sec:[A-Za-z0-9_-]*[A-Za-z0-9])\}\s*$", RegexOptions.Compiled);

    private static readonly Regex AttributeTokenRegex = new(
        @"#(?<id>[A-Za-z][\w:.-]*)|(?<key>[A-Za-z][\w-]*)\s*=\s*(?:[""“”](?<qval>[^""“”]*)[""“”]|(?<val>[^\s""]+))",
        RegexOptions.Compiled);

    /// <summary>The role a paragraph's own syntax gives it (figure, table or equation), or null.</summary>
    public static BlockStyleRole? DetectRole(string text)
    {
        string trimmed = text.TrimStart();
        if (trimmed.StartsWith("![", StringComparison.Ordinal) && FigureRegex.IsMatch(trimmed))
        {
            return BlockStyleRole.Figure;
        }
        if (trimmed.StartsWith("Table:", StringComparison.Ordinal) && TableRegex.IsMatch(trimmed))
        {
            return BlockStyleRole.Table;
        }
        if (trimmed.StartsWith("$$", StringComparison.Ordinal) && EquationRegex.IsMatch(trimmed))
        {
            return BlockStyleRole.Equation;
        }
        return null;
    }

    public static FigureBlock? ParseFigure(string text)
    {
        var m = FigureRegex.Match(text.Trim());
        if (!m.Success)
        {
            return null;
        }
        var (id, attrs) = ParseAttributes(m.Groups["attrs"].Value);
        return new FigureBlock(m.Groups["cap"].Value.Trim(), m.Groups["path"].Value, id, attrs);
    }

    public static TableBlock? ParseTable(string text)
    {
        var m = TableRegex.Match(text.Trim());
        if (!m.Success)
        {
            return null;
        }
        var (id, attrs) = ParseAttributes(m.Groups["attrs"].Value);
        return new TableBlock(m.Groups["cap"].Value.Trim(), id, attrs.TryGetValue("file", out var file) ? file : null, attrs);
    }

    public static EquationBlock? ParseEquation(string text)
    {
        var m = EquationRegex.Match(text.Trim());
        if (!m.Success)
        {
            return null;
        }
        var (id, attrs) = ParseAttributes(m.Groups["attrs"].Value);
        return new EquationBlock(m.Groups["tex"].Value.Trim(), id, attrs);
    }

    /// <summary>
    /// Splits a figure or table block into the caption and the fixed syntax around it, so the caption can be
    /// edited (or carry tracked changes) on its own: <c>prefix + caption + suffix == text</c>.
    /// </summary>
    public static (string Prefix, string Caption, string Suffix)? SplitCaption(string text)
    {
        var m = FigureRegex.Match(text);
        if (!m.Success)
        {
            m = TableRegex.Match(text);
        }
        if (!m.Success)
        {
            return null;
        }
        var cap = m.Groups["cap"];
        return (text[..cap.Index], cap.Value, text[(cap.Index + cap.Length)..]);
    }

    /// <summary>The section label at the end of a heading (<c>{#sec:methods}</c>), or null.</summary>
    public static string? HeadingLabel(string headingText)
    {
        var m = HeadingLabelRegex.Match(headingText);
        return m.Success ? m.Groups["label"].Value : null;
    }

    /// <summary>The heading text without its section label.</summary>
    public static string StripHeadingLabel(string headingText) => HeadingLabelRegex.Replace(headingText, string.Empty);

    /// <summary>Parses <c>#fig:a width=80% alt="Drone image"</c> into the id and the key/value pairs.</summary>
    public static (string? Id, IReadOnlyDictionary<string, string> Values) ParseAttributes(string attributes)
    {
        string? id = null;
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in AttributeTokenRegex.Matches(attributes))
        {
            if (m.Groups["id"].Success)
            {
                id ??= m.Groups["id"].Value;
            }
            else
            {
                values[m.Groups["key"].Value] = m.Groups["qval"].Success ? m.Groups["qval"].Value : m.Groups["val"].Value;
            }
        }
        return (id, values);
    }

    public static string FormatFigure(string caption, string imagePath, string? label, IReadOnlyDictionary<string, string>? attributes = null)
        => $"![{caption.Trim()}]({imagePath.Replace('\\', '/')}){FormatAttributes(label, attributes)}";

    public static string FormatTable(string caption, string? label, string? csvPath, IReadOnlyDictionary<string, string>? attributes = null)
    {
        var values = new Dictionary<string, string>(attributes ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(csvPath))
        {
            values["file"] = csvPath.Replace('\\', '/');
        }
        return $"Table: {caption.Trim()} {FormatAttributes(label, values)}".TrimEnd();
    }

    public static string FormatEquation(string latex, string? label)
        => $"$$ {latex.Trim()} $${(string.IsNullOrWhiteSpace(label) ? string.Empty : " " + FormatAttributes(label, null))}";

    private static string FormatAttributes(string? label, IReadOnlyDictionary<string, string>? attributes)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(label))
        {
            parts.Add("#" + label.Trim());
        }
        foreach (var (key, value) in attributes ?? new Dictionary<string, string>())
        {
            parts.Add(value.Any(char.IsWhiteSpace) || value.Length == 0 ? $"{key}=\"{value}\"" : $"{key}={value}");
        }
        return parts.Count == 0 ? string.Empty : "{" + string.Join(' ', parts) + "}";
    }

    /// <summary>Parses "80%" or "0.8" into a fraction of the text width.</summary>
    public static double? ParsePercent(string value)
    {
        string v = value.Trim();
        bool percent = v.EndsWith('%');
        if (percent)
        {
            v = v[..^1];
        }
        if (!double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out double d) || d <= 0)
        {
            return null;
        }
        double fraction = percent || d > 1 ? d / 100.0 : d;
        return Math.Clamp(fraction, 0.05, 1.0);
    }

    /// <summary>A label made from a caption, e.g. "Study area map" → "study-area-map", for new figures and tables.</summary>
    public static string SuggestLabel(string kind, string caption, IReadOnlySet<string> existingLabels)
    {
        var sb = new StringBuilder();
        foreach (char c in Serialization.CriticMarkupSerializer.ResolveCriticMarkupText(caption, MarkupDisplayMode.NoMarkupFinal).ToLowerInvariant())
        {
            if (char.IsAsciiLetterOrDigit(c))
            {
                sb.Append(c);
            }
            else if (sb.Length > 0 && sb[^1] != '-')
            {
                sb.Append('-');
            }
            if (sb.Length >= 24)
            {
                break;
            }
        }
        string slug = sb.ToString().Trim('-');
        if (slug.Length == 0)
        {
            slug = "1";
        }
        string label = $"{kind}:{slug}";
        for (int n = 2; existingLabels.Contains(label); n++)
        {
            label = $"{kind}:{slug}-{n}";
        }
        return label;
    }
}
