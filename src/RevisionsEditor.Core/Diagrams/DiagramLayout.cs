namespace RevisionsEditor.Core.Diagrams;

public readonly record struct DiagramPoint(double X, double Y);

public readonly record struct DiagramRect(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;
    public double Bottom => Y + Height;
    public double CenterX => X + Width / 2;
    public double CenterY => Y + Height / 2;
    public bool IsEmpty => Width <= 0 || Height <= 0;

    public bool Intersects(DiagramRect other, double tolerance = 0.5)
        => X < other.Right - tolerance && other.X < Right - tolerance && Y < other.Bottom - tolerance && other.Y < Bottom - tolerance;

    public bool Contains(double x, double y) => x >= X && x <= Right && y >= Y && y <= Bottom;

    public DiagramRect Union(DiagramRect other)
    {
        if (IsEmpty) return other;
        if (other.IsEmpty) return this;
        double x = Math.Min(X, other.X), y = Math.Min(Y, other.Y);
        return new DiagramRect(x, y, Math.Max(Right, other.Right) - x, Math.Max(Bottom, other.Bottom) - y);
    }

    public DiagramRect Inflate(double dx, double dy) => new(X - dx, Y - dy, Width + 2 * dx, Height + 2 * dy);
}

/// <summary>A label placed at a position: its left end and baseline.</summary>
public sealed record DiagramPlacedText(DiagramTextBox Box, double X, double Baseline)
{
    public DiagramRect Bounds => new(X, Baseline - Box.Ascent, Box.Width, Box.Height);
}

public sealed class DiagramNodeLayout
{
    public required DiagramNode Node { get; init; }
    public int Row { get; init; }
    public int Column { get; init; }
    /// <summary>The drawn shape (for a tensor: the slab with its depth).</summary>
    public DiagramRect Body { get; set; }
    /// <summary>For a tensor: the front face.</summary>
    public DiagramRect Front { get; set; }
    public double Depth { get; set; }
    public List<DiagramPlacedText> Texts { get; } = [];
    public List<DiagramPlacedText> Captions { get; } = [];
    /// <summary>Everything the node draws, captions included.</summary>
    public DiagramRect Bounds => Captions.Aggregate(Body, (r, c) => r.Union(c.Bounds));
    /// <summary>Height of the line arrows meet the node at.</summary>
    public double PortY { get; set; }
}

public sealed class DiagramEdgeLayout
{
    public required DiagramEdge Edge { get; init; }
    public List<DiagramPoint> Points { get; } = [];
    public DiagramPlacedText? Label { get; set; }
}

public sealed class DiagramGroupLayout
{
    public required DiagramGroup Group { get; init; }
    public DiagramRect Box { get; set; }
    public DiagramPlacedText? Title { get; set; }
}

public sealed class DiagramLayoutResult
{
    public double Width { get; set; }
    public double Height { get; set; }
    public string FontFamily { get; set; } = "Times New Roman";
    public double FontSize { get; set; }
    public double LineWidth { get; set; }
    public double ArrowLength { get; set; }
    public List<DiagramNodeLayout> Nodes { get; } = [];
    public List<DiagramEdgeLayout> Edges { get; } = [];
    public List<DiagramGroupLayout> Groups { get; } = [];

    /// <summary>The node drawn at a point (for clicking on the canvas), or null.</summary>
    public DiagramNodeLayout? NodeAt(double x, double y)
        => Nodes.LastOrDefault(n => n.Body.Inflate(4, 4).Contains(x, y)) ?? Nodes.LastOrDefault(n => n.Bounds.Contains(x, y));
}

/// <summary>
/// Lays a diagram out on a grid of rows and columns. Nodes go left to right in the order they are declared;
/// below=/above= put a node in the next row in the same column. Every row is a band of zones stacked top to
/// bottom — arrows routed over the row, a group's title, the nodes, their captions, arrows routed under the
/// row, the group's bottom padding — each as tall as the row needs, and column gaps widen to fit edge labels
/// and group frames. So no text, frame or arrow is drawn over another, whatever the labels say.
/// </summary>
public static class DiagramLayout
{
    private enum RouteKind { Straight, Lane, Elbow, Vertical }

    private sealed class EdgePlan
    {
        public required DiagramEdge Edge { get; init; }
        public required DiagramNodeLayout From { get; init; }
        public required DiagramNodeLayout To { get; init; }
        public RouteKind Kind { get; set; }
        public bool Over { get; set; }
        public bool InsideGroup { get; set; }
        public int LaneRow { get; set; }
        public int LaneIndex { get; set; }
        public DiagramTextBox? Label { get; set; }
    }

    /// <summary>Row and column of every node.</summary>
    public static Dictionary<string, (int Row, int Column)> AssignGrid(Diagram diagram)
    {
        var nodes = diagram.Nodes.ToList();
        var cells = new Dictionary<string, (int Row, int Column)>(StringComparer.Ordinal);
        var occupied = new HashSet<(int, int)>();
        var lastColumn = new Dictionary<int, int>();
        int Span(string id) => diagram.FindNode(id)?.EffectiveSpan ?? 1;
        // A node continues the span of the node before it in its row (the flow after a merge stays centred).
        var carry = new Dictionary<int, int>();

        bool Place(DiagramNode node, int row, int column)
        {
            while (Enumerable.Range(row, node.EffectiveSpan).Any(r => occupied.Contains((r, column))))
            {
                column++;
            }
            cells[node.Id] = (row, column);
            for (int r = row; r < row + node.EffectiveSpan; r++)
            {
                occupied.Add((r, column));
                lastColumn[r] = Math.Max(lastColumn.GetValueOrDefault(r, -1), column);
            }
            return true;
        }

        // Several passes, so a node can refer to one declared after it.
        var pending = new List<DiagramNode>(nodes);
        for (int pass = 0; pass < 4 && pending.Count > 0; pass++)
        {
            foreach (var node in pending.ToList())
            {
                node.EffectiveSpan = node.RowSpan;
                string? reference = node.Below ?? node.Above ?? node.After;
                bool last = pass == 3;
                if (reference != null && !cells.ContainsKey(reference) && !last && nodes.Any(n => n.Id == reference))
                {
                    continue;
                }
                if (node.Below != null && cells.TryGetValue(node.Below, out var b))
                {
                    Place(node, b.Row + Span(node.Below), b.Column);
                }
                else if (node.Above != null && cells.TryGetValue(node.Above, out var a))
                {
                    Place(node, a.Row - node.EffectiveSpan, a.Column);
                }
                else if (node.After != null && cells.TryGetValue(node.After, out var f))
                {
                    int row = node.Row ?? f.Row;
                    if (!node.SpanSet && carry.GetValueOrDefault(row, 1) > 1) node.EffectiveSpan = carry[row];
                    Place(node, row, f.Column + 1);
                    carry[row] = node.EffectiveSpan;
                }
                else
                {
                    int row = node.Row ?? 0;
                    if (!node.SpanSet && carry.GetValueOrDefault(row, 1) > 1) node.EffectiveSpan = carry[row];
                    Place(node, row, lastColumn.GetValueOrDefault(row, -1) + 1);
                    carry[row] = node.EffectiveSpan;
                }
                pending.Remove(node);
            }
        }

        // Rows from 0 (above= can reach above the first row).
        int minRow = cells.Count == 0 ? 0 : cells.Values.Min(c => c.Row);
        if (minRow != 0)
        {
            foreach (var key in cells.Keys.ToList())
            {
                cells[key] = (cells[key].Row - minRow, cells[key].Column);
            }
        }
        return cells;
    }

    /// <summary>
    /// Lays the diagram out. Arrows routed round a row hug the nodes they pass when nothing is in the way, and
    /// otherwise take a lane reserved over or under the row: a first pass finds which fit, the next lays out
    /// without reserving room for those.
    /// </summary>
    public static DiagramLayoutResult Layout(Diagram diagram)
    {
        if (diagram.AutoLayout)
        {
            return DiagramAutoLayout.Layout(diagram);
        }
        var hugging = new HashSet<int>();
        var result = LayoutPass(diagram, hugging, out var fits);
        hugging = fits;
        for (int attempt = 0; attempt < 6 && hugging.Count > 0; attempt++)
        {
            result = LayoutPass(diagram, hugging, out fits);
            var misfits = hugging.Where(i => !fits.Contains(i)).ToList();
            if (misfits.Count == 0) break;
            hugging.ExceptWith(misfits);
            if (hugging.Count == 0) result = LayoutPass(diagram, hugging, out _);
        }
        return result;
    }

    private static DiagramLayoutResult LayoutPass(Diagram diagram, HashSet<int> hugging, out HashSet<int> fitsHugging)
    {
        fitsHugging = [];
        double fs = diagram.FontSize;
        string family = diagram.FontFamily;
        var result = new DiagramLayoutResult
        {
            FontFamily = family,
            FontSize = fs,
            LineWidth = Math.Max(1, fs * 0.11),
            ArrowLength = fs * 0.55
        };
        var grid = AssignGrid(diagram);
        if (grid.Count == 0)
        {
            result.Width = fs * 10;
            result.Height = fs * 4;
            return result;
        }

        // ---- Measure nodes -------------------------------------------------------------------------
        var layouts = new Dictionary<string, DiagramNodeLayout>(StringComparer.Ordinal);
        var above = new Dictionary<string, double>(); // extent above the port line
        var below = new Dictionary<string, double>(); // extent below the port line (body only)
        var captionBoxes = new Dictionary<string, List<DiagramTextBox>>();
        var width = new Dictionary<string, double>();
        foreach (var node in diagram.Nodes)
        {
            if (!grid.TryGetValue(node.Id, out var cell)) continue;
            var m = Measure(node, cell.Row, cell.Column, family, fs);
            layouts[node.Id] = m.Layout;
            captionBoxes[node.Id] = m.Captions;
            width[node.Id] = m.Width;
            above[node.Id] = m.Above;
            below[node.Id] = m.Below;
        }

        // Blocks in a row share one height, as in a drawn pipeline.
        foreach (var row in layouts.Values.Where(l => l.Node.Kind == DiagramNodeKind.Block && l.Node.EffectiveSpan == 1).GroupBy(l => l.Row))
        {
            double half = row.Max(l => above[l.Node.Id]);
            foreach (var l in row)
            {
                above[l.Node.Id] = below[l.Node.Id] = half;
            }
        }

        int rows = layouts.Values.Max(l => l.Row + l.Node.EffectiveSpan);
        int columns = layouts.Values.Max(l => l.Column) + 1;
        double captionGap = fs * 0.45, captionLine = fs * 0.2;
        double CaptionHeight(string id) => captionBoxes[id].Count == 0 ? 0
            : captionGap + captionBoxes[id].Sum(c => c.Height) + captionLine * (captionBoxes[id].Count - 1);
        double CaptionWidth(string id) => captionBoxes[id].Count == 0 ? 0 : captionBoxes[id].Max(c => c.Width);

        // ---- Groups --------------------------------------------------------------------------------
        var groupOf = new Dictionary<string, DiagramGroup>(StringComparer.Ordinal);
        foreach (var g in diagram.Groups)
        {
            foreach (string m in g.Members.Where(layouts.ContainsKey))
            {
                groupOf.TryAdd(m, g);
            }
        }
        var groupTitles = diagram.Groups.ToDictionary(g => g, g => g.Title.Length > 0 ? DiagramText.Layout(g.Title, family, fs * 0.9, true) : null);
        double groupPad = fs * 0.7;
        var groupRows = diagram.Groups
            .Where(g => g.Members.Any(layouts.ContainsKey))
            .ToDictionary(g => g, g =>
            {
                var members = g.Members.Where(layouts.ContainsKey).Select(m => layouts[m]).ToList();
                return (Top: members.Min(m => m.Row), Bottom: members.Max(m => m.Row + m.Node.EffectiveSpan - 1));
            });

        // ---- Plan edges ----------------------------------------------------------------------------
        var plans = new List<EdgePlan>();
        foreach (var edge in diagram.Edges)
        {
            if (!layouts.TryGetValue(edge.From, out var from) || !layouts.TryGetValue(edge.To, out var to) || from == to) continue;
            var plan = new EdgePlan { Edge = edge, From = from, To = to };
            plan.Label = edge.Label.Length > 0 ? DiagramText.Layout(edge.Label, family, fs * 0.8, false) : null;
            bool sameRow = Covers(to, from.Row) || Covers(from, to.Row);
            bool blocked = layouts.Values.Any(n => n != from && n != to && Covers(n, from.Row) && n.Column > Math.Min(from.Column, to.Column) && n.Column < Math.Max(from.Column, to.Column));
            plan.InsideGroup = groupOf.TryGetValue(to.Node.Id, out var gt)
                && (groupOf.GetValueOrDefault(from.Node.Id) == gt || StraightSuccessor(diagram, layouts, from) is { } succ && groupOf.GetValueOrDefault(succ.Node.Id) == gt);
            if (edge.Route != DiagramRoute.Auto || (sameRow && (blocked || to.Column <= from.Column) && from.Column != to.Column))
            {
                plan.Kind = RouteKind.Lane;
                plan.Over = edge.Route != DiagramRoute.Under && !(edge.Route == DiagramRoute.Auto && to.Column < from.Column);
                plan.LaneRow = plan.Over ? Math.Min(from.Row, to.Row) : Math.Max(from.Row + from.Node.EffectiveSpan, to.Row + to.Node.EffectiveSpan) - 1;
            }
            else if (sameRow)
            {
                plan.Kind = RouteKind.Straight;
            }
            else if (to.Column > from.Column)
            {
                plan.Kind = RouteKind.Elbow;
            }
            else
            {
                plan.Kind = RouteKind.Vertical;
            }
            plans.Add(plan);
        }
        // Lanes stacked per row, inside or outside groups.
        foreach (var lanes in plans.Where(p => p.Kind == RouteKind.Lane).GroupBy(p => (p.LaneRow, p.Over, p.InsideGroup)))
        {
            int i = 0;
            foreach (var p in lanes.OrderBy(p => Math.Abs(p.To.Column - p.From.Column)))
            {
                p.LaneIndex = i++;
            }
        }
        double laneStep = fs * 1.1;
        double LaneHeight(EdgePlan p) => laneStep + (p.Label != null ? p.Label.Height + fs * 0.2 : 0);
        bool Reserved(EdgePlan p) => !hugging.Contains(plans.IndexOf(p));
        double LaneZone(int row, bool over, bool inside)
            => plans.Where(p => p.Kind == RouteKind.Lane && Reserved(p) && p.LaneRow == row && p.Over == over && p.InsideGroup == inside).Sum(LaneHeight)
               + (plans.Any(p => p.Kind == RouteKind.Lane && Reserved(p) && p.LaneRow == row && p.Over == over && p.InsideGroup == inside) ? fs * 0.3 : 0);

        // ---- Columns -------------------------------------------------------------------------------
        var colWidth = new double[columns];
        foreach (var l in layouts.Values)
        {
            colWidth[l.Column] = Math.Max(colWidth[l.Column], Math.Max(width[l.Node.Id], CaptionWidth(l.Node.Id)));
        }
        var gapAfter = Enumerable.Repeat(diagram.Gap, columns).ToArray();
        double minArrowRun = fs * 1.4;
        foreach (var p in plans)
        {
            int c = Math.Min(p.From.Column, p.To.Column);
            if (p.Kind == RouteKind.Straight && p.Label != null && Math.Abs(p.To.Column - p.From.Column) == 1)
            {
                gapAfter[c] = Math.Max(gapAfter[c], p.Label.Width + fs * 1.2);
            }
            if (p.Kind == RouteKind.Elbow)
            {
                // The vertical part runs down the middle of the gap, with its label beside it.
                gapAfter[p.From.Column] = Math.Max(gapAfter[p.From.Column], 2 * ((p.Label?.Width ?? 0) + fs * 0.6) + minArrowRun * 0.5);
            }
        }
        for (int c = 0; c + 1 < columns; c++)
        {
            // A group frame between two columns needs its padding on each side, plus room for the arrow.
            bool boundary = layouts.Values.Any(a => a.Column == c && layouts.Values.Any(b => b.Column == c + 1 && SpansRow(a, b)
                && (groupOf.GetValueOrDefault(a.Node.Id) != groupOf.GetValueOrDefault(b.Node.Id))));
            if (boundary)
            {
                gapAfter[c] = Math.Max(gapAfter[c], 2 * groupPad + minArrowRun);
            }
        }
        double margin = fs * 0.8;
        var colX = new double[columns];
        double x = margin;
        for (int c = 0; c < columns; c++)
        {
            colX[c] = x;
            x += colWidth[c] + (c + 1 < columns ? gapAfter[c] : 0);
        }

        // ---- Rows ----------------------------------------------------------------------------------
        var bodyAbove = new double[rows];
        var bodyBelow = new double[rows];
        var captionZone = new double[rows];
        foreach (var l in layouts.Values.Where(l => l.Node.EffectiveSpan == 1))
        {
            bodyAbove[l.Row] = Math.Max(bodyAbove[l.Row], above[l.Node.Id]);
            bodyBelow[l.Row] = Math.Max(bodyBelow[l.Row], below[l.Node.Id]);
            captionZone[l.Row] = Math.Max(captionZone[l.Row], CaptionHeight(l.Node.Id));
        }
        for (int r = 0; r < rows; r++)
        {
            if (bodyAbove[r] == 0 && bodyBelow[r] == 0)
            {
                bodyAbove[r] = bodyBelow[r] = fs * 1.5; // a row only spanned into
            }
        }
        var groupHead = new double[rows];
        var groupFoot = new double[rows];
        foreach (var (g, span) in groupRows)
        {
            groupHead[span.Top] = Math.Max(groupHead[span.Top], groupPad + (groupTitles[g] is { } t ? t.Height + fs * 0.35 : 0));
            groupFoot[span.Bottom] = Math.Max(groupFoot[span.Bottom], groupPad);
        }
        var portY = new double[rows];
        var bandTop = new double[rows];
        var groupTop = new double[rows];
        var groupBottom = new double[rows];
        var laneOverOut = new double[rows];
        var laneOverIn = new double[rows];
        var laneUnderIn = new double[rows];
        var laneUnderOut = new double[rows];
        double rowGap = fs * 1.2;
        // Room between rows for labels of arrows running down between them.
        var labelBetween = new double[rows];
        foreach (var p in plans.Where(p => p.Kind is RouteKind.Elbow or RouteKind.Vertical && p.Label != null))
        {
            int r = Math.Min(p.From.Row, p.To.Row);
            labelBetween[r] = Math.Max(labelBetween[r], p.Label!.Height + fs * 0.4);
        }
        double y = margin;
        for (int r = 0; r < rows; r++)
        {
            bandTop[r] = y;
            laneOverOut[r] = y + LaneZone(r, true, false);
            y = laneOverOut[r];
            groupTop[r] = y;
            y += groupHead[r];
            laneOverIn[r] = y + LaneZone(r, true, true);
            y = laneOverIn[r];
            portY[r] = y + bodyAbove[r];
            y = portY[r] + bodyBelow[r] + captionZone[r];
            laneUnderIn[r] = y;
            y += LaneZone(r, false, true);
            y += groupFoot[r];
            groupBottom[r] = y;
            laneUnderOut[r] = y;
            y += LaneZone(r, false, false);
            y += rowGap + labelBetween[r];
        }

        // ---- Place nodes ---------------------------------------------------------------------------
        foreach (var l in layouts.Values)
        {
            string id = l.Node.Id;
            double port = l.Node.EffectiveSpan == 1 ? portY[l.Row] : (portY[l.Row] + portY[l.Row + l.Node.EffectiveSpan - 1]) / 2;
            double w = width[id];
            double left = colX[l.Column] + (colWidth[l.Column] - w) / 2;
            // Captions line up across a row, under the tallest node.
            double captionTop = l.Node.EffectiveSpan == 1 ? portY[l.Row] + bodyBelow[l.Row] + captionGap : port + below[id] + captionGap;
            Place(new MeasuredNode(l, w, above[id], below[id], captionBoxes[id]), left, port, captionTop, fs);
            result.Nodes.Add(l);
        }

        // ---- Route edges ---------------------------------------------------------------------------
        int[] Rows(DiagramNodeLayout n) => Enumerable.Range(n.Row, n.Node.EffectiveSpan).ToArray();
        // Just over (or under) the nodes the lane passes, stacked with the other lanes that hug them.
        double HuggingLaneY(EdgePlan p)
        {
            int lo = Math.Min(p.From.Column, p.To.Column), hi = Math.Max(p.From.Column, p.To.Column);
            var rowsCovered = Rows(p.From).Concat(Rows(p.To)).ToHashSet();
            var around = layouts.Values.Where(n => n.Column >= lo && n.Column <= hi && Rows(n).Any(rowsCovered.Contains)).ToList();
            var stacked = plans.Where(q => q.Kind == RouteKind.Lane && !Reserved(q) && q.Over == p.Over && q.InsideGroup == p.InsideGroup
                && q.LaneRow == p.LaneRow && q.LaneIndex < p.LaneIndex).Sum(LaneHeight);
            double clearance = fs * 0.9;
            return p.Over
                ? around.Min(n => n.Body.Y) - clearance - stacked - (p.Label != null ? p.Label.Height + fs * 0.2 : 0)
                : around.Max(n => n.Bounds.Bottom) + clearance + stacked;
        }
        // Whether a lane at y crosses no node (other than its ends) and its label no text.
        bool LaneIsClear(EdgePlan p, DiagramPoint start, DiagramPoint end, double y, DiagramPlacedText? label)
        {
            double lw = result.LineWidth * 2;
            var parts = new List<DiagramRect>
            {
                new(Math.Min(start.X, end.X), y - lw, Math.Abs(end.X - start.X), 2 * lw),
                new(start.X - lw, Math.Min(start.Y, y), 2 * lw, Math.Abs(y - start.Y)),
                new(end.X - lw, Math.Min(end.Y, y), 2 * lw, Math.Abs(y - end.Y))
            };
            if (label != null) parts.Add(label.Bounds.Inflate(fs * 0.15, fs * 0.1));
            return layouts.Values.Where(n => n != p.From && n != p.To).All(n => parts.All(r => !r.Intersects(n.Bounds)));
        }

        double laneOffset(EdgePlan p)
        {
            var before = plans.Where(q => q.Kind == RouteKind.Lane && Reserved(q) == Reserved(p) && q.LaneRow == p.LaneRow && q.Over == p.Over && q.InsideGroup == p.InsideGroup && q.LaneIndex < p.LaneIndex);
            return before.Sum(LaneHeight) + laneStep * 0.5 + (p.Label != null && p.Over ? p.Label.Height + fs * 0.2 : 0);
        }
        bool HasStraightOut(DiagramNodeLayout n) => plans.Any(q => q.Kind == RouteKind.Straight && q.From == n && q.To.Column > n.Column);
        double GapMid(DiagramNodeLayout n) => colX[n.Column] + colWidth[n.Column] + gapAfter[n.Column] / 2;

        foreach (var p in plans)
        {
            var e = new DiagramEdgeLayout { Edge = p.Edge };
            var pts = e.Points;
            var from = p.From;
            var to = p.To;
            switch (p.Kind)
            {
                case RouteKind.Straight:
                {
                    bool forward = to.Column > from.Column;
                    var a = Port(from, forward ? Side.Right : Side.Left, fs);
                    var b = Port(to, forward ? Side.Left : Side.Right, fs);
                    pts.Add(a);
                    if (Math.Abs(a.Y - b.Y) > 0.5)
                    {
                        int before = forward ? to.Column - 1 : to.Column;
                        double mx = before >= 0 && before < columns - 1
                            ? colX[before] + colWidth[before] + gapAfter[before] / 2
                            : (a.X + b.X) / 2;
                        pts.Add(new DiagramPoint(mx, a.Y));
                        pts.Add(new DiagramPoint(mx, b.Y));
                    }
                    pts.Add(b);
                    if (p.Label != null)
                    {
                        // Above the longest horizontal part.
                        var (s, t) = LongestHorizontal(pts);
                        e.Label = new DiagramPlacedText(p.Label, (s.X + t.X) / 2 - p.Label.Width / 2, s.Y - fs * 0.35 - p.Label.Descent);
                    }
                    break;
                }
                case RouteKind.Lane:
                {
                    int r = p.LaneRow;
                    double huggingY = HuggingLaneY(p);
                    double laneY = !Reserved(p) ? huggingY : p.Over
                        ? (p.InsideGroup ? laneOverIn[r] : laneOverOut[r]) - laneOffset(p)
                        : (p.InsideGroup ? laneUnderIn[r] : laneUnderOut[r]) + laneOffset(p);
                    var side = p.Over ? Side.Top : Side.Bottom;
                    DiagramPoint start;
                    if (HasStraightOut(from) && to.Column > from.Column)
                    {
                        // Branch off the arrow leaving the node, as a skip connection is drawn; into a group, just
                        // inside its frame.
                        double tap = Math.Min(GapMid(from), Port(from, Side.Right, fs).X + fs * 0.6);
                        if (p.InsideGroup && !groupOf.ContainsKey(from.Node.Id) && StraightSuccessor(diagram, layouts, from) is { } next)
                        {
                            tap = colX[next.Column] - groupPad / 2;
                        }
                        start = new DiagramPoint(tap, from.PortY);
                    }
                    else
                    {
                        start = Port(from, side, fs);
                    }
                    var end = Port(to, side, fs);
                    pts.Add(start);
                    pts.Add(new DiagramPoint(start.X, laneY));
                    pts.Add(new DiagramPoint(end.X, laneY));
                    pts.Add(end);
                    DiagramPlacedText? LabelAt(double ly) => p.Label == null ? null
                        : new DiagramPlacedText(p.Label, (start.X + end.X) / 2 - p.Label.Width / 2, p.Over ? ly - fs * 0.3 - p.Label.Descent : ly + fs * 0.3 + p.Label.Ascent);
                    e.Label = LabelAt(laneY);
                    // Over a group's own nodes, a lane keeps its reserved room under the group's title.
                    bool mayHug = !(p.Over && p.InsideGroup);
                    if (mayHug && LaneIsClear(p, start, end, huggingY, LabelAt(huggingY)))
                    {
                        fitsHugging.Add(plans.IndexOf(p));
                    }
                    break;
                }
                case RouteKind.Elbow:
                {
                    double mx = GapMid(from);
                    DiagramPoint start = HasStraightOut(from)
                        ? new DiagramPoint(mx, from.PortY)
                        : Port(from, Side.Right, fs);
                    var end = Port(to, Side.Left, fs);
                    pts.Add(start);
                    if (Math.Abs(start.X - mx) > 0.5) pts.Add(new DiagramPoint(mx, start.Y));
                    pts.Add(new DiagramPoint(mx, end.Y));
                    pts.Add(end);
                    if (p.Label != null)
                    {
                        // Beside the vertical part, between the rows.
                        double between = (Math.Max(from.Row, to.Row) > Math.Min(from.Row, to.Row))
                            ? (laneUnderOut[Math.Min(from.Row, to.Row)] + bandTop[Math.Max(from.Row, to.Row)]) / 2
                            : (start.Y + end.Y) / 2;
                        e.Label = new DiagramPlacedText(p.Label, mx - fs * 0.4 - p.Label.Width, between + (p.Label.Ascent - p.Label.Descent) / 2);
                    }
                    break;
                }
                default:
                {
                    // Down (or up) from one row to another.
                    bool down = to.Row > from.Row;
                    var start = down ? new DiagramPoint(from.Body.CenterX, from.Bounds.Bottom + fs * 0.2) : Port(from, Side.Top, fs);
                    var end = down ? Port(to, Side.Top, fs) : new DiagramPoint(to.Body.CenterX, to.Bounds.Bottom + fs * 0.2);
                    pts.Add(start);
                    if (Math.Abs(start.X - end.X) > 0.5)
                    {
                        double my = down
                            ? (laneUnderOut[from.Row] + bandTop[to.Row]) / 2
                            : (laneUnderOut[to.Row] + bandTop[from.Row]) / 2;
                        pts.Add(new DiagramPoint(start.X, my));
                        pts.Add(new DiagramPoint(end.X, my));
                    }
                    pts.Add(end);
                    if (p.Label != null)
                    {
                        double my = (start.Y + end.Y) / 2;
                        e.Label = new DiagramPlacedText(p.Label, end.X + fs * 0.4, my + (p.Label.Ascent - p.Label.Descent) / 2);
                    }
                    break;
                }
            }
            result.Edges.Add(e);
        }

        // ---- Group frames --------------------------------------------------------------------------
        foreach (var (g, span) in groupRows)
        {
            var members = g.Members.Where(layouts.ContainsKey).Select(m => layouts[m]).ToList();
            double left = members.Min(m => colX[m.Column]) - groupPad;
            double right = members.Max(m => colX[m.Column] + colWidth[m.Column]) + groupPad;
            // The frame hugs its nodes and the arrows routed inside it.
            var inside = members.Aggregate(new DiagramRect(), (r, m) => r.Union(m.Bounds));
            foreach (var (plan, edge) in plans.Zip(result.Edges))
            {
                if (plan.Kind == RouteKind.Lane && plan.InsideGroup && groupOf.GetValueOrDefault(plan.To.Node.Id) == g)
                {
                    foreach (var pt in edge.Points) inside = inside.Union(new DiagramRect(pt.X, pt.Y, 0.1, 0.1));
                    if (edge.Label != null) inside = inside.Union(edge.Label.Bounds);
                }
            }
            double top = Math.Max(groupTop[span.Top] + (groupTitles[g] is { } t ? t.Height + fs * 0.35 : 0), inside.Y - groupPad);
            double bottom = Math.Min(groupBottom[span.Bottom], inside.Bottom + groupPad);
            var layout = new DiagramGroupLayout { Group = g, Box = new DiagramRect(left, top, right - left, bottom - top) };
            if (groupTitles[g] is { } title)
            {
                layout.Title = new DiagramPlacedText(title, (left + right) / 2 - title.Width / 2, top - fs * 0.35 - title.Descent);
            }
            result.Groups.Add(layout);
        }

        Frame(result, margin);
        return result;
    }

    /// <summary>Moves everything drawn to <paramref name="margin"/> from the top left and sizes the drawing to fit.</summary>
    internal static void Frame(DiagramLayoutResult result, double margin)
    {
        var bounds = new DiagramRect();
        foreach (var n in result.Nodes) bounds = bounds.Union(n.Bounds);
        foreach (var g in result.Groups) bounds = bounds.Union(g.Box).Union(g.Title?.Bounds ?? default);
        foreach (var e in result.Edges)
        {
            foreach (var pt in e.Points) bounds = bounds.Union(new DiagramRect(pt.X - 1, pt.Y - 1, 2, 2));
            if (e.Label != null) bounds = bounds.Union(e.Label.Bounds);
        }
        Shift(result, margin - bounds.X, margin - bounds.Y);
        result.Width = bounds.Width + 2 * margin;
        result.Height = bounds.Height + 2 * margin;
    }

    /// <summary>The node the first plain arrow out of <paramref name="from"/> goes to, in the same row.</summary>
    private static DiagramNodeLayout? StraightSuccessor(Diagram diagram, Dictionary<string, DiagramNodeLayout> layouts, DiagramNodeLayout from)
        => diagram.Edges
            .Where(e => e.From == from.Node.Id && e.Route == DiagramRoute.Auto && layouts.ContainsKey(e.To))
            .Select(e => layouts[e.To])
            .Where(t => t.Column == from.Column + 1 && (Covers(t, from.Row) || Covers(from, t.Row)))
            .FirstOrDefault();

    /// <summary>A node measured: its layout (texts not yet placed), width, and extent above and below its port line.</summary>
    internal sealed record MeasuredNode(DiagramNodeLayout Layout, double Width, double Above, double Below, List<DiagramTextBox> Captions)
    {
        /// <summary>Height of the captions under the node, with the gap above them.</summary>
        public double CaptionHeight(double fs) => Captions.Count == 0 ? 0 : fs * 0.45 + Captions.Sum(c => c.Height) + fs * 0.2 * (Captions.Count - 1);
        public double CaptionWidth => Captions.Count == 0 ? 0 : Captions.Max(c => c.Width);
    }

    /// <summary>Measures a node: the shape it draws and its texts, set in the diagram's font.</summary>
    internal static MeasuredNode Measure(DiagramNode node, int row, int column, string family, double fs)
    {
        var layout = new DiagramNodeLayout { Node = node, Row = row, Column = column };
        var captions = new List<DiagramTextBox>();
        double w, up, down;
        switch (node.Kind)
        {
            case DiagramNodeKind.Tensor:
            {
                var (fw, fh, depth) = node.Shape switch
                {
                    "thin" => (fs * 0.65, fs * 5.2, fs * 0.7),
                    "deep" => (fs * 1.7, fs * 6.0, fs * 3.4),
                    "flat" => (fs * 3.5, fs * 1.2, fs * 1.0),
                    _ => (fs * 1.6, fs * 4.6, fs * 1.6)
                };
                double dy = depth * 0.75;
                layout.Depth = depth;
                w = fw + depth;
                up = fh / 2 + dy;
                down = fh / 2;
                layout.Front = new DiagramRect(0, 0, fw, fh);
                if (node.Title.Length > 0) captions.Add(DiagramText.Layout(node.Title, family, fs * 0.95, node.IsTitleBold));
                if (node.Subtitle.Length > 0) captions.Add(DiagramText.Layout(node.Subtitle, family, fs * 0.85, false));
                break;
            }
            case DiagramNodeKind.Op:
            {
                double r = fs * 0.75;
                if (node.Title is not ("add" or "mul" or "times" or "concat"))
                {
                    var symbol = DiagramText.Layout(node.Title, family, fs, node.IsTitleBold);
                    r = Math.Max(r, Math.Max(symbol.Width, symbol.Height) / 2 + fs * 0.3);
                    layout.Texts.Add(new DiagramPlacedText(symbol, 0, 0));
                }
                w = 2 * r;
                up = down = r;
                if (node.Subtitle.Length > 0) captions.Add(DiagramText.Layout(node.Subtitle, family, fs * 0.85, false));
                break;
            }
            case DiagramNodeKind.Text:
            {
                var title = DiagramText.Layout(node.Title, family, fs, node.IsTitleBold);
                var sub = node.Subtitle.Length > 0 ? DiagramText.Layout(node.Subtitle, family, fs * 0.85, false) : null;
                double h = title.Height + (sub != null ? fs * 0.35 + sub.Height : 0);
                w = Math.Max(title.Width, sub?.Width ?? 0) + fs * 0.3;
                layout.Texts.Add(new DiagramPlacedText(title, 0, 0));
                if (sub != null) layout.Texts.Add(new DiagramPlacedText(sub, 0, 0));
                // Arrows meet text at its first line.
                up = title.Ascent * 0.75 + fs * 0.15;
                down = h - up + fs * 0.15;
                break;
            }
            default:
            {
                var title = DiagramText.Layout(node.Title, family, fs, node.IsTitleBold);
                var sub = node.Subtitle.Length > 0 ? DiagramText.Layout(node.Subtitle, family, fs * 0.85, false) : null;
                double padX = fs * 0.9, padY = fs * 0.75;
                double textH = title.Height + (sub != null ? fs * 0.45 + sub.Height : 0);
                w = Math.Max(Math.Max(title.Width, sub?.Width ?? 0) + 2 * padX, node.MinWidth ?? fs * 5.5);
                double h = Math.Max(textH + 2 * padY, fs * 3.8);
                layout.Texts.Add(new DiagramPlacedText(title, 0, 0));
                if (sub != null) layout.Texts.Add(new DiagramPlacedText(sub, 0, 0));
                up = down = h / 2;
                break;
            }
        }
        if (node.Caption.Length > 0)
        {
            foreach (string line in node.Caption.Split("\\n"))
            {
                captions.Add(DiagramText.Layout(line, family, fs * 0.85, false));
            }
        }
        return new MeasuredNode(layout, w, up, down, captions);
    }

    /// <summary>Places a measured node: its shape from <paramref name="left"/> around the port line, its texts, and its captions from <paramref name="captionTop"/>.</summary>
    internal static void Place(MeasuredNode m, double left, double port, double captionTop, double fs)
    {
        var l = m.Layout;
        l.PortY = port;
        l.Body = new DiagramRect(left, port - m.Above, m.Width, m.Above + m.Below);
        switch (l.Node.Kind)
        {
            case DiagramNodeKind.Tensor:
                l.Front = new DiagramRect(left, port - l.Front.Height / 2, l.Front.Width, l.Front.Height);
                break;
            case DiagramNodeKind.Op:
                for (int i = 0; i < l.Texts.Count; i++)
                {
                    var t = l.Texts[i].Box;
                    l.Texts[i] = new DiagramPlacedText(t, l.Body.CenterX - t.Width / 2, port + (t.Ascent - t.Descent) / 2);
                }
                break;
            default:
            {
                // Title and subtitle centred in the box (text: from the top).
                double total = l.Texts.Sum(t => t.Box.Height) + (l.Texts.Count > 1 ? fs * (l.Node.Kind == DiagramNodeKind.Text ? 0.35 : 0.45) : 0);
                double top = l.Node.Kind == DiagramNodeKind.Text ? l.Body.Y + fs * 0.15 : l.Body.CenterY - total / 2;
                for (int i = 0; i < l.Texts.Count; i++)
                {
                    var t = l.Texts[i].Box;
                    l.Texts[i] = new DiagramPlacedText(t, l.Body.CenterX - t.Width / 2, top + t.Ascent);
                    top += t.Height + fs * (l.Node.Kind == DiagramNodeKind.Text ? 0.35 : 0.45);
                }
                break;
            }
        }
        foreach (var c in m.Captions)
        {
            l.Captions.Add(new DiagramPlacedText(c, l.Body.X + (l.Node.Kind == DiagramNodeKind.Tensor ? l.Front.Width / 2 + l.Depth / 2 : l.Body.Width / 2) - c.Width / 2, captionTop + c.Ascent));
            captionTop += c.Height + fs * 0.2;
        }
    }

    private static bool Covers(DiagramNodeLayout n, int row) => row >= n.Row && row < n.Row + n.Node.EffectiveSpan;

    private static bool SpansRow(DiagramNodeLayout a, DiagramNodeLayout b)
        => a.Row < b.Row + b.Node.EffectiveSpan && b.Row < a.Row + a.Node.EffectiveSpan;

    private enum Side { Left, Right, Top, Bottom }

    /// <summary>Where an arrow meets a node.</summary>
    private static DiagramPoint Port(DiagramNodeLayout n, Side side, double fs)
    {
        double pad = n.Node.Kind == DiagramNodeKind.Text ? fs * 0.25 : 0;
        return (n.Node.Kind, side) switch
        {
            (DiagramNodeKind.Tensor, Side.Left) => new(n.Front.X, n.PortY),
            (DiagramNodeKind.Tensor, Side.Right) => new(n.Body.Right, n.PortY),
            (DiagramNodeKind.Tensor, Side.Top) => new(n.Front.CenterX + n.Depth / 2, n.Body.Y),
            (DiagramNodeKind.Tensor, Side.Bottom) => new(n.Front.CenterX, n.Front.Bottom),
            (DiagramNodeKind.Text, Side.Top) => new(n.Body.CenterX, n.Body.Y - pad),
            (DiagramNodeKind.Text, Side.Bottom) => new(n.Body.CenterX, n.Body.Bottom + pad),
            (_, Side.Left) => new(n.Body.X - pad, n.PortY),
            (_, Side.Right) => new(n.Body.Right + pad, n.PortY),
            (_, Side.Top) => new(n.Body.CenterX, n.Body.Y),
            _ => new(n.Body.CenterX, n.Body.Bottom)
        };
    }

    private static (DiagramPoint, DiagramPoint) LongestHorizontal(List<DiagramPoint> pts)
    {
        var best = (pts[0], pts[^1]);
        double length = -1;
        for (int i = 0; i + 1 < pts.Count; i++)
        {
            if (Math.Abs(pts[i].Y - pts[i + 1].Y) < 0.5 && Math.Abs(pts[i + 1].X - pts[i].X) > length)
            {
                length = Math.Abs(pts[i + 1].X - pts[i].X);
                best = (pts[i], pts[i + 1]);
            }
        }
        return best;
    }

    private static void Shift(DiagramLayoutResult result, double dx, double dy)
    {
        static DiagramRect R(DiagramRect r, double dx, double dy) => r with { X = r.X + dx, Y = r.Y + dy };
        static DiagramPlacedText T(DiagramPlacedText t, double dx, double dy) => t with { X = t.X + dx, Baseline = t.Baseline + dy };
        foreach (var n in result.Nodes)
        {
            n.Body = R(n.Body, dx, dy);
            n.Front = R(n.Front, dx, dy);
            n.PortY += dy;
            for (int i = 0; i < n.Texts.Count; i++) n.Texts[i] = T(n.Texts[i], dx, dy);
            for (int i = 0; i < n.Captions.Count; i++) n.Captions[i] = T(n.Captions[i], dx, dy);
        }
        foreach (var e in result.Edges)
        {
            for (int i = 0; i < e.Points.Count; i++) e.Points[i] = new DiagramPoint(e.Points[i].X + dx, e.Points[i].Y + dy);
            if (e.Label != null) e.Label = T(e.Label, dx, dy);
        }
        foreach (var g in result.Groups)
        {
            g.Box = R(g.Box, dx, dy);
            if (g.Title != null) g.Title = T(g.Title, dx, dy);
        }
    }
}
