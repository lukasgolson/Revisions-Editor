using System.IO;
using System.IO.Compression;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media.Imaging;
using RevisionsEditor.Core.Docx;
using RevisionsEditor.Core.Formatting;
using RevisionsEditor.Core.Latex;
using RevisionsEditor.Core.Models;
using RevisionsEditor.Core.Scholarly;
using Xunit;

namespace RevisionsEditor.Tests;

public sealed partial class RevisionsEditorTests
{
    /// <summary>A chart as matplotlib saves it: its size in points.</summary>
    private const string SampleSvg = """
        <svg xmlns="http://www.w3.org/2000/svg" width="360pt" height="180pt" viewBox="0 0 360 180">
          <rect width="360" height="180" fill="#ffffff"/>
          <polyline points="10,170 120,90 240,120 350,10" fill="none" stroke="#1f77b4" stroke-width="3"/>
          <text x="180" y="175" font-family="Arial" font-size="12" text-anchor="middle">Height (m)</text>
        </svg>
        """;

    [Fact]
    public void LatexTypography_SetsQuotesDashesUnitsAndEllipses_ButLeavesUrlsAndDatesAlone()
    {
        string Set(string text) => LatexExportService.Escape(LatexExportService.Typeset(text));

        Assert.Equal("The “vector” figures… stay sharp", Set("The \"vector\" figures... stay sharp"));
        Assert.Equal(@"Heights ranged 10–25\,m – see ‘below’", Set("Heights ranged 10-25 m - see 'below'"));
        Assert.Equal("the crown’s 2020-05-01 survey", Set("the crown's 2020-05-01 survey"));
        Assert.Equal("see https://doi.org/10.1007/s10342-021-01379-2 for 3–5 trees", Set("see https://doi.org/10.1007/s10342-021-01379-2 for 3-5 trees"));
        Assert.Equal("COVID-19 at 3 sites in 2 h", Set("COVID-19 at 3 sites in 2 h").Replace(@"\,", " "));
        Assert.Equal(@"12\,m and 0.5\,ha", Set("12 m and 0.5 ha"));
    }

    [Fact]
    public void VectorFigures_BecomeVectorPdfForLatex_AndSvgWithAPngForWord()
    {
        var (ws, doc) = ScholarlyWorkspace();
        try
        {
            string svg = Path.Combine(ws, "figures", "trend.svg");
            string pdf = Path.Combine(ws, "figures", "map.pdf");
            File.WriteAllText(svg, SampleSvg);
            // A PDF figure, as plotting tools save them (made here from the SVG).
            Assert.True(FigureImages.SvgToPdf(svg, pdf));
            Assert.Equal(FigureImageKind.Svg, FigureImages.KindOf(svg));
            Assert.Equal(FigureImageKind.Pdf, FigureImages.KindOf(pdf));

            // Sizes in points, from the SVG's own units and the PDF's page.
            var svgSize = FigureImages.NaturalSizePoints(svg)!.Value;
            Assert.Equal(360, svgSize.Width, 0.5);
            Assert.Equal(180, svgSize.Height, 0.5);
            var pdfSize = FigureImages.NaturalSizePoints(pdf)!.Value;
            Assert.Equal(360, pdfSize.Width, 0.5);
            Assert.Equal(180, pdfSize.Height, 0.5);

            // Drawn as PNG for the preview and Word, at the resolution asked for.
            var fromPdf = ImageInfo.TryRead(FigureImages.RenderPng(pdf, dpi: 144)!)!;
            Assert.Equal(720, fromPdf.Width, 2.0);
            var fromSvg = ImageInfo.TryRead(FigureImages.RenderPng(svg, dpi: 144)!)!;
            Assert.Equal(720, fromSvg.Width, 2.0);

            File.WriteAllText(Path.Combine(ws, "figures", "old.eps"), "%!PS-Adobe-3.0 EPSF-3.0\n");
            doc.Blocks.Insert(5, new DocumentBlock { ParagraphId = "p-040", Role = BlockStyleRole.Figure, Text = "![Trend of heights.](figures/trend.svg){#fig:trend width=90%}" });
            doc.Blocks.Insert(6, new DocumentBlock { ParagraphId = "p-041", Role = BlockStyleRole.Figure, Text = "![Map of the sites.](figures/map.pdf){#fig:map}" });
            doc.Blocks.Add(new DocumentBlock { ParagraphId = "p-042", Role = BlockStyleRole.Figure, Text = "![An old figure.](figures/old.eps){#fig:old}" });
            var context = ContextFor(doc, ws);
            var profile = FormattingRuleProfile.GetBuiltInProfiles()[0];

            // LaTeX: the SVG as a vector PDF; 90% of the text width spans both columns; EPS is explained.
            var latex = LatexExportService.Build(doc, context, profile, new LatexExportOptions { Template = LatexTemplate.JournalTwoColumn, WorkspacePath = ws });
            Assert.Contains(@"\begin{figure*}[tp]", latex.Source);
            Assert.Contains(@"\includegraphics[width=0.9\textwidth,height=0.5\textheight,keepaspectratio]{figures/trend.pdf}", latex.Source);
            Assert.Contains(@"\includegraphics[width=\columnwidth,height=0.45\textheight,keepaspectratio]{figures/map.pdf}", latex.Source);
            Assert.Contains(latex.Warnings, w => w.Contains("EPS") && w.Contains("old.eps"));
            string folder = Path.Combine(ws, "latex-out");
            LatexExportService.WriteTo(latex, folder);
            byte[] converted = File.ReadAllBytes(Path.Combine(folder, "figures", "trend.pdf"));
            Assert.Equal("%PDF", Encoding.ASCII.GetString(converted, 0, 4));
            Assert.DoesNotContain("/Subtype /Image", Encoding.Latin1.GetString(converted)); // lines and text, not pixels
            Assert.True(File.Exists(Path.Combine(folder, "figures", "map.pdf")));

            // Word: the SVG itself (drawn as a vector by Word 2016 and later) with a PNG for older readers; the PDF as a PNG.
            using var ms = new MemoryStream();
            DocxExportService.Export(doc, ms, new DocxExportOptions { Scholarly = context, WorkspacePath = ws });
            ms.Position = 0;
            using var zip = new ZipArchive(ms);
            var media = zip.Entries.Where(e => e.FullName.StartsWith("word/media/", StringComparison.Ordinal)).Select(e => Path.GetExtension(e.Name)).ToList();
            Assert.Single(media, ".svg");
            Assert.Equal(3, media.Count(m => m == ".png")); // the study-site photo, the SVG's PNG and the PDF drawn
            string Part(string name)
            {
                using var reader = new StreamReader(zip.GetEntry(name)!.Open());
                return reader.ReadToEnd();
            }
            Assert.Contains("asvg:svgBlip", Part("word/document.xml"));
            Assert.Contains("image/svg+xml", Part("[Content_Types].xml"));
        }
        finally
        {
            Directory.Delete(ws, recursive: true);
        }
    }

    [Fact]
    public void DraftNotes_ShowOnThePageAndInDrafts_ButNotInCopiesForTheJournal()
        => RunOnStaThread(() =>
        {
            var (ws, doc) = ScholarlyWorkspace();
            try
            {
                var front = SampleFrontMatter();
                front.DraftNotes = "Target journal: ISPRS\r\nTarget word count: 8,000\n\n";
                front.Save(ws);
                front = FrontMatter.Load(ws);
                Assert.Equal(["Target journal: ISPRS", "Target word count: 8,000"], front.DraftNoteLines);
                File.WriteAllText(Path.Combine(ws, "figures", "trend.svg"), SampleSvg);
                doc.Blocks.Add(new DocumentBlock { ParagraphId = "p-040", Role = BlockStyleRole.Figure, Text = "![Trend.](figures/trend.svg){#fig:trend}" });
                var context = ContextFor(doc, ws);
                var profile = FormattingRuleProfile.GetBuiltInProfiles()[0];

                // The page: the read-only title, the title page, then the draft-notes box; the SVG figure is drawn.
                var page = RevisionsEditor.App.Rendering.FlowDocumentBuilder.BuildManuscriptEditorDocument(
                    doc, profile, MarkupDisplayMode.AllMarkup, scholarly: context, workspacePath: ws);
                var notes = page.Blocks.OfType<BlockUIContainer>().Select(c => c.Child).OfType<Border>()
                    .Select(b => b.Child).OfType<TextBlock>().Select(t => new TextRange(t.ContentStart, t.ContentEnd).Text).ToList();
                Assert.Contains(notes, n => n.StartsWith("Draft notes", StringComparison.Ordinal) && n.Contains("Target journal: ISPRS"));
                var images = page.Blocks.OfType<BlockUIContainer>().SelectMany(c => Descendants(c.Child)).OfType<Image>().ToList();
                Assert.Contains(images, i => i.Source is BitmapSource { PixelWidth: > 100 });

                var read = RevisionsEditor.App.Rendering.ManuscriptEditorReader.Read(page, []);
                Assert.Equal(doc.Blocks.Select(b => (b.ParagraphId, b.Text)), read.Blocks.Select(b => (b.ParagraphId, b.Text)));

                // Word and PDF: in a draft, not in a copy for the journal.
                string WordText(bool draft)
                {
                    string path = Path.Combine(ws, $"export-{draft}.docx");
                    DocxExportService.Export(doc, path, new DocxExportOptions { Scholarly = context, WorkspacePath = ws, IncludeDraftNotes = draft });
                    return DocxText(path);
                }
                Assert.Contains("Target word count: 8,000", WordText(draft: true));
                Assert.DoesNotContain("Target word count", WordText(draft: false));
                string Tex(bool draft) => LatexExportService.Build(doc, context, profile, new LatexExportOptions { WorkspacePath = ws, IncludeDraftNotes = draft }).Source;
                Assert.Contains(@"\fcolorbox{draftframe}{draftfill}", Tex(draft: true));
                Assert.Contains("Target word count: 8,000", Tex(draft: true));
                Assert.DoesNotContain("Target word count", Tex(draft: false));

                // A draft coming back from Google Docs (no markers): the notes are left out like the title page.
                var (drop, _) = TitlePageText.Recognize(
                    [(BlockStyleRole.Title, doc.Blocks[0].Text), (BlockStyleRole.BodyParagraph, "Draft notes"),
                     (BlockStyleRole.BodyParagraph, "Target journal: ISPRS"), (BlockStyleRole.BodyParagraph, "Target word count: 8,000"),
                     (BlockStyleRole.Abstract, doc.Blocks[1].Text)],
                    front, "Keywords", doc);
                Assert.Equal([1, 2, 3], drop.Order());
            }
            finally
            {
                Directory.Delete(ws, recursive: true);
            }
        });

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            yield return child;
            foreach (var d in Descendants(child))
            {
                yield return d;
            }
        }
    }
}
