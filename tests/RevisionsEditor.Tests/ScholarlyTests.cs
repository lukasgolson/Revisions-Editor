using System.IO;
using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using RevisionsEditor.Core.Bibliography;
using RevisionsEditor.Core.Checks;
using RevisionsEditor.Core.Docx;
using RevisionsEditor.Core.Formatting;
using RevisionsEditor.Core.Git;
using RevisionsEditor.Core.Journals;
using RevisionsEditor.Core.Models;
using RevisionsEditor.Core.Reviews;
using RevisionsEditor.Core.Scholarly;
using RevisionsEditor.Core.Serialization;
using RevisionsEditor.Core.Storage;
using Xunit;

namespace RevisionsEditor.Tests;

public sealed partial class RevisionsEditorTests
{
    private const string SampleBib = """
        @article{white2016remote,
          title = {Remote Sensing Technologies for Enhancing Forest Inventories: A Review},
          author = {White, Joanne C. and Coops, Nicholas C. and Wulder, Michael A. and Vastaranta, Mikko and Hilker, Thomas and Tompalski, Piotr},
          year = {2016},
          journal = {Canadian Journal of Remote Sensing},
          shortjournal = {Can. J. Remote Sens.},
          volume = {42}, number = {5}, pages = {619--641},
          doi = {10.1080/07038992.2016.1207484}
        }

        @article{naesset2002predicting,
          title = {Predicting Forest Stand Characteristics with Airborne Scanning Laser Using a Practical Two-Stage Procedure and Field Data},
          author = {N{\ae}sset, Erik},
          year = {2002},
          journal = {Remote Sensing of Environment},
          shortjournal = {Remote Sens. Environ.},
          volume = {80}, number = {1}, pages = {88--99},
          doi = {10.1016/S0034-4257(01)00290-5}
        }
        """;

    private static readonly XNamespace Wns = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";

    /// <summary>A valid PNG of the given size (solid grey) with a 300 dpi pHYs chunk.</summary>
    private static byte[] TinyPng(int width, int height)
    {
        static byte[] Chunk(string type, byte[] data)
        {
            var ms = new MemoryStream();
            var len = BitConverter.GetBytes(data.Length); Array.Reverse(len); ms.Write(len);
            var typeBytes = Encoding.ASCII.GetBytes(type);
            ms.Write(typeBytes); ms.Write(data);
            uint crc = Crc32([.. typeBytes, .. data]);
            var crcBytes = BitConverter.GetBytes(crc); Array.Reverse(crcBytes); ms.Write(crcBytes);
            return ms.ToArray();
        }
        static uint Crc32(byte[] bytes)
        {
            uint c = 0xFFFFFFFF;
            foreach (byte b in bytes)
            {
                c ^= b;
                for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            }
            return c ^ 0xFFFFFFFF;
        }
        static byte[] BigEndian(int v) { var b = BitConverter.GetBytes(v); Array.Reverse(b); return b; }

        var raw = new MemoryStream();
        for (int y = 0; y < height; y++)
        {
            raw.WriteByte(0);
            for (int x = 0; x < width; x++) { raw.WriteByte(0x80); raw.WriteByte(0x90); raw.WriteByte(0xA0); }
        }
        var compressed = new MemoryStream();
        using (var z = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true)) { raw.Position = 0; raw.CopyTo(z); }

        var png = new MemoryStream();
        png.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        png.Write(Chunk("IHDR", [.. BigEndian(width), .. BigEndian(height), 8, 2, 0, 0, 0]));
        png.Write(Chunk("pHYs", [.. BigEndian(11811), .. BigEndian(11811), 1]));
        png.Write(Chunk("IDAT", compressed.ToArray()));
        png.Write(Chunk("IEND", []));
        return png.ToArray();
    }

    /// <summary>A small paper using every scholarly feature, in a temporary workspace with its files.</summary>
    private static (string Workspace, ManuscriptDocument Doc) ScholarlyWorkspace()
    {
        string ws = Path.Combine(Path.GetTempPath(), $"RevEditorTest_Scholarly_{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(ws, "figures"));
        Directory.CreateDirectory(Path.Combine(ws, "tables"));
        Directory.CreateDirectory(Path.Combine(ws, "results"));
        File.WriteAllText(Path.Combine(ws, "references.bib"), SampleBib);
        File.WriteAllBytes(Path.Combine(ws, "figures", "site.png"), TinyPng(600, 300));
        File.WriteAllText(Path.Combine(ws, "tables", "accuracy.csv"), "Site,RMSE (m),Bias (m)\nGrimston,0.42,-0.05\nKwantlen,0.51,0.03\n");
        File.WriteAllText(Path.Combine(ws, "results", "height.json"), """{ "rmse": 0.4237, "bias": -0.051, "n": 128, "leafOn": { "r2": 0.873 } }""");

        var doc = Paper(
            ("p-001", BlockStyleRole.Title, "Tree height from UAV laser scanning"),
            ("p-002", BlockStyleRole.Abstract, "Heights had an RMSE of {{height.rmse:0.00}} m across {{height.n}} trees."),
            ("p-003", BlockStyleRole.Heading1, "Introduction {#sec:intro}"),
            ("p-004", BlockStyleRole.BodyParagraph, "Laser scanning supports inventories [@white2016remote; @naesset2002predicting]. @naesset2002predicting introduced the area-based approach. The sites are shown in @fig:site and accuracy in @tbl:accuracy, computed with @eq:rmse."),
            ("p-005", BlockStyleRole.Figure, "![Study sites in *Grimston Park*.](figures/site.png){#fig:site width=50%}"),
            ("p-006", BlockStyleRole.Table, "Table: Height accuracy by site [@white2016remote]. {#tbl:accuracy file=tables/accuracy.csv}"),
            ("p-007", BlockStyleRole.Equation, "$$ RMSE = \\sqrt{\\frac{1}{n}\\sum_{i=1}^{n}(\\hat{y}_i - y_i)^2} $$ {#eq:rmse}"),
            ("p-008", BlockStyleRole.BodyParagraph, "Crowns covered 12 m^2^ on average, see @sec:intro and $R^2$ values."),
            ("p-009", BlockStyleRole.Heading1, "References"));
        return (ws, doc);
    }

    private static ScholarlyContext ContextFor(ManuscriptDocument doc, string workspace, string journalId = "rse")
    {
        new PaperSettings { JournalId = journalId }.Save(workspace);
        var project = ScholarlyProject.Load(workspace);
        var formatted = doc.Clone();
        FormattingRuleEngine.ApplyFormattingRules(formatted, FormattingRuleProfile.GetBuiltInProfiles()[0]);
        return ScholarlyContext.Build(formatted.Blocks, project);
    }

    [Fact]
    public void ScholarlyBlocks_SurviveMarkdownAndSectionFiles_AndAreRecognisedWithoutAnchors()
    {
        var (ws, doc) = ScholarlyWorkspace();
        try
        {
            var figure = ScholarlyBlocks.ParseFigure(doc.Blocks[4].Text)!;
            Assert.Equal("figures/site.png", figure.ImagePath);
            Assert.Equal("fig:site", figure.Label);
            Assert.Equal(0.5, figure.WidthFraction);
            Assert.Equal("Study sites in *Grimston Park*.", figure.Caption);
            Assert.Equal("tables/accuracy.csv", ScholarlyBlocks.ParseTable(doc.Blocks[5].Text)!.CsvPath);
            Assert.Equal("eq:rmse", ScholarlyBlocks.ParseEquation(doc.Blocks[6].Text)!.Label);
            Assert.Equal("sec:intro", ScholarlyBlocks.HeadingLabel(doc.Blocks[2].Text));
            // A CriticMarkup mark at the end of a caption is not an attribute group.
            Assert.Null(ScholarlyBlocks.ParseTable("Table: Caption {++new++}")!.Label);

            var split = ScholarlyBlocks.SplitCaption(doc.Blocks[4].Text)!.Value;
            Assert.Equal(doc.Blocks[4].Text, split.Prefix + split.Caption + split.Suffix);

            // Through Markdown without anchors: roles come from the syntax.
            var parsed = CriticMarkupSerializer.ParseMarkdown(CriticMarkupSerializer.ToMarkdown(doc));
            Assert.Equal(doc.Blocks.Select(b => b.Role), parsed.Blocks.Select(b => b.Role));
            Assert.Equal(doc.Blocks.Select(b => b.Text), parsed.Blocks.Select(b => b.Text));

            // Through the section files.
            var read = ManuscriptSectionStore.Read(ManuscriptSectionStore.Write(doc));
            Assert.Equal(doc.Blocks.Select(b => (b.ParagraphId, b.Role, b.Text)), read.Blocks.Select(b => (b.ParagraphId, b.Role, b.Text)));
        }
        finally
        {
            Directory.Delete(ws, recursive: true);
        }
    }

    [Fact]
    public void ScholarlyContext_NumbersItems_RendersCitationsCrossReferencesAndNumbers_PerJournal()
    {
        var (ws, doc) = ScholarlyWorkspace();
        try
        {
            var rse = ContextFor(doc, ws, "rse");
            Assert.Equal("1", rse.CrossReferences.Find("fig", "site")!.Number);
            Assert.Equal("1", rse.CrossReferences.Find("tbl", "accuracy")!.Number);
            Assert.Equal("1", rse.CrossReferences.Find("sec", "intro")!.Number);

            string body = rse.Render(doc.Blocks[3].Text);
            Assert.Contains("(White et al., 2016; Næsset, 2002)", body);
            Assert.Contains("Næsset (2002) introduced", body);
            Assert.Contains("shown in Fig. 1 and accuracy in Table 1, computed with Eq. (1).", body);
            Assert.Equal("Heights had an RMSE of 0.42 m across 128 trees.", rse.Render(doc.Blocks[1].Text));
            Assert.Contains("see Section 1", rse.Render(doc.Blocks[7].Text));
            Assert.Equal(2, rse.Citations.References.Count);
            Assert.StartsWith("Næsset, E., 2002.", rse.Citations.References[0].Text); // alphabetical

            var mdpi = ContextFor(doc, ws, "mdpi-remote-sensing");
            string numbered = mdpi.Render(doc.Blocks[3].Text);
            Assert.Contains("inventories [1,2]", numbered);
            Assert.Contains("Figure 1", numbered);
            Assert.Equal("1.", mdpi.Citations.References[0].Label);

            var ieee = ContextFor(doc, ws, "ieee-tgrs");
            Assert.Contains("Table I", ieee.Render(doc.Blocks[3].Text));
            Assert.Contains("computed with (1)", ieee.Render(doc.Blocks[3].Text));

            // Groups and ranges.
            var three = Paper(
                ("a", BlockStyleRole.Figure, "![A](a.png){#fig:a}"),
                ("b", BlockStyleRole.Figure, "![B](b.png){#fig:b}"),
                ("c", BlockStyleRole.Figure, "![C](c.png){#fig:c}"));
            var index = CrossReferenceIndex.Build(three.Blocks, JournalProfiles.Find("rse")!.CrossReferenceNames);
            var use = CrossReferenceIndex.FindUses("see [@fig:a; @fig:b; @fig:c] and [@fig:a; @fig:c]");
            Assert.Equal("Figs. 1–3", index.Render(use[0], false));
            Assert.Equal("Figs. 1 and 3", index.Render(use[1], false));
            Assert.Equal("Fig. ??", index.Render(CrossReferenceIndex.FindUses("@fig:nothing")[0], false));
        }
        finally
        {
            Directory.Delete(ws, recursive: true);
        }
    }

    [Fact]
    public void ResultsLibrary_ReadsJsonAndCsv_FormatsValues_AndNoticesChangedSources()
    {
        var library = ResultsLibrary.FromContents(
        [
            ("height", "height.json", """{ "rmse": 0.4237, "bias": -0.051, "sites": [ { "name": "G", "r2": 0.873 } ] }"""),
            ("acc", "acc.csv", "site,condition,rmse\nGrimston,leaf-on,0.42\nGrimston,leaf-off,0.38\nKwantlen,leaf-on,0.51\n"),
            ("summary", "summary.csv", "metric,value\nrmse,0.45\nn,128\n")
        ]);

        string? Value(string placeholder)
        {
            var p = ResultsLibrary.FindPlaceholders(placeholder).Single();
            return library.Resolve(p, out _);
        }

        Assert.Equal("0.42", Value("{{height.rmse:0.00}}"));
        Assert.Equal("−0.05", Value("{{height.bias:F2}}"));
        Assert.Equal("87.3%", Value("{{height.sites[0].r2:P1}}"));
        Assert.Equal("0.424", Value("{{height.rmse:3sf}}"));
        Assert.Equal("0.38", Value("{{acc.Grimston.leaf-off.rmse}}"));
        Assert.Equal("0.51", Value("{{acc.Kwantlen.rmse}}"));
        Assert.Equal("128", Value("{{summary.n}}"));
        Assert.Null(Value("{{acc.Grimston.rmse}}")); // two Grimston rows: needs the condition
        Assert.Null(Value("{{nothing.rmse}}"));

        string ws = Path.Combine(Path.GetTempPath(), $"RevEditorTest_Results_{Guid.NewGuid():N}");
        string outside = Path.Combine(Path.GetTempPath(), $"RevEditorTest_Analysis_{Guid.NewGuid():N}.csv");
        try
        {
            Directory.CreateDirectory(ws);
            File.WriteAllText(outside, "metric,value\nrmse,0.40\n");
            var source = ResultsLibrary.ImportFile(ws, outside, "metrics");
            Assert.Equal("results/metrics.csv", source.File);
            var loaded = ResultsLibrary.Load(ws);
            Assert.False(loaded.IsStale("metrics"));

            File.WriteAllText(outside, "metric,value\nrmse,0.39\n"); // the analysis was rerun
            Assert.True(ResultsLibrary.Load(ws).IsStale("metrics"));
            Assert.Equal(["metrics"], ResultsLibrary.RefreshChangedSources(ws));
            var refreshed = ResultsLibrary.Load(ws);
            Assert.False(refreshed.IsStale("metrics"));
            Assert.Equal("0.39", refreshed.Resolve(ResultsLibrary.FindPlaceholders("{{metrics.rmse}}")[0], out _));
        }
        finally
        {
            Directory.Delete(ws, recursive: true);
            File.Delete(outside);
        }
    }

    [Fact]
    public void WordExport_RendersScholarlyContent_AndAReviewedCopyImportsBackToTheSource()
    {
        var (ws, doc) = ScholarlyWorkspace();
        try
        {
            var context = ContextFor(doc, ws);
            using var ms = new MemoryStream();
            DocxExportService.Export(doc, ms, new DocxExportOptions
            {
                Scholarly = context,
                WorkspacePath = ws,
                IncludeTrackedChanges = true,
                LineNumbers = true,
                PageNumbers = true,
                DoubleSpacing = true
            });

            ms.Position = 0;
            XDocument documentXml;
            using (var zip = new ZipArchive(ms, ZipArchiveMode.Read, leaveOpen: true))
            {
                Assert.NotNull(zip.GetEntry("word/media/image1.png"));
                Assert.NotNull(zip.GetEntry("word/footer1.xml"));
                using var s = zip.GetEntry("word/document.xml")!.Open();
                documentXml = XDocument.Load(s);
            }
            string text = string.Concat(documentXml.Descendants(Wns + "t").Select(t => t.Value));
            Assert.Contains("(White et al., 2016; Næsset, 2002)", text);
            Assert.Contains("Fig. 1.", text);
            Assert.Contains("Grimston", text); // table cells
            Assert.Contains("White, J.C., Coops, N.C.", text); // reference list
            Assert.DoesNotContain("[@", text);
            Assert.DoesNotContain("{{", text);
            XNamespace m = "http://schemas.openxmlformats.org/officeDocument/2006/math";
            Assert.True(documentXml.Descendants(m + "oMath").Count() >= 2); // display and inline
            // The data table, and the borderless table that centres the numbered equation.
            Assert.Equal(2, documentXml.Descendants(Wns + "tbl").Count());
            Assert.Single(documentXml.Descendants(Wns + "tblHeader"));
            Assert.NotNull(documentXml.Descendants(Wns + "lnNumType").SingleOrDefault());
            Assert.Contains(documentXml.Descendants(Wns + "vertAlign"), v => v.Attribute(Wns + "val")?.Value == "superscript");

            // The reviewer adds a tracked insertion to the paragraph with the citations.
            var paragraph = documentXml.Descendants(Wns + "p").First(p => string.Concat(p.Descendants(Wns + "t").Select(t => t.Value)).Contains("introduced the area-based"));
            paragraph.Add(new XElement(Wns + "ins", new XAttribute(Wns + "id", 900), new XAttribute(Wns + "author", "Dr. Coops"), new XAttribute(Wns + "date", "2026-10-01T10:00:00Z"),
                new XElement(Wns + "r", new XElement(Wns + "t", new XAttribute(XNamespace.Xml + "space", "preserve"), " Both sites were scanned twice."))));

            var reviewed = new MemoryStream();
            ms.Position = 0;
            ms.CopyTo(reviewed);
            using (var zip = new ZipArchive(reviewed, ZipArchiveMode.Update, leaveOpen: true))
            {
                zip.GetEntry("word/document.xml")!.Delete();
                using var s = zip.CreateEntry("word/document.xml").Open();
                documentXml.Save(s);
            }
            reviewed.Position = 0;

            var imported = DocxIngestionService.IngestDocx(reviewed, "Reviewed", doc);
            Assert.Equal(doc.Blocks.Count, imported.Blocks.Count);
            for (int i = 0; i < doc.Blocks.Count; i++)
            {
                string expected = doc.Blocks[i].Text;
                string actual = CriticMarkupSerializer.ResolveCriticMarkupText(imported.Blocks[i].Text, MarkupDisplayMode.Original);
                Assert.Equal(ScholarlyBlocks.StripHeadingLabel(expected), ScholarlyBlocks.StripHeadingLabel(actual));
                Assert.Equal(doc.Blocks[i].Role, imported.Blocks[i].Role);
            }
            Assert.Equal("Introduction {#sec:intro}", imported.Blocks[2].Text);
            var hunk = Assert.Single(imported.Hunks);
            Assert.Equal(RevisionHunkType.Insertion, hunk.HunkType);
            Assert.Equal("Dr. Coops", hunk.Author);
            Assert.Contains("[@white2016remote; @naesset2002predicting]", imported.Blocks[3].Text);
        }
        finally
        {
            Directory.Delete(ws, recursive: true);
        }
    }

    [Fact]
    public void ManuscriptChecks_FindCitationCrossReferenceNumberAndJournalProblems()
    {
        var (ws, doc) = ScholarlyWorkspace();
        try
        {
            doc.Blocks[3].Text += " Others disagree [@missing2020]. Ages differ (Table 3).";
            doc.Blocks[1].Text = "The RMSE was 0.61 m. " + string.Join(" ", Enumerable.Repeat("word", 420));
            doc.Blocks.Insert(5, new DocumentBlock { ParagraphId = "p-020", Role = BlockStyleRole.Figure, Text = "![Unused](figures/site.png){#fig:unused}" });
            var context = ContextFor(doc, ws, "rse");
            var formatted = doc.Clone();
            FormattingRuleEngine.ApplyFormattingRules(formatted, FormattingRuleProfile.GetBuiltInProfiles()[0]);
            var issues = ManuscriptChecks.Run(formatted.Blocks, context);

            Assert.Contains(issues, i => i.Category == ManuscriptChecks.Citations && i.Message.Contains("missing2020") && i.ParagraphId == "p-004");
            Assert.Contains(issues, i => i.Category == ManuscriptChecks.CrossReferences && i.Message.Contains("Figure 2 is never referred to"));
            Assert.Contains(issues, i => i.Category == ManuscriptChecks.CrossReferences && i.Message.Contains("“Table 3” is typed as text"));
            Assert.Contains(issues, i => i.Category == ManuscriptChecks.Numbers && i.Message.Contains("RMSE of 0.61 agrees with none"));
            Assert.Contains(issues, i => i.Category == ManuscriptChecks.Submission && i.Message.Contains("The abstract has"));
            Assert.Contains(issues, i => i.Category == ManuscriptChecks.Submission && i.Message.Contains("requires highlights"));
            Assert.Contains(issues, i => i.Category == ManuscriptChecks.Submission && i.Message.Contains("Declaration of competing interest"));
            Assert.DoesNotContain(issues, i => i.Message.Contains("white2016remote"));

            // A literal number equal to a result is offered as a link.
            var crowns = doc.Blocks.First(b => b.ParagraphId == "p-008");
            crowns.Text = "The bias was -0.05 m and the RMSE 0.42 m.";
            var linkIssues = ManuscriptChecks.Run(doc.Blocks, ContextFor(doc, ws, "rse"));
            var link = Assert.Single(linkIssues, i => i.Fix != null && i.Fix.Contains("height.rmse"));
            Assert.Equal("0.42", crowns.Text.Substring(link.Start, link.Length));
        }
        finally
        {
            Directory.Delete(ws, recursive: true);
        }
    }

    [Fact]
    public void ReviewComments_AreParsed_AndTheMarkedUpManuscriptAndResponseLetterAreBuilt()
    {
        const string letter = """
            Reviewer #1: Comments to the Author

            The manuscript is interesting and well written.

            1. The accuracy assessment lacks independent validation data.
            Please add field measurements.
            2. Line 120: define ALS at first use.

            Reviewer 2

            - Figure 3 needs a scale bar.
            - The discussion is too long.

            Editor's comments:
            Please shorten the abstract.
            """;
        var comments = ReviewCommentParser.Parse(letter);
        Assert.Equal(["R1.1", "R1.2", "R1.3", "R2.1", "R2.2", "E.1"], comments.Select(c => c.Id));
        Assert.Equal("The manuscript is interesting and well written.", comments[0].Text);
        Assert.Equal("The accuracy assessment lacks independent validation data. Please add field measurements.", comments[1].Text);
        Assert.Equal("Figure 3 needs a scale bar.", comments[3].Text);
        Assert.Equal("Editor", comments[5].Reviewer);

        var submitted = Paper(
            ("p-001", BlockStyleRole.Title, "A Study"),
            ("p-002", BlockStyleRole.BodyParagraph, "Heights were measured. Accuracy was high [@white2016remote]."),
            ("p-003", BlockStyleRole.BodyParagraph, "This paragraph is removed."));
        var revised = Paper(
            ("p-001", BlockStyleRole.Title, "A Study"),
            ("p-002", BlockStyleRole.BodyParagraph, "Heights were measured. Accuracy was high against 80 field trees [@naesset2002predicting]."),
            ("p-004", BlockStyleRole.BodyParagraph, "A new paragraph on validation."));
        var marked = RevisionDocuments.BuildMarkedUpManuscript(submitted, revised, "Revision 1", DateTimeOffset.UtcNow);
        Assert.Equal("Heights were measured. Accuracy was high {~~[@white2016remote]~>against 80 field trees [@naesset2002predicting]~~}.", marked.Blocks[1].Text);
        Assert.Contains(marked.Blocks, b => b.Text == "{--This paragraph is removed.--}");
        Assert.Contains(marked.Blocks, b => b.Text == "{++A new paragraph on validation.++}");
        Assert.All(marked.Hunks, h => Assert.Equal("Revision 1", h.Author));

        var round = new ReviewRound { Name = "Revision 1", ManuscriptNumber = "RSE-D-26-01234", Comments = comments };
        comments[1].Response = "We added independent field validation.";
        comments[1].ParagraphIds.Add("p-002");
        var locations = RevisionDocuments.StructuralLocations(revised);
        var response = RevisionDocuments.BuildResponseLetter(round, submitted, revised, locations, "A Study");
        var texts = response.Blocks.Select(b => b.Text).ToList();
        Assert.Contains("**Comment R1.2.** The accuracy assessment lacks independent validation data. Please add field measurements.", texts);
        Assert.Contains("**Response:** We added independent field validation.", texts);
        Assert.Contains("“… Accuracy was high against 80 field trees [@naesset2002predicting].”", texts);
        Assert.Contains("**Response:** [Response to be written.]", texts);

        // Two computers answered different comments.
        var baseStore = new ReviewStore { Rounds = [new ReviewRound { Id = "r1", Comments = comments.Select(c => c.Clone()).ToList() }] };
        var ours = ReviewStore.Parse(baseStore.Serialize());
        var theirs = ReviewStore.Parse(baseStore.Serialize());
        ours.Rounds[0].Comments[0].Response = "Thank you.";
        theirs.Rounds[0].Comments[3].Response = "Scale bar added.";
        theirs.Rounds[0].Comments[3].Status = ReviewCommentStatus.Addressed;
        var merged = ReviewStore.Merge(baseStore, ours, theirs);
        Assert.Equal("Thank you.", merged.Rounds[0].Comments[0].Response);
        Assert.Equal("Scale bar added.", merged.Rounds[0].Comments[3].Response);
        Assert.Equal(ReviewCommentStatus.Addressed, merged.Rounds[0].Comments[3].Status);
    }

    [Fact]
    public void SubmissionChecks_ReadHighlightsAsPublished_AndStopAtTheAbstract()
    {
        var (ws, _) = ScholarlyWorkspace();
        try
        {
            var doc = Paper(
                ("p-001", BlockStyleRole.Title, "A Study"),
                ("p-002", BlockStyleRole.Heading1, "Highlights"),
                ("p-003", BlockStyleRole.BodyParagraph, "• Heights agree within {{height.rmse:0.00}} m of field measurements"),
                ("p-004", BlockStyleRole.BodyParagraph, "• Leaf-off flights reduce height error for deciduous crowns in urban parks of the region today"),
                ("p-005", BlockStyleRole.Abstract, "A long abstract " + string.Join(" ", Enumerable.Repeat("word", 80))),
                ("p-006", BlockStyleRole.BodyParagraph, "Keywords: lidar; tree height"));
            var structure = new ManuscriptStructure(doc.Blocks);
            var context = ContextFor(doc, ws, "rse");
            var highlights = structure.Highlights(context.Render)!;
            Assert.Equal(["p-003", "p-004"], highlights.Select(h => h.Block.ParagraphId));
            Assert.Equal("Heights agree within 0.42 m of field measurements", highlights[0].Text);

            var issues = ManuscriptChecks.Run(doc.Blocks, context);
            Assert.Contains(issues, i => i.Message == "There are 2 highlights; Remote Sensing of Environment asks for 3–5.");
            var tooLong = Assert.Single(issues, i => i.Message.StartsWith("This highlight has", StringComparison.Ordinal));
            Assert.Equal("p-004", tooLong.ParagraphId);
            Assert.Equal(["lidar", "tree height"], structure.Keywords().Keywords);
        }
        finally
        {
            Directory.Delete(ws, recursive: true);
        }
    }

    [Fact]
    public void ManuscriptStatistics_CountsThePublishedText_ByPartAndSection_AndAgainstTheJournalsLimit()
    {
        var (ws, _) = ScholarlyWorkspace();
        try
        {
            var doc = Paper(
                ("p-001", BlockStyleRole.Title, "Tree height"),
                ("p-002", BlockStyleRole.Abstract, "Heights had an RMSE of {{height.rmse:0.00}} m."),
                ("p-003", BlockStyleRole.Heading1, "Introduction"),
                ("p-004", BlockStyleRole.BodyParagraph, "Laser scanning supports inventories [@white2016remote]. Leaf-off data help."),
                ("p-005", BlockStyleRole.Heading2, "Study area"),
                ("p-006", BlockStyleRole.BodyParagraph, "Two parks were scanned."),
                ("p-007", BlockStyleRole.Figure, "![Study sites in two parks.](figures/site.png){#fig:site}"),
                ("p-008", BlockStyleRole.Heading1, "Acknowledgements"),
                ("p-009", BlockStyleRole.BodyParagraph, "We thank the City."),
                ("p-010", BlockStyleRole.Heading1, "References"));
            var formatted = doc.Clone();
            FormattingRuleEngine.ApplyFormattingRules(formatted, FormattingRuleProfile.GetBuiltInProfiles()[0]);
            var context = ContextFor(doc, ws, "cjfr");
            var stats = ManuscriptStatistics.Compute(formatted.Blocks, context, JournalProfiles.Find("cjfr"));

            Assert.Equal(7, stats.Abstract.Words); // "0.42" counts as one word, as in Word
            // Introduction (1) + "Laser scanning supports inventories (White et al. 2016). Leaf-off data help." (11)
            // + Study area (2) + "Two parks were scanned." (4); not the caption or the back matter.
            Assert.Equal(18, stats.MainText.Words);
            Assert.Equal(5, stats.Captions.Words);
            Assert.Equal(6, stats.BackMatter.Words); // with the headings
            Assert.True(stats.References.Words > 10);
            Assert.Equal(["1. Introduction", "1.1 Study area", "Acknowledgements", "References"], stats.Sections.Select(s => s.Title));
            Assert.Equal(1 + 11 + 2 + 4 + 5, stats.Sections[0].Words); // with its subsection and the caption
            Assert.Equal(new WordLimit(6000, 18, "main text, Introduction to Conclusions"), stats.JournalLimit);
            Assert.Equal(1, stats.Figures);
            Assert.Equal(1, stats.Citations);

            var long40 = Paper(("p-1", BlockStyleRole.BodyParagraph, string.Join(" ", Enumerable.Repeat("word", 45)) + ". Short one, e.g. this."));
            var longStats = ManuscriptStatistics.Compute(long40.Blocks, null);
            Assert.Equal(2, longStats.Sentences);
            Assert.Equal(45, Assert.Single(longStats.LongSentences).Words);
        }
        finally
        {
            Directory.Delete(ws, recursive: true);
        }
    }

    [Fact]
    public void LineReferences_AreFoundInReviewerComments_AndMappedToParagraphsOfTheSubmittedVersion()
    {
        var refs = LineReferences.Find("Line 120: unclear. See also lines 45-50, L230, ll. 12–18, p. 4, line 7 and lines 120-5. Not: ALS 12, Fig. 3.");
        Assert.Equal(
            [(null, 120, 120), (null, 45, 50), (null, 230, 230), (null, 12, 18), ((int?)4, 7, 7), (null, 120, 125)],
            refs.Select(r => (r.Page, r.FirstLine, r.LastLine)));
        Assert.Equal("lines 45–50", refs[1].Label);

        var map = new SubmittedLineMap
        {
            Version = "submitted-v1",
            Paragraphs =
            [
                new LineSpan("p-001", 1, 1, 1, 1, 1, 1),
                new LineSpan("p-002", 1, 3, 1, 20, 3, 20),
                new LineSpan("p-003", 1, 21, 2, 6, 21, 29),
                new LineSpan("p-004", 2, 8, 2, 12, 31, 35)
            ]
        };
        Assert.Equal("p-002", map.Find(10)!.ParagraphId);
        Assert.Equal("p-003", map.Find(25)!.ParagraphId);
        Assert.Equal("p-003", map.Find(30)!.ParagraphId); // between paragraphs: the one before
        Assert.Equal("p-003", map.Find(4, page: 2)!.ParagraphId);
        Assert.Equal("p-004", map.Find(9, page: 2)!.ParagraphId);
        Assert.Equal(0.5, SubmittedLineMap.Fraction(map.Paragraphs[3], 33, null));
    }

    [Fact]
    public void BibFileMerge_KeepsEntriesAddedOnBothComputers()
    {
        string baseBib = "@article{a, title={A}}\n\n@article{b, title={B}}\n";
        string ours = baseBib + "\n@article{c, title={C}}\n";
        string theirs = "@article{a, title={A revised}}\n\n@article{b, title={B}}\n\n@article{d, title={D}}\n";
        var merged = BibTeXParser.Parse(BibFileMerge.Merge(baseBib, ours, theirs));
        Assert.Equal(["a", "b", "c", "d"], merged.Entries.Select(e => e.Key));
        Assert.Equal("A revised", merged.Entries[0].Title);
    }

    [Fact]
    public async Task GitHubSync_CarriesBibliographyFiguresReviewsAndSubmissionTags_AndMergesThem()
    {
        string root = Path.Combine(Path.GetTempPath(), $"RevEditorTest_ScholarlySync_{Guid.NewGuid():N}");
        string remote = Path.Combine(root, "remote.git");
        try
        {
            Directory.CreateDirectory(remote);
            RunGitIn(remote, "init", "-q", "--bare", "-b", "main");

            var laptop = new GitRepositoryService(Path.Combine(root, "laptop"));
            laptop.EnsureInitialized();
            File.WriteAllText(Path.Combine(laptop.WorkspacePath, "references.bib"), "@article{a, title={A}, year={2020}, author={Smith, J.}}\n");
            Directory.CreateDirectory(Path.Combine(laptop.WorkspacePath, "figures"));
            File.WriteAllBytes(Path.Combine(laptop.WorkspacePath, "figures", "map.png"), TinyPng(4, 4));
            var (sha, _) = laptop.CommitSnapshot(BasePaper(), "Initial draft");
            laptop.CreateTag("submitted-v1", sha);
            laptop.SetRemoteUrl(remote);
            laptop.SyncWithRemote();

            var desktop = GitRepositoryService.CloneFromRemote(remote, Path.Combine(root, "desktop"));
            Assert.True(File.Exists(Path.Combine(desktop.WorkspacePath, "figures", "map.png")));
            Assert.Equal(sha, desktop.GetTags()["submitted-v1"]);

            // Both computers add a reference and answer a reviewer, then both sync.
            File.AppendAllText(Path.Combine(laptop.WorkspacePath, "references.bib"), "\n@article{laptop, title={L}}\n");
            new ReviewStore { Rounds = [new ReviewRound { Id = "r1", Comments = [new ReviewComment { Id = "R1.1", Response = "From the laptop." }] }] }.Save(laptop.WorkspacePath);
            var laptopDoc = BasePaper();
            laptopDoc.Blocks[2].Text = "Alpha beta gamma delta, from the laptop.";
            laptop.CommitSnapshot(laptopDoc, "Laptop edits");

            File.AppendAllText(Path.Combine(desktop.WorkspacePath, "references.bib"), "\n@article{desktop, title={D}}\n");
            File.WriteAllBytes(Path.Combine(desktop.WorkspacePath, "figures", "plot.png"), TinyPng(8, 8));
            var desktopDoc = BasePaper();
            desktopDoc.Blocks[6].Text = "Methods text, from the desktop.";
            desktop.CommitSnapshot(desktopDoc, "Desktop edits");
            desktop.SyncWithRemote();

            var report = laptop.SyncWithRemote();
            Assert.Equal(1, report.BranchesMerged);
            var keys = BibTeXParser.Parse(File.ReadAllText(Path.Combine(laptop.WorkspacePath, "references.bib"))).Entries.Select(e => e.Key).ToList();
            Assert.Equal(["a", "laptop", "desktop"], keys);
            Assert.True(File.Exists(Path.Combine(laptop.WorkspacePath, "figures", "plot.png")));
            Assert.Equal("From the laptop.", ReviewStore.Load(laptop.WorkspacePath).Rounds[0].Comments[0].Response);
            string tracked = RunGitIn(laptop.WorkspacePath, "ls-tree", "-r", "--name-only", "HEAD");
            Assert.Contains("figures/plot.png", tracked);
            Assert.Contains(".revisions/reviews.json", tracked);

            desktop.SyncWithRemote();
            Assert.Contains("@article{laptop", File.ReadAllText(Path.Combine(desktop.WorkspacePath, "references.bib")));
            Assert.Equal("From the laptop.", ReviewStore.Load(desktop.WorkspacePath).Rounds[0].Comments[0].Response);
            await Task.CompletedTask;
        }
        finally
        {
            TestWorkspaceBuilder.DeleteDirectory(root);
        }
    }
}
