using System.IO;
using System.IO.Compression;
using System.Windows.Documents;
using System.Xml.Linq;
using RevisionsEditor.Core.Checks;
using RevisionsEditor.Core.Docx;
using RevisionsEditor.Core.Formatting;
using RevisionsEditor.Core.Latex;
using RevisionsEditor.Core.Models;
using RevisionsEditor.Core.Scholarly;
using Xunit;

namespace RevisionsEditor.Tests;

public sealed partial class RevisionsEditorTests
{
    [Fact]
    public void DraftPlaceholders_AreFound_RenderedAndListedByTheChecks()
    {
        var found = DraftPlaceholders.Find("The introduction runs to [[1200 words|still to write]] and approaches to [[WHAT?]] rely on SIFT.");
        Assert.Equal(2, found.Count);
        Assert.Equal(("1200 words", "still to write"), (found[0].Text, found[0].Note));
        Assert.Equal(("WHAT?", ""), (found[1].Text, found[1].Note));
        Assert.Empty(DraftPlaceholders.Find("A [[]] and [link](x) and [@cite]"));
        Assert.Equal("[[a (b)/c|note]]", DraftPlaceholders.Format("a [b]|c", " note "));
        Assert.Equal("[[…|]]".Length, "[[…|]]".Length);
        Assert.Equal("…", DraftPlaceholders.Find("[[|the final RMSE]]").Single().Display);

        var (ws, doc) = ScholarlyWorkspace();
        try
        {
            doc.Blocks[3].Text += " Heights were [[0.42 m|final RMSE from the field season]] on average.";
            doc.Blocks[4].Text = doc.Blocks[4].Text.Replace("width=50%", "width=50% placeholder=\"synthetic data until the field season ends\"");
            var context = ContextFor(doc, ws);

            // The tokenizer gives the placeholder as one piece (its note as the detail), not a citation or number.
            var pieces = context.Tokenize("Mean [[0.42 m|final RMSE]] here.");
            var piece = Assert.Single(pieces, p => p.Kind == InlinePieceKind.Placeholder);
            Assert.Equal(("0.42 m", "final RMSE"), (piece.Display, piece.Detail));
            Assert.Equal("Mean 0.42 m here.", context.Render("Mean [[0.42 m|final RMSE]] here."));

            // The checks list both: the text placeholder (where it is) and the placeholder figure.
            var issues = ManuscriptChecks.Run(doc.Blocks, context).Where(i => i.Category == ManuscriptChecks.Placeholders).ToList();
            Assert.Equal(2, issues.Count);
            var text = Assert.Single(issues, i => i.Start >= 0);
            Assert.Equal("[[0.42 m|final RMSE from the field season]]", doc.Blocks[3].Text.Substring(text.Start, text.Length));
            Assert.Contains(issues, i => i.ParagraphId == doc.Blocks[4].ParagraphId && i.Message.Contains("synthetic data"));

            // LaTeX: marked in the text with its note as a footnote; the figure stamped and its caption flagged.
            var profile = FormattingRuleProfile.GetBuiltInProfiles()[0];
            string tex = LatexExportService.Build(doc, context, profile, new LatexExportOptions { WorkspacePath = ws }).Source;
            Assert.Contains(@"\placeholder{0.42\,m}{final RMSE from the field season}", tex);
            Assert.Contains(@"\placeholderstamp{\includegraphics", tex);
            Assert.Contains(@"[Placeholder: synthetic data until the field season ends]", tex);
            AssertWellFormedLatex(tex);

            // Word: highlighted with its note; a copy coming back gives the placeholder's source again, or (typed
            // over) what was written.
            string path = Path.Combine(ws, "draft.docx");
            DocxExportService.Export(doc, path, new DocxExportOptions { Scholarly = context, WorkspacePath = ws });
            string wordText = DocxText(path);
            Assert.Contains("0.42 m [final RMSE from the field season]", wordText);
            Assert.Contains("[Placeholder: synthetic data until the field season ends]", wordText);
            var back = DocxIngestionService.IngestDocx(path, doc);
            Assert.Contains("[[0.42 m|final RMSE from the field season]]", back.Blocks[3].Text);
            Assert.Equal(doc.Blocks[4].Text, back.Blocks[4].Text);

            string typed = Path.Combine(ws, "typed.docx");
            File.Copy(path, typed);
            using (var zip = ZipFile.Open(typed, ZipArchiveMode.Update))
            {
                var entry = zip.GetEntry("word/document.xml")!;
                XDocument xml;
                using (var s = entry.Open())
                {
                    xml = XDocument.Load(s);
                }
                XNamespace w = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
                var t = xml.Descendants(w + "t").First(e => e.Value == "0.42 m");
                t.Value = "0.39 m";
                t.Parent!.ElementsAfterSelf(w + "r").Remove(); // and the note after it
                entry.Delete();
                using var output = zip.CreateEntry("word/document.xml").Open();
                xml.Save(output);
            }
            // Read as written: a change to the placeholder, to accept or reject like any reviewer's edit.
            var edited = DocxIngestionService.IngestDocx(typed, doc);
            string final = RevisionsEditor.Core.Serialization.CriticMarkupSerializer.ResolveCriticMarkupText(edited.Blocks[3].Text, MarkupDisplayMode.NoMarkupFinal);
            Assert.Contains("Heights were 0.39 m on average.", final);
            Assert.DoesNotContain("[[", final);
            Assert.Contains(edited.Hunks, h => h.ParagraphId == doc.Blocks[3].ParagraphId);
        }
        finally
        {
            Directory.Delete(ws, recursive: true);
        }
    }

    [Fact]
    public void DraftPlaceholders_AreMarkedOnThePage_AndInThePreview()
        => RunOnStaThread(() =>
        {
            var (ws, doc) = ScholarlyWorkspace();
            try
            {
                doc.Blocks[3].Text += " Approaches to [[WHAT?|which detectors? see Olson 2025]] rely on SIFT.";
                var context = ContextFor(doc, ws);
                var profile = FormattingRuleProfile.GetBuiltInProfiles()[0];

                var page = RevisionsEditor.App.Rendering.FlowDocumentBuilder.BuildManuscriptEditorDocument(doc, profile, MarkupDisplayMode.AllMarkup, scholarly: context, workspacePath: ws);
                var run = page.Blocks.OfType<Paragraph>().SelectMany(p => p.Inlines).OfType<Run>()
                    .Single(r => ReferenceEquals(r.Tag, RevisionsEditor.App.Rendering.FlowDocumentBuilder.PlaceholderTag));
                Assert.Equal("[[WHAT?|which detectors? see Olson 2025]]", run.Text); // typed over like any text
                Assert.Contains("which detectors?", (string)run.ToolTip);
                // Read back unchanged.
                var read = RevisionsEditor.App.Rendering.ManuscriptEditorReader.Read(page, []);
                Assert.Equal(doc.Blocks[3].Text, read.Blocks.Single(b => b.ParagraphId == doc.Blocks[3].ParagraphId).Text);

                var preview = RevisionsEditor.App.Rendering.FlowDocumentBuilder.BuildManuscriptEditorDocument(doc, profile, MarkupDisplayMode.NoMarkupFinal, scholarly: context, workspacePath: ws, preview: true);
                var span = preview.Blocks.OfType<Paragraph>().SelectMany(p => p.Inlines).OfType<Span>().Single(s => s.ToolTip is string tip && tip.StartsWith("Placeholder", StringComparison.Ordinal));
                Assert.Equal("WHAT?", new TextRange(span.ContentStart, span.ContentEnd).Text);
            }
            finally
            {
                Directory.Delete(ws, recursive: true);
            }
        });

    [Fact]
    public void PaperNotes_AreSavedWithThePaper_TaggedAndFiltered()
    {
        string ws = Path.Combine(Path.GetTempPath(), $"RevEditorTest_Notes_{Guid.NewGuid():N}");
        Directory.CreateDirectory(ws);
        try
        {
            Assert.Empty(PaperNotes.Load(ws));
            PaperNotes.Save(ws, []);
            Assert.False(File.Exists(Path.Combine(ws, ".revisions", "notes.json"))); // nothing to save yet

            var older = new PaperNote { Text = "Compare leaf-off returns with #ALS density", Created = DateTimeOffset.Now.AddDays(-2) };
            var done = new PaperNote { Text = "Ask Nicholas about the #methods figure", Done = true };
            var pinned = new PaperNote { Text = "Reviewer 2 wants a sensitivity analysis #methods", Pinned = true, Created = DateTimeOffset.Now.AddDays(-5), ParagraphId = "p-004", ParagraphPreview = "Laser scanning supports…" };
            var newest = new PaperNote { Text = "Try the shadow index in the discussion" };
            PaperNotes.Save(ws, [older, done, pinned, newest]);

            var loaded = PaperNotes.Load(ws);
            Assert.Equal(4, loaded.Count);
            Assert.Equal(["methods"], loaded.Single(n => n.Pinned).Tags);
            Assert.Equal(["als"], loaded.Single(n => n.Text.StartsWith("Compare", StringComparison.Ordinal)).Tags);
            Assert.Equal("p-004", loaded.Single(n => n.Pinned).ParagraphId);

            // Pinned first, then the open notes newest first; done ones only when asked for.
            Assert.Equal([pinned.Id, newest.Id, older.Id], PaperNotes.Filter(loaded, null, includeDone: false).Select(n => n.Id));
            Assert.Equal(done.Id, PaperNotes.Filter(loaded, null, includeDone: true).Last().Id);
            Assert.Equal([pinned.Id, done.Id], PaperNotes.Filter(loaded, "#methods", includeDone: true).Select(n => n.Id));
            Assert.Equal([newest.Id], PaperNotes.Filter(loaded, "shadow discussion", includeDone: true).Select(n => n.Id));
            Assert.Equal([pinned.Id], PaperNotes.Filter(loaded, "laser", includeDone: true).Select(n => n.Id)); // by its paragraph
            Assert.Empty(PaperNotes.TagsOf("issue#3 and C# code"));
        }
        finally
        {
            Directory.Delete(ws, recursive: true);
        }
    }
}
