using System.IO;
using RevisionsEditor.Core.Diagrams;
using Xunit;

namespace RevisionsEditor.Tests;

public sealed partial class RevisionsEditorTests
{
    private const string SampleMermaid = """
        %% A workflow drawn in Mermaid
        flowchart LR
            A[Drone video] --> B(Frame extraction)
            B --> C{Enough overlap?}
            C -->|yes| D[Feature matching]
            C -- no --> B
            subgraph rec [Reconstruction]
              D --> E[(Point cloud)] & F[Mesh]
            end
            E -.-> G((QA));
            classDef main fill:#f9f
            class A main
        """;

    [Fact]
    public void MermaidFlowchart_ConvertsIntoAnEditableDiagram_LaidOutAutomatically()
    {
        Assert.True(MermaidFlowchart.LooksLikeFlowchart(SampleMermaid));
        Assert.False(MermaidFlowchart.LooksLikeFlowchart("gantt\n  title Plan"));

        var (source, notes) = MermaidFlowchart.Convert(SampleMermaid);
        var diagram = DiagramSource.Parse(source, out var errors);
        Assert.True(errors.Count == 0, string.Join("; ", errors.Select(e => $"line {e.Line}: {e.Message}")) + "\n" + source);
        Assert.True(diagram.AutoLayout);
        Assert.Equal("right", diagram.Direction);

        Assert.Equal(["A", "B", "C", "D", "E", "F", "G"], diagram.Nodes.Select(n => n.Id));
        Assert.Equal("Enough overlap?", diagram.FindNode("C")!.Title);
        Assert.Equal("Point cloud", diagram.FindNode("E")!.Title);
        Assert.Equal(DiagramNodeKind.Op, diagram.FindNode("G")!.Kind);

        var edges = diagram.Edges.Select(e => (e.From, e.To, e.Label, e.Style)).ToList();
        Assert.Contains(("C", "D", "yes", DiagramLineStyle.Solid), edges);
        Assert.Contains(("C", "B", "no", DiagramLineStyle.Solid), edges); // the loop back
        Assert.Contains(("D", "E", "", DiagramLineStyle.Solid), edges);
        Assert.Contains(("D", "F", "", DiagramLineStyle.Solid), edges);  // D --> E & F
        Assert.Contains(("E", "G", "", DiagramLineStyle.Dashed), edges); // dotted link

        var group = Assert.Single(diagram.Groups);
        Assert.Equal("Reconstruction", group.Title);
        Assert.Equal(["D", "E", "F"], group.Members);
        Assert.Contains(notes, n => n.Contains("Styling", StringComparison.Ordinal));

        // It lays out like any diagram: nothing overlaps, arrows cross no other node.
        AssertNoOverlaps("converted Mermaid flowchart", DiagramLayout.Layout(diagram));
    }

    /// <summary>Runs the dispatcher until <paramref name="done"/> holds (WebView2 answers on this thread).</summary>
    private static bool PumpUntil(Func<bool> done, TimeSpan timeout)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (!done() && clock.Elapsed < timeout)
        {
            var frame = new System.Windows.Threading.DispatcherFrame();
            var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
            timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
            timer.Start();
            System.Windows.Threading.Dispatcher.PushFrame(frame);
        }
        return done();
    }

    [Fact]
    public void MermaidDialog_DrawsWithMermaid_AndSavesAPdfTheSizeOfTheDrawing()
        => RunOnStaThread(() =>
        {
            if (!RevisionsEditor.App.Rendering.MermaidView.IsAvailable)
            {
                Console.WriteLine("    (WebView2 or the bundled Mermaid is not available here; skipped)");
                return;
            }
            string gantt = RevisionsEditor.Core.Diagrams.MermaidFiles.Templates.First(t => t.Name == "Gantt chart").Source;
            var dialog = new RevisionsEditor.App.Dialogs.MermaidDialog(null, gantt, new HashSet<string>()) { Left = -20000, Top = -20000, ShowInTaskbar = false, ShowActivated = false };
            dialog.Show();
            string pdf = Path.Combine(Path.GetTempPath(), $"mermaid-test-{Guid.NewGuid():N}.pdf");
            try
            {
                Assert.True(PumpUntil(() => dialog.DrawnOk || dialog.ErrorText.Length > 0, TimeSpan.FromSeconds(60)), "Mermaid did not draw in time.");
                Assert.True(dialog.DrawnOk, dialog.ErrorText);

                bool saved = false;
                var task = dialog.SavePdfForTestAsync(pdf);
                Assert.True(PumpUntil(() => task.IsCompleted, TimeSpan.FromSeconds(60)));
                saved = task.Result;
                Assert.True(saved && File.Exists(pdf));
                var bytes = File.ReadAllBytes(pdf);
                Assert.StartsWith("%PDF", System.Text.Encoding.ASCII.GetString(bytes, 0, 4));
                // One page, the size of the drawing (a Gantt chart is wide and short), not a sheet of paper.
                var size = PDFtoImage.Conversion.GetPageSize(bytes, page: 0);
                Assert.True(size.Width > size.Height * 1.5, $"page {size.Width} x {size.Height} pt");
                Assert.True(size.Width < 2000 && size.Height > 50);
                if (Environment.GetEnvironmentVariable("DIAGRAM_OUT") is { } keep)
                {
                    File.Copy(pdf, Path.Combine(keep, "mermaid-gantt.pdf"), overwrite: true);
                    File.WriteAllBytes(Path.Combine(keep, "mermaid-gantt.png"), RevisionsEditor.Core.Scholarly.FigureImages.RenderPng(pdf, dpi: 110)!);
                }

                // A mistake is reported with Mermaid's own message.
                dialog.SetSource("flowchart LR\n  A --> ");
                Assert.True(PumpUntil(() => !dialog.DrawnOk && dialog.ErrorText.Length > 0, TimeSpan.FromSeconds(30)));
                Assert.StartsWith("Mermaid:", dialog.ErrorText);
            }
            finally
            {
                dialog.Close();
                if (File.Exists(pdf)) File.Delete(pdf);
            }
        });
}
