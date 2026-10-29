using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace RevisionsEditor.Core.Equations;

/// <summary>Converts LaTeX math to Office Math Markup Language (OMML) so Word shows native, editable equations.</summary>
public static class LatexToOmml
{
    /// <summary>The OMML namespace, conventionally bound to the prefix <c>m</c>.</summary>
    public static readonly XNamespace M = "http://schemas.openxmlformats.org/officeDocument/2006/math";

    /// <summary>Converts LaTeX math to an <c>m:oMath</c> element (inline). Never throws; unknown commands become plain text.</summary>
    public static XElement Convert(string latex, out IReadOnlyList<string> warnings)
    {
        var list = new List<string>();
        warnings = list;
        try
        {
            var root = LatexMathParser.Parse(latex, list);
            var math = new XElement(M + "oMath", new XAttribute(XNamespace.Xmlns + "m", M.NamespaceName));
            math.Add(BuildRow(root.Items));
            return math;
        }
        catch (Exception ex)
        {
            list.Add($"Equation could not be converted ({ex.Message}); it is shown as plain text.");
            return new XElement(M + "oMath", new XAttribute(XNamespace.Xmlns + "m", M.NamespaceName),
                Run(latex ?? string.Empty, MathFont.Text));
        }
    }

    /// <summary>Display equation: <c>m:oMathPara</c> containing the <c>m:oMath</c>.</summary>
    public static XElement ConvertDisplay(string latex, out IReadOnlyList<string> warnings)
    {
        var math = Convert(latex, out warnings);
        math.Attributes().Where(a => a.IsNamespaceDeclaration).Remove();
        return new XElement(M + "oMathPara", new XAttribute(XNamespace.Xmlns + "m", M.NamespaceName), math);
    }

    private static List<XElement> Build(MathNode? node) => node switch
    {
        null => [],
        RowNode row => BuildRow(row.Items),
        _ => BuildRow([node]),
    };

    private static List<XElement> BuildRow(IReadOnlyList<MathNode> items)
    {
        var output = new List<XElement>();
        var pending = new StringBuilder();
        var pendingFont = MathFont.Default;

        void Flush()
        {
            if (pending.Length == 0) return;
            output.Add(Run(pending.ToString(), pendingFont));
            pending.Clear();
        }

        foreach (var item in items)
        {
            if (item is TextNode text)
            {
                if (pending.Length > 0 && text.Font != pendingFont) Flush();
                pendingFont = text.Font;
                pending.Append(text.Text);
                continue;
            }

            Flush();
            if (item is RowNode row) output.AddRange(BuildRow(row.Items));
            else output.Add(BuildElement(item));
        }

        Flush();
        return output;
    }

    private static XElement BuildElement(MathNode node)
    {
        switch (node)
        {
            case ScriptNode s when s.Sub is not null && s.Sup is not null:
                return new XElement(M + "sSubSup", Arg("e", s.Base), Arg("sub", s.Sub), Arg("sup", s.Sup));
            case ScriptNode s when s.Sub is not null:
                return new XElement(M + "sSub", Arg("e", s.Base), Arg("sub", s.Sub));
            case ScriptNode s:
                return new XElement(M + "sSup", Arg("e", s.Base), Arg("sup", s.Sup));
            case FracNode f:
                return new XElement(M + "f",
                    f.NoBar ? new XElement(M + "fPr", Val("type", "noBar")) : null,
                    Arg("num", f.Num), Arg("den", f.Den));
            case RadicalNode r:
                return new XElement(M + "rad",
                    r.Degree is null ? new XElement(M + "radPr", Val("degHide", "1")) : null,
                    Arg("deg", r.Degree), Arg("e", r.Body));
            case NaryNode n:
                return new XElement(M + "nary",
                    new XElement(M + "naryPr",
                        Val("chr", n.Symbol),
                        Val("limLoc", n.UnderOver ? "undOvr" : "subSup"),
                        n.Sub is null ? Val("subHide", "1") : null,
                        n.Sup is null ? Val("supHide", "1") : null),
                    Arg("sub", n.Sub), Arg("sup", n.Sup), Arg("e", n.Body));
            case DelimNode d:
                return Delimiter(d.Open, d.Close, Build(d.Body));
            case AccentNode a when a.Kind == AccentKind.Accent:
                return new XElement(M + "acc",
                    new XElement(M + "accPr", Val("chr", a.Mark == '̄' ? "̅" : a.Mark.ToString())),
                    Arg("e", a.Body));
            case AccentNode a:
                return new XElement(M + "bar",
                    new XElement(M + "barPr", Val("pos", a.Kind == AccentKind.OverBar ? "top" : "bot")),
                    Arg("e", a.Body));
            case FuncNode f:
                var name = f.Limit is null ? Build(f.Name) : [Limit(f.Name, f.Limit, above: false)];
                if (f.Argument is null) return name.Count == 1 ? name[0] : new XElement(M + "box", new XElement(M + "e", name));
                return new XElement(M + "func", new XElement(M + "fName", name), Arg("e", f.Argument));
            case LimitNode l:
                return Limit(l.Base, l.Limit, l.Above);
            case MatrixNode m:
                return Matrix(m);
            default:
                return Run(string.Empty, MathFont.Default);
        }
    }

    private static XElement Limit(MathNode baseNode, MathNode limit, bool above) =>
        new(M + (above ? "limUpp" : "limLow"), Arg("e", baseNode), Arg("lim", limit));

    private static XElement Delimiter(string open, string close, IEnumerable<XElement> content) =>
        new(M + "d",
            new XElement(M + "dPr", Val("begChr", open), Val("endChr", close)),
            new XElement(M + "e", content));

    private static XElement Matrix(MatrixNode node)
    {
        if (node.Layout == MatrixLayout.Matrix)
        {
            var columns = Math.Max(1, node.Rows.Max(r => r.Count));
            var matrix = new XElement(M + "m",
                new XElement(M + "mPr",
                    new XElement(M + "mcs",
                        new XElement(M + "mc",
                            new XElement(M + "mcPr", Val("count", columns.ToString(CultureInfo.InvariantCulture)), Val("mcJc", "center"))))));
            foreach (var row in node.Rows)
            {
                var mr = new XElement(M + "mr");
                for (var i = 0; i < columns; i++) mr.Add(Arg("e", i < row.Count ? row[i] : null));
                matrix.Add(mr);
            }

            return node.Open.Length == 0 && node.Close.Length == 0 ? matrix : Delimiter(node.Open, node.Close, [matrix]);
        }

        var array = new XElement(M + "eqArr");
        var separator = node.Layout == MatrixLayout.Cases ? "& " : "&";
        foreach (var row in node.Rows)
        {
            var e = new XElement(M + "e");
            for (var i = 0; i < row.Count; i++)
            {
                if (i > 0) e.Add(Run(separator, MathFont.Default));
                e.Add(Build(row[i]));
            }

            array.Add(e);
        }

        return node.Open.Length == 0 && node.Close.Length == 0 ? array : Delimiter(node.Open, node.Close, [array]);
    }

    private static XElement Arg(string name, MathNode? node) => new(M + name, Build(node));

    private static XElement Val(string name, string value) => new(M + name, new XAttribute(M + "val", value));

    private static XElement Run(string text, MathFont font)
    {
        text = XmlSafe(text);
        var t = new XElement(M + "t", text);
        if (text.Length > 0 && (char.IsWhiteSpace(text[0]) || char.IsWhiteSpace(text[^1])))
            t.Add(new XAttribute(XNamespace.Xml + "space", "preserve"));
        return new XElement(M + "r", RunProperties(font), t);
    }

    private static XElement? RunProperties(MathFont font)
    {
        static XElement Sty(string v) => Val("sty", v);
        static XElement Scr(string v) => Val("scr", v);
        object[]? content = font switch
        {
            MathFont.Roman => [Sty("p")],
            MathFont.Bold => [Sty("b")],
            MathFont.Italic => [Sty("i")],
            MathFont.BoldItalic => [Sty("bi")],
            MathFont.Script => [Scr("script"), Sty("p")],
            MathFont.DoubleStruck => [Scr("double-struck"), Sty("p")],
            MathFont.Fraktur => [Scr("fraktur"), Sty("p")],
            MathFont.SansSerif => [Scr("sans-serif"), Sty("p")],
            MathFont.Monospace => [Scr("monospace"), Sty("p")],
            MathFont.Text => [new XElement(M + "nor")],
            _ => null,
        };
        return content is null ? null : new XElement(M + "rPr", content);
    }

    private static string XmlSafe(string text)
    {
        var sb = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                sb.Append(c).Append(text[++i]);
            }
            else if (!char.IsSurrogate(c) && XmlConvert.IsXmlChar(c))
            {
                sb.Append(c is '\r' or '\n' or '\t' ? ' ' : c);
            }
        }

        return sb.ToString();
    }
}

/// <summary>Font applied to a run of math text.</summary>
internal enum MathFont { Default, Roman, Bold, Italic, BoldItalic, Script, DoubleStruck, Fraktur, SansSerif, Monospace, Text }

/// <summary>TeX atom class of a piece of text, used for spacing and for finding where a big operator's operand ends.</summary>
internal enum AtomKind { Ord, Bin, Rel, Open, Close, Punct, Space }

internal enum AccentKind { Accent, OverBar, UnderBar }

internal enum MatrixLayout { Matrix, Cases, Aligned }

/// <summary>Node of the parsed LaTeX math tree shared by the OMML and Unicode converters.</summary>
internal abstract record MathNode;

internal sealed record TextNode(string Text, MathFont Font, AtomKind Kind = AtomKind.Ord) : MathNode;

internal sealed record RowNode(IReadOnlyList<MathNode> Items) : MathNode
{
    public static readonly RowNode Empty = new([]);
}

internal sealed record ScriptNode(MathNode Base, MathNode? Sub, MathNode? Sup) : MathNode;

internal sealed record FracNode(MathNode Num, MathNode Den, bool NoBar = false) : MathNode;

internal sealed record RadicalNode(MathNode? Degree, MathNode Body) : MathNode;

internal sealed record NaryNode(string Symbol, bool UnderOver, MathNode? Sub, MathNode? Sup, MathNode Body) : MathNode;

internal sealed record DelimNode(string Open, string Close, MathNode Body) : MathNode;

internal sealed record AccentNode(char Mark, AccentKind Kind, MathNode Body) : MathNode;

internal sealed record FuncNode(MathNode Name, MathNode? Limit, MathNode? Argument) : MathNode;

internal sealed record LimitNode(MathNode Base, MathNode Limit, bool Above) : MathNode;

internal sealed record MatrixNode(MatrixLayout Layout, string Open, string Close, IReadOnlyList<IReadOnlyList<MathNode>> Rows) : MathNode;

/// <summary>Recursive-descent parser for the LaTeX math subset used in manuscripts. Never throws on malformed input.</summary>
internal sealed class LatexMathParser
{
    private const int MaxDepth = 60;

    private static readonly Dictionary<string, string> Greek = new(StringComparer.Ordinal)
    {
        ["alpha"] = "α", ["beta"] = "β", ["gamma"] = "γ", ["delta"] = "δ", ["epsilon"] = "ϵ", ["varepsilon"] = "ε",
        ["zeta"] = "ζ", ["eta"] = "η", ["theta"] = "θ", ["vartheta"] = "ϑ", ["iota"] = "ι", ["kappa"] = "κ",
        ["varkappa"] = "ϰ", ["lambda"] = "λ", ["mu"] = "μ", ["nu"] = "ν", ["xi"] = "ξ", ["omicron"] = "ο", ["pi"] = "π",
        ["varpi"] = "ϖ", ["rho"] = "ρ", ["varrho"] = "ϱ", ["sigma"] = "σ", ["varsigma"] = "ς", ["tau"] = "τ",
        ["upsilon"] = "υ", ["phi"] = "ϕ", ["varphi"] = "φ", ["chi"] = "χ", ["psi"] = "ψ", ["omega"] = "ω",
        ["Gamma"] = "Γ", ["Delta"] = "Δ", ["Theta"] = "Θ", ["Lambda"] = "Λ", ["Xi"] = "Ξ", ["Pi"] = "Π",
        ["Sigma"] = "Σ", ["Upsilon"] = "Υ", ["Phi"] = "Φ", ["Psi"] = "Ψ", ["Omega"] = "Ω",
        ["varGamma"] = "Γ", ["varDelta"] = "Δ", ["varTheta"] = "Θ", ["varLambda"] = "Λ", ["varSigma"] = "Σ",
        ["varPhi"] = "Φ", ["varPsi"] = "Ψ", ["varOmega"] = "Ω",
    };

    private static readonly Dictionary<string, (string Text, AtomKind Kind)> Symbols = new(StringComparer.Ordinal)
    {
        ["cdot"] = ("⋅", AtomKind.Bin), ["times"] = ("×", AtomKind.Bin), ["div"] = ("÷", AtomKind.Bin),
        ["pm"] = ("±", AtomKind.Bin), ["mp"] = ("∓", AtomKind.Bin), ["cup"] = ("∪", AtomKind.Bin),
        ["cap"] = ("∩", AtomKind.Bin), ["ast"] = ("∗", AtomKind.Bin), ["star"] = ("⋆", AtomKind.Bin),
        ["circ"] = ("∘", AtomKind.Bin), ["bullet"] = ("∙", AtomKind.Bin), ["oplus"] = ("⊕", AtomKind.Bin),
        ["otimes"] = ("⊗", AtomKind.Bin), ["odot"] = ("⊙", AtomKind.Bin), ["setminus"] = ("∖", AtomKind.Bin),
        ["wedge"] = ("∧", AtomKind.Bin), ["land"] = ("∧", AtomKind.Bin), ["vee"] = ("∨", AtomKind.Bin),
        ["lor"] = ("∨", AtomKind.Bin), ["dagger"] = ("†", AtomKind.Bin),
        ["leq"] = ("≤", AtomKind.Rel), ["le"] = ("≤", AtomKind.Rel), ["geq"] = ("≥", AtomKind.Rel),
        ["ge"] = ("≥", AtomKind.Rel), ["leqslant"] = ("⩽", AtomKind.Rel), ["geqslant"] = ("⩾", AtomKind.Rel),
        ["neq"] = ("≠", AtomKind.Rel), ["ne"] = ("≠", AtomKind.Rel), ["approx"] = ("≈", AtomKind.Rel),
        ["sim"] = ("∼", AtomKind.Rel), ["simeq"] = ("≃", AtomKind.Rel), ["cong"] = ("≅", AtomKind.Rel),
        ["equiv"] = ("≡", AtomKind.Rel), ["propto"] = ("∝", AtomKind.Rel), ["ll"] = ("≪", AtomKind.Rel),
        ["gg"] = ("≫", AtomKind.Rel), ["lt"] = ("<", AtomKind.Rel), ["gt"] = (">", AtomKind.Rel),
        ["to"] = ("→", AtomKind.Rel), ["rightarrow"] = ("→", AtomKind.Rel), ["leftarrow"] = ("←", AtomKind.Rel),
        ["gets"] = ("←", AtomKind.Rel), ["leftrightarrow"] = ("↔", AtomKind.Rel), ["Rightarrow"] = ("⇒", AtomKind.Rel),
        ["Leftarrow"] = ("⇐", AtomKind.Rel), ["Leftrightarrow"] = ("⇔", AtomKind.Rel), ["implies"] = ("⟹", AtomKind.Rel),
        ["iff"] = ("⟺", AtomKind.Rel), ["mapsto"] = ("↦", AtomKind.Rel), ["longrightarrow"] = ("⟶", AtomKind.Rel),
        ["uparrow"] = ("↑", AtomKind.Rel), ["downarrow"] = ("↓", AtomKind.Rel),
        ["in"] = ("∈", AtomKind.Rel), ["notin"] = ("∉", AtomKind.Rel), ["ni"] = ("∋", AtomKind.Rel),
        ["subset"] = ("⊂", AtomKind.Rel), ["subseteq"] = ("⊆", AtomKind.Rel), ["supset"] = ("⊃", AtomKind.Rel),
        ["supseteq"] = ("⊇", AtomKind.Rel), ["perp"] = ("⊥", AtomKind.Rel), ["parallel"] = ("∥", AtomKind.Rel),
        ["mid"] = ("∣", AtomKind.Rel), ["vdash"] = ("⊢", AtomKind.Rel),
        ["infty"] = ("∞", AtomKind.Ord), ["partial"] = ("∂", AtomKind.Ord), ["nabla"] = ("∇", AtomKind.Ord),
        ["forall"] = ("∀", AtomKind.Ord), ["exists"] = ("∃", AtomKind.Ord), ["nexists"] = ("∄", AtomKind.Ord),
        ["ldots"] = ("…", AtomKind.Ord), ["dots"] = ("…", AtomKind.Ord), ["dotsc"] = ("…", AtomKind.Ord),
        ["cdots"] = ("⋯", AtomKind.Ord), ["dotsb"] = ("⋯", AtomKind.Ord), ["vdots"] = ("⋮", AtomKind.Ord),
        ["ddots"] = ("⋱", AtomKind.Ord), ["prime"] = ("′", AtomKind.Ord), ["emptyset"] = ("∅", AtomKind.Ord),
        ["varnothing"] = ("∅", AtomKind.Ord), ["neg"] = ("¬", AtomKind.Ord), ["lnot"] = ("¬", AtomKind.Ord),
        ["angle"] = ("∠", AtomKind.Ord), ["degree"] = ("°", AtomKind.Ord), ["hbar"] = ("ℏ", AtomKind.Ord),
        ["ell"] = ("ℓ", AtomKind.Ord), ["Re"] = ("ℜ", AtomKind.Ord), ["Im"] = ("ℑ", AtomKind.Ord),
        ["aleph"] = ("ℵ", AtomKind.Ord), ["top"] = ("⊤", AtomKind.Ord), ["bot"] = ("⊥", AtomKind.Ord),
        ["triangle"] = ("△", AtomKind.Ord), ["Box"] = ("□", AtomKind.Ord), ["square"] = ("□", AtomKind.Ord),
        ["backslash"] = ("\\", AtomKind.Ord), ["vert"] = ("|", AtomKind.Ord), ["Vert"] = ("‖", AtomKind.Ord),
        ["lvert"] = ("|", AtomKind.Open), ["rvert"] = ("|", AtomKind.Close), ["lVert"] = ("‖", AtomKind.Open),
        ["rVert"] = ("‖", AtomKind.Close), ["langle"] = ("⟨", AtomKind.Open), ["rangle"] = ("⟩", AtomKind.Close),
        ["lfloor"] = ("⌊", AtomKind.Open), ["rfloor"] = ("⌋", AtomKind.Close), ["lceil"] = ("⌈", AtomKind.Open),
        ["rceil"] = ("⌉", AtomKind.Close), ["lbrace"] = ("{", AtomKind.Open), ["rbrace"] = ("}", AtomKind.Close),
        ["lbrack"] = ("[", AtomKind.Open), ["rbrack"] = ("]", AtomKind.Close), ["colon"] = (":", AtomKind.Punct),
        ["checkmark"] = ("✓", AtomKind.Ord), ["S"] = ("§", AtomKind.Ord), ["P"] = ("¶", AtomKind.Ord),
    };

    private static readonly Dictionary<string, (string Symbol, bool UnderOver)> BigOperators = new(StringComparer.Ordinal)
    {
        ["sum"] = ("∑", true), ["prod"] = ("∏", true), ["coprod"] = ("∐", true), ["bigcup"] = ("⋃", true),
        ["bigcap"] = ("⋂", true), ["bigoplus"] = ("⨁", true), ["bigotimes"] = ("⨂", true), ["bigvee"] = ("⋁", true),
        ["bigwedge"] = ("⋀", true), ["int"] = ("∫", false), ["iint"] = ("∬", false), ["iiint"] = ("∭", false),
        ["oint"] = ("∮", false), ["oiint"] = ("∯", false),
    };

    /// <summary>Function names; the flag says whether a subscript is written below the name (lim, max).</summary>
    private static readonly Dictionary<string, bool> Functions = new(StringComparer.Ordinal)
    {
        ["log"] = false, ["ln"] = false, ["lg"] = false, ["exp"] = false, ["sin"] = false, ["cos"] = false,
        ["tan"] = false, ["cot"] = false, ["sec"] = false, ["csc"] = false, ["arcsin"] = false, ["arccos"] = false,
        ["arctan"] = false, ["sinh"] = false, ["cosh"] = false, ["tanh"] = false, ["coth"] = false, ["arg"] = false,
        ["deg"] = false, ["dim"] = false, ["ker"] = false, ["hom"] = false,
        ["max"] = true, ["min"] = true, ["det"] = true, ["lim"] = true, ["sup"] = true, ["inf"] = true,
        ["limsup"] = true, ["liminf"] = true, ["gcd"] = true, ["Pr"] = true, ["argmax"] = true, ["argmin"] = true,
    };

    private static readonly Dictionary<string, (char Mark, AccentKind Kind)> Accents = new(StringComparer.Ordinal)
    {
        ["hat"] = ('̂', AccentKind.Accent), ["widehat"] = ('̂', AccentKind.Accent),
        ["bar"] = ('̄', AccentKind.Accent), ["tilde"] = ('̃', AccentKind.Accent),
        ["widetilde"] = ('̃', AccentKind.Accent), ["dot"] = ('̇', AccentKind.Accent),
        ["ddot"] = ('̈', AccentKind.Accent), ["vec"] = ('⃗', AccentKind.Accent),
        ["check"] = ('̌', AccentKind.Accent), ["breve"] = ('̆', AccentKind.Accent),
        ["acute"] = ('́', AccentKind.Accent), ["grave"] = ('̀', AccentKind.Accent),
        ["overline"] = ('̅', AccentKind.OverBar), ["underline"] = ('̲', AccentKind.UnderBar),
    };

    private static readonly Dictionary<string, MathFont> FontCommands = new(StringComparer.Ordinal)
    {
        ["mathrm"] = MathFont.Roman, ["mathup"] = MathFont.Roman, ["mathbf"] = MathFont.Bold,
        ["mathit"] = MathFont.Italic, ["boldsymbol"] = MathFont.BoldItalic, ["bm"] = MathFont.BoldItalic,
        ["mathcal"] = MathFont.Script, ["mathscr"] = MathFont.Script, ["mathbb"] = MathFont.DoubleStruck,
        ["mathfrak"] = MathFont.Fraktur, ["mathsf"] = MathFont.SansSerif, ["mathtt"] = MathFont.Monospace,
    };

    private static readonly Dictionary<string, MathFont> FontSwitches = new(StringComparer.Ordinal)
    {
        ["rm"] = MathFont.Roman, ["bf"] = MathFont.Bold, ["it"] = MathFont.Italic, ["cal"] = MathFont.Script,
        ["sf"] = MathFont.SansSerif, ["tt"] = MathFont.Monospace,
    };

    private static readonly HashSet<string> TextCommands = new(StringComparer.Ordinal)
    {
        "text", "textrm", "textnormal", "textup", "textit", "textbf", "textsf", "texttt", "mbox", "hbox", "textmd", "emph",
    };

    private static readonly Dictionary<string, string> Spaces = new(StringComparer.Ordinal)
    {
        ["quad"] = " ", ["qquad"] = "  ", ["enspace"] = " ", ["thinspace"] = " ",
        ["medspace"] = " ", ["thickspace"] = " ", ["space"] = " ", ["nobreakspace"] = " ",
    };

    private static readonly HashSet<string> Ignored = new(StringComparer.Ordinal)
    {
        "displaystyle", "textstyle", "scriptstyle", "scriptscriptstyle", "limits", "nolimits", "nonumber", "notag",
        "mathstrut", "strut", "allowbreak", "nobreak", "negthinspace", "negmedspace", "negthickspace", "relax", "mathop",
    };

    private static readonly HashSet<string> IgnoredWithArgument = new(StringComparer.Ordinal)
    {
        "label", "tag", "vspace", "phantom", "vphantom", "hphantom", "color",
    };

    private static readonly HashSet<string> BigDelimiters = new(StringComparer.Ordinal)
    {
        "big", "Big", "bigg", "Bigg", "bigl", "Bigl", "biggl", "Biggl", "bigr", "Bigr", "biggr", "Biggr", "bigm", "Bigm",
    };

    private readonly string _s;
    private readonly List<string> _warnings;
    private int _pos;
    private int _depth;
    private int _groupDepth;
    private int _leftDepth;
    private int _envDepth;
    private MathFont _font = MathFont.Default;

    private LatexMathParser(string source, List<string> warnings, int depth = 0)
    {
        _s = source;
        _warnings = warnings;
        _depth = depth;
    }

    /// <summary>Parses LaTeX math (optionally wrapped in <c>$…$</c>, <c>$$…$$</c>, <c>\[…\]</c> or <c>\(…\)</c>) into a row of nodes.</summary>
    public static RowNode Parse(string? latex, List<string> warnings) =>
        new LatexMathParser(StripDelimiters(latex ?? string.Empty), warnings).ParseTop();

    private static string StripDelimiters(string latex)
    {
        var s = latex.Trim();
        foreach (var (open, close) in new[] { ("$$", "$$"), ("\\[", "\\]"), ("\\(", "\\)"), ("$", "$") })
        {
            if (s.Length >= open.Length + close.Length
                && s.StartsWith(open, StringComparison.Ordinal) && s.EndsWith(close, StringComparison.Ordinal))
                return s[open.Length..^close.Length].Trim();
        }

        return s;
    }

    private RowNode ParseTop() => new(ParseUntil(() => false));

    private bool AtEnd => _pos >= _s.Length;

    private char Peek(int offset = 0) => _pos + offset < _s.Length ? _s[_pos + offset] : '\0';

    private void Warn(string message)
    {
        if (_warnings.Count < 50) _warnings.Add(message);
    }

    private void SkipSpace()
    {
        while (!AtEnd && char.IsWhiteSpace(_s[_pos])) _pos++;
    }

    private string? PeekCommand()
    {
        if (Peek() != '\\') return null;
        var end = _pos + 1;
        while (end < _s.Length && char.IsAsciiLetter(_s[end])) end++;
        return end > _pos + 1 ? _s[(_pos + 1)..end] : null;
    }

    private bool AtLineBreak => Peek() == '\\' && Peek(1) == '\\';

    private bool AtRowEnd()
    {
        if (AtEnd) return true;
        var c = _s[_pos];
        if (c is '}' or '&' || AtLineBreak) return true;
        var command = PeekCommand();
        return command is "right" or "end";
    }

    private bool EnclosingAccepts()
    {
        if (AtEnd) return true;
        var c = _s[_pos];
        if (c == '}') return _groupDepth > 0;
        if (c == '&' || AtLineBreak) return _envDepth > 0;
        return PeekCommand() switch
        {
            "right" => _leftDepth > 0,
            "end" => _envDepth > 0,
            _ => false,
        };
    }

    private List<MathNode> ParseUntil(Func<bool> isOwnTerminator)
    {
        var items = new List<MathNode>();
        while (true)
        {
            ParseRowInto(items);
            if (AtEnd || isOwnTerminator() || EnclosingAccepts()) return items;
            ConsumeStray();
        }
    }

    private void ConsumeStray()
    {
        var c = _s[_pos];
        if (c == '}')
        {
            Warn("Unmatched closing brace '}' was ignored.");
            _pos++;
        }
        else if (c == '&')
        {
            Warn("Alignment '&' outside a matrix or aligned environment was ignored.");
            _pos++;
        }
        else if (AtLineBreak)
        {
            Warn("Line break '\\\\' outside a matrix or aligned environment was ignored.");
            _pos += 2;
        }
        else if (PeekCommand() == "right")
        {
            Warn("\\right without a matching \\left was ignored.");
            _pos += 6;
            ReadDelimiter("\\right");
        }
        else if (PeekCommand() == "end")
        {
            Warn("\\end without a matching \\begin was ignored.");
            _pos += 4;
            SkipSpace();
            ReadRawBraced();
        }
        else
        {
            _pos++;
        }
    }

    private void ParseRowInto(List<MathNode> items)
    {
        while (true)
        {
            SkipSpace();
            if (AtRowEnd()) return;
            var start = _pos;
            var node = ParseScripted();
            if (node is not null) items.Add(node);
            if (_pos == start) _pos++;
        }
    }

    private MathNode? ParseScripted() => ParseScripts(ParseAtom());

    private MathNode? ParseScripts(MathNode? baseNode)
    {
        var (sub, sup) = ReadScripts();
        if (sub is null && sup is null) return baseNode;
        return new ScriptNode(baseNode ?? RowNode.Empty, sub, sup);
    }

    private (MathNode? Sub, MathNode? Sup) ReadScripts()
    {
        MathNode? sub = null, sup = null;
        while (true)
        {
            SkipSpace();
            var c = Peek();
            if (c is not ('^' or '_')) return (sub, sup);
            _pos++;
            var arg = ParseArgument(c == '^' ? "^" : "_");
            if (c == '^')
            {
                if (sup is not null) Warn("Double superscript; the parts were joined.");
                sup = sup is null ? arg : new RowNode([sup, arg]);
            }
            else
            {
                if (sub is not null) Warn("Double subscript; the parts were joined.");
                sub = sub is null ? arg : new RowNode([sub, arg]);
            }
        }
    }

    /// <summary>Reads a command argument or script: a braced group or a single token.</summary>
    private MathNode ParseArgument(string owner)
    {
        SkipSpace();
        if (AtRowEnd() || Peek() is '^' or '_')
        {
            Warn($"Missing argument after {owner}.");
            return RowNode.Empty;
        }

        var c = _s[_pos];
        if (c == '{') return ParseGroup();
        if (char.IsAsciiDigit(c))
        {
            _pos++;
            return new TextNode(c.ToString(), _font);
        }

        if (char.IsAsciiLetter(c))
        {
            _pos++;
            return new TextNode(c.ToString(), _font);
        }

        return ParseAtom() ?? RowNode.Empty;
    }

    private MathNode ParseGroup()
    {
        _pos++;
        _groupDepth++;
        var savedFont = _font;
        var items = ParseUntil(() => Peek() == '}');
        _font = savedFont;
        _groupDepth--;
        if (Peek() == '}') _pos++;
        else Warn("Missing closing brace '}'.");
        return items.Count == 1 ? items[0] : new RowNode(items);
    }

    private MathNode? ParseAtom()
    {
        if (AtEnd) return null;
        if (_depth >= MaxDepth)
        {
            Warn("Equation is nested too deeply; the rest is shown as text.");
            var rest = _s[_pos..];
            _pos = _s.Length;
            return new TextNode(rest, MathFont.Text);
        }

        _depth++;
        try
        {
            return ParseAtomCore();
        }
        finally
        {
            _depth--;
        }
    }

    private MathNode? ParseAtomCore()
    {
        var c = _s[_pos];
        switch (c)
        {
            case '{':
                return ParseGroup();
            case '^' or '_':
                return null;
            case '\\':
                return ParseCommand();
            case '\'':
            {
                var n = 0;
                while (Peek() == '\'') { n++; _pos++; }
                return new TextNode(n switch { 1 => "′", 2 => "″", 3 => "‴", _ => new string('′', n) }, MathFont.Default);
            }
            case '~':
                _pos++;
                return new TextNode(" ", MathFont.Default, AtomKind.Space);
            case '$':
                _pos++;
                return null;
        }

        if (char.IsAsciiDigit(c) || (c == '.' && char.IsAsciiDigit(Peek(1))))
        {
            var start = _pos;
            while (!AtEnd && (char.IsAsciiDigit(_s[_pos])
                || (_s[_pos] is '.' or ',' && char.IsAsciiDigit(Peek(1)) && _pos > start)))
                _pos++;
            return new TextNode(_s[start.._pos], _font);
        }

        if (char.IsHighSurrogate(c) && char.IsLowSurrogate(Peek(1)))
        {
            _pos += 2;
            return new TextNode(_s.Substring(_pos - 2, 2), _font);
        }

        _pos++;
        if (char.IsLetter(c)) return new TextNode(c.ToString(), _font);
        return c switch
        {
            '+' => new TextNode("+", _font, AtomKind.Bin),
            '-' or '−' => new TextNode("−", _font, AtomKind.Bin),
            '*' => new TextNode("∗", _font, AtomKind.Bin),
            '±' or '∓' or '×' or '÷' or '⋅' or '·' => new TextNode(c.ToString(), _font, AtomKind.Bin),
            '=' or '<' or '>' or ':' or '≤' or '≥' or '≠' or '≈' or '∼' or '≡' or '∝' or '→' or '←' or '⇒' or '∈' or '∉' or '⊂'
                => new TextNode(c.ToString(), _font, AtomKind.Rel),
            ',' or ';' => new TextNode(c.ToString(), _font, AtomKind.Punct),
            '(' or '[' => new TextNode(c.ToString(), _font, AtomKind.Open),
            ')' or ']' => new TextNode(c.ToString(), _font, AtomKind.Close),
            _ => new TextNode(c.ToString(), _font),
        };
    }

    private MathNode? ParseCommand()
    {
        _pos++;
        if (AtEnd)
        {
            Warn("Trailing backslash was shown as text.");
            return new TextNode("\\", MathFont.Text);
        }

        var c = _s[_pos];
        if (!char.IsAsciiLetter(c))
        {
            _pos++;
            return c switch
            {
                ',' => new TextNode(" ", MathFont.Default, AtomKind.Space),
                ':' or '>' => new TextNode(" ", MathFont.Default, AtomKind.Space),
                ';' => new TextNode(" ", MathFont.Default, AtomKind.Space),
                ' ' or '\t' or '\n' or '\r' => new TextNode(" ", MathFont.Default, AtomKind.Space),
                '!' or '/' => null,
                '{' => new TextNode("{", MathFont.Default, AtomKind.Open),
                '}' => new TextNode("}", MathFont.Default, AtomKind.Close),
                '|' => new TextNode("‖", MathFont.Default),
                '%' or '$' or '&' or '#' or '_' => new TextNode(c.ToString(), MathFont.Default),
                _ => new TextNode(c.ToString(), MathFont.Default),
            };
        }

        var start = _pos;
        while (!AtEnd && char.IsAsciiLetter(_s[_pos])) _pos++;
        var name = _s[start.._pos];
        var starred = false;
        if (Peek() == '*' && name is "operatorname")
        {
            starred = true;
            _pos++;
        }

        if (Greek.TryGetValue(name, out var greek)) return new TextNode(greek, _font);
        if (Symbols.TryGetValue(name, out var symbol)) return new TextNode(symbol.Text, MathFont.Default, symbol.Kind);
        if (BigOperators.TryGetValue(name, out var op)) return ParseNary(op.Symbol, op.UnderOver);
        if (Functions.TryGetValue(name, out var limits)) return ParseFunction(name, limits);
        if (Accents.TryGetValue(name, out var accent)) return new AccentNode(accent.Mark, accent.Kind, ParseArgument("\\" + name));
        if (FontCommands.TryGetValue(name, out var font)) return ParseWithFont(font, name);
        if (FontSwitches.TryGetValue(name, out var fontSwitch))
        {
            _font = fontSwitch;
            return null;
        }

        if (TextCommands.Contains(name))
        {
            var text = ReadTextArgument();
            return text.Length == 0 ? null : new TextNode(text, MathFont.Text);
        }

        if (Spaces.TryGetValue(name, out var space)) return new TextNode(space, MathFont.Default, AtomKind.Space);
        if (Ignored.Contains(name)) return null;
        if (IgnoredWithArgument.Contains(name))
        {
            SkipSpace();
            ReadRawBraced();
            return null;
        }

        if (BigDelimiters.Contains(name))
        {
            var delimiter = ReadDelimiter("\\" + name);
            var kind = name.EndsWith('l') ? AtomKind.Open : name.EndsWith('r') ? AtomKind.Close : AtomKind.Ord;
            return delimiter.Length == 0 ? null : new TextNode(delimiter, MathFont.Default, kind);
        }

        switch (name)
        {
            case "frac" or "dfrac" or "tfrac" or "cfrac":
            {
                var num = ParseArgument("\\" + name);
                return new FracNode(num, ParseArgument("\\" + name));
            }
            case "binom" or "dbinom" or "tbinom":
            {
                var n = ParseArgument("\\" + name);
                return new DelimNode("(", ")", new FracNode(n, ParseArgument("\\" + name), NoBar: true));
            }
            case "sqrt":
                return ParseRadical();
            case "left":
                return ParseLeft();
            case "right":
                Warn("\\right without a matching \\left was ignored.");
                ReadDelimiter("\\right");
                return null;
            case "middle":
            {
                var delimiter = ReadDelimiter("\\middle");
                return delimiter.Length == 0 ? null : new TextNode(delimiter, MathFont.Default);
            }
            case "begin":
                return ParseEnvironment();
            case "end":
                Warn("\\end without a matching \\begin was ignored.");
                SkipSpace();
                ReadRawBraced();
                return null;
            case "operatorname":
            {
                var text = ReadTextArgument();
                return ParseFunction(text.Length == 0 ? "?" : text, starred);
            }
            case "not":
                return ParseNegation();
            case "overset" or "underset" or "stackrel":
            {
                var limit = ParseArgument("\\" + name);
                return new LimitNode(ParseArgument("\\" + name), limit, Above: name != "underset");
            }
            case "textcolor":
                SkipSpace();
                ReadRawBraced();
                return ParseArgument("\\" + name);
            case "overbrace" or "underbrace" or "boxed" or "substack" or "displaylines" or "mathrlap" or "mathllap":
                return ParseArgument("\\" + name);
            case "hspace":
                SkipSpace();
                if (Peek() == '*') _pos++;
                SkipSpace();
                ReadRawBraced();
                return new TextNode(" ", MathFont.Default, AtomKind.Space);
        }

        Warn($"Unknown command \\{name} is shown as text.");
        return new TextNode("\\" + name, MathFont.Roman);
    }

    private MathNode? ParseWithFont(MathFont font, string name)
    {
        var saved = _font;
        _font = font;
        try
        {
            return ParseArgument("\\" + name);
        }
        finally
        {
            _font = saved;
        }
    }

    private MathNode ParseNary(string symbol, bool underOver)
    {
        MathNode? sub = null, sup = null;
        while (true)
        {
            SkipSpace();
            var command = PeekCommand();
            if (command is "limits" or "nolimits")
            {
                underOver = command == "limits";
                _pos += command.Length + 1;
                continue;
            }

            if (Peek() is not ('^' or '_')) break;
            var (s, p) = ReadScripts();
            sub = s ?? sub;
            sup = p ?? sup;
        }

        var body = new List<MathNode>();
        var parens = 0;
        while (true)
        {
            SkipSpace();
            if (AtRowEnd()) break;
            var save = _pos;
            var warningCount = _warnings.Count;
            var node = ParseScripted();
            var text = node is null ? null : BaseText(node);
            var closesOuter = text is ")" or "]" && parens == 0;
            if (closesOuter || parens == 0 && (node is TextNode { Kind: AtomKind.Rel or AtomKind.Punct }
                || node is TextNode { Kind: AtomKind.Bin, Text: "+" or "−" or "±" or "∓" } && body.Count > 0
                || node is TextNode { Kind: AtomKind.Space, Text: " " or "  " }))
            {
                _pos = save;
                _warnings.RemoveRange(warningCount, _warnings.Count - warningCount);
                break;
            }

            if (text is "(" or "[") parens++;
            else if (text is ")" or "]") parens--;
            if (node is not null) body.Add(node);
            if (_pos == save) _pos++;
        }

        return new NaryNode(symbol, underOver, sub, sup, body.Count == 1 ? body[0] : new RowNode(body));
    }

    private MathNode ParseFunction(string name, bool limitStyle)
    {
        MathNode nameNode = new TextNode(name, MathFont.Roman);
        SkipSpace();
        var command = PeekCommand();
        if (command is "limits" or "nolimits")
        {
            limitStyle = command == "limits";
            _pos += command.Length + 1;
        }

        var (sub, sup) = ReadScripts();
        MathNode? limit = null;
        if (limitStyle && sub is not null)
        {
            limit = sub;
            sub = null;
        }

        if (sub is not null || sup is not null) nameNode = new ScriptNode(nameNode, sub, sup);
        return new FuncNode(nameNode, limit, ParseFunctionArgument());
    }

    private MathNode? ParseFunctionArgument()
    {
        var save = _pos;
        var warningCount = _warnings.Count;
        SkipSpace();
        if (AtRowEnd()) return null;
        var first = ParseScripted();
        if (first is null || first is TextNode { Kind: AtomKind.Bin or AtomKind.Rel or AtomKind.Punct or AtomKind.Close or AtomKind.Space })
        {
            _pos = save;
            _warnings.RemoveRange(warningCount, _warnings.Count - warningCount);
            return null;
        }

        var items = new List<MathNode> { first };
        if (BaseText(first) != "(")
        {
            SkipSpace();
            if (Peek() != '(') return first;
            items.Add(ParseScripted()!);
        }

        var depth = 1;
        while (depth > 0)
        {
            SkipSpace();
            if (AtRowEnd()) break;
            var start = _pos;
            var node = ParseScripted();
            if (node is not null)
            {
                items.Add(node);
                var text = BaseText(node);
                if (text == "(") depth++;
                else if (text == ")") depth--;
            }

            if (_pos == start) _pos++;
        }

        return new RowNode(items);
    }

    private static string? BaseText(MathNode node) => node switch
    {
        TextNode t => t.Text,
        ScriptNode { Base: TextNode t } => t.Text,
        _ => null,
    };

    private MathNode ParseRadical()
    {
        SkipSpace();
        MathNode? degree = null;
        if (Peek() == '[')
        {
            var close = FindClosingBracket(_pos);
            if (close < 0)
            {
                Warn("Missing ']' after \\sqrt[.");
                _pos++;
            }
            else
            {
                var inner = _s[(_pos + 1)..close];
                _pos = close + 1;
                var row = new LatexMathParser(inner, _warnings, _depth).ParseTop();
                degree = row.Items.Count == 1 ? row.Items[0] : row;
            }
        }

        return new RadicalNode(degree, ParseArgument("\\sqrt"));
    }

    private int FindClosingBracket(int open)
    {
        var depth = 0;
        for (var i = open + 1; i < _s.Length; i++)
        {
            var c = _s[i];
            if (c == '\\') i++;
            else if (c == '{') depth++;
            else if (c == '}') { if (--depth < 0) return -1; }
            else if (c == ']' && depth == 0) return i;
        }

        return -1;
    }

    private MathNode ParseLeft()
    {
        var open = ReadDelimiter("\\left");
        _leftDepth++;
        var items = ParseUntil(() => PeekCommand() == "right");
        _leftDepth--;
        var close = string.Empty;
        if (PeekCommand() == "right")
        {
            _pos += 6;
            close = ReadDelimiter("\\right");
        }
        else
        {
            Warn("\\left without a matching \\right; the closing delimiter was left empty.");
        }

        return new DelimNode(open, close, items.Count == 1 ? items[0] : new RowNode(items));
    }

    private string ReadDelimiter(string owner)
    {
        SkipSpace();
        if (AtEnd)
        {
            Warn($"Missing delimiter after {owner}.");
            return string.Empty;
        }

        var c = _s[_pos];
        if (c != '\\')
        {
            _pos++;
            return c switch
            {
                '.' => string.Empty,
                '(' or ')' or '[' or ']' or '|' or '/' => c.ToString(),
                '<' => "⟨",
                '>' => "⟩",
                _ => Unknown(c.ToString()),
            };
        }

        var command = PeekCommand();
        if (command is null)
        {
            var next = Peek(1);
            _pos += 2;
            return next switch
            {
                '{' => "{",
                '}' => "}",
                '|' => "‖",
                _ => Unknown("\\" + next),
            };
        }

        _pos += command.Length + 1;
        return command switch
        {
            "langle" => "⟨", "rangle" => "⟩", "lbrace" => "{", "rbrace" => "}", "vert" or "lvert" or "rvert" => "|",
            "Vert" or "lVert" or "rVert" => "‖", "lfloor" => "⌊", "rfloor" => "⌋", "lceil" => "⌈", "rceil" => "⌉",
            "lbrack" => "[", "rbrack" => "]", "backslash" => "\\", "uparrow" => "↑", "downarrow" => "↓",
            _ => Unknown("\\" + command),
        };

        string Unknown(string text)
        {
            Warn($"Unsupported delimiter '{text}' after {owner} was left empty.");
            return string.Empty;
        }
    }

    private MathNode ParseNegation()
    {
        SkipSpace();
        var node = ParseAtom();
        if (node is not TextNode t) return node ?? new TextNode("/", MathFont.Default);
        var negated = t.Text switch
        {
            "=" => "≠", "∈" => "∉", "<" => "≮", ">" => "≯", "≤" => "≰", "≥" => "≱", "⊂" => "⊄", "⊆" => "⊈",
            "≡" => "≢", "∼" => "≁", "≈" => "≉", "∃" => "∄", "∣" => "∤", "∥" => "∦",
            _ => t.Text + "̸",
        };
        return t with { Text = negated };
    }

    private MathNode ParseEnvironment()
    {
        SkipSpace();
        var name = ReadRawBraced();
        if (name is null)
        {
            Warn("\\begin without an environment name was ignored.");
            return RowNode.Empty;
        }

        name = name.Trim();
        if (name is "array" or "subarray")
        {
            SkipSpace();
            if (Peek() == '[') { var close = FindClosingBracket(_pos); _pos = close < 0 ? _pos + 1 : close + 1; SkipSpace(); }
            ReadRawBraced();
        }

        var (layout, open, close2) = name switch
        {
            "pmatrix" => (MatrixLayout.Matrix, "(", ")"),
            "bmatrix" => (MatrixLayout.Matrix, "[", "]"),
            "Bmatrix" => (MatrixLayout.Matrix, "{", "}"),
            "vmatrix" => (MatrixLayout.Matrix, "|", "|"),
            "Vmatrix" => (MatrixLayout.Matrix, "‖", "‖"),
            "matrix" or "smallmatrix" or "array" or "subarray" => (MatrixLayout.Matrix, "", ""),
            "cases" or "dcases" => (MatrixLayout.Cases, "{", ""),
            "rcases" => (MatrixLayout.Cases, "", "}"),
            "aligned" or "align" or "align*" or "alignat" or "alignat*" or "split" or "gathered" or "gather" or "gather*"
                or "eqnarray" or "eqnarray*" or "multline" or "multline*" or "equation" or "equation*" or "flalign" or "flalign*"
                => (MatrixLayout.Aligned, "", ""),
            _ => (MatrixLayout.Matrix, "", ""),
        };
        if (layout == MatrixLayout.Matrix && open.Length == 0 && name is not ("matrix" or "smallmatrix" or "array" or "subarray"))
            Warn($"Unknown environment '{name}' was treated as a matrix.");
        if (name.StartsWith("alignat", StringComparison.Ordinal)) { SkipSpace(); ReadRawBraced(); }

        _envDepth++;
        var rows = new List<IReadOnlyList<MathNode>>();
        var cells = new List<MathNode>();
        while (true)
        {
            var items = ParseUntil(() => Peek() == '&' || AtLineBreak || PeekCommand() == "end");
            cells.Add(items.Count == 1 ? items[0] : new RowNode(items));
            if (Peek() == '&')
            {
                _pos++;
                continue;
            }

            if (AtLineBreak)
            {
                _pos += 2;
                SkipSpace();
                if (Peek() == '[') { var close = FindClosingBracket(_pos); if (close > 0) _pos = close + 1; }
                rows.Add(cells);
                cells = [];
                continue;
            }

            rows.Add(cells);
            if (PeekCommand() == "end")
            {
                _pos += 4;
                SkipSpace();
                var endName = ReadRawBraced()?.Trim();
                if (!string.Equals(endName, name, StringComparison.Ordinal))
                    Warn($"\\begin{{{name}}} was closed by \\end{{{endName}}}.");
            }
            else
            {
                Warn($"Missing \\end{{{name}}}.");
            }

            break;
        }

        _envDepth--;
        while (rows.Count > 1 && rows[^1] is [RowNode { Items.Count: 0 }]) rows.RemoveAt(rows.Count - 1);
        return new MatrixNode(layout, open, close2, rows);
    }

    /// <summary>Reads a braced argument verbatim (nested braces kept), or null when none follows.</summary>
    private string? ReadRawBraced()
    {
        if (Peek() != '{') return null;
        var depth = 0;
        var start = _pos + 1;
        for (var i = _pos; i < _s.Length; i++)
        {
            var c = _s[i];
            if (c == '\\') { i++; continue; }
            if (c == '{') depth++;
            else if (c == '}' && --depth == 0)
            {
                _pos = i + 1;
                return _s[start..i];
            }
        }

        Warn("Missing closing brace '}'.");
        _pos = _s.Length;
        return _s[start..];
    }

    /// <summary>Reads the argument of <c>\text</c>-like commands as plain text, resolving escapes.</summary>
    private string ReadTextArgument()
    {
        SkipSpace();
        string raw;
        if (Peek() == '{')
        {
            raw = ReadRawBraced() ?? string.Empty;
        }
        else if (AtRowEnd())
        {
            Warn("Missing argument after a text command.");
            return string.Empty;
        }
        else
        {
            raw = _s[_pos].ToString();
            _pos++;
        }

        var sb = new StringBuilder(raw.Length);
        for (var i = 0; i < raw.Length; i++)
        {
            var c = raw[i];
            switch (c)
            {
                case '\\' when i + 1 < raw.Length && !char.IsAsciiLetter(raw[i + 1]):
                    i++;
                    sb.Append(raw[i] switch { ',' => ' ', ';' or ':' => ' ', '\\' => ' ', var x => x });
                    break;
                case '\\':
                    var j = i + 1;
                    while (j < raw.Length && char.IsAsciiLetter(raw[j])) j++;
                    var command = raw[(i + 1)..j];
                    if (Greek.TryGetValue(command, out var g)) sb.Append(g);
                    else if (Symbols.TryGetValue(command, out var sym)) sb.Append(sym.Text);
                    else if (Spaces.TryGetValue(command, out var sp)) sb.Append(sp);
                    i = j - 1;
                    break;
                case '{' or '}' or '$':
                    break;
                case '~':
                    sb.Append(' ');
                    break;
                case '\r' or '\n' or '\t':
                    sb.Append(' ');
                    break;
                default:
                    sb.Append(c);
                    break;
            }
        }

        return sb.ToString();
    }
}
