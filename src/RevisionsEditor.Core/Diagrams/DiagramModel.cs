using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace RevisionsEditor.Core.Diagrams;

/// <summary>What a diagram node draws.</summary>
public enum DiagramNodeKind
{
    /// <summary>A box with a bold title and a subtitle inside (a layer, a stage, a model).</summary>
    Block,
    /// <summary>A 3D slab standing for a tensor or feature map; its title and subtitle (the shape) go underneath.</summary>
    Tensor,
    /// <summary>A small circle with an operator: ⊕ (add), ⊗/× (mul), or any symbol.</summary>
    Op,
    /// <summary>Text on its own, such as an input "x" or an output "x + γf(x)".</summary>
    Text
}

/// <summary>How an arrow is drawn.</summary>
public enum DiagramLineStyle { Solid, Dashed, Dotted }

/// <summary>Which way an edge goes round other nodes: chosen automatically, over the row, or under it.</summary>
public enum DiagramRoute { Auto, Over, Under }

public sealed class DiagramNode
{
    public string Id { get; set; } = string.Empty;
    public DiagramNodeKind Kind { get; set; } = DiagramNodeKind.Block;
    public string Title { get; set; } = string.Empty;
    public string Subtitle { get; set; } = string.Empty;
    /// <summary>Text under the node (for a tensor, its title and subtitle come first).</summary>
    public string Caption { get; set; } = string.Empty;
    /// <summary>The node directly below (or above) which this one sits, sharing its column.</summary>
    public string? Below { get; set; }
    public string? Above { get; set; }
    /// <summary>The node this one follows in its row (default: the previous node declared in the row).</summary>
    public string? After { get; set; }
    /// <summary>An explicit row (0 is the top row), when not placed by <see cref="Below"/> or <see cref="Above"/>.</summary>
    public int? Row { get; set; }
    /// <summary>How many rows the node spans; it is centred across them (e.g. a concatenation of two branches).</summary>
    public int RowSpan { get; set; } = 1;
    /// <summary>Whether span= was given; without it a node continues the span of the node before it in its row.</summary>
    public bool SpanSet { get; set; }
    /// <summary>The rows the node spans as laid out (set by <see cref="DiagramLayout.AssignGrid"/>).</summary>
    public int EffectiveSpan { get; internal set; } = 1;
    /// <summary>Outline: solid, dashed or dotted.</summary>
    public DiagramLineStyle Border { get; set; } = DiagramLineStyle.Solid;
    public bool Shaded { get; set; }
    /// <summary>Title weight: null for the default (bold in a block, regular in text), or bold / regular.</summary>
    public bool? BoldTitle { get; set; }

    /// <summary>Whether the title is drawn bold.</summary>
    public bool IsTitleBold => BoldTitle ?? Kind is DiagramNodeKind.Block or DiagramNodeKind.Tensor;
    /// <summary>Tensor proportions: "thin" (an image), "deep" (a feature stack) or "" (a descriptor).</summary>
    public string Shape { get; set; } = string.Empty;
    /// <summary>Smallest width, in diagram units.</summary>
    public double? MinWidth { get; set; }

    public DiagramNode Clone() => (DiagramNode)MemberwiseClone();
}

public sealed class DiagramEdge
{
    public string From { get; set; } = string.Empty;
    public string To { get; set; } = string.Empty;
    public DiagramLineStyle Style { get; set; } = DiagramLineStyle.Solid;
    public bool Arrow { get; set; } = true;
    public string Label { get; set; } = string.Empty;
    public DiagramRoute Route { get; set; } = DiagramRoute.Auto;
}

public sealed class DiagramGroup
{
    public string Id { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public List<string> Members { get; set; } = [];
    public DiagramLineStyle Border { get; set; } = DiagramLineStyle.Dashed;
    public bool Shaded { get; set; }
}

/// <summary>One line of a diagram's source: kept so that edits made on the canvas keep comments and order.</summary>
public abstract record DiagramStatement;
public sealed record DiagramCommentStatement(string Text) : DiagramStatement;
public sealed record DiagramSettingStatement(string Key, string Value) : DiagramStatement;
public sealed record DiagramNodeStatement(DiagramNode Node) : DiagramStatement;
/// <summary>A chain such as <c>a -> b --> c</c>: Nodes[i] to Nodes[i+1] drawn with Styles[i].</summary>
public sealed record DiagramChainStatement(List<string> Nodes, List<(DiagramLineStyle Style, bool Arrow)> Links, string Label, DiagramRoute Route) : DiagramStatement;
public sealed record DiagramGroupStatement(DiagramGroup Group) : DiagramStatement;

public sealed record DiagramError(int Line, string Message);

/// <summary>
/// A block diagram (a pipeline or a network architecture) described as text, one statement per line:
/// <code>
/// block dino "DINOv3" "ConvNeXt-L"
/// tensor hyp "Hyperstack" "$2880 \times \frac{H}{4} \times \frac{W}{4}$" deep
/// op add add
/// group refine "Refiner" a b c dotted
/// in -> dino -> hyp        # solid arrows
/// ref --> oos label="$\nabla z = 0$"   # dashed
/// x --> add over           # routed over the row
/// </code>
/// Text in quotes may hold LaTeX math between $…$. The source text is the saved form; the canvas edits it
/// through <see cref="DiagramSource"/> so the text stays readable and keeps its comments.
/// </summary>
public sealed class Diagram
{
    public List<DiagramStatement> Statements { get; } = [];

    public string FontFamily { get; set; } = "Times New Roman";
    /// <summary>Font size of titles in diagram units (subtitles and captions are a little smaller).</summary>
    public double FontSize { get; set; } = 16;
    /// <summary>Horizontal space between columns.</summary>
    public double Gap { get; set; } = 34;
    /// <summary>
    /// "layout auto": placed automatically from the arrows (a layered layout, as for flowcharts) instead of on the
    /// grid the statements describe.
    /// </summary>
    public bool AutoLayout { get; set; }
    /// <summary>Which way an automatic layout flows: "right" (left to right) or "down".</summary>
    public string Direction { get; set; } = "right";

    public IEnumerable<DiagramNode> Nodes => Statements.OfType<DiagramNodeStatement>().Select(s => s.Node);
    public IEnumerable<DiagramGroup> Groups => Statements.OfType<DiagramGroupStatement>().Select(s => s.Group);

    public IEnumerable<DiagramEdge> Edges
    {
        get
        {
            foreach (var chain in Statements.OfType<DiagramChainStatement>())
            {
                for (int i = 0; i + 1 < chain.Nodes.Count; i++)
                {
                    yield return new DiagramEdge
                    {
                        From = chain.Nodes[i],
                        To = chain.Nodes[i + 1],
                        Style = chain.Links[i].Style,
                        Arrow = chain.Links[i].Arrow,
                        Label = chain.Label,
                        Route = chain.Route
                    };
                }
            }
        }
    }

    public DiagramNode? FindNode(string id) => Nodes.FirstOrDefault(n => n.Id == id);
}

/// <summary>Reads and writes the diagram source text, and edits it the way the canvas does.</summary>
public static class DiagramSource
{
    private static readonly Regex TokenRegex = new(
        @"\G\s*(?:(?<arrow>-->|->|\.\.>|---|--)(?=\s|$)|(?<key>[A-Za-z][\w-]*)=(?:""(?<qval>(?:[^""\\]|\\.)*)""|(?<val>\S+))|""(?<str>(?:[^""\\]|\\.)*)""|(?<word>[^\s""]+))",
        RegexOptions.Compiled);

    private static readonly Regex IdRegex = new(@"^[A-Za-z_][\w.-]*$", RegexOptions.Compiled);

    private static readonly HashSet<string> Kinds = ["block", "tensor", "op", "text"];

    public static Diagram Parse(string source, out List<DiagramError> errors)
    {
        errors = [];
        var diagram = new Diagram();
        var lineOf = new Dictionary<DiagramStatement, int>(ReferenceEqualityComparer.Instance);
        string[] lines = source.Replace("\r\n", "\n").Split('\n');
        for (int index = 0; index < lines.Length; index++)
        {
            string line = lines[index];
            string trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('#'))
            {
                var comment = new DiagramCommentStatement(trimmed);
                diagram.Statements.Add(comment);
                lineOf[comment] = index + 1;
                continue;
            }
            var tokens = Tokenize(StripComment(trimmed), out string? tokenError);
            if (tokenError != null)
            {
                errors.Add(new DiagramError(index + 1, tokenError));
                continue;
            }
            try
            {
                if (ParseStatement(tokens, diagram) is { } statement)
                {
                    diagram.Statements.Add(statement);
                    lineOf[statement] = index + 1;
                }
            }
            catch (FormatException ex)
            {
                errors.Add(new DiagramError(index + 1, ex.Message));
            }
        }

        // Trailing blank lines are not statements worth keeping.
        while (diagram.Statements.Count > 0 && diagram.Statements[^1] is DiagramCommentStatement { Text.Length: 0 })
        {
            diagram.Statements.RemoveAt(diagram.Statements.Count - 1);
        }
        Validate(diagram, lineOf, errors);
        return diagram;
    }

    private sealed record Token(string Kind, string Value, string? Key = null);

    /// <summary>A line without its trailing "# comment" (a # inside quotes is kept).</summary>
    private static string StripComment(string line)
    {
        bool quoted = false;
        for (int i = 0; i < line.Length; i++)
        {
            if (line[i] == '\\' && quoted) { i++; continue; }
            if (line[i] == '"') quoted = !quoted;
            else if (line[i] == '#' && !quoted && i > 0 && char.IsWhiteSpace(line[i - 1])) return line[..i].TrimEnd();
        }
        return line;
    }

    private static List<Token> Tokenize(string line, out string? error)
    {
        error = null;
        var tokens = new List<Token>();
        int at = 0;
        while (at < line.Length)
        {
            if (line[at..].Trim().Length == 0) break;
            var m = TokenRegex.Match(line, at);
            if (!m.Success || m.Length == 0)
            {
                error = $"Cannot read “{line[at..].Trim()}” (is a quote missing?).";
                return tokens;
            }
            if (m.Groups["arrow"].Success) tokens.Add(new Token("arrow", m.Groups["arrow"].Value));
            else if (m.Groups["key"].Success) tokens.Add(new Token("option", Unescape(m.Groups["qval"].Success ? m.Groups["qval"].Value : m.Groups["val"].Value), m.Groups["key"].Value.ToLowerInvariant()));
            else if (m.Groups["str"].Success) tokens.Add(new Token("string", Unescape(m.Groups["str"].Value)));
            else tokens.Add(new Token("word", m.Groups["word"].Value));
            at = m.Index + m.Length;
        }
        return tokens;
    }

    private static string Unescape(string s) => s.Replace("\\\"", "\"");

    private static DiagramStatement? ParseStatement(List<Token> tokens, Diagram diagram)
    {
        var first = tokens[0];
        string keyword = first.Kind == "word" ? first.Value.ToLowerInvariant() : string.Empty;

        if (keyword is "layout" or "direction")
        {
            string value = tokens.ElementAtOrDefault(1)?.Value.ToLowerInvariant() ?? string.Empty;
            switch (keyword, value)
            {
                case ("layout", "auto" or "grid"):
                    diagram.AutoLayout = value == "auto";
                    break;
                case ("direction", "right" or "lr" or "left-right"):
                    diagram.Direction = "right";
                    break;
                case ("direction", "down" or "td" or "tb" or "top-down"):
                    diagram.Direction = "down";
                    break;
                default:
                    throw new FormatException(keyword == "layout"
                        ? "“layout” is grid (placed as written) or auto (placed from the arrows)."
                        : "“direction” is right or down.");
            }
            return new DiagramSettingStatement(keyword, value);
        }

        if (keyword is "font" or "size" or "gap")
        {
            if (tokens.Count < 2) throw new FormatException($"“{keyword}” needs a value.");
            string value = tokens[1].Value;
            switch (keyword)
            {
                case "font": diagram.FontFamily = value; break;
                case "size": diagram.FontSize = Number(value, keyword); break;
                case "gap": diagram.Gap = Number(value, keyword); break;
            }
            return new DiagramSettingStatement(keyword, value);
        }

        if (Kinds.Contains(keyword))
        {
            return new DiagramNodeStatement(ParseNode(keyword, tokens));
        }

        if (keyword == "group")
        {
            return new DiagramGroupStatement(ParseGroup(tokens));
        }

        if (tokens.Any(t => t.Kind == "arrow"))
        {
            return ParseChain(tokens);
        }

        throw new FormatException($"Unknown statement “{first.Value}”. Start a line with block, tensor, op, text, group, layout, direction, or write an arrow such as a -> b.");
    }

    private static double Number(string value, string what)
        => double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double n) && n > 0
            ? n
            : throw new FormatException($"“{what}” needs a positive number, not “{value}”.");

    private static string RequireId(Token? token, string what)
    {
        if (token == null || token.Kind != "word" || !IdRegex.IsMatch(token.Value))
        {
            throw new FormatException($"{what} needs a name (letters, digits, _ - .), e.g. “block encoder \"Encoder\"”.");
        }
        return token.Value;
    }

    private static DiagramNode ParseNode(string keyword, List<Token> tokens)
    {
        var node = new DiagramNode
        {
            Kind = keyword switch { "tensor" => DiagramNodeKind.Tensor, "op" => DiagramNodeKind.Op, "text" => DiagramNodeKind.Text, _ => DiagramNodeKind.Block },
            Id = RequireId(tokens.ElementAtOrDefault(1), keyword)
        };
        var strings = new List<string>();
        foreach (var token in tokens.Skip(2))
        {
            switch (token.Kind)
            {
                case "string":
                    strings.Add(token.Value);
                    break;
                case "word" when node.Kind == DiagramNodeKind.Op && strings.Count == 0 && token.Value is "add" or "mul" or "times" or "concat":
                    strings.Add(token.Value);
                    break;
                case "word":
                    ApplyFlag(node, token.Value.ToLowerInvariant());
                    break;
                case "option":
                    ApplyOption(node, token.Key!, token.Value);
                    break;
                default:
                    throw new FormatException($"Unexpected “{token.Value}” in {keyword} {node.Id}.");
            }
        }
        if (strings.Count > 3)
        {
            throw new FormatException($"{keyword} {node.Id} has {strings.Count} texts; give a title, a subtitle and a caption at most.");
        }
        node.Title = strings.ElementAtOrDefault(0) ?? (node.Kind == DiagramNodeKind.Op ? "add" : string.Empty);
        node.Subtitle = strings.ElementAtOrDefault(1) ?? string.Empty;
        if (strings.Count == 3) node.Caption = strings[2];
        return node;
    }

    private static void ApplyFlag(DiagramNode node, string flag)
    {
        switch (flag)
        {
            case "dashed": node.Border = DiagramLineStyle.Dashed; break;
            case "dotted": node.Border = DiagramLineStyle.Dotted; break;
            case "shaded": node.Shaded = true; break;
            case "plain": node.BoldTitle = false; break;
            case "bold": node.BoldTitle = true; break;
            case "thin" or "deep" or "flat": node.Shape = flag; break;
            default: throw new FormatException($"Unknown option “{flag}” for {node.Id}. Options: dashed, dotted, shaded, bold, plain, thin, deep, below=…, above=…, after=…, row=…, span=…, caption=\"…\", width=….");
        }
    }

    private static void ApplyOption(DiagramNode node, string key, string value)
    {
        switch (key)
        {
            case "below": node.Below = value; break;
            case "above": node.Above = value; break;
            case "after": node.After = value; break;
            case "caption": node.Caption = value; break;
            case "row":
                node.Row = int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int row) && row >= 0
                    ? row
                    : throw new FormatException($"“row=” needs a row number (0 is the top row), not “{value}”.");
                break;
            case "span": node.RowSpan = Math.Max(1, (int)Number(value, "span")); node.SpanSet = true; break;
            case "width": node.MinWidth = Number(value, "width"); break;
            default: throw new FormatException($"Unknown option “{key}=” for {node.Id}.");
        }
    }

    private static DiagramGroup ParseGroup(List<Token> tokens)
    {
        var group = new DiagramGroup { Id = RequireId(tokens.ElementAtOrDefault(1), "group") };
        foreach (var token in tokens.Skip(2))
        {
            switch (token.Kind)
            {
                case "string" when group.Title.Length == 0: group.Title = token.Value; break;
                case "word" when token.Value.ToLowerInvariant() == "dashed": group.Border = DiagramLineStyle.Dashed; break;
                case "word" when token.Value.ToLowerInvariant() == "dotted": group.Border = DiagramLineStyle.Dotted; break;
                case "word" when token.Value.ToLowerInvariant() == "solid": group.Border = DiagramLineStyle.Solid; break;
                case "word" when token.Value.ToLowerInvariant() == "shaded": group.Shaded = true; break;
                case "word" when IdRegex.IsMatch(token.Value): group.Members.Add(token.Value); break;
                default: throw new FormatException($"Unexpected “{token.Value}” in group {group.Id}.");
            }
        }
        if (group.Members.Count == 0)
        {
            throw new FormatException($"Group {group.Id} lists no nodes, e.g. “group refiner \"Refiner\" a b c”.");
        }
        return group;
    }

    private static DiagramChainStatement ParseChain(List<Token> tokens)
    {
        var nodes = new List<string>();
        var links = new List<(DiagramLineStyle, bool)>();
        string label = string.Empty;
        var route = DiagramRoute.Auto;
        bool expectNode = true;
        foreach (var token in tokens)
        {
            if (expectNode)
            {
                nodes.Add(RequireId(token, "An arrow"));
                expectNode = false;
                continue;
            }
            switch (token.Kind)
            {
                case "arrow":
                    links.Add(token.Value switch
                    {
                        "-->" => (DiagramLineStyle.Dashed, true),
                        "..>" => (DiagramLineStyle.Dotted, true),
                        "---" => (DiagramLineStyle.Dashed, false),
                        "--" => (DiagramLineStyle.Solid, false),
                        _ => (DiagramLineStyle.Solid, true)
                    });
                    expectNode = true;
                    break;
                case "option" when token.Key == "label":
                    label = token.Value;
                    break;
                case "string":
                    label = token.Value;
                    break;
                case "word" when token.Value.ToLowerInvariant() is "over" or "above":
                    route = DiagramRoute.Over;
                    break;
                case "word" when token.Value.ToLowerInvariant() is "under" or "below":
                    route = DiagramRoute.Under;
                    break;
                default:
                    throw new FormatException($"Unexpected “{token.Value}” in an arrow. After the nodes you can add label=\"…\", over or under.");
            }
        }
        if (expectNode || nodes.Count < 2)
        {
            throw new FormatException("An arrow needs a node at each end, e.g. “a -> b”.");
        }
        return new DiagramChainStatement(nodes, links, label, route);
    }

    private static void Validate(Diagram diagram, Dictionary<DiagramStatement, int> lineOf, List<DiagramError> errors)
    {
        var ids = new Dictionary<string, int>(StringComparer.Ordinal);
        int LineOf(DiagramStatement s) => lineOf.GetValueOrDefault(s);
        foreach (var s in diagram.Statements.OfType<DiagramNodeStatement>())
        {
            if (!ids.TryAdd(s.Node.Id, LineOf(s)))
            {
                errors.Add(new DiagramError(LineOf(s), $"The name “{s.Node.Id}” is used twice."));
            }
        }
        void Check(DiagramStatement s, string? id, string what)
        {
            if (id != null && !ids.ContainsKey(id))
            {
                errors.Add(new DiagramError(LineOf(s), $"{what} “{id}” is not a node."));
            }
        }
        foreach (var s in diagram.Statements)
        {
            switch (s)
            {
                case DiagramNodeStatement n:
                    Check(s, n.Node.Below, "below=");
                    Check(s, n.Node.Above, "above=");
                    Check(s, n.Node.After, "after=");
                    break;
                case DiagramChainStatement c:
                    foreach (string id in c.Nodes) Check(s, id, "The arrow end");
                    break;
                case DiagramGroupStatement g:
                    foreach (string id in g.Group.Members) Check(s, id, "Group member");
                    break;
            }
        }
    }

    // ---- Writing -----------------------------------------------------------------------------------------

    public static string Write(Diagram diagram)
    {
        var sb = new StringBuilder();
        foreach (var statement in diagram.Statements)
        {
            sb.Append(Write(statement)).Append('\n');
        }
        return sb.ToString();
    }

    public static string Write(DiagramStatement statement) => statement switch
    {
        DiagramCommentStatement c => c.Text,
        DiagramSettingStatement s => $"{s.Key} {Quote(s.Value, always: s.Key == "font")}",
        DiagramNodeStatement n => WriteNode(n.Node),
        DiagramChainStatement c => WriteChain(c),
        DiagramGroupStatement g => WriteGroup(g.Group),
        _ => string.Empty
    };

    private static string Quote(string text, bool always = true)
        => always || text.Length == 0 || text.Any(char.IsWhiteSpace) || text.Contains('"') ? $"\"{text.Replace("\"", "\\\"")}\"" : text;

    private static string WriteNode(DiagramNode n)
    {
        string kind = n.Kind switch { DiagramNodeKind.Tensor => "tensor", DiagramNodeKind.Op => "op", DiagramNodeKind.Text => "text", _ => "block" };
        var parts = new List<string> { kind.PadRight(6), n.Id };
        if (n.Kind == DiagramNodeKind.Op && n.Title is "add" or "mul" or "times" or "concat")
        {
            parts.Add(n.Title);
        }
        else
        {
            parts.Add(Quote(n.Title));
        }
        if (n.Subtitle.Length > 0) parts.Add(Quote(n.Subtitle));
        if (n.Caption.Length > 0) parts.Add($"caption={Quote(n.Caption)}");
        if (n.Below != null) parts.Add($"below={n.Below}");
        if (n.Above != null) parts.Add($"above={n.Above}");
        if (n.After != null) parts.Add($"after={n.After}");
        if (n.Row != null) parts.Add($"row={n.Row}");
        if (n.SpanSet) parts.Add($"span={n.RowSpan}");
        if (n.MinWidth != null) parts.Add($"width={n.MinWidth.Value.ToString(CultureInfo.InvariantCulture)}");
        if (n.Shape.Length > 0) parts.Add(n.Shape);
        if (n.Border == DiagramLineStyle.Dashed) parts.Add("dashed");
        if (n.Border == DiagramLineStyle.Dotted) parts.Add("dotted");
        if (n.Shaded) parts.Add("shaded");
        if (n.BoldTitle == false) parts.Add("plain");
        if (n.BoldTitle == true) parts.Add("bold");
        return string.Join(' ', parts);
    }

    private static string WriteChain(DiagramChainStatement c)
    {
        var sb = new StringBuilder(c.Nodes[0]);
        for (int i = 0; i < c.Links.Count; i++)
        {
            string arrow = c.Links[i] switch
            {
                (DiagramLineStyle.Dashed, true) => "-->",
                (DiagramLineStyle.Dotted, _) => "..>",
                (DiagramLineStyle.Dashed, false) => "---",
                (DiagramLineStyle.Solid, false) => "--",
                _ => "->"
            };
            sb.Append(' ').Append(arrow).Append(' ').Append(c.Nodes[i + 1]);
        }
        if (c.Label.Length > 0) sb.Append(" label=").Append(Quote(c.Label));
        if (c.Route == DiagramRoute.Over) sb.Append(" over");
        if (c.Route == DiagramRoute.Under) sb.Append(" under");
        return sb.ToString();
    }

    private static string WriteGroup(DiagramGroup g)
    {
        var parts = new List<string> { "group", g.Id, Quote(g.Title) };
        parts.AddRange(g.Members);
        if (g.Border == DiagramLineStyle.Dotted) parts.Add("dotted");
        if (g.Border == DiagramLineStyle.Solid) parts.Add("solid");
        if (g.Shaded) parts.Add("shaded");
        return string.Join(' ', parts);
    }

    // ---- Editing, as the canvas does ------------------------------------------------------------------

    /// <summary>A name not used yet, such as block3.</summary>
    public static string NewId(Diagram diagram, string stem)
    {
        var used = diagram.Nodes.Select(n => n.Id).Concat(diagram.Groups.Select(g => g.Id)).ToHashSet(StringComparer.Ordinal);
        for (int n = 1; ; n++)
        {
            string id = $"{stem}{n}";
            if (!used.Contains(id)) return id;
        }
    }

    /// <summary>
    /// Adds a node after <paramref name="afterId"/> in its row, spliced into the arrow leaving it (a -> b becomes
    /// a -> new -> b), or at the end of the diagram connected from the last node.
    /// </summary>
    public static DiagramNode AddNode(Diagram diagram, DiagramNodeKind kind, string? afterId)
    {
        string stem = kind switch { DiagramNodeKind.Tensor => "tensor", DiagramNodeKind.Op => "op", DiagramNodeKind.Text => "text", _ => "block" };
        var node = new DiagramNode
        {
            Id = NewId(diagram, stem),
            Kind = kind,
            Title = kind switch { DiagramNodeKind.Op => "add", DiagramNodeKind.Tensor => "Features", DiagramNodeKind.Text => "$x$", _ => "Layer" },
            Subtitle = kind switch { DiagramNodeKind.Block => "$C \\to C$", DiagramNodeKind.Tensor => "$C \\times H \\times W$", _ => string.Empty }
        };

        var anchor = afterId != null ? diagram.FindNode(afterId) : diagram.Nodes.LastOrDefault();
        var statement = new DiagramNodeStatement(node);
        int declaration = anchor != null
            ? diagram.Statements.FindIndex(s => s is DiagramNodeStatement d && d.Node.Id == anchor.Id)
            : -1;
        if (anchor != null)
        {
            // Same row as the anchor.
            node.Row = anchor.Row;
            if (anchor.Below != null) node.After = anchor.Id;
            if (anchor.Above != null) node.After = anchor.Id;
        }
        diagram.Statements.Insert(declaration >= 0 ? declaration + 1 : LastNodeIndex(diagram) + 1, statement);

        if (anchor == null)
        {
            return node;
        }
        // Splice into the first solid arrow leaving the anchor; otherwise continue a chain ending there.
        foreach (var chain in diagram.Statements.OfType<DiagramChainStatement>())
        {
            for (int i = 0; i + 1 < chain.Nodes.Count; i++)
            {
                if (chain.Nodes[i] == anchor.Id && chain.Links[i].Style == DiagramLineStyle.Solid)
                {
                    chain.Nodes.Insert(i + 1, node.Id);
                    chain.Links.Insert(i, chain.Links[i]);
                    return node;
                }
            }
        }
        var ending = diagram.Statements.OfType<DiagramChainStatement>().FirstOrDefault(c => c.Nodes[^1] == anchor.Id && c.Route == DiagramRoute.Auto && c.Label.Length == 0);
        if (ending != null)
        {
            ending.Nodes.Add(node.Id);
            ending.Links.Add((DiagramLineStyle.Solid, true));
        }
        else
        {
            diagram.Statements.Add(new DiagramChainStatement([anchor.Id, node.Id], [(DiagramLineStyle.Solid, true)], string.Empty, DiagramRoute.Auto));
        }
        return node;
    }

    private static int LastNodeIndex(Diagram diagram)
        => diagram.Statements.FindLastIndex(s => s is DiagramNodeStatement or DiagramSettingStatement);

    /// <summary>Removes a node; arrows through it are joined (a -> x -> b becomes a -> b), others removed.</summary>
    public static void RemoveNode(Diagram diagram, string id)
    {
        diagram.Statements.RemoveAll(s => s is DiagramNodeStatement n && n.Node.Id == id);
        foreach (var node in diagram.Nodes)
        {
            if (node.Below == id) node.Below = null;
            if (node.Above == id) node.Above = null;
            if (node.After == id) node.After = null;
        }
        foreach (var chain in diagram.Statements.OfType<DiagramChainStatement>().ToList())
        {
            for (int i = chain.Nodes.Count - 1; i >= 0; i--)
            {
                if (chain.Nodes[i] != id) continue;
                chain.Nodes.RemoveAt(i);
                // Keep the link into the removed node when joining (a -> x --> b becomes a -> b).
                chain.Links.RemoveAt(i < chain.Links.Count ? i : i - 1);
            }
            if (chain.Nodes.Count < 2)
            {
                diagram.Statements.Remove(chain);
            }
        }
        foreach (var group in diagram.Groups.ToList())
        {
            group.Members.Remove(id);
            if (group.Members.Count == 0)
            {
                diagram.Statements.RemoveAll(s => s is DiagramGroupStatement g && g.Group == group);
            }
        }
    }

    /// <summary>Adds an arrow from one node to another.</summary>
    public static void Connect(Diagram diagram, string from, string to, DiagramLineStyle style = DiagramLineStyle.Solid)
        => diagram.Statements.Add(new DiagramChainStatement([from, to], [(style, true)], string.Empty, DiagramRoute.Auto));

    /// <summary>
    /// Moves a node <paramref name="steps"/> places along its row (negative: left), by moving its declaration
    /// among the nodes laid out in the same row. Returns false when it cannot move.
    /// </summary>
    public static bool MoveNode(Diagram diagram, string id, int steps)
    {
        var layoutRows = DiagramLayout.AssignGrid(diagram);
        if (!layoutRows.TryGetValue(id, out var cell)) return false;
        var row = layoutRows.Where(kv => kv.Value.Row == cell.Row).OrderBy(kv => kv.Value.Column).Select(kv => kv.Key).ToList();
        int from = row.IndexOf(id);
        int to = Math.Clamp(from + steps, 0, row.Count - 1);
        if (to == from) return false;

        // Nodes placed relative to another (after=, below=) are moved by changing that reference.
        var node = diagram.FindNode(id)!;
        var target = diagram.FindNode(row[to])!;
        if (node.Below != null || node.Above != null)
        {
            return false;
        }
        int nodeIndex = diagram.Statements.FindIndex(s => s is DiagramNodeStatement d && d.Node == node);
        var statement = diagram.Statements[nodeIndex];
        diagram.Statements.RemoveAt(nodeIndex);
        int targetIndex = diagram.Statements.FindIndex(s => s is DiagramNodeStatement d && d.Node == target);
        diagram.Statements.Insert(to > from ? targetIndex + 1 : targetIndex, statement);

        // after= links that pinned the order are dropped so the new order holds.
        if (node.After != null) node.After = null;
        foreach (var other in diagram.Nodes.Where(n => n.After == id || n.After == target.Id))
        {
            other.After = null;
        }
        return true;
    }
}
