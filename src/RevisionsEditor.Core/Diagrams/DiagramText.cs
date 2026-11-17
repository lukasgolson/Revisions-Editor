using System.Collections.Concurrent;
using System.Text;
using RevisionsEditor.Core.Equations;
using SkiaSharp;

namespace RevisionsEditor.Core.Diagrams;

/// <summary>A piece of text placed at a baseline, in one font.</summary>
public sealed record DiagramTextRun(string Text, double X, double Baseline, double Size, bool Bold, bool Italic, string Family);

/// <summary>A rule drawn with text: a fraction bar or an overline.</summary>
public sealed record DiagramTextRule(double X1, double X2, double Y, double Thickness);

/// <summary>
/// Laid-out text: runs and rules relative to the origin (x = 0 at the left, y = 0 on the baseline, y grows down),
/// with its extent above (<see cref="Ascent"/>) and below (<see cref="Descent"/>) the baseline.
/// </summary>
public sealed class DiagramTextBox
{
    public double Width { get; internal set; }
    public double Ascent { get; internal set; }
    public double Descent { get; internal set; }
    public double Height => Ascent + Descent;
    public List<DiagramTextRun> Runs { get; } = [];
    public List<DiagramTextRule> Rules { get; } = [];
    /// <summary>The source text, kept so the LaTeX output can typeset it itself.</summary>
    public string Source { get; internal set; } = string.Empty;
    public bool Bold { get; internal set; }
    public double Size { get; internal set; }

    public static DiagramTextBox Empty(double size) => new() { Size = size };

    internal void Append(DiagramTextBox other, double dx, double dy)
    {
        foreach (var r in other.Runs) Runs.Add(r with { X = r.X + dx, Baseline = r.Baseline + dy });
        foreach (var l in other.Rules) Rules.Add(l with { X1 = l.X1 + dx, X2 = l.X2 + dx, Y = l.Y + dy });
        Width = Math.Max(Width, dx + other.Width);
        Ascent = Math.Max(Ascent, other.Ascent - dy);
        Descent = Math.Max(Descent, other.Descent + dy);
    }
}

/// <summary>
/// Lays out a diagram label: plain text with LaTeX math between $…$ (\$ for a dollar sign). Math is set the way
/// TeX sets it: letters italic, operators upright with space around them, scripts smaller and raised or
/// lowered, fractions stacked on a bar, \mathbb and \mathcal letters as their Unicode characters. Text is
/// measured with the real font (and a fallback font for characters it lacks), so boxes fit what is drawn.
/// </summary>
public static class DiagramText
{
    private const double ScriptScale = 0.7;
    private const double FractionScale = 0.8;

    private static readonly ConcurrentDictionary<(string, bool, bool), SKTypeface> Typefaces = new();
    private static readonly ConcurrentDictionary<(string, bool, bool, int), string> FallbackFamily = new();

    public static DiagramTextBox Layout(string text, string family, double size, bool bold)
    {
        var box = new DiagramTextBox { Source = text, Bold = bold, Size = size };
        var (ascent, descent) = LineMetrics(family, size);
        box.Ascent = ascent;
        box.Descent = descent;
        if (string.IsNullOrEmpty(text))
        {
            return box;
        }

        double x = 0;
        foreach (var (segment, isMath) in Split(text))
        {
            DiagramTextBox part = isMath
                ? LayoutMath(segment, family, size, bold)
                : PlainRun(segment, family, size, bold, italic: false);
            box.Append(part, x, 0);
            x += part.Width;
        }
        box.Width = x;
        return box;
    }

    /// <summary>The text with its math as Unicode, for accessibility text and plain-text uses.</summary>
    public static string PlainText(string text)
        => string.Concat(Split(text).Select(p => p.IsMath ? LatexToUnicode.Convert(p.Text) : p.Text));

    /// <summary>Plain and math parts: $…$ is math; \$ is a dollar sign.</summary>
    public static IEnumerable<(string Text, bool IsMath)> Split(string text)
    {
        var current = new StringBuilder();
        bool math = false;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '\\' && i + 1 < text.Length && text[i + 1] == '$')
            {
                current.Append(math ? "\\$" : "$");
                i++;
            }
            else if (c == '$')
            {
                if (current.Length > 0) yield return (current.ToString(), math);
                current.Clear();
                math = !math;
            }
            else
            {
                current.Append(c);
            }
        }
        if (current.Length > 0) yield return (current.ToString(), math);
    }

    // ---- Fonts and measuring --------------------------------------------------------------------------

    private static SKTypeface Typeface(string family, bool bold, bool italic)
        => Typefaces.GetOrAdd((family, bold, italic), key => SKTypeface.FromFamilyName(
               key.Item1,
               key.Item2 ? SKFontStyleWeight.Bold : SKFontStyleWeight.Normal,
               SKFontStyleWidth.Normal,
               key.Item3 ? SKFontStyleSlant.Italic : SKFontStyleSlant.Upright)
           ?? SKTypeface.Default);

    private static (double Ascent, double Descent) LineMetrics(string family, double size)
    {
        using var font = new SKFont(Typeface(family, false, false), (float)size);
        var m = font.Metrics;
        // Ascent of capitals and descent of descenders, a little tighter than the font's line box.
        double ascent = m.CapHeight > 0 ? m.CapHeight * 1.12 : -m.Ascent * 0.8;
        return (ascent, Math.Max(m.Descent * 0.85, size * 0.18));
    }

    private static double Measure(string text, string family, double size, bool bold, bool italic)
    {
        using var font = new SKFont(Typeface(family, bold, italic), (float)size);
        return font.MeasureText(text);
    }

    private static bool HasGlyph(SKTypeface typeface, int codepoint)
    {
        using var font = new SKFont(typeface);
        return font.GetGlyph(codepoint) != 0;
    }

    /// <summary>A family that has the character: the given one, or one the system offers for it.</summary>
    private static string FamilyFor(int codepoint, string family, bool bold, bool italic)
    {
        if (HasGlyph(Typeface(family, bold, italic), codepoint))
        {
            return family;
        }
        return FallbackFamily.GetOrAdd((family, bold, italic, codepoint), key =>
        {
            foreach (string candidate in new[] { "Cambria Math", "Segoe UI Symbol", "STIX Two Math", "Cambria" })
            {
                if (HasGlyph(Typeface(candidate, key.Item2, key.Item3), key.Item4)) return candidate;
            }
            using var matched = SKFontManager.Default.MatchCharacter(key.Item1, key.Item2 ? SKFontStyle.Bold : SKFontStyle.Normal, null, key.Item4);
            return matched?.FamilyName ?? key.Item1;
        });
    }

    /// <summary>A run of text in one style, split where a character needs another font.</summary>
    private static DiagramTextBox PlainRun(string text, string family, double size, bool bold, bool italic)
    {
        var box = new DiagramTextBox { Size = size, Bold = bold };
        var (ascent, descent) = LineMetrics(family, size);
        box.Ascent = ascent;
        box.Descent = descent;
        double x = 0;
        var chunk = new StringBuilder();
        string? chunkFamily = null;
        void Flush()
        {
            if (chunk.Length == 0) return;
            string s = chunk.ToString();
            double w = Measure(s, chunkFamily!, size, bold, italic);
            box.Runs.Add(new DiagramTextRun(s, x, 0, size, bold, italic, chunkFamily!));
            x += w;
            chunk.Clear();
        }
        for (int i = 0; i < text.Length; i += char.IsSurrogatePair(text, i) ? 2 : 1)
        {
            int cp = char.ConvertToUtf32(text, i);
            string f = char.IsWhiteSpace(text[i]) ? chunkFamily ?? family : FamilyFor(cp, family, bold, italic);
            if (chunkFamily != null && f != chunkFamily) Flush();
            chunkFamily = f;
            chunk.Append(char.ConvertFromUtf32(cp));
        }
        Flush();
        box.Width = x;
        return box;
    }

    // ---- Math ---------------------------------------------------------------------------------------------

    private static DiagramTextBox LayoutMath(string latex, string family, double size, bool bold)
    {
        var row = LatexMathParser.Parse(latex, []);
        return Node(row, family, size, bold);
    }

    private static DiagramTextBox Node(MathNode node, string family, double size, bool bold) => node switch
    {
        RowNode row => Row(row.Items, family, size, bold),
        TextNode text => Text(text, family, size, bold),
        ScriptNode script => Scripts(Node(script.Base, family, size, bold), script.Sub, script.Sup, family, size, bold),
        FracNode frac => Fraction(frac, family, size, bold),
        RadicalNode radical => Radical(radical, family, size, bold),
        NaryNode nary => Scripts(
            Concat([PlainRun(nary.Symbol, family, size * 1.15, bold, false), Node(nary.Body, family, size, bold)], size),
            nary.Sub, nary.Sup, family, size, bold),
        DelimNode delim => Concat([
            PlainRun(delim.Open, family, size, bold, false),
            Node(delim.Body, family, size, bold),
            PlainRun(delim.Close, family, size, bold, false)], size),
        AccentNode accent => Accent(accent, family, size, bold),
        FuncNode func => Concat([
            Scripts(Node(func.Name, family, size, bold), func.Limit, null, family, size, bold),
            Space(size * 0.17, size),
            func.Argument != null ? Node(func.Argument, family, size, bold) : DiagramTextBox.Empty(size)], size),
        LimitNode limit => UnderOver(limit, family, size, bold),
        MatrixNode matrix => Matrix(matrix, family, size, bold),
        _ => DiagramTextBox.Empty(size)
    };

    private static DiagramTextBox Concat(IEnumerable<DiagramTextBox> parts, double size)
    {
        var box = DiagramTextBox.Empty(size);
        double x = 0;
        foreach (var part in parts)
        {
            box.Append(part, x, 0);
            x += part.Width;
        }
        box.Width = x;
        return box;
    }

    private static DiagramTextBox Space(double width, double size) => new() { Width = width, Size = size };

    /// <summary>A row of atoms with TeX's spacing: medium space around binary operators, thick around relations.</summary>
    private static DiagramTextBox Row(IReadOnlyList<MathNode> items, string family, double size, bool bold)
    {
        var parts = new List<DiagramTextBox>();
        AtomKind? previous = null;
        for (int i = 0; i < items.Count; i++)
        {
            var item = items[i];
            var kind = (item as TextNode)?.Kind ?? AtomKind.Ord;
            // A binary operator at the start, or after another operator, is unary (−x).
            bool unary = kind == AtomKind.Bin && (previous is null or AtomKind.Bin or AtomKind.Rel or AtomKind.Open or AtomKind.Punct);
            double space = kind switch
            {
                AtomKind.Bin when !unary => size * 4 / 18,
                AtomKind.Rel => size * 5 / 18,
                _ => 0
            };
            bool scripted = size < 12 * 0.75; // TeX drops this space in scripts
            if (space > 0 && !scripted && previous != null) parts.Add(Space(space, size));
            parts.Add(Node(item, family, size, bold));
            if (space > 0 && !scripted && i + 1 < items.Count) parts.Add(Space(space, size));
            if (kind == AtomKind.Punct) parts.Add(Space(size * 3 / 18, size));
            previous = kind;
        }
        return Concat(parts, size);
    }

    private static DiagramTextBox Text(TextNode node, string family, double size, bool bold)
    {
        if (node.Kind == AtomKind.Space)
        {
            // \, \: \; \quad and "\ " come as space characters; measure them.
            return Space(Math.Max(Measure(node.Text, family, size, false, false), size * 0.17), size);
        }
        string text = Map(node.Text, node.Font);
        bool b = bold || node.Font is MathFont.Bold or MathFont.BoldItalic;
        switch (node.Font)
        {
            case MathFont.Default:
            {
                // Letters italic, digits and symbols upright.
                var parts = new List<DiagramTextBox>();
                var chunk = new StringBuilder();
                bool? italic = null;
                void Flush()
                {
                    if (chunk.Length == 0) return;
                    parts.Add(PlainRun(chunk.ToString(), family, size, b, italic == true));
                    chunk.Clear();
                }
                foreach (char c in text)
                {
                    bool it = char.IsLetter(c) && (c < 0x0370 || (c >= 0x03B1 && c <= 0x03F5));
                    if (italic != null && italic != it) Flush();
                    italic = it;
                    chunk.Append(c);
                }
                Flush();
                return Concat(parts, size);
            }
            case MathFont.Italic or MathFont.BoldItalic:
                return PlainRun(text, family, size, b, italic: true);
            case MathFont.SansSerif:
                return PlainRun(text, "Arial", size, b, italic: false);
            case MathFont.Monospace:
                return PlainRun(text, "Consolas", size, b, italic: false);
            default:
                return PlainRun(text, family, size, b, italic: false);
        }
    }

    /// <summary>\mathbb, \mathcal and \mathfrak letters as their Unicode mathematical characters.</summary>
    private static string Map(string text, MathFont font)
    {
        if (font is not (MathFont.DoubleStruck or MathFont.Script or MathFont.Fraktur))
        {
            return text;
        }
        var sb = new StringBuilder();
        foreach (char c in text)
        {
            string? special = (font, c) switch
            {
                (MathFont.DoubleStruck, 'C') => "ℂ", (MathFont.DoubleStruck, 'H') => "ℍ", (MathFont.DoubleStruck, 'N') => "ℕ",
                (MathFont.DoubleStruck, 'P') => "ℙ", (MathFont.DoubleStruck, 'Q') => "ℚ", (MathFont.DoubleStruck, 'R') => "ℝ",
                (MathFont.DoubleStruck, 'Z') => "ℤ",
                (MathFont.Script, 'B') => "ℬ", (MathFont.Script, 'E') => "ℰ", (MathFont.Script, 'F') => "ℱ", (MathFont.Script, 'H') => "ℋ",
                (MathFont.Script, 'I') => "ℐ", (MathFont.Script, 'L') => "ℒ", (MathFont.Script, 'M') => "ℳ", (MathFont.Script, 'R') => "ℛ",
                (MathFont.Script, 'e') => "ℯ", (MathFont.Script, 'g') => "ℊ", (MathFont.Script, 'o') => "ℴ",
                (MathFont.Fraktur, 'C') => "ℭ", (MathFont.Fraktur, 'H') => "ℌ", (MathFont.Fraktur, 'I') => "ℑ",
                (MathFont.Fraktur, 'R') => "ℜ", (MathFont.Fraktur, 'Z') => "ℨ",
                _ => null
            };
            if (special != null) { sb.Append(special); continue; }
            int start = font switch { MathFont.DoubleStruck => 0x1D538, MathFont.Script => 0x1D49C, _ => 0x1D504 };
            if (c is >= 'A' and <= 'Z') sb.Append(char.ConvertFromUtf32(start + (c - 'A')));
            else if (c is >= 'a' and <= 'z') sb.Append(char.ConvertFromUtf32(start + 26 + (c - 'a')));
            else if (font == MathFont.DoubleStruck && c is >= '0' and <= '9') sb.Append(char.ConvertFromUtf32(0x1D7D8 + (c - '0')));
            else sb.Append(c);
        }
        return sb.ToString();
    }

    private static DiagramTextBox Scripts(DiagramTextBox baseBox, MathNode? sub, MathNode? sup, string family, double size, bool bold)
    {
        if (sub == null && sup == null) return baseBox;
        double small = size * ScriptScale;
        var box = DiagramTextBox.Empty(size);
        box.Append(baseBox, 0, 0);
        double x = baseBox.Width + size * 0.03;
        double width = 0;
        if (sup != null)
        {
            var s = Node(sup, family, small, bold);
            double raise = Math.Max(size * 0.42, baseBox.Ascent - small * 0.35);
            box.Append(s, x, -raise);
            width = Math.Max(width, s.Width);
        }
        if (sub != null)
        {
            var s = Node(sub, family, small, bold);
            double lower = Math.Max(size * (sup != null ? 0.28 : 0.2), baseBox.Descent - small * 0.1);
            box.Append(s, x, lower);
            width = Math.Max(width, s.Width);
        }
        box.Width = x + width + size * 0.03;
        return box;
    }

    private static DiagramTextBox Fraction(FracNode frac, string family, double size, bool bold)
    {
        double small = Math.Max(size * FractionScale, 6);
        var num = Node(frac.Num, family, small, bold);
        var den = Node(frac.Den, family, small, bold);
        double axis = size * 0.27, rule = Math.Max(0.6, size * 0.055), gap = size * 0.12, pad = size * 0.08;
        double width = Math.Max(num.Width, den.Width) + 2 * pad;
        var box = DiagramTextBox.Empty(size);
        box.Append(num, (width - num.Width) / 2, -(axis + rule / 2 + gap + num.Descent));
        box.Append(den, (width - den.Width) / 2, -axis + rule / 2 + gap + den.Ascent);
        if (!frac.NoBar)
        {
            box.Rules.Add(new DiagramTextRule(pad / 2, width - pad / 2, -axis, rule));
        }
        box.Width = width + size * 0.05;
        return box;
    }

    private static DiagramTextBox Radical(RadicalNode radical, string family, double size, bool bold)
    {
        var body = Node(radical.Body, family, size, bold);
        var sign = PlainRun("√", family, size, bold, false);
        var box = DiagramTextBox.Empty(size);
        if (radical.Degree != null)
        {
            var degree = Node(radical.Degree, family, size * 0.55, bold);
            box.Append(degree, 0, -size * 0.45);
            box.Append(sign, Math.Max(0, degree.Width - size * 0.25), 0);
        }
        else
        {
            box.Append(sign, 0, 0);
        }
        double x = box.Width;
        box.Append(body, x, 0);
        double top = -Math.Max(body.Ascent + size * 0.1, sign.Ascent);
        box.Rules.Add(new DiagramTextRule(x - size * 0.05, x + body.Width, top, Math.Max(0.6, size * 0.05)));
        box.Ascent = Math.Max(box.Ascent, -top + size * 0.05);
        return box;
    }

    private static DiagramTextBox Accent(AccentNode accent, string family, double size, bool bold)
    {
        var body = Node(accent.Body, family, size, bold);
        var box = DiagramTextBox.Empty(size);
        box.Append(body, 0, 0);
        double thickness = Math.Max(0.6, size * 0.05);
        switch (accent.Kind)
        {
            case AccentKind.OverBar:
                box.Rules.Add(new DiagramTextRule(0, body.Width, -(body.Ascent + size * 0.08), thickness));
                box.Ascent += size * 0.12;
                break;
            case AccentKind.UnderBar:
                box.Rules.Add(new DiagramTextRule(0, body.Width, body.Descent + size * 0.08, thickness));
                box.Descent += size * 0.12;
                break;
            default:
            {
                // Spacing forms of the accents (ˆ ˜ ˙ ¨ → ¯) set above the middle of the body.
                string mark = accent.Mark switch
                {
                    '̂' => "ˆ", '̃' => "˜", '̇' => "˙", '̈' => "¨", '⃗' => "→",
                    '̄' => "¯", '̆' => "˘", '̌' => "ˇ", '́' => "´", '̀' => "`",
                    var c => c.ToString()
                };
                var m = PlainRun(mark, family, size, bold, false);
                double italicShift = size * 0.08;
                box.Append(m, (body.Width - m.Width) / 2 + italicShift, -(body.Ascent - size * 0.62));
                break;
            }
        }
        return box;
    }

    private static DiagramTextBox UnderOver(LimitNode limit, string family, double size, bool bold)
    {
        var b = Node(limit.Base, family, size, bold);
        var l = Node(limit.Limit, family, size * ScriptScale, bold);
        double width = Math.Max(b.Width, l.Width);
        var box = DiagramTextBox.Empty(size);
        box.Append(b, (width - b.Width) / 2, 0);
        box.Append(l, (width - l.Width) / 2, limit.Above ? -(b.Ascent + l.Descent + size * 0.1) : b.Descent + l.Ascent + size * 0.1);
        return box;
    }

    private static DiagramTextBox Matrix(MatrixNode matrix, string family, double size, bool bold)
    {
        var cells = matrix.Rows.Select(r => r.Select(c => Node(c, family, size, bold)).ToList()).ToList();
        int columns = cells.Count == 0 ? 0 : cells.Max(r => r.Count);
        var widths = Enumerable.Range(0, columns).Select(c => cells.Max(r => c < r.Count ? r[c].Width : 0)).ToList();
        double gap = size * 0.8, rowGap = size * 0.25;
        var body = DiagramTextBox.Empty(size);
        double y = 0;
        var heights = cells.Select(r => (Asc: r.Count == 0 ? size * 0.7 : r.Max(c => c.Ascent), Desc: r.Count == 0 ? size * 0.2 : r.Max(c => c.Descent))).ToList();
        double total = heights.Sum(h => h.Asc + h.Desc) + rowGap * Math.Max(0, cells.Count - 1);
        y = -total / 2 - size * 0.27;
        for (int r = 0; r < cells.Count; r++)
        {
            y += heights[r].Asc;
            double x = 0;
            for (int c = 0; c < cells[r].Count; c++)
            {
                double align = matrix.Layout == MatrixLayout.Matrix ? (widths[c] - cells[r][c].Width) / 2 : 0;
                body.Append(cells[r][c], x + align, y);
                x += widths[c] + gap;
            }
            y += heights[r].Desc + rowGap;
        }
        body.Width = widths.Sum() + gap * Math.Max(0, columns - 1);
        return Concat([PlainRun(matrix.Open, family, size, bold, false), body, PlainRun(matrix.Close, family, size, bold, false)], size);
    }
}
