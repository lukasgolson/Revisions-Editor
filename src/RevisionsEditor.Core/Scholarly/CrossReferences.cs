using System.Text;
using System.Text.RegularExpressions;
using RevisionsEditor.Core.Models;
using RevisionsEditor.Core.Serialization;

namespace RevisionsEditor.Core.Scholarly;

/// <summary>How a journal writes references to figures, tables, equations and sections.</summary>
public sealed record CrossReferenceNames
{
    public string Figure { get; init; } = "Fig.";
    public string Figures { get; init; } = "Figs.";
    public string FigureAtSentenceStart { get; init; } = "Figure";
    public string FiguresAtSentenceStart { get; init; } = "Figures";
    public string Table { get; init; } = "Table";
    public string Tables { get; init; } = "Tables";
    public string Equation { get; init; } = "Eq.";
    public string Equations { get; init; } = "Eqs.";
    public string EquationAtSentenceStart { get; init; } = "Equation";
    public string EquationsAtSentenceStart { get; init; } = "Equations";
    public string Section { get; init; } = "Section";
    public string Sections { get; init; } = "Sections";
    /// <summary>Label before a figure caption, e.g. "Fig." gives "Fig. 1. Caption".</summary>
    public string FigureCaptionLabel { get; init; } = "Fig.";
    public string TableCaptionLabel { get; init; } = "Table";
    public string CaptionSeparator { get; init; } = ". ";
    /// <summary>IEEE numbers tables with Roman numerals (TABLE I).</summary>
    public bool RomanTableNumbers { get; init; }
    public bool UppercaseTableCaptionLabel { get; init; }

    public static CrossReferenceNames Default { get; } = new();
}

/// <summary>A numbered thing that can be referred to.</summary>
public sealed record CrossReferenceTarget(string Label, string Kind, string Number, string ParagraphId, int Position);

/// <summary>One reference in the text: a single <c>@fig:a</c> or a group <c>[@fig:a; @fig:b]</c>.</summary>
public sealed record CrossReferenceUse(int Start, int Length, IReadOnlyList<(string Kind, string Label, bool Capitalized)> Items);

/// <summary>
/// Numbers figures, tables and equations in the order they appear (labelled or not), records section numbers
/// for labelled headings, and renders references such as "Fig. 3", "Figs. 1–3" or "Eq. (2)".
/// </summary>
public sealed class CrossReferenceIndex
{
    private static readonly Regex SingleRegex = new(
        @"(?<![\w@\\])@(?<kind>[Ff]ig|[Tt]bl|[Ee]q|[Ss]ec):(?<label>[A-Za-z0-9_-]*[A-Za-z0-9])",
        RegexOptions.Compiled);

    private static readonly Regex GroupRegex = new(@"\[(?<body>[^\[\]]*)\]", RegexOptions.Compiled);

    private static readonly Regex GroupItemRegex = new(
        @"^\s*@(?<kind>[Ff]ig|[Tt]bl|[Ee]q|[Ss]ec):(?<label>[A-Za-z0-9_-]*[A-Za-z0-9])\s*$",
        RegexOptions.Compiled);

    private readonly Dictionary<string, CrossReferenceTarget> _targets = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _numberByParagraph = new(StringComparer.Ordinal);

    public CrossReferenceNames Names { get; }
    public IReadOnlyDictionary<string, CrossReferenceTarget> Targets => _targets;
    /// <summary>Labels defined more than once, with the paragraphs that define them again.</summary>
    public List<(string Label, string ParagraphId)> DuplicateLabels { get; } = [];
    /// <summary>Figures, tables and equations in document order.</summary>
    public List<CrossReferenceTarget> NumberedItems { get; } = [];

    private CrossReferenceIndex(CrossReferenceNames names) => Names = names;

    /// <summary>Builds the index from blocks whose section numbers are computed (formatting rules applied).</summary>
    public static CrossReferenceIndex Build(IReadOnlyList<DocumentBlock> blocks, CrossReferenceNames? names = null)
    {
        var index = new CrossReferenceIndex(names ?? CrossReferenceNames.Default);
        int figures = 0, tables = 0, equations = 0, position = 0;

        foreach (var block in blocks)
        {
            position++;
            string text = CriticMarkupSerializer.ResolveCriticMarkupText(block.Text, MarkupDisplayMode.NoMarkupFinal);
            string? label = null;
            string kind;
            string number;
            switch (block.Role)
            {
                case BlockStyleRole.Figure:
                    kind = "fig";
                    number = (++figures).ToString();
                    label = ScholarlyBlocks.ParseFigure(text)?.Label;
                    break;
                case BlockStyleRole.Table:
                    kind = "tbl";
                    tables++;
                    number = index.Names.RomanTableNumbers ? ToRoman(tables) : tables.ToString();
                    label = ScholarlyBlocks.ParseTable(text)?.Label;
                    break;
                case BlockStyleRole.Equation:
                    kind = "eq";
                    number = (++equations).ToString();
                    label = ScholarlyBlocks.ParseEquation(text)?.Label;
                    break;
                case BlockStyleRole.Heading1 or BlockStyleRole.Heading2 or BlockStyleRole.Heading3:
                    label = ScholarlyBlocks.HeadingLabel(text);
                    if (label == null)
                    {
                        continue;
                    }
                    kind = "sec";
                    number = block.ComputedSectionNumber.Trim().TrimEnd('.');
                    if (number.Length == 0)
                    {
                        // Unnumbered headings are referred to by name.
                        number = $"“{DiffingText(ScholarlyBlocks.StripHeadingLabel(text))}”";
                    }
                    break;
                default:
                    continue;
            }

            var target = new CrossReferenceTarget(label ?? string.Empty, kind, number, block.ParagraphId, position);
            if (kind != "sec")
            {
                index.NumberedItems.Add(target);
                index._numberByParagraph[block.ParagraphId] = number;
            }
            if (label == null)
            {
                continue;
            }
            if (!label.StartsWith(kind + ":", StringComparison.OrdinalIgnoreCase))
            {
                // A figure labelled #tbl:x would never be found by @fig:x; keep it under its written prefix.
                label = label.Contains(':') ? label : $"{kind}:{label}";
            }
            if (!index._targets.TryAdd(label.ToLowerInvariant(), target with { Label = label }))
            {
                index.DuplicateLabels.Add((label, block.ParagraphId));
            }
        }
        return index;
    }

    private static string DiffingText(string text) => Diffing.DiffEngineService.ToGoogleDocsText(text);

    /// <summary>The number of the figure, table or equation in a paragraph ("3", or "II" for IEEE tables).</summary>
    public string? NumberOf(string paragraphId) => _numberByParagraph.TryGetValue(paragraphId, out var n) ? n : null;

    public CrossReferenceTarget? Find(string kind, string label)
        => _targets.TryGetValue($"{kind.ToLowerInvariant()}:{label}".ToLowerInvariant(), out var t) ? t : null;

    /// <summary>The caption label of a numbered block, e.g. "Fig. 3. " or "Table 2. ".</summary>
    public string CaptionLabel(DocumentBlock block)
    {
        string? number = NumberOf(block.ParagraphId);
        if (number == null)
        {
            return string.Empty;
        }
        if (block.Role == BlockStyleRole.Figure)
        {
            return $"{Names.FigureCaptionLabel} {number}{Names.CaptionSeparator}";
        }
        string label = Names.UppercaseTableCaptionLabel ? Names.TableCaptionLabel.ToUpperInvariant() : Names.TableCaptionLabel;
        return $"{label} {number}{Names.CaptionSeparator}";
    }

    /// <summary>Finds the cross-references in a text, in order (bracketed groups and single references).</summary>
    public static List<CrossReferenceUse> FindUses(string text)
    {
        var uses = new List<CrossReferenceUse>();
        var covered = new List<(int Start, int End)>();

        foreach (Match g in GroupRegex.Matches(text))
        {
            var parts = g.Groups["body"].Value.Split([';', ',']);
            var items = new List<(string, string, bool)>();
            foreach (string part in parts)
            {
                var m = GroupItemRegex.Match(part);
                if (!m.Success)
                {
                    items.Clear();
                    break;
                }
                items.Add(ItemOf(m));
            }
            if (items.Count > 0)
            {
                uses.Add(new CrossReferenceUse(g.Index, g.Length, items));
                covered.Add((g.Index, g.Index + g.Length));
            }
        }

        foreach (Match m in SingleRegex.Matches(text))
        {
            if (covered.Any(c => m.Index >= c.Start && m.Index < c.End))
            {
                continue;
            }
            uses.Add(new CrossReferenceUse(m.Index, m.Length, [ItemOf(m)]));
        }

        uses.Sort((a, b) => a.Start.CompareTo(b.Start));
        return uses;
    }

    private static (string Kind, string Label, bool Capitalized) ItemOf(Match m)
    {
        string kind = m.Groups["kind"].Value;
        return (kind.ToLowerInvariant(), m.Groups["label"].Value, char.IsUpper(kind[0]));
    }

    /// <summary>
    /// The text for one use, e.g. "Fig. 3", "Figs. 1 and 2", "Eqs. (1)–(3)". Unknown labels render as "??" so
    /// they stand out. <paramref name="atSentenceStart"/> selects the spelled-out form.
    /// </summary>
    public string Render(CrossReferenceUse use, bool atSentenceStart)
    {
        var parts = new List<string>();
        foreach (var group in use.Items.GroupBy(i => i.Kind))
        {
            bool start = (atSentenceStart && parts.Count == 0) || group.Any(i => i.Capitalized);
            var numbers = group.Select(i => Find(i.Kind, i.Label)?.Number ?? "??").ToList();
            bool plural = numbers.Count > 1;
            string name = group.Key switch
            {
                "fig" => start ? (plural ? Names.FiguresAtSentenceStart : Names.FigureAtSentenceStart) : (plural ? Names.Figures : Names.Figure),
                "tbl" => plural ? Names.Tables : Names.Table,
                "eq" => start ? (plural ? Names.EquationsAtSentenceStart : Names.EquationAtSentenceStart) : (plural ? Names.Equations : Names.Equation),
                _ => plural ? Names.Sections : Names.Section
            };
            Func<string, string> wrap = group.Key == "eq" ? n => $"({n})" : n => n;
            parts.Add(name.Length == 0 ? JoinNumbers(numbers, wrap) : $"{name} {JoinNumbers(numbers, wrap)}");
        }
        return string.Join("; ", parts);
    }

    /// <summary>"1", "1 and 2", "1–3", "1, 3 and 5" (consecutive runs of three or more become ranges).</summary>
    private static string JoinNumbers(List<string> numbers, Func<string, string> wrap)
    {
        var pieces = new List<string>();
        int i = 0;
        while (i < numbers.Count)
        {
            int j = i;
            while (j + 1 < numbers.Count && int.TryParse(numbers[j], out int a) && int.TryParse(numbers[j + 1], out int b) && b == a + 1)
            {
                j++;
            }
            pieces.Add(j - i >= 2 ? $"{wrap(numbers[i])}–{wrap(numbers[j])}" : wrap(numbers[i]));
            if (j - i == 1)
            {
                pieces.Add(wrap(numbers[j]));
            }
            i = j + 1;
        }
        return pieces.Count == 1 ? pieces[0] : string.Join(", ", pieces.Take(pieces.Count - 1)) + " and " + pieces[^1];
    }

    /// <summary>True when the text before <paramref name="index"/> ends a sentence (or is empty).</summary>
    public static bool IsSentenceStart(string text, int index)
    {
        int i = index - 1;
        while (i >= 0 && (char.IsWhiteSpace(text[i]) || text[i] is '*' or '(' or '"' or '“'))
        {
            i--;
        }
        if (i < 0 || text[i] is '!' or '?')
        {
            return true;
        }
        if (text[i] != '.')
        {
            return false;
        }
        // "e.g. Fig. 2" and "et al. (2016)" do not end a sentence.
        int wordStart = i;
        while (wordStart > 0 && !char.IsWhiteSpace(text[wordStart - 1]) && text[wordStart - 1] != '(')
        {
            wordStart--;
        }
        string word = text[wordStart..(i + 1)];
        return !NonTerminalAbbreviations.Contains(word);
    }

    private static readonly HashSet<string> NonTerminalAbbreviations = new(StringComparer.OrdinalIgnoreCase)
    {
        "e.g.", "i.e.", "al.", "cf.", "vs.", "approx.", "ca.", "Fig.", "Figs.", "Eq.", "Eqs.", "Sect.", "no.", "p.", "pp."
    };

    public static string ToRoman(int number)
    {
        (int Value, string Numeral)[] map = [(1000, "M"), (900, "CM"), (500, "D"), (400, "CD"), (100, "C"), (90, "XC"), (50, "L"), (40, "XL"), (10, "X"), (9, "IX"), (5, "V"), (4, "IV"), (1, "I")];
        var sb = new StringBuilder();
        foreach (var (value, numeral) in map)
        {
            while (number >= value)
            {
                sb.Append(numeral);
                number -= value;
            }
        }
        return sb.ToString();
    }
}
