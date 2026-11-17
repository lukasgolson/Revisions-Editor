using Microsoft.Msagl.Core.Geometry;
using Microsoft.Msagl.Core.Geometry.Curves;
using Microsoft.Msagl.Core.Layout;
using Microsoft.Msagl.Core.Routing;
using Microsoft.Msagl.Layout.Layered;
using Microsoft.Msagl.Miscellaneous;
using MsaglNode = Microsoft.Msagl.Core.Layout.Node;
using MsaglEdge = Microsoft.Msagl.Core.Layout.Edge;
using MsaglLabel = Microsoft.Msagl.Core.Layout.Label;

namespace RevisionsEditor.Core.Diagrams;

/// <summary>
/// Automatic layout for diagrams that say only what connects to what ("layout auto"), such as flowcharts: the
/// layered (Sugiyama) layout of Microsoft's Automatic Graph Layout puts the nodes in layers along the flow,
/// orders them to cross as few arrows as possible, keeps groups together, and routes the arrows at right angles
/// round the nodes, with their labels placed clear of everything else.
/// <para>
/// Groups are framed round their members after a flat layout, so every arrow counts in placing the nodes
/// (MSAGL's own clusters are laid out as one block and lose the arrows that leave them). Each group's top member
/// is given room for the group's title and padding, and the layout is run again, so arrows are routed clear of
/// the titles too.
/// </para>
/// <para>
/// Nodes, texts, captions and the drawing are the same as in the grid layout (<see cref="DiagramLayout"/>), so
/// both kinds of diagram look alike and both are typeset with LaTeX math in the PDF.
/// </para>
/// </summary>
internal static class DiagramAutoLayout
{
    public static DiagramLayoutResult Layout(Diagram diagram)
    {
        double fs = diagram.FontSize;
        string family = diagram.FontFamily;
        bool down = diagram.Direction == "down";
        var result = new DiagramLayoutResult
        {
            FontFamily = family,
            FontSize = fs,
            LineWidth = Math.Max(1, fs * 0.11),
            ArrowLength = fs * 0.55
        };

        foreach (var node in diagram.Nodes) node.EffectiveSpan = 1;
        var nodes = diagram.Nodes.ToList();
        if (nodes.Count == 0)
        {
            result.Width = fs * 10;
            result.Height = fs * 4;
            return result;
        }

        var measured = nodes.ToDictionary(n => n.Id, n => DiagramLayout.Measure(n, 0, 0, family, fs), StringComparer.Ordinal);
        double groupPad = fs * 0.7;
        // Groups: a node belongs to the first group that lists it.
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var groups = diagram.Groups
            .Select(g => (Group: g, Members: g.Members.Where(id => measured.ContainsKey(id) && seen.Add(id)).ToList(),
                Title: g.Title.Length > 0 ? DiagramText.Layout(g.Title, family, fs * 0.9, true) : null))
            .Where(g => g.Members.Count > 0)
            .ToList();
        double Room((DiagramGroup Group, List<string> Members, DiagramTextBox? Title) g) => groupPad + (g.Title != null ? g.Title.Height + fs * 0.3 : 0);

        // First pass: where each group's top member lands. Second: that member has room above it for the title.
        var room = new Dictionary<string, double>(StringComparer.Ordinal);
        var (graph, shapes, edges) = Run(diagram, nodes, measured, room, down, fs);
        if (groups.Count > 0)
        {
            foreach (var g in groups)
            {
                string top = g.Members.OrderBy(id => -shapes[id].BoundingBox.Top).First();
                room[top] = Math.Max(room.GetValueOrDefault(top), Room(g));
            }
            (graph, shapes, edges) = Run(diagram, nodes, measured, room, down, fs);
        }


        // ---- Back into the drawing (MSAGL's y grows upwards) ------------------------------------------
        foreach (var node in nodes)
        {
            var m = measured[node.Id];
            var box = shapes[node.Id].BoundingBox;
            double extra = room.GetValueOrDefault(node.Id);
            double cx = box.Center.X, top = -box.Top + extra, bottom = -box.Bottom - (down ? 0 : extra);
            double left = cx - m.Width / 2;
            double port = down
                ? top + m.Above                                   // body at the top, captions under it
                : (top + bottom) / 2 + (m.Above - m.Below) / 2;   // body in the middle
            DiagramLayout.Place(m, left, port, port + m.Below + fs * 0.45, fs);
            result.Nodes.Add(m.Layout);
        }
        var bodies = result.Nodes.ToDictionary(n => n.Node.Id, StringComparer.Ordinal);

        foreach (var (edge, shape, label) in edges)
        {
            var layout = new DiagramEdgeLayout { Edge = edge };
            var points = PointsOf(shape.Curve).Select(p => new DiagramPoint(p.X, -p.Y)).ToList();
            if (points.Count < 2)
            {
                var a = bodies[edge.From].Body;
                var b = bodies[edge.To].Body;
                points = [new(a.CenterX, a.CenterY), new(b.CenterX, b.CenterY)];
            }
            // The router ends arrows at the node's box (captions included); they end at the shape itself.
            points[0] = OntoBody(points[0], points[1], bodies[edge.From]);
            points[^1] = OntoBody(points[^1], points[^2], bodies[edge.To]);
            layout.Points.AddRange(points);
            if (label != null && shape.Label != null)
            {
                var c = shape.Label.Center;
                layout.Label = new DiagramPlacedText(label, c.X - label.Width / 2, -c.Y + (label.Ascent - label.Descent) / 2);
            }
            result.Edges.Add(layout);
        }

        foreach (var g in groups)
        {
            // Round the members (and their captions), with the title in the room left above the top one.
            var members = g.Members.Select(id => bodies[id].Bounds).Aggregate((a, b) => a.Union(b));
            var box = members.Inflate(groupPad, groupPad);
            box = box with { Y = box.Y - Room(g) + groupPad, Height = box.Height + Room(g) - groupPad };
            if (g.Title != null && g.Title.Width + 2 * groupPad > box.Width)
            {
                box = box with { X = box.CenterX - g.Title.Width / 2 - groupPad, Width = g.Title.Width + 2 * groupPad };
            }
            var layout = new DiagramGroupLayout { Group = g.Group, Box = box };
            if (g.Title != null)
            {
                layout.Title = new DiagramPlacedText(g.Title, box.CenterX - g.Title.Width / 2, box.Y + groupPad * 0.7 + g.Title.Ascent);
            }
            result.Groups.Add(layout);
        }

        DiagramLayout.Frame(result, fs * 0.8);
        return result;
    }

    /// <summary>
    /// Lays the graph out: every node as large as it draws (plus <paramref name="room"/> above it, and below too
    /// when the flow runs left to right so arrows still meet the node's middle), arrows routed at right angles.
    /// </summary>
    private static (GeometryGraph Graph, Dictionary<string, MsaglNode> Shapes, List<(DiagramEdge Edge, MsaglEdge Shape, DiagramTextBox? Label)> Edges) Run(
        Diagram diagram, List<DiagramNode> nodes, Dictionary<string, DiagramLayout.MeasuredNode> measured, Dictionary<string, double> room, bool down, double fs)
    {
        var graph = new GeometryGraph();
        var shapes = new Dictionary<string, MsaglNode>(StringComparer.Ordinal);
        foreach (var node in nodes)
        {
            var m = measured[node.Id];
            double body = m.Above + m.Below, captions = m.CaptionHeight(fs), extra = room.GetValueOrDefault(node.Id);
            double width = Math.Max(m.Width, m.CaptionWidth);
            // Left to right, arrows meet a node at its middle: the box is as tall above the body as the captions
            // are below it, so its middle is the body's.
            double height = down ? body + captions + extra : body + 2 * captions + 2 * extra;
            var shape = new MsaglNode(CurveFactory.CreateRectangle(width, height, new Point()), node.Id);
            graph.Nodes.Add(shape);
            shapes[node.Id] = shape;
        }
        var edges = new List<(DiagramEdge, MsaglEdge, DiagramTextBox?)>();
        foreach (var e in diagram.Edges)
        {
            if (!shapes.TryGetValue(e.From, out var from) || !shapes.TryGetValue(e.To, out var to) || from == to) continue;
            var edge = new MsaglEdge(from, to) { UserData = e, Weight = e.Style == DiagramLineStyle.Solid ? 2 : 1 };
            DiagramTextBox? label = null;
            if (e.Label.Length > 0)
            {
                label = DiagramText.Layout(e.Label, diagram.FontFamily, fs * 0.8, false);
                edge.Label = new MsaglLabel(label.Width + fs * 0.4, label.Height + fs * 0.2, edge);
            }
            graph.Edges.Add(edge);
            edges.Add((e, edge, label));
        }

        var settings = new SugiyamaLayoutSettings
        {
            NodeSeparation = fs * 1.6,
            LayerSeparation = diagram.Gap * 1.3,
            EdgeRoutingSettings = { EdgeRoutingMode = EdgeRoutingMode.Rectilinear, Padding = fs * 0.4, CornerRadius = 0 }
        };
        if (!down)
        {
            // Layers run top to bottom; turned a quarter, the flow runs left to right.
            settings.Transformation = PlaneTransformation.Rotation(Math.PI / 2);
        }
        LayoutHelpers.CalculateLayout(graph, settings, null);
        return (graph, shapes, edges);
    }

    /// <summary>The points of a routed arrow (curved parts, if any, sampled).</summary>
    private static List<Point> PointsOf(ICurve? curve)
    {
        var points = new List<Point>();
        void Add(Point p)
        {
            if (points.Count == 0 || (points[^1] - p).Length > 0.01) points.Add(p);
        }
        void Walk(ICurve c)
        {
            switch (c)
            {
                case Curve composite:
                    foreach (var segment in composite.Segments) Walk(segment);
                    break;
                case LineSegment line:
                    Add(line.Start);
                    Add(line.End);
                    break;
                case Polyline polyline:
                    foreach (var p in polyline) Add(p);
                    break;
                default:
                    for (int i = 0; i <= 8; i++) Add(c[c.ParStart + (c.ParEnd - c.ParStart) * i / 8]);
                    break;
            }
        }
        if (curve != null) Walk(curve);
        return points;
    }

    /// <summary>
    /// Moves an arrow's end from the node's box onto its shape, along the arrow's last part (straight in or out
    /// of the box at right angles, so moving along it keeps the angle).
    /// </summary>
    private static DiagramPoint OntoBody(DiagramPoint end, DiagramPoint toward, DiagramNodeLayout node)
    {
        var body = node.Node.Kind == DiagramNodeKind.Tensor ? node.Front.Union(node.Body) : node.Body;
        bool horizontal = Math.Abs(end.Y - toward.Y) < 0.5;
        if (horizontal && end.Y >= body.Y && end.Y <= body.Bottom)
        {
            return new DiagramPoint(toward.X < end.X ? body.X : body.Right, end.Y);
        }
        if (!horizontal && Math.Abs(end.X - toward.X) < 0.5 && end.X >= body.X && end.X <= body.Right)
        {
            return new DiagramPoint(end.X, toward.Y < end.Y ? body.Y : body.Bottom);
        }
        return end;
    }
}
