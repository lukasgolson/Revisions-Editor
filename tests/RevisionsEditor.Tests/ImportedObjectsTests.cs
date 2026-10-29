using System.IO;
using System.Net.Http;
using RevisionsEditor.Core.Bridge;
using RevisionsEditor.Core.Diffing;
using RevisionsEditor.Core.Docx;
using RevisionsEditor.Core.Git;
using RevisionsEditor.Core.Models;
using RevisionsEditor.Core.Scholarly;
using Xunit;

namespace RevisionsEditor.Tests;

public sealed partial class RevisionsEditorTests
{
    [Fact]
    public void ImportedCaptions_AreRecognised_WithoutTheirNumbers()
    {
        Assert.True(ImportedObjects.TryFigureCaption("**Figure 1.** Study sites in the park.", out var caption));
        Assert.Equal("Study sites in the park.", caption);
        Assert.True(ImportedObjects.TryFigureCaption("Fig. 12: Heights", out caption));
        Assert.Equal("Heights", caption);
        Assert.True(ImportedObjects.TryTableCaption("Table 3 – Accuracy by site", out caption));
        Assert.Equal("Accuracy by site", caption);
        Assert.False(ImportedObjects.TryFigureCaption("Figures were drawn in R.", out _));
        Assert.False(ImportedObjects.TryTableCaption("Tables are in the appendix.", out _));
    }

    [Fact]
    public async Task GoogleDocsPull_ReadsImagesAsFigures_AndTablesAsTableBlocks_AndPullsAgainWithoutChanges()
    {
        int port = 18000 + Random.Shared.Next(6000, 8000);
        string ws = Path.Combine(Path.GetTempPath(), $"RevEditorTest_PullObjects_{Guid.NewGuid():N}");
        var repo = new GitRepositoryService(ws);
        repo.EnsureInitialized();
        try
        {
            using var bridge = new LocalBridgeServer(repo, port);
            if (!bridge.TryStart())
            {
                return; // Port unavailable on this machine; nothing to verify.
            }
            var log = new List<string>();
            bridge.BridgeActivityLogged += log.Add;
            string image = Convert.ToBase64String(TinyPng(400, 200));

            using var http = new HttpClient();
            async Task PullAsync(string cell)
            {
                var request = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{port}/api/google-docs/pull")
                {
                    // As the extension reads the Google Doc (see tabToBlocks in docs-apply.js).
                    Content = new StringContent(System.Text.Json.JsonSerializer.Serialize(new
                    {
                        googleDocId = "DOC1",
                        title = "Paper",
                        blocks = new object[]
                        {
                            new { role = "Title", text = "Paper" },
                            new { role = "BodyParagraph", text = "Heights were measured at two sites." },
                            new { role = "Figure", text = "", imageBase64 = image, mimeType = "image/png" },
                            new { role = "BodyParagraph", text = "**Figure 1.** Study sites in the park." },
                            new { role = "BodyParagraph", text = "Table 1: Accuracy by site" },
                            new { role = "Table", text = "", rows = new[] { new[] { "Site", "RMSE (m)" }, new[] { "Grimston", cell } } },
                            new { role = "BodyParagraph", text = "Errors were lower in leaf-off conditions." }
                        }
                    }), System.Text.Encoding.UTF8, "application/json")
                };
                request.Headers.Add("Origin", "chrome-extension://abcdefghijklmnop");
                var response = await http.SendAsync(request);
                Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
            }

            await PullAsync("0.42");
            var doc = repo.LoadCurrentWorkingDocument();
            Assert.Equal(
                [BlockStyleRole.Title, BlockStyleRole.BodyParagraph, BlockStyleRole.Figure, BlockStyleRole.Table, BlockStyleRole.BodyParagraph],
                doc.Blocks.Select(b => b.Role));
            var figure = ScholarlyBlocks.ParseFigure(doc.Blocks[2].Text)!;
            Assert.Equal("Study sites in the park.", figure.Caption);
            Assert.Equal("fig:study-sites-in-the-park", figure.Label);
            Assert.StartsWith("figures/imported-", figure.ImagePath);
            Assert.Equal(TinyPng(400, 200), File.ReadAllBytes(Path.Combine(ws, figure.ImagePath)));
            var table = ScholarlyBlocks.ParseTable(doc.Blocks[3].Text)!;
            Assert.Equal("Accuracy by site", table.Caption);
            Assert.Equal("tbl:accuracy-by-site", table.Label);
            Assert.Equal("tables/accuracy-by-site.csv", table.CsvPath);
            Assert.Equal("Site,RMSE (m)\nGrimston,0.42\n", File.ReadAllText(Path.Combine(ws, table.CsvPath!)));
            Assert.Contains(log, l => l.Contains("1 figure and 1 table"));

            // Pulled again unchanged: the same blocks and files; nothing to send back.
            string figureText = doc.Blocks[2].Text, tableText = doc.Blocks[3].Text;
            await PullAsync("0.42");
            doc = repo.LoadCurrentWorkingDocument();
            Assert.Equal(figureText, doc.Blocks[2].Text);
            Assert.Equal(tableText, doc.Blocks[3].Text);
            Assert.Single(Directory.GetFiles(Path.Combine(ws, "figures")));
            bridge.RefreshPendingPatches();
            Assert.Empty(bridge.GetPendingPatches());

            // A cell edited in Google Docs updates the table's file; the table keeps its label.
            await PullAsync("0.40");
            doc = repo.LoadCurrentWorkingDocument();
            Assert.Equal(tableText, doc.Blocks[3].Text);
            Assert.Equal("Site,RMSE (m)\nGrimston,0.40\n", File.ReadAllText(Path.Combine(ws, table.CsvPath!)));

            // A figure added in the app is not typed into the Google Doc as text.
            var here = repo.LoadCurrentWorkingDocument();
            here.Blocks.Insert(4, new DocumentBlock { ParagraphId = "p-100", Role = BlockStyleRole.Figure, Text = "![A new map.](figures/map.png){#fig:map}" });
            here.Blocks[1].Text = "Heights were measured at two urban sites.";
            repo.CommitSnapshot(here, "Edit in app");
            bridge.RefreshPendingPatches();
            var patch = Assert.Single(bridge.GetPendingPatches());
            Assert.Equal("Heights were measured at two urban sites.", patch.AfterText);
        }
        finally
        {
            TestWorkspaceBuilder.DeleteDirectory(ws);
        }
    }

    [Fact]
    public void WordImport_ReadsPicturesAsFigures_AndTablesAsTableBlocks_ReusingTheManuscriptsOwn()
    {
        var (ws, doc) = ScholarlyWorkspace();
        try
        {
            var context = ContextFor(doc, ws);
            // A copy without the export's markers, as Google Docs or another word processor saves it.
            string path = Path.Combine(ws, "plain.docx");
            DocxExportService.Export(doc, path, new DocxExportOptions { Scholarly = context, WorkspacePath = ws, RoundTripMarkers = false });

            // Without the option, as before: no figure or table blocks.
            Assert.DoesNotContain(DocxIngestionService.IngestDocx(path).Blocks, b => b.Role is BlockStyleRole.Figure or BlockStyleRole.Table);

            var objects = new ImportedObjects(doc, ws);
            var imported = DocxIngestionService.IngestDocx(path, null, null, null, objects);
            var figure = Assert.Single(imported.Blocks, b => b.Role == BlockStyleRole.Figure);
            var table = Assert.Single(imported.Blocks, b => b.Role == BlockStyleRole.Table);
            // The same picture and cells as the manuscript's: its own file, label and width are kept.
            var original = ScholarlyBlocks.ParseFigure(doc.Blocks[4].Text)!;
            var read = ScholarlyBlocks.ParseFigure(figure.Text)!;
            Assert.Equal((original.ImagePath, original.Label, original.Attributes["width"]), (read.ImagePath, read.Label, read.Attributes["width"]));
            Assert.StartsWith("Study sites", read.Caption); // from the "Fig. 1." paragraph, without the number
            var readTable = ScholarlyBlocks.ParseTable(table.Text)!;
            Assert.Equal(("tables/accuracy.csv", "tbl:accuracy"), (readTable.CsvPath, readTable.Label));
            Assert.Empty(objects.Files); // nothing new to write
            Assert.DoesNotContain(imported.Blocks, b => b.Text is "Grimston" or "0.42"); // cells are not paragraphs
            // The figure and table sit where they were.
            Assert.True(imported.Blocks.IndexOf(figure) < imported.Blocks.IndexOf(table));
        }
        finally
        {
            Directory.Delete(ws, recursive: true);
        }
    }
}
