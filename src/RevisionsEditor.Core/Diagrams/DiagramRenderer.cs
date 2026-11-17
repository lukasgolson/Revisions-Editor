using System.Globalization;
using System.Text;

namespace RevisionsEditor.Core.Diagrams;

/// <summary>
/// Draws a laid-out diagram as SVG (for the editor, the preview and Word) or as TikZ (for the LaTeX output,
/// where TeX typesets the labels' math itself). Both draw the same shapes at the same places.
/// </summary>
public static class DiagramRenderer
{
    private const string Ink = "#000000";
    private const string FrameInk = "#7A7A7A";
    private const string ShadeFill = "#F0F0F0";
    private const string FrontFill = "#F7F7F7";
    private const string TopFill = "#ECECEC";
    private const string SideFill = "#D4D4D4";

    private static string N(double v) => Math.Round(v, 2).ToString("0.##", CultureInfo.InvariantCulture);

    public static string ToSvg(Diagram diagram) => ToSvg(DiagramLayout.Layout(diagram));

    public static string ToSvg(DiagramLayoutResult layout)
    {
        var svg = new StringBuilder();
        double lw = layout.LineWidth;
        svg.Append($"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{N(layout.Width)}\" height=\"{N(layout.Height)}\" viewBox=\"0 0 {N(layout.Width)} {N(layout.Height)}\">\n");
        svg.Append($"<rect x=\"0\" y=\"0\" width=\"{N(layout.Width)}\" height=\"{N(layout.Height)}\" fill=\"#FFFFFF\"/>\n");

        foreach (var g in layout.Groups)
        {
            var b = g.Group.Border;
            svg.Append($"<rect x=\"{N(g.Box.X)}\" y=\"{N(g.Box.Y)}\" width=\"{N(g.Box.Width)}\" height=\"{N(g.Box.Height)}\" fill=\"{(g.Group.Shaded ? "#FAFAFA" : "none")}\" stroke=\"{FrameInk}\" stroke-width=\"{N(lw * 0.7)}\"{Dash(b, lw * 0.7)}/>\n");
            if (g.Title != null) Text(svg, g.Title, "#333333");
        }

        foreach (var e in layout.Edges)
        {
            var pts = e.Points;
            if (pts.Count < 2) continue;
            var line = new List<DiagramPoint>(pts);
            if (e.Edge.Arrow)
            {
                line[^1] = Shorten(line[^2], line[^1], layout.ArrowLength * 0.8);
            }
            svg.Append($"<polyline points=\"{string.Join(' ', line.Select(p => $"{N(p.X)},{N(p.Y)}"))}\" fill=\"none\" stroke=\"{Ink}\" stroke-width=\"{N(lw)}\" stroke-linejoin=\"round\"{Dash(e.Edge.Style, lw)}/>\n");
            if (e.Edge.Arrow)
            {
                var head = ArrowHead(pts[^2], pts[^1], layout.ArrowLength);
                svg.Append($"<polygon points=\"{string.Join(' ', head.Select(p => $"{N(p.X)},{N(p.Y)}"))}\" fill=\"{Ink}\"/>\n");
            }
            if (e.Label != null) Text(svg, e.Label, "#222222");
        }

        foreach (var n in layout.Nodes)
        {
            DrawNode(svg, n, layout);
        }
        svg.Append("</svg>\n");
        return svg.ToString();
    }

    private static void DrawNode(StringBuilder svg, DiagramNodeLayout n, DiagramLayoutResult layout)
    {
        double lw = layout.LineWidth;
        string dash = Dash(n.Node.Border, lw);
        var b = n.Body;
        switch (n.Node.Kind)
        {
            case DiagramNodeKind.Block:
                svg.Append($"<rect x=\"{N(b.X)}\" y=\"{N(b.Y)}\" width=\"{N(b.Width)}\" height=\"{N(b.Height)}\" fill=\"{(n.Node.Shaded ? ShadeFill : "#FFFFFF")}\" stroke=\"{Ink}\" stroke-width=\"{N(lw * 1.15)}\"{dash}/>\n");
                break;
            case DiagramNodeKind.Tensor:
                foreach (var (points, fill) in TensorFaces(n))
                {
                    svg.Append($"<polygon points=\"{string.Join(' ', points.Select(p => $"{N(p.X)},{N(p.Y)}"))}\" fill=\"{fill}\" stroke=\"{Ink}\" stroke-width=\"{N(lw * 1.15)}\" stroke-linejoin=\"round\"{dash}/>\n");
                }
                break;
            case DiagramNodeKind.Op:
            {
                double r = b.Width / 2;
                svg.Append($"<circle cx=\"{N(b.CenterX)}\" cy=\"{N(b.CenterY)}\" r=\"{N(r)}\" fill=\"{(n.Node.Shaded ? ShadeFill : "#FFFFFF")}\" stroke=\"{Ink}\" stroke-width=\"{N(lw * 1.15)}\"{dash}/>\n");
                foreach (var (a, c) in OpStrokes(n))
                {
                    svg.Append($"<line x1=\"{N(a.X)}\" y1=\"{N(a.Y)}\" x2=\"{N(c.X)}\" y2=\"{N(c.Y)}\" stroke=\"{Ink}\" stroke-width=\"{N(lw * 1.1)}\" stroke-linecap=\"round\"/>\n");
                }
                break;
            }
        }
        foreach (var t in n.Texts) Text(svg, t, Ink);
        foreach (var t in n.Captions) Text(svg, t, Ink);
    }

    /// <summary>Front, top and side of a tensor slab.</summary>
    private static IEnumerable<(DiagramPoint[] Points, string Fill)> TensorFaces(DiagramNodeLayout n)
    {
        var f = n.Front;
        double dx = n.Depth, dy = n.Depth * 0.75;
        yield return ([new(f.X, f.Y), new(f.Right, f.Y), new(f.Right + dx, f.Y - dy), new(f.X + dx, f.Y - dy)], TopFill);
        yield return ([new(f.Right, f.Y), new(f.Right + dx, f.Y - dy), new(f.Right + dx, f.Bottom - dy), new(f.Right, f.Bottom)], SideFill);
        yield return ([new(f.X, f.Y), new(f.Right, f.Y), new(f.Right, f.Bottom), new(f.X, f.Bottom)], FrontFill);
    }

    /// <summary>The strokes of a built-in operator: + for add, × for mul, ‖ for concat.</summary>
    private static IEnumerable<(DiagramPoint, DiagramPoint)> OpStrokes(DiagramNodeLayout n)
    {
        var b = n.Body;
        double r = b.Width / 2, cx = b.CenterX, cy = b.CenterY, s = r * 0.55;
        switch (n.Node.Title)
        {
            case "add":
                yield return (new(cx - s, cy), new(cx + s, cy));
                yield return (new(cx, cy - s), new(cx, cy + s));
                break;
            case "mul" or "times":
                double d = s * 0.75;
                yield return (new(cx - d, cy - d), new(cx + d, cy + d));
                yield return (new(cx - d, cy + d), new(cx + d, cy - d));
                break;
            case "concat":
                yield return (new(cx - s * 0.3, cy - s), new(cx - s * 0.3, cy + s));
                yield return (new(cx + s * 0.3, cy - s), new(cx + s * 0.3, cy + s));
                break;
        }
    }

    private static string Dash(DiagramLineStyle style, double lw) => style switch
    {
        DiagramLineStyle.Dashed => $" stroke-dasharray=\"{N(lw * 3.2)} {N(lw * 2.2)}\"",
        DiagramLineStyle.Dotted => $" stroke-dasharray=\"{N(lw * 0.1)} {N(lw * 2.2)}\" stroke-linecap=\"round\"",
        _ => string.Empty
    };

    private static DiagramPoint Shorten(DiagramPoint from, DiagramPoint to, double by)
    {
        double dx = to.X - from.X, dy = to.Y - from.Y, len = Math.Sqrt(dx * dx + dy * dy);
        return len <= by ? from : new DiagramPoint(to.X - dx / len * by, to.Y - dy / len * by);
    }

    private static DiagramPoint[] ArrowHead(DiagramPoint from, DiagramPoint tip, double length)
    {
        double dx = tip.X - from.X, dy = tip.Y - from.Y, len = Math.Sqrt(dx * dx + dy * dy);
        if (len < 1e-6) return [tip, tip, tip];
        double ux = dx / len, uy = dy / len, half = length * 0.42;
        var back = new DiagramPoint(tip.X - ux * length, tip.Y - uy * length);
        return [tip, new(back.X - uy * half, back.Y + ux * half), new(back.X + uy * half, back.Y - ux * half)];
    }

    private static void Text(StringBuilder svg, DiagramPlacedText placed, string color)
    {
        foreach (var run in placed.Box.Runs)
        {
            if (run.Text.Trim().Length == 0) continue;
            svg.Append($"<text x=\"{N(placed.X + run.X)}\" y=\"{N(placed.Baseline + run.Baseline)}\" font-family=\"{Xml(FamilyList(run.Family))}\" font-size=\"{N(run.Size)}\"");
            if (run.Bold) svg.Append(" font-weight=\"bold\"");
            if (run.Italic) svg.Append(" font-style=\"italic\"");
            svg.Append($" fill=\"{color}\" xml:space=\"preserve\">{Xml(run.Text)}</text>\n");
        }
        foreach (var rule in placed.Box.Rules)
        {
            svg.Append($"<line x1=\"{N(placed.X + rule.X1)}\" y1=\"{N(placed.Baseline + rule.Y)}\" x2=\"{N(placed.X + rule.X2)}\" y2=\"{N(placed.Baseline + rule.Y)}\" stroke=\"{color}\" stroke-width=\"{N(rule.Thickness)}\"/>\n");
        }
    }

    private static string FamilyList(string family) => family.Contains(' ') ? $"'{family}', serif" : $"{family}, serif";

    private static string Xml(string s) => s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");

    // ---- TikZ -----------------------------------------------------------------------------------------

    /// <summary>
    /// A tikzpicture drawing the diagram in diagram units (0.75 pt each, as SVG pixels), for \input in a figure.
    /// Needs the tikz package. Labels are typeset by TeX from their source, so their math is real LaTeX math.
    /// </summary>
    public static string ToTikz(Diagram diagram) => ToTikz(DiagramLayout.Layout(diagram));

    public static string ToTikz(DiagramLayoutResult layout)
    {
        var tex = new StringBuilder();
        double lw = layout.LineWidth * 0.75;
        tex.AppendLine("% Drawn by Revisions Editor from a .diagram file; edit the diagram there, not this file.");
        tex.AppendLine(@"\begin{tikzpicture}[x=0.75pt,y=-0.75pt,line join=round]");
        tex.AppendLine($@"\path[use as bounding box] (0,0) rectangle ({N(layout.Width)},{N(layout.Height)});");

        foreach (var g in layout.Groups)
        {
            tex.AppendLine($@"\draw[draw=black!50,line width={N(lw * 0.7)}pt{TikzDash(g.Group.Border, lw * 0.7)}{(g.Group.Shaded ? ",fill=black!2" : string.Empty)}] ({N(g.Box.X)},{N(g.Box.Y)}) rectangle ({N(g.Box.Right)},{N(g.Box.Bottom)});");
            if (g.Title != null) TikzText(tex, g.Title, "black!80");
        }

        foreach (var e in layout.Edges)
        {
            var pts = e.Points;
            if (pts.Count < 2) continue;
            var line = new List<DiagramPoint>(pts);
            if (e.Edge.Arrow) line[^1] = Shorten(line[^2], line[^1], layout.ArrowLength * 0.8);
            tex.AppendLine($@"\draw[line width={N(lw)}pt{TikzDash(e.Edge.Style, lw)}] {string.Join(" -- ", line.Select(P))};");
            if (e.Edge.Arrow)
            {
                tex.AppendLine($@"\fill {string.Join(" -- ", ArrowHead(pts[^2], pts[^1], layout.ArrowLength).Select(P))} -- cycle;");
            }
            if (e.Label != null) TikzText(tex, e.Label, "black!90");
        }

        foreach (var n in layout.Nodes)
        {
            string dash = TikzDash(n.Node.Border, lw);
            var b = n.Body;
            switch (n.Node.Kind)
            {
                case DiagramNodeKind.Block:
                    tex.AppendLine($@"\draw[line width={N(lw * 1.15)}pt{dash},fill={(n.Node.Shaded ? "black!6" : "white")}] ({N(b.X)},{N(b.Y)}) rectangle ({N(b.Right)},{N(b.Bottom)});");
                    break;
                case DiagramNodeKind.Tensor:
                    foreach (var (points, fill) in TensorFaces(n))
                    {
                        string f = fill == TopFill ? "black!8" : fill == SideFill ? "black!17" : "black!3";
                        tex.AppendLine($@"\draw[line width={N(lw * 1.15)}pt{dash},fill={f}] {string.Join(" -- ", points.Select(P))} -- cycle;");
                    }
                    break;
                case DiagramNodeKind.Op:
                    tex.AppendLine($@"\draw[line width={N(lw * 1.15)}pt{dash},fill={(n.Node.Shaded ? "black!6" : "white")}] ({N(b.CenterX)},{N(b.CenterY)}) circle[radius={N(b.Width / 2)}];");
                    foreach (var (a, c) in OpStrokes(n))
                    {
                        tex.AppendLine($@"\draw[line width={N(lw * 1.1)}pt,line cap=round] {P(a)} -- {P(c)};");
                    }
                    break;
            }
            foreach (var t in n.Texts) TikzText(tex, t, null);
            foreach (var t in n.Captions) TikzText(tex, t, null);
        }
        tex.AppendLine(@"\end{tikzpicture}");
        return tex.ToString();
    }

    private static string P(DiagramPoint p) => $"({N(p.X)},{N(p.Y)})";

    private static string TikzDash(DiagramLineStyle style, double lw) => style switch
    {
        DiagramLineStyle.Dashed => $",dash pattern=on {N(lw * 3.2)}pt off {N(lw * 2.2)}pt",
        DiagramLineStyle.Dotted => $",line cap=round,dash pattern=on 0pt off {N(lw * 2.2)}pt",
        _ => string.Empty
    };

    private static void TikzText(StringBuilder tex, DiagramPlacedText placed, string? color)
    {
        var box = placed.Box;
        if (box.Source.Trim().Length == 0) return;
        double pt = box.Size * 0.75;
        string font = $@"\fontsize{{{N(pt)}}}{{{N(pt * 1.2)}}}\selectfont{(box.Bold ? @"\bfseries" : string.Empty)}";
        // Centred on the measured box, at its baseline: TeX's own widths differ a little from the screen font's.
        tex.AppendLine($@"\node[anchor=base,inner sep=0pt,outer sep=0pt{(color != null ? "," + color : string.Empty)}] at ({N(placed.X + box.Width / 2)},{N(placed.Baseline)}) {{{font} {ToLatex(box.Source)}}};");
    }

    /// <summary>A label as LaTeX: text escaped (symbols set as math), $…$ kept as it is.</summary>
    public static string ToLatex(string label)
    {
        var sb = new StringBuilder();
        foreach (var (text, isMath) in DiagramText.Split(label))
        {
            if (isMath)
            {
                sb.Append('$').Append(text).Append('$');
                continue;
            }
            foreach (char c in text)
            {
                sb.Append(c switch
                {
                    '\\' => @"\textbackslash{}",
                    '{' => @"\{",
                    '}' => @"\}",
                    '$' => @"\$",
                    '&' => @"\&",
                    '%' => @"\%",
                    '#' => @"\#",
                    '_' => @"\_",
                    '^' => @"\^{}",
                    '~' => @"\textasciitilde{}",
                    // Arrows, operators and Greek letters as math, which every math font has.
                    _ when c is '×' or '·' or '±' or '‖' or '∥' or '⊕' or '⊗' or '−' or '≤' or '≥' or '≈' or '∇' or '∈' or '→' or '←' or '↔' or '⇒'
                        || (c >= 'Α' && c <= 'ω') || (c >= '←' && c <= '⋿') => $"${c}$",
                    _ => c.ToString()
                });
            }
        }
        return sb.ToString();
    }
}
