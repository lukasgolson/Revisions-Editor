using System.Globalization;
using System.Text;

namespace RevisionsEditor.Core.Equations;

/// <summary>Renders LaTeX math as a linear, readable Unicode string for on-screen display.</summary>
public static class LatexToUnicode
{
    private static readonly Dictionary<char, char> Superscripts = Pairs(
        ("0123456789+-−=()", "⁰¹²³⁴⁵⁶⁷⁸⁹⁺⁻⁻⁼⁽⁾"),
        ("abcdefghijklmnoprstuvwxyz", "ᵃᵇᶜᵈᵉᶠᵍʰⁱʲᵏˡᵐⁿᵒᵖʳˢᵗᵘᵛʷˣʸᶻ"),
        ("ABDEGHIJKLMNOPRTUVW", "ᴬᴮᴰᴱᴳᴴᴵᴶᴷᴸᴹᴺᴼᴾᴿᵀᵁⱽᵂ"),
        ("αβγδεϵθιφϕχ", "ᵅᵝᵞᵟᵋᵋᶿᶥᵠᵠᵡ"),
        ("′″‴∘°", "′″‴°°"));

    private static readonly Dictionary<char, char> Subscripts = Pairs(
        ("0123456789+-−=()", "₀₁₂₃₄₅₆₇₈₉₊₋₋₌₍₎"),
        ("aehijklmnoprstuvx", "ₐₑₕᵢⱼₖₗₘₙₒₚᵣₛₜᵤᵥₓ"),
        ("βγρφϕχ", "ᵦᵧᵨᵩᵩᵪ"));

    private static readonly Dictionary<char, string> DoubleStruckSpecial = new()
    {
        ['C'] = "ℂ", ['H'] = "ℍ", ['N'] = "ℕ", ['P'] = "ℙ", ['Q'] = "ℚ", ['R'] = "ℝ", ['Z'] = "ℤ",
    };

    private static readonly Dictionary<char, string> ScriptSpecial = new()
    {
        ['B'] = "ℬ", ['E'] = "ℰ", ['F'] = "ℱ", ['H'] = "ℋ", ['I'] = "ℐ", ['L'] = "ℒ", ['M'] = "ℳ", ['R'] = "ℛ",
        ['e'] = "ℯ", ['g'] = "ℊ", ['o'] = "ℴ",
    };

    private enum Kind { None, Ord, Bin, Rel, Open, Close, Punct, Space, Nary, Func }

    /// <summary>Linear, readable rendering for screen display, e.g. "RMSE = √((1/n) Σᵢ₌₁ⁿ (ŷᵢ − yᵢ)²)". Never throws.</summary>
    public static string Convert(string latex)
    {
        try
        {
            var row = LatexMathParser.Parse(latex, []);
            return CollapseSpaces(RenderRow(row.Items, compact: false));
        }
        catch (Exception)
        {
            return latex ?? string.Empty;
        }
    }

    private static string Render(MathNode? node, bool compact) => node switch
    {
        null => string.Empty,
        RowNode row => RenderRow(row.Items, compact),
        TextNode t => RenderText(t, compact),
        ScriptNode s => RenderBase(s.Base, compact) + Script(s.Sub, sup: false) + Script(s.Sup, sup: true),
        FracNode { NoBar: true } f => Render(f.Num, compact) + ", " + Render(f.Den, compact),
        FracNode f => RenderBase(f.Num, compact) + "/" + RenderBase(f.Den, compact),
        RadicalNode r => RenderRadical(r, compact),
        NaryNode n => RenderNary(n, compact),
        DelimNode d => d.Open + Render(d.Body, compact) + d.Close,
        AccentNode a => ApplyMark(Render(a.Body, compact), a.Mark),
        FuncNode f => RenderFunction(f, compact),
        LimitNode l => RenderBase(l.Base, compact) + Script(l.Limit, l.Above),
        MatrixNode m => RenderMatrix(m, compact),
        _ => string.Empty,
    };

    private static string RenderRow(IReadOnlyList<MathNode> items, bool compact)
    {
        var sb = new StringBuilder();
        var prev = Kind.None;
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            var kind = KindOf(item);
            var text = Render(item, compact);
            if (item is FracNode { NoBar: false } && IsJuxtaposed(items, i)) text = "(" + text + ")";
            if (text.Length == 0) continue;
            if (compact)
            {
                sb.Append(text);
                prev = kind;
                continue;
            }

            switch (kind)
            {
                case Kind.Rel:
                    AddSpace(sb);
                    sb.Append(text);
                    AddSpace(sb);
                    break;
                case Kind.Bin when prev is Kind.None or Kind.Rel or Kind.Bin or Kind.Open or Kind.Punct:
                    sb.Append(text);
                    break;
                case Kind.Bin:
                    AddSpace(sb);
                    sb.Append(text);
                    AddSpace(sb);
                    break;
                case Kind.Punct:
                    sb.Append(text);
                    AddSpace(sb);
                    break;
                case Kind.Nary:
                    if (prev is not (Kind.None or Kind.Open)) AddSpace(sb);
                    sb.Append(text);
                    kind = Kind.Ord;
                    break;
                case Kind.Func:
                    if (prev is Kind.Ord or Kind.Close or Kind.Func) AddSpace(sb);
                    sb.Append(text);
                    break;
                case Kind.Close:
                    if (sb.Length > 0 && sb[^1] == ' ') sb.Length--;
                    sb.Append(text);
                    break;
                default:
                    sb.Append(text);
                    break;
            }

            prev = kind;
        }

        return sb.ToString().TrimEnd(' ');
    }

    private static void AddSpace(StringBuilder sb)
    {
        if (sb.Length == 0 || char.IsWhiteSpace(sb[^1]) || sb[^1] is '(' or '[' or '{' or '⟨') return;
        sb.Append(' ');
    }

    private static bool IsJuxtaposed(IReadOnlyList<MathNode> items, int i) =>
        (i > 0 && KindOf(items[i - 1]) is not (Kind.Rel or Kind.Bin or Kind.Punct or Kind.Space or Kind.Open))
        || (i < items.Count - 1 && KindOf(items[i + 1]) is not (Kind.Rel or Kind.Bin or Kind.Punct or Kind.Space or Kind.Close));

    private static Kind KindOf(MathNode node) => node switch
    {
        TextNode t => t.Kind switch
        {
            AtomKind.Bin => Kind.Bin,
            AtomKind.Rel => Kind.Rel,
            AtomKind.Open => Kind.Open,
            AtomKind.Close => Kind.Close,
            AtomKind.Punct => Kind.Punct,
            AtomKind.Space => Kind.Space,
            _ => Kind.Ord,
        },
        ScriptNode { Base: TextNode } s => KindOf(s.Base),
        NaryNode => Kind.Nary,
        FuncNode => Kind.Func,
        _ => Kind.Ord,
    };

    private static string RenderText(TextNode t, bool compact)
    {
        if (t.Kind == AtomKind.Space) return compact ? string.Empty : t.Text;
        return t.Font switch
        {
            MathFont.DoubleStruck => MapLetters(t.Text, DoubleStruckSpecial, 0x1D538, 0x1D552),
            MathFont.Script => MapLetters(t.Text, ScriptSpecial, 0x1D49C, 0x1D4B6),
            _ => t.Text,
        };
    }

    private static string MapLetters(string text, Dictionary<char, string> special, int upperBase, int lowerBase)
    {
        var sb = new StringBuilder();
        foreach (var c in text)
        {
            if (special.TryGetValue(c, out var s)) sb.Append(s);
            else if (c is >= 'A' and <= 'Z') sb.Append(char.ConvertFromUtf32(upperBase + c - 'A'));
            else if (c is >= 'a' and <= 'z') sb.Append(char.ConvertFromUtf32(lowerBase + c - 'a'));
            else sb.Append(c);
        }

        return sb.ToString();
    }

    private static string Script(MathNode? node, bool sup)
    {
        var s = Render(node, compact: true);
        if (s.Length == 0) return string.Empty;
        var map = sup ? Superscripts : Subscripts;
        var mapped = new StringBuilder(s.Length);
        foreach (var c in s)
        {
            if (!map.TryGetValue(c, out var m)) { mapped = null; break; }
            mapped.Append(m);
        }

        if (mapped is not null) return mapped.ToString();
        var marker = sup ? "^" : "_";
        return new StringInfo(s).LengthInTextElements == 1 ? marker + s : marker + "(" + s + ")";
    }

    private static string RenderBase(MathNode node, bool compact)
    {
        var s = Render(node, compact);
        return IsCompound(node) && !IsEnclosed(node) ? "(" + s + ")" : s;
    }

    private static bool IsCompound(MathNode node) => node switch
    {
        RowNode row => row.Items.Count > 1,
        FracNode or NaryNode or MatrixNode => true,
        FuncNode f => f.Argument is not null,
        _ => false,
    };

    private static bool IsEnclosed(MathNode node) =>
        node is RowNode { Items.Count: > 1 } row
        && row.Items[0] is TextNode { Text: "(" or "[" }
        && row.Items[^1] is TextNode { Text: ")" or "]" }
        && row.Items.Count(n => n is TextNode { Kind: AtomKind.Open }) == 1;

    private static string RenderRadical(RadicalNode r, bool compact)
    {
        var body = Render(r.Body, compact);
        var simple = r.Body switch
        {
            TextNode t => t.Kind == AtomKind.Ord && !t.Text.Contains(' ', StringComparison.Ordinal),
            DelimNode or AccentNode or RadicalNode => true,
            ScriptNode { Base: TextNode } => true,
            _ => IsEnclosed(r.Body),
        };
        if (!simple || body.Length == 0) body = "(" + body + ")";
        var degree = Render(r.Degree, compact: true);
        var sign = degree switch
        {
            "" => "√",
            "3" => "∛",
            "4" => "∜",
            _ => Script(r.Degree, sup: true) + "√",
        };
        return sign + body;
    }

    private static string RenderNary(NaryNode n, bool compact)
    {
        var head = n.Symbol + Script(n.Sub, sup: false) + Script(n.Sup, sup: true);
        var body = Render(n.Body, compact);
        if (body.Length == 0) return head;
        return compact ? head + body : head + " " + body;
    }

    private static string RenderFunction(FuncNode f, bool compact)
    {
        var name = Render(f.Name, compact) + Script(f.Limit, sup: false);
        if (f.Argument is null) return name;
        var argument = Render(f.Argument, compact);
        if (argument.Length == 0) return name;
        return compact || StartsWithParenthesis(f.Argument) ? name + argument : name + " " + argument;
    }

    private static bool StartsWithParenthesis(MathNode node) => node switch
    {
        RowNode { Items.Count: > 0 } row => StartsWithParenthesis(row.Items[0]),
        TextNode { Text: "(" or "[" } => true,
        DelimNode { Open.Length: > 0 } => true,
        _ => false,
    };

    private static string RenderMatrix(MatrixNode m, bool compact)
    {
        string body = m.Layout switch
        {
            MatrixLayout.Aligned => string.Join("; ", m.Rows.Select(row => RenderRow(Flatten(row), compact))),
            _ => string.Join("; ", m.Rows.Select(row => string.Join(", ", row.Select(cell => Render(cell, compact))))),
        };
        return m.Open + body + m.Close;
    }

    private static List<MathNode> Flatten(IReadOnlyList<MathNode> cells)
    {
        var items = new List<MathNode>();
        foreach (var cell in cells)
        {
            if (cell is RowNode row) items.AddRange(row.Items);
            else items.Add(cell);
        }

        return items;
    }

    private static string ApplyMark(string body, char mark)
    {
        if (body.Length == 0) return mark.ToString();
        var sb = new StringBuilder(body.Length * 2);
        var e = StringInfo.GetTextElementEnumerator(body);
        while (e.MoveNext())
        {
            var element = e.GetTextElement();
            sb.Append(element);
            if (!string.IsNullOrWhiteSpace(element)) sb.Append(mark);
        }

        return sb.ToString();
    }

    private static string CollapseSpaces(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (c == ' ' && sb.Length > 0 && sb[^1] == ' ') continue;
            sb.Append(c);
        }

        return SafeNormalize(sb.ToString().Trim(' '));
    }

    private static string SafeNormalize(string text)
    {
        try
        {
            return text.Normalize(NormalizationForm.FormC);
        }
        catch (ArgumentException)
        {
            return text;
        }
    }

    private static Dictionary<char, char> Pairs(params (string From, string To)[] tables)
    {
        var map = new Dictionary<char, char>();
        foreach (var (from, to) in tables)
            for (var i = 0; i < from.Length && i < to.Length; i++) map[from[i]] = to[i];
        return map;
    }
}
