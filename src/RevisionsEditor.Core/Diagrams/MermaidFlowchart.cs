using System.Text;
using System.Text.RegularExpressions;

namespace RevisionsEditor.Core.Diagrams;

/// <summary>
/// Converts a Mermaid flowchart into this app's diagram text, laid out automatically ("layout auto"), so it can be
/// edited on the canvas and typeset with LaTeX math. Covers what flowcharts in papers use: <c>flowchart</c> /
/// <c>graph</c> with a direction; nodes <c>A[box]</c>, <c>A(rounded)</c>, <c>A([pill])</c>, <c>A{decision}</c>,
/// <c>A((circle))</c>, <c>A[(database)]</c>, <c>A[[subroutine]]</c>, <c>A&gt;flag]</c>; arrows <c>--&gt;</c>,
/// <c>---</c>, <c>-.-&gt;</c>, <c>==&gt;</c> with <c>|label|</c> or <c>-- label --&gt;</c>; chains (<c>A --&gt; B --&gt; C</c>),
/// <c>&amp;</c>; and <c>subgraph … end</c> as groups. Styling (<c>classDef</c>, <c>style</c>, <c>click</c>) is skipped.
/// </summary>
public static class MermaidFlowchart
{
    private static readonly Regex HeaderRegex = new(@"^\s*(flowchart|graph)\s*(?<dir>TB|TD|BT|LR|RL)?\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // A node: id, then optionally a shape with text. Longest brackets first.
    private static readonly Regex NodeRegex = new(
        @"\G\s*(?<id>[A-Za-z0-9_][\w.-]*)(?:(?<open>\(\[|\[\[|\[\(|\(\(|\[/|\[\\|\{\{|\[|\(|\{|>)(?<text>""[^""]*""|[^\]\)\}]*?)(?<close>\]\)|\]\]|\)\]|\)\)|/\]|\\\]|\}\}|\]|\)|\}))?",
        RegexOptions.Compiled);

    // An arrow: its kind, with an optional label written into it (-- text -->) or after it (|text|).
    private static readonly Regex ArrowRegex = new(
        @"\G\s*(?:(?<lead>--|==|-\.)\s*(?<inner>[^-=|>\s][^>|]*?)\s*)?(?<arrow><?(?:-{2,}>|-{3,}|={2,}>|={3,}|-\.+->|-\.+-|~~~|--[ox]|==[ox]))(?:\s*\|(?<label>[^|]*)\|)?",
        RegexOptions.Compiled);

    public static bool LooksLikeFlowchart(string text)
        => text.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0 && !l.StartsWith("%%", StringComparison.Ordinal)) is { } first && HeaderRegex.IsMatch(first);

    /// <summary>The diagram text for a Mermaid flowchart, and notes on anything that was left out.</summary>
    public static (string Source, List<string> Notes) Convert(string mermaid)
    {
        var notes = new List<string>();
        var order = new List<string>();
        var nodes = new Dictionary<string, (string Kind, string Text, string Shape)>(StringComparer.Ordinal);
        var arrows = new List<(string From, string To, string Arrow, string Label)>();
        var groups = new List<(string Id, string Title, List<string> Members)>();
        var open = new Stack<(string Id, string Title, List<string> Members)>();
        string direction = "down";

        void Node(string id, string? shapeOpen, string? text)
        {
            if (!nodes.ContainsKey(id))
            {
                order.Add(id);
                nodes[id] = ("block", id, string.Empty);
            }
            if (shapeOpen != null)
            {
                string label = Unquote(text ?? string.Empty);
                nodes[id] = shapeOpen switch
                {
                    "((" => ("op", label, "circle"),
                    "{" or "{{" => ("block", label, "decision"),
                    "[(" => ("block", label, "database"),
                    "(" or "([" => ("block", label, "rounded"),
                    _ => ("block", label, string.Empty)
                };
            }
            foreach (var g in open)
            {
                if (!g.Members.Contains(id)) g.Members.Add(id);
                break; // innermost group only: a node belongs to one frame
            }
        }

        foreach (var raw in mermaid.Replace("\r\n", "\n").Split('\n'))
        {
            string line = raw.Trim().TrimEnd(';');
            if (line.Length == 0 || line.StartsWith("%%", StringComparison.Ordinal)) continue;
            if (HeaderRegex.Match(line) is { Success: true } header)
            {
                direction = header.Groups["dir"].Value.ToUpperInvariant() is "LR" or "RL" ? "right" : "down";
                continue;
            }
            if (Regex.Match(line, @"^subgraph\s+(?<id>[\w.-]+)?\s*(?:\[(?<title>[^\]]*)\])?\s*(?<rest>.*)$") is { Success: true } sub)
            {
                string id = sub.Groups["id"].Success ? Safe(sub.Groups["id"].Value) : $"group{groups.Count + open.Count + 1}";
                string title = sub.Groups["title"].Success ? Unquote(sub.Groups["title"].Value) : Unquote(sub.Groups["rest"].Value.Length > 0 ? sub.Groups["rest"].Value : sub.Groups["id"].Value);
                open.Push((id, title, []));
                continue;
            }
            if (line == "end")
            {
                if (open.Count > 0) groups.Add(open.Pop());
                continue;
            }
            if (Regex.IsMatch(line, @"^(classDef|class|style|linkStyle|click|direction)\b"))
            {
                if (!notes.Contains("Styling (classDef, style, click) is left out; the diagram's own style is used.")) notes.Add("Styling (classDef, style, click) is left out; the diagram's own style is used.");
                continue;
            }

            // Statements: node (& node)* (arrow node (& node)*)*
            int at = 0;
            List<string>? previous = null;
            string? pendingArrow = null, pendingLabel = null;
            bool ok = true;
            while (at < line.Length && ok)
            {
                var current = new List<string>();
                while (true)
                {
                    var m = NodeRegex.Match(line, at);
                    if (!m.Success || m.Length == 0) { ok = false; break; }
                    string id = Safe(m.Groups["id"].Value);
                    Node(id, m.Groups["open"].Success ? m.Groups["open"].Value : null, m.Groups["text"].Success ? m.Groups["text"].Value : null);
                    current.Add(id);
                    at = m.Index + m.Length;
                    var amp = Regex.Match(line[at..], @"^\s*&");
                    if (!amp.Success) break;
                    at += amp.Length;
                }
                if (!ok) break;
                if (previous != null && pendingArrow != null)
                {
                    foreach (string from in previous)
                        foreach (string to in current)
                            arrows.Add((from, to, pendingArrow, pendingLabel ?? string.Empty));
                }
                previous = current;
                if (at >= line.Length || line[at..].Trim().Length == 0) break;
                var arrow = ArrowRegex.Match(line, at);
                if (!arrow.Success || arrow.Length == 0) { ok = false; break; }
                pendingArrow = arrow.Groups["arrow"].Value;
                pendingLabel = arrow.Groups["label"].Success ? arrow.Groups["label"].Value.Trim()
                    : arrow.Groups["inner"].Success ? arrow.Groups["inner"].Value.Trim() : null;
                at = arrow.Index + arrow.Length;
            }
            if (!ok)
            {
                notes.Add($"Not understood, left out: {line}");
            }
        }
        while (open.Count > 0) groups.Add(open.Pop());

        var sb = new StringBuilder();
        sb.Append("# Converted from a Mermaid flowchart; laid out automatically from the arrows.\n");
        sb.Append("layout auto\n");
        sb.Append($"direction {direction}\n");
        foreach (string id in order)
        {
            var (kind, text, shape) = nodes[id];
            string flags = shape switch
            {
                "decision" => " shaded",
                "database" => " shaded",
                _ => string.Empty
            };
            sb.Append(kind == "op"
                ? $"op     {id} \"{Escape(text)}\"\n"
                : $"block  {id} \"{Escape(text)}\"{flags}\n");
        }
        if (groups.Count > 0) sb.Append('\n');
        foreach (var (id, title, members) in groups.Where(g => g.Members.Count > 0))
        {
            sb.Append($"group {id} \"{Escape(title)}\" {string.Join(' ', members)} dashed\n");
        }
        if (arrows.Count > 0) sb.Append('\n');
        foreach (var (from, to, arrow, label) in arrows.Where(a => a.Arrow != "~~~"))
        {
            string a = arrow.Contains('.') ? "-->" : arrow.Contains('>') || arrow.EndsWith('o') || arrow.EndsWith('x') ? "->" : "--";
            if (arrow.Contains('.') && !arrow.Contains('>')) a = "---";
            sb.Append($"{from} {a} {to}{(label.Length > 0 ? $" label=\"{Escape(label)}\"" : string.Empty)}\n");
        }
        return (sb.ToString(), notes);
    }

    private static string Unquote(string text)
    {
        text = text.Trim();
        if (text.Length >= 2 && text[0] == '"' && text[^1] == '"') text = text[1..^1];
        // Mermaid line breaks (<br>, <br/>) become spaces; markdown strings keep their text.
        text = Regex.Replace(text, @"<br\s*/?>", " ", RegexOptions.IgnoreCase);
        text = text.Replace("`", string.Empty);
        return Regex.Replace(text, @"\s+", " ").Trim();
    }

    private static string Escape(string text) => text.Replace("\"", "'");

    /// <summary>An id the diagram format accepts (letters, digits, _ - .), starting with a letter or _.</summary>
    private static string Safe(string id)
    {
        string s = Regex.Replace(id, @"[^\w.-]", "_");
        return char.IsLetter(s[0]) || s[0] == '_' ? s : "n" + s;
    }
}
