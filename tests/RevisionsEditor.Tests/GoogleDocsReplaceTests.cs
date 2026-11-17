using System.IO;
using System.Net.Http;
using RevisionsEditor.Core.Diffing;
using RevisionsEditor.Core.Merging;
using RevisionsEditor.Core.Models;
using RevisionsEditor.Core.Scholarly;
using Xunit;

namespace RevisionsEditor.Tests;

public sealed partial class RevisionsEditorTests
{
    private static DocumentBlock B(string id, BlockStyleRole role, string text) => new() { ParagraphId = id, Role = role, Text = text };

    [Fact]
    public void GoogleDocsReplace_TakesTheTextExactly_AndKeepsTheManuscriptsFigures()
    {
        var current = new ManuscriptDocument
        {
            Title = "Paper",
            Blocks =
            [
                B("p-001", BlockStyleRole.Title, "Paper"),
                B("p-002", BlockStyleRole.BodyParagraph, "Intro as written {++in the app++}."),
                B("p-003", BlockStyleRole.Figure, ScholarlyBlocks.FormatFigure("Network.", "figures/net.svg", "fig:net", new Dictionary<string, string> { ["source"] = "figures/net.diagram", ["width"] = "90%" })),
                B("p-004", BlockStyleRole.BodyParagraph, "Methods paragraph."),
                B("p-005", BlockStyleRole.Equation, ScholarlyBlocks.FormatEquation(@"y = mx + b", "eq:line")),
                B("p-006", BlockStyleRole.Figure, ScholarlyBlocks.FormatFigure("Site map.", "figures/map.pdf", "fig:map")),
                B("p-007", BlockStyleRole.Table, ScholarlyBlocks.FormatTable("Accuracy.", "tbl:acc", "tables/acc.csv")),
                B("p-008", BlockStyleRole.BodyParagraph, "Only in the app.")
            ],
            Hunks = [new RevisionHunk { ParagraphId = "p-002", HunkType = RevisionHunkType.Insertion, ProposedText = "in the app" }]
        };
        // The Google Doc: the network figure as a picture with a new caption, no map figure, no equation, the table
        // with new cells and caption, an image only there, and different text.
        var pulled = new ManuscriptDocument
        {
            Title = "Paper",
            GoogleDocId = "DOC1",
            Blocks =
            [
                B("p-001", BlockStyleRole.Title, "Paper"),
                B("p-002", BlockStyleRole.BodyParagraph, "Intro as edited in Google Docs."),
                B("p-003", BlockStyleRole.Figure, ScholarlyBlocks.FormatFigure("The feature network.", "figures/imported-aaaa.png", "fig:the-feature-network")),
                B("p-004", BlockStyleRole.BodyParagraph, "Methods paragraph."),
                B("p-005", BlockStyleRole.Table, ScholarlyBlocks.FormatTable("Accuracy by site.", "tbl:accuracy-by-site", "tables/accuracy-by-site.csv")),
                B("p-006", BlockStyleRole.BodyParagraph, "A paragraph added in Google Docs."),
                B("p-007", BlockStyleRole.Figure, ScholarlyBlocks.FormatFigure("A photo.", "figures/imported-bbbb.png", "fig:a-photo"))
            ]
        };

        var result = GoogleDocsReplace.Replace(current, pulled);
        var blocks = result.Document.Blocks;

        // The text is the Google Doc's, word for word; pending changes are gone.
        Assert.Equal(
            ["Paper", "Intro as edited in Google Docs.", "Methods paragraph.", "A paragraph added in Google Docs."],
            blocks.Where(b => b.Role is not (BlockStyleRole.Figure or BlockStyleRole.Table or BlockStyleRole.Equation)).Select(b => b.Text));
        Assert.Empty(result.Document.Hunks);
        Assert.DoesNotContain(blocks, b => b.Text.Contains("Only in the app", StringComparison.Ordinal));

        // The network figure is the manuscript's (diagram, label, width), with the caption from the Google Doc.
        var net = ScholarlyBlocks.ParseFigure(blocks.Single(b => b.Text.Contains("net.svg", StringComparison.Ordinal)).Text)!;
        Assert.Equal("The feature network.", net.Caption);
        Assert.Equal("fig:net", net.Label);
        Assert.Equal("figures/net.diagram", net.Attributes["source"]);
        Assert.Equal("90%", net.Attributes["width"]);
        Assert.DoesNotContain(blocks, b => b.Text.Contains("imported-aaaa", StringComparison.Ordinal));

        // The map figure and the equation, missing from the Google Doc, stay after "Methods paragraph."
        int methods = blocks.FindIndex(b => b.Text == "Methods paragraph.");
        Assert.Equal(BlockStyleRole.Equation, blocks[methods + 1].Role);
        Assert.Contains("figures/map.pdf", blocks[methods + 2].Text);

        // The table keeps its label and file; its cells come from the Google Doc's file.
        var table = ScholarlyBlocks.ParseTable(blocks.Single(b => b.Role == BlockStyleRole.Table).Text)!;
        Assert.Equal("tbl:acc", table.Label);
        Assert.Equal("tables/acc.csv", table.CsvPath);
        Assert.Equal("Accuracy by site.", table.Caption);
        Assert.Contains(("tables/accuracy-by-site.csv", "tables/acc.csv"), result.TableFileMoves);

        // The photo only in the Google Doc is added.
        Assert.Contains(blocks, b => b.Text.Contains("imported-bbbb", StringComparison.Ordinal));
        Assert.Equal((1, 2, 1), (result.FiguresMatched, result.ObjectsKept, result.FiguresAdded));

        // Paragraphs keep their ids where they are the same paragraph; ids stay unique.
        Assert.Equal("p-004", blocks.Single(b => b.Text == "Methods paragraph.").ParagraphId);
        Assert.Equal(blocks.Count, blocks.Select(b => b.ParagraphId).Distinct().Count());

        // An image whose caption was lost in Google Docs still stands for the figure in its place.
        var uncaptioned = new ManuscriptDocument
        {
            Blocks =
            [
                B("p-1", BlockStyleRole.BodyParagraph, "Before."),
                B("p-2", BlockStyleRole.Figure, ScholarlyBlocks.FormatFigure("", "figures/imported-cccc.png", "fig:x")),
                B("p-3", BlockStyleRole.BodyParagraph, "After.")
            ]
        };
        var kept = GoogleDocsReplace.Replace(current, uncaptioned);
        var network = ScholarlyBlocks.ParseFigure(kept.Document.Blocks.First(b => b.Role == BlockStyleRole.Figure).Text)!;
        Assert.Equal("figures/net.svg", network.ImagePath);
        Assert.Equal("Network.", network.Caption); // no caption there: the manuscript's is kept
    }

    [Fact]
    public Task GoogleDocsReplace_ThroughTheBridge_CommitsTheDocWithTheFiguresKept_AndLeavesNothingToApply() => WithBridgeAsync("Replace", async h =>
    {
        SeedDoc(h.Docs);
        await h.PullAsync();

        // A diagram figure and an equation exist only here (they are never sent to Google Docs), and the app has
        // an edit the Google Doc does not; the Google Doc was edited too.
        h.EditHere(doc =>
        {
            doc.Blocks.Insert(3, B("p-fig", BlockStyleRole.Figure, ScholarlyBlocks.FormatFigure("Network.", "figures/net.svg", "fig:net", new Dictionary<string, string> { ["source"] = "figures/net.diagram" })));
            doc.Blocks.Add(B("p-eq", BlockStyleRole.Equation, ScholarlyBlocks.FormatEquation("a = b", "eq:ab")));
            doc.Blocks[2].Text = "Alpha beta gamma delta, edited only in the app.";
        });
        h.Docs.Set("The second paragraph is here.", "The second paragraph, rewritten in Google Docs.");

        var request = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{h.Port}/api/google-docs/pull")
        {
            Content = new StringContent(System.Text.Json.JsonSerializer.Serialize(new
            {
                googleDocId = "DOC1",
                title = "Paper",
                mode = "replace",
                blocks = h.Docs.Paragraphs.Select(p => new { role = p.Role, text = p.Text }).ToArray()
            }), System.Text.Encoding.UTF8, "application/json")
        };
        request.Headers.Add("Origin", "chrome-extension://abcdefghijklmnop");
        using var http = new HttpClient();
        var response = await http.SendAsync(request);
        string body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, body);
        Assert.Contains("\"mode\": \"replace\"", body);

        var manuscript = h.Manuscript;
        var text = manuscript.Blocks.Where(b => b.Role is not (BlockStyleRole.Figure or BlockStyleRole.Equation)).Select(b => DiffEngineService.GoogleDocsMatchKey(DiffEngineService.ToGoogleDocsText(b.Text)));
        Assert.Equal(h.Docs.Plain.Select(DiffEngineService.GoogleDocsMatchKey), text);
        Assert.Contains(manuscript.Blocks, b => b.Text.Contains("figures/net.svg", StringComparison.Ordinal));
        Assert.Contains(manuscript.Blocks, b => b.Role == BlockStyleRole.Equation);
        Assert.Empty(h.Pending()); // nothing for Google Docs to apply: the text already matches

        // The replaced version is in the history.
        Assert.Contains(h.Repo.GetCommitGraph().Select(c => c.Message), m => m.StartsWith("Replace the manuscript with Google Docs", StringComparison.Ordinal));
    });
}
