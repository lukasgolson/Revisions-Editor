using System.IO;
using RevisionsEditor.Core.Diagrams;
using RevisionsEditor.Core.Formatting;
using RevisionsEditor.Core.Latex;
using RevisionsEditor.Core.Models;
using RevisionsEditor.Core.Scholarly;
using Xunit;

namespace RevisionsEditor.Tests;

public sealed partial class RevisionsEditorTests
{
    /// <summary>Everything a diagram draws as text or a shape, with what it belongs to.</summary>
    private static List<(string Owner, DiagramRect Rect)> DrawnRects(DiagramLayoutResult layout, bool includeBodies)
    {
        var rects = new List<(string, DiagramRect)>();
        foreach (var n in layout.Nodes)
        {
            if (includeBodies) rects.Add(($"{n.Node.Id} body", n.Body));
            foreach (var c in n.Captions) rects.Add(($"{n.Node.Id} caption '{c.Box.Source}'", c.Bounds));
        }
        foreach (var e in layout.Edges.Where(e => e.Label != null))
        {
            rects.Add(($"label '{e.Label!.Box.Source}'", e.Label.Bounds));
        }
        foreach (var g in layout.Groups.Where(g => g.Title != null))
        {
            rects.Add(($"group title '{g.Title!.Box.Source}'", g.Title.Bounds));
        }
        return rects;
    }

    private static void AssertNoOverlaps(string name, DiagramLayoutResult layout)
    {
        var rects = DrawnRects(layout, includeBodies: true);
        for (int i = 0; i < rects.Count; i++)
        {
            for (int j = i + 1; j < rects.Count; j++)
            {
                Assert.False(rects[i].Rect.Intersects(rects[j].Rect, tolerance: 1),
                    $"{name}: {rects[i].Owner} {rects[i].Rect} overlaps {rects[j].Owner} {rects[j].Rect}");
            }
        }
        // A block's own text stays inside it.
        foreach (var n in layout.Nodes.Where(n => n.Node.Kind == DiagramNodeKind.Block))
        {
            foreach (var t in n.Texts)
            {
                var b = t.Bounds;
                Assert.True(b.X >= n.Body.X + 2 && b.Right <= n.Body.Right - 2 && b.Y >= n.Body.Y && b.Bottom <= n.Body.Bottom,
                    $"{name}: the text '{t.Box.Source}' {b} spills out of block {n.Node.Id} {n.Body}");
            }
        }
        // Group titles sit outside every node, and frames do not cut through nodes outside the group.
        foreach (var g in layout.Groups)
        {
            foreach (var n in layout.Nodes.Where(n => !g.Group.Members.Contains(n.Node.Id)))
            {
                Assert.False(g.Box.Intersects(n.Body, tolerance: 1), $"{name}: group {g.Group.Id} frame overlaps {n.Node.Id}");
            }
        }
    }

    [Fact]
    public void DiagramTemplates_ParseCleanly_AndLayOutWithoutOverlaps()
    {
        string? outDir = Environment.GetEnvironmentVariable("DIAGRAM_OUT");
        foreach (var template in DiagramTemplates.All)
        {
            var diagram = DiagramSource.Parse(template.Source, out var errors);
            Assert.True(errors.Count == 0, $"{template.Name}: {string.Join("; ", errors.Select(e => $"line {e.Line}: {e.Message}"))}");
            var layout = DiagramLayout.Layout(diagram);
            AssertNoOverlaps(template.Name, layout);

            string svg = DiagramRenderer.ToSvg(layout);
            string path = Path.Combine(outDir ?? Path.GetTempPath(), $"diagram-{template.Name.Replace(' ', '-').ToLowerInvariant()}.svg");
            File.WriteAllText(path, svg);
            Assert.NotNull(FigureImages.NaturalSizePoints(path)); // the SVG reads back
            if (outDir != null)
            {
                File.WriteAllBytes(Path.ChangeExtension(path, ".png"), FigureImages.RenderPng(path, dpi: 110)!);
                File.WriteAllText(Path.ChangeExtension(path, ".tikz"), DiagramRenderer.ToTikz(layout));
            }
            else
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public void DiagramMath_IsLaidOutWithScriptsAndFractions_AndSourceGoesToTikz()
    {
        var plain = DiagramText.Layout("PCA", "Times New Roman", 16, bold: true);
        var math = DiagramText.Layout(@"$W \in \mathbb{R}^{1024 \times 2880}$", "Times New Roman", 16, bold: false);
        // W italic, ℝ as its own character, the exponent smaller and raised.
        Assert.Contains(math.Runs, r => r.Text == "W" && r.Italic);
        Assert.Contains(math.Runs, r => r.Text.Contains('ℝ'));
        var exponent = math.Runs.First(r => r.Text.StartsWith("1024", StringComparison.Ordinal));
        Assert.True(exponent.Size < 16 && exponent.Baseline < 0);
        Assert.True(plain.Runs.All(r => r.Bold));

        var fraction = DiagramText.Layout(@"$\frac{H}{4}$", "Times New Roman", 16, bold: false);
        Assert.Single(fraction.Rules);
        Assert.True(fraction.Runs.First(r => r.Text == "H").Baseline < fraction.Runs.First(r => r.Text == "4").Baseline);

        var diagram = DiagramSource.Parse("block pca \"PCA Matrix\" \"$W \\in \\mathbb{R}^{1024}$\"\nblock lin \"Linear & more\" \"1024 × 1024\"\npca -> lin", out var errors);
        Assert.Empty(errors);
        string tikz = DiagramRenderer.ToTikz(diagram);
        Assert.Contains(@"$W \in \mathbb{R}^{1024}$", tikz);
        Assert.Contains(@"Linear \& more", tikz);
        Assert.Contains("1024 $×$ 1024", tikz);
        Assert.Contains(@"\begin{tikzpicture}", tikz);
    }

    [Fact]
    public void DiagramSource_RoundTrips_AndReportsMistakesByLine()
    {
        foreach (var template in DiagramTemplates.All)
        {
            var once = DiagramSource.Write(DiagramSource.Parse(template.Source, out _));
            var twice = DiagramSource.Write(DiagramSource.Parse(once, out var errors));
            Assert.Empty(errors);
            Assert.Equal(once, twice);
        }

        DiagramSource.Parse("block a \"A\"\nblok b \"B\"\nblock c \"C\" below=nowhere\na -> missing", out var problems);
        Assert.Contains(problems, e => e.Line == 2 && e.Message.Contains("Unknown statement"));
        Assert.Contains(problems, e => e.Line == 3 && e.Message.Contains("nowhere"));
        Assert.Contains(problems, e => e.Line == 4 && e.Message.Contains("missing"));
    }

    [Fact]
    public void DiagramEditing_InsertsIntoTheFlow_RemovesAndJoins_AndMovesAlongTheRow()
    {
        var diagram = DiagramSource.Parse("# pipeline\nblock a \"A\"\nblock b \"B\"\nblock c \"C\"\na -> b -> c", out _);

        var added = DiagramSource.AddNode(diagram, DiagramNodeKind.Block, "a");
        Assert.Equal(["a", added.Id, "b", "c"], diagram.Edges.SelectMany(e => new[] { e.From, e.To }).Distinct());
        Assert.Equal(1, DiagramLayout.AssignGrid(diagram)[added.Id].Column);

        DiagramSource.RemoveNode(diagram, "b");
        Assert.Equal([("a", added.Id), (added.Id, "c")], diagram.Edges.Select(e => (e.From, e.To)));

        Assert.True(DiagramSource.MoveNode(diagram, "c", -1));
        var grid = DiagramLayout.AssignGrid(diagram);
        Assert.True(grid["c"].Column < grid[added.Id].Column);

        // The comment is kept through the edits.
        Assert.StartsWith("# pipeline\n", DiagramSource.Write(diagram));
    }

    [Fact]
    public async Task DiagramFigure_IsDrawnAsTikzInLatex_AndTypesets()
    {
        var (ws, doc) = ScholarlyWorkspace();
        try
        {
            string source = DiagramTemplates.All.First(t => t.Name == "Feature extraction network").Source;
            var (diagramPath, svgPath, _) = DiagramFiles.Save(ws, null, "fig:extractor", source);
            Assert.Equal("figures/extractor.diagram", diagramPath);
            Assert.True(File.Exists(Path.Combine(ws, "figures", "extractor.svg")));
            doc.Blocks.Add(new DocumentBlock
            {
                ParagraphId = "p-090",
                Role = BlockStyleRole.Figure,
                Text = ScholarlyBlocks.FormatFigure("Feature extraction network.", svgPath, "fig:extractor", new Dictionary<string, string> { ["source"] = diagramPath })
            });
            var context = ContextFor(doc, ws, "rse");
            var profile = FormattingRuleProfile.GetBuiltInProfiles()[0];
            var latex = LatexExportService.Build(doc, context, profile, new LatexExportOptions { WorkspacePath = ws });
            AssertWellFormedLatex(latex.Source);
            Assert.Contains(@"\usepackage{tikz}", latex.Source);
            Assert.Contains(@"\input{figures/extractor.tikz}", latex.Source);
            Assert.Contains(latex.Files, f => f.RelativePath == "figures/extractor.tikz");
            Assert.DoesNotContain(latex.Warnings, w => w.Contains("extractor", StringComparison.Ordinal));

            // Typeset it when a LaTeX engine is at hand: TikZ and the labels' math must compile.
            if (LatexCompiler.FindEngine() is { } engine)
            {
                string folder = Path.Combine(ws, "latex-out");
                string tex = LatexExportService.WriteTo(latex, folder);
                if (Environment.GetEnvironmentVariable("DIAGRAM_OUT") is { } dump) File.Copy(tex, Path.Combine(dump, "latex-diagram.tex"), overwrite: true);
                var compiled = await LatexCompiler.CompileAsync(tex, engine, TimeSpan.FromMinutes(8));
                Assert.True(compiled.Success, string.Join("\n", compiled.Errors) + "\n" + compiled.Log[^Math.Min(compiled.Log.Length, 3000)..]);
                if (Environment.GetEnvironmentVariable("DIAGRAM_OUT") is { } keep && compiled.PdfPath != null)
                {
                    File.Copy(compiled.PdfPath, Path.Combine(keep, "latex-diagram.pdf"), overwrite: true);
                }
            }
            else
            {
                Console.WriteLine("    (no LaTeX engine found; the TikZ output was checked but not typeset)");
            }
        }
        finally
        {
            Directory.Delete(ws, recursive: true);
        }
    }
}
