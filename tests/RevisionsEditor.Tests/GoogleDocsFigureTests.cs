using System.IO;
using System.Net.Http;
using RevisionsEditor.Core.Models;
using RevisionsEditor.Core.Scholarly;
using Xunit;

namespace RevisionsEditor.Tests;

public sealed partial class RevisionsEditorTests
{
    /// <summary>A small PNG of one colour (different colours make different files).</summary>
    private static byte[] Png(byte shade)
    {
        using var bitmap = new SkiaSharp.SKBitmap(40, 30);
        bitmap.Erase(new SkiaSharp.SKColor(shade, 120, 60));
        using var data = bitmap.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    [Fact]
    public Task GoogleDocsFigures_ChangedAndNewFigures_BecomeImagePatches_AndTheImagesAreServed() => WithBridgeAsync("Figures", async h =>
    {
        object Pull(byte[] image) => new
        {
            googleDocId = "DOC1",
            title = "Paper",
            blocks = new object[]
            {
                new { role = "Title", text = "Paper" },
                new { role = "BodyParagraph", text = "The study site is shown below." },
                new { role = "Figure", text = "", imageBase64 = Convert.ToBase64String(image), mimeType = "image/png" },
                new { role = "BodyParagraph", text = "Figure 1. Site map." },
                new { role = "BodyParagraph", text = "Results follow." }
            }
        };
        using var http = new HttpClient();
        async Task<string> Post(string path, object body)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{h.Port}{path}")
            {
                Content = new StringContent(System.Text.Json.JsonSerializer.Serialize(body), System.Text.Encoding.UTF8, "application/json")
            };
            request.Headers.Add("Origin", "chrome-extension://abcdefghijklmnop");
            var response = await http.SendAsync(request);
            string text = await response.Content.ReadAsStringAsync();
            Assert.True(response.IsSuccessStatusCode, text);
            return text;
        }

        await Post("/api/google-docs/pull", Pull(Png(10)));
        var figureBlock = h.Manuscript.Blocks.Single(b => b.Role == BlockStyleRole.Figure);
        var figure = ScholarlyBlocks.ParseFigure(figureBlock.Text)!;
        Assert.Equal("Site map.", figure.Caption);
        Assert.False(figure.Attributes.ContainsKey("shown")); // fingerprints stay in the snapshot
        Assert.Empty(h.Pending());

        // Pulling again unchanged leaves the figure alone (the snapshot's fingerprints do not look like an edit).
        await Post("/api/google-docs/pull", Pull(Png(10)));
        Assert.Equal(figureBlock.Text, h.Manuscript.Blocks.Single(b => b.Role == BlockStyleRole.Figure).Text);
        Assert.Empty(h.Manuscript.Hunks);

        // The figure's file is saved again (a plot redrawn): its image in Google Docs is out of date.
        File.WriteAllBytes(Path.Combine(h.Repo.WorkspacePath, figure.ImagePath.Replace('/', Path.DirectorySeparatorChar)), Png(200));
        var replace = Assert.Single(h.Pending());
        Assert.Equal(GoogleDocPatchKind.ReplaceImage, replace.Kind);
        Assert.Equal(0, replace.ImageOrdinal);
        Assert.Equal(figure.ImagePath, replace.ImagePath);

        // The bridge serves that image (and nothing else of the workspace).
        var served = await http.GetAsync($"http://127.0.0.1:{h.Port}/api/google-docs/figure?path={Uri.EscapeDataString(figure.ImagePath)}");
        Assert.Equal("image/png", served.Content.Headers.ContentType?.MediaType);
        Assert.Equal(Png(200), await served.Content.ReadAsByteArrayAsync());
        var refused = await http.GetAsync($"http://127.0.0.1:{h.Port}/api/google-docs/figure?path={Uri.EscapeDataString(".revisions/author-key.json")}");
        Assert.Equal(System.Net.HttpStatusCode.NotFound, refused.StatusCode);

        // A new figure (here a diagram) is inserted after the paragraph it follows, with its caption.
        string diagramText = RevisionsEditor.Core.Diagrams.DiagramTemplates.All[0].Source;
        var (source, svg, _) = RevisionsEditor.Core.Diagrams.DiagramFiles.Save(h.Repo.WorkspacePath, null, "fig:pipeline", diagramText);
        h.EditHere(doc => doc.Blocks.Add(new DocumentBlock
        {
            ParagraphId = "p-new-fig",
            Role = BlockStyleRole.Figure,
            Text = ScholarlyBlocks.FormatFigure("The *pipeline*.", svg, "fig:pipeline", new Dictionary<string, string> { ["source"] = source })
        }));
        var insert = Assert.Single(h.Pending(), p => p.Kind == GoogleDocPatchKind.InsertImage);
        Assert.Equal("Results follow.", insert.AnchorText);
        Assert.Equal("Figure 2. The pipeline.", insert.AfterText);
        Assert.True(insert.ImageWidthPt is > 72 and <= 468);
        var drawn = await http.GetAsync($"http://127.0.0.1:{h.Port}/api/google-docs/figure?path={Uri.EscapeDataString(svg)}");
        Assert.Equal("image/png", drawn.Content.Headers.ContentType?.MediaType); // the SVG, drawn

        // Once Google Docs shows both images (its new content sent as the snapshot), nothing is left to send.
        await Post("/api/google-docs/baseline", new
        {
            googleDocId = "DOC1",
            title = "Paper",
            blocks = new object[]
            {
                new { role = "Title", text = "Paper" },
                new { role = "BodyParagraph", text = "The study site is shown below." },
                new { role = "Figure", text = "", imageBase64 = Convert.ToBase64String(Png(200)), mimeType = "image/png" },
                new { role = "BodyParagraph", text = "Figure 1. Site map." },
                new { role = "BodyParagraph", text = "Results follow." },
                new { role = "Figure", text = "", imageBase64 = Convert.ToBase64String(GoogleDocsImage(h.Repo.WorkspacePath, svg)), mimeType = "image/png" },
                new { role = "BodyParagraph", text = "Figure 2. The pipeline." }
            }
        });
        Assert.Empty(h.Pending());
    });

    private static byte[] GoogleDocsImage(string workspace, string path) => RevisionsEditor.Core.Bridge.GoogleDocsFigures.ImageFor(workspace, path)!.Value.Bytes;
}
