using System.IO;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using RevisionsEditor.Core.Docx;
using RevisionsEditor.Core.Formatting;
using RevisionsEditor.Core.Latex;
using RevisionsEditor.Core.Models;
using RevisionsEditor.Core.Scholarly;
using Xunit;

namespace RevisionsEditor.Tests;

public sealed partial class RevisionsEditorTests
{
    private static FrontMatter SampleFrontMatter() => new()
    {
        ShortTitle = "Tree height from ULS",
        ArticleType = "Research article",
        Authors =
        [
            new FrontMatterAuthor { Name = "Lukas Olson", Affiliations = "2", Email = "author@example.org", Orcid = "0000-0002-1825-0097", Corresponding = true },
            new FrontMatterAuthor { Name = "Nicholas C. Coops", Affiliations = "1,2" }
        ],
        Affiliations =
        [
            new FrontMatterAffiliation { Number = 1, Text = "Integrated Remote Sensing Studio, Faculty of Forestry, University of British Columbia, Vancouver, BC, Canada" },
            new FrontMatterAffiliation { Number = 2, Text = "Department of Geography & Environment, Some University, Canada" }
        ],
        Keywords = ["lidar", "tree height", "leaf-off"]
    };

    /// <summary>Braces balance and every \begin has its \end (outside verbatim text, which the export never writes).</summary>
    private static void AssertWellFormedLatex(string tex)
    {
        string stripped = Regex.Replace(tex, @"\\[{}%$&#_]", string.Empty);
        stripped = Regex.Replace(stripped, @"(?m)(?<!\\)%.*$", string.Empty);
        int depth = 0;
        foreach (char c in stripped)
        {
            depth += c == '{' ? 1 : c == '}' ? -1 : 0;
            Assert.True(depth >= 0, "A closing brace has no opening brace.");
        }
        Assert.Equal(0, depth);
        var stack = new Stack<string>();
        foreach (Match m in Regex.Matches(stripped, @"\\(begin|end)\{([^}]+)\}"))
        {
            if (m.Groups[1].Value == "begin")
            {
                stack.Push(m.Groups[2].Value);
            }
            else
            {
                Assert.Equal(stack.Pop(), m.Groups[2].Value);
            }
        }
        Assert.Empty(stack);
    }

    [Fact]
    public void LatexExport_WritesAJournalArticle_WithTheTitlePageFromTheForm_ForEveryLayout()
    {
        var (ws, doc) = ScholarlyWorkspace();
        try
        {
            SampleFrontMatter().Save(ws);
            doc.Blocks.Insert(2, new DocumentBlock { ParagraphId = "p-030", Role = BlockStyleRole.BodyParagraph, Text = "Keywords: ignored; because the form has keywords" });
            doc.Blocks.Add(new DocumentBlock { ParagraphId = "p-031", Role = BlockStyleRole.BodyParagraph, Text = "Costs were 5% & #1 for {x}_y with ~3 m^2^ and *Pinus* spp." });
            var context = ContextFor(doc, ws, "rse");
            var profile = FormattingRuleProfile.GetBuiltInProfiles()[0];
            // Unicode scripts are missing from the text fonts.
            Assert.Equal(@"m\textsuperscript{−2} and CO\textsubscript{2}, R\textsubscript{12}", LatexExportService.Escape("m⁻² and CO₂, R₁₂"));

            foreach (var template in Enum.GetValues<LatexTemplate>())
            {
                var latex = LatexExportService.Build(doc, context, profile, new LatexExportOptions { Template = template, WorkspacePath = ws, JournalName = "Remote Sensing of Environment" });
                string tex = latex.Source;
                AssertWellFormedLatex(tex);

                Assert.Contains(@"\usepackage[protrusion=true,final]{microtype}", tex);
                Assert.Contains("Lukas Olson", tex);
                Assert.Contains(@"Department of Geography \& Environment", tex);
                Assert.Contains("lidar", tex);
                Assert.DoesNotContain("ignored", tex); // the manuscript's keywords paragraph gives way to the form's
                Assert.Contains(@"\hyperlink{cite.white2016remote}{(White et al., 2016; Næsset, 2002)}", tex);
                Assert.Contains(@"\hyperref[fig:site]{Fig.~1}", tex);
                Assert.Contains(@"\label{fig:site}", tex);
                // width=50% is half the text width: a column in two-column layouts.
                Assert.Contains(template == LatexTemplate.ReviewManuscript ? @"\includegraphics[width=0.5\linewidth" : @"\includegraphics[width=\columnwidth", tex);
                Assert.Contains(@"\begin{equation}", tex);
                Assert.Contains(@"\label{eq:rmse}", tex);
                Assert.Contains(@"\toprule", tex);
                // Decimals aligned on the point by siunitx, which prints the minus sign.
                Assert.Contains(@"\begin{tabular}{lS[table-format=1.2]S[table-format=-1.2]}", tex);
                Assert.Contains("Grimston & 0.42 & -0.05 \\\\", tex);
                Assert.Contains(@"{\textbf{RMSE (m)}}", tex);
                Assert.Contains(@"\textsuperscript{2}", tex);
                // A thin space that does not break between a number and its unit.
                Assert.Contains(@"Costs were 5\% \& \#1 for \{x\}\_y with \textasciitilde{}3\,m\textsuperscript{2} and \emph{Pinus} spp.", tex);
                Assert.Contains(@"\setmainfont{texgyretermes}", tex);
                Assert.Contains("Kerning=On", tex);
                Assert.Contains(@"\usepackage{realscripts}", tex);
                Assert.Contains(@"\frenchspacing", tex);
                Assert.DoesNotContain("Draft notes", tex); // only when asked for
                Assert.Contains(@"0.42", tex); // linked number
                Assert.Contains(@"\begin{reflist}{References}", tex);
                Assert.Contains(@"\hypertarget{cite.white2016remote}", tex);
                Assert.Single(Regex.Matches(tex, @"\\begin\{reflist\}"));
                Assert.Contains(("figures/site.png"), latex.Files.Select(f => f.RelativePath));

                string expectedClass = template switch
                {
                    LatexTemplate.Elsevier => "{elsarticle}",
                    LatexTemplate.Ieee => "{IEEEtran}",
                    _ => "{article}"
                };
                Assert.Contains(expectedClass, tex);
                if (template == LatexTemplate.Elsevier)
                {
                    Assert.Contains(@"\journal{Remote Sensing of Environment}", tex);
                    Assert.Contains(@"\author[1]{Lukas Olson", tex); // affiliations renumbered in order of first use
                    Assert.Contains(@"\begin{keyword}", tex);
                }
                if (template == LatexTemplate.ReviewManuscript)
                {
                    Assert.Contains(@"\linenumbers", tex);
                    Assert.Contains(@"\doublespacing", tex);
                }
            }

            string folder = Path.Combine(ws, "latex-out");
            var written = LatexExportService.Build(doc, context, profile, new LatexExportOptions { WorkspacePath = ws });
            string path = LatexExportService.WriteTo(written, folder);
            Assert.True(File.Exists(path));
            Assert.True(File.Exists(Path.Combine(folder, "figures", "site.png")));
        }
        finally
        {
            Directory.Delete(ws, recursive: true);
        }
    }

    [Fact]
    public void PreviewAndEditor_ShowTheTitlePageFromTheForm_WhichIsNotReadBackAsManuscriptText()
        => RunOnStaThread(() =>
        {
            var (ws, doc) = ScholarlyWorkspace();
            try
            {
                SampleFrontMatter().Save(ws);
                var context = ContextFor(doc, ws, "rse");
                var profile = FormattingRuleProfile.GetBuiltInProfiles()[0];

                foreach (bool preview in new[] { true, false })
                {
                    var page = RevisionsEditor.App.Rendering.FlowDocumentBuilder.BuildManuscriptEditorDocument(
                        doc, profile, MarkupDisplayMode.AllMarkup, scholarly: context, workspacePath: ws, preview: preview);
                    string shown = string.Join("\n", page.Blocks.OfType<System.Windows.Documents.BlockUIContainer>()
                        .Select(c => c.Child)
                        .SelectMany(e => e is System.Windows.Controls.Panel p ? p.Children.OfType<System.Windows.Controls.TextBlock>() : e is System.Windows.Controls.TextBlock t ? [t] : [])
                        .Select(t => new System.Windows.Documents.TextRange(t.ContentStart, t.ContentEnd).Text));
                    Assert.Contains("Lukas Olson1,*, Nicholas C. Coops2,1", shown);
                    Assert.Contains("1 Department of Geography & Environment", shown);
                    Assert.Contains("* Corresponding author: Lukas Olson, author@example.org", shown);
                    Assert.Contains("Keywords: lidar; tree height; leaf-off", shown); // after the abstract: the manuscript has none

                    if (!preview)
                    {
                        var read = RevisionsEditor.App.Rendering.ManuscriptEditorReader.Read(page, []);
                        Assert.DoesNotContain(read.Blocks, b => b.Text.Contains("Lukas Olson") || b.Text.Contains("lidar"));
                    }
                }
            }
            finally
            {
                Directory.Delete(ws, recursive: true);
            }
        });

    [Fact]
    public void WordExport_LaysOutTheTitlePageFromTheForm_AndAReviewedCopyImportsWithoutIt()
    {
        var (ws, doc) = ScholarlyWorkspace();
        try
        {
            SampleFrontMatter().Save(ws);
            var front = FrontMatter.Load(ws);
            // Affiliations are numbered in the order the authors first cite them: the first author's comes first.
            Assert.Equal([1], front.PrintedAffiliationNumbers(front.Authors[0]));
            Assert.Equal([2, 1], front.PrintedAffiliationNumbers(front.Authors[1]));
            Assert.Equal("Department of Geography & Environment, Some University, Canada", front.AffiliationsInOrder()[0].Affiliation.Text);

            var context = ContextFor(doc, ws, "rse");
            using var ms = new MemoryStream();
            DocxExportService.Export(doc, ms, new DocxExportOptions { Scholarly = context, WorkspacePath = ws });
            ms.Position = 0;
            string path = Path.Combine(ws, "out.docx");
            File.WriteAllBytes(path, ms.ToArray());
            string text = DocxText(path);
            Assert.Contains("Lukas Olson1,*, Nicholas C. Coops2,1", text);
            Assert.Contains("1 Department of Geography & Environment", text);
            Assert.Contains("* Corresponding author: Lukas Olson, author@example.org; ORCID 0000-0002-1825-0097", text);
            Assert.Contains("Keywords: lidar; tree height; leaf-off", text);

            var imported = DocxIngestionService.IngestDocx(path, doc);
            Assert.Equal(doc.Blocks.Select(b => b.ParagraphId), imported.Blocks.Select(b => b.ParagraphId));
            Assert.DoesNotContain(imported.Blocks, b => b.Text.Contains("Lukas Olson") || b.Text.StartsWith("Keywords", StringComparison.Ordinal));
            Assert.Empty(imported.Hunks);

            // Saved by Google Docs or LibreOffice: no content controls, bookmarks or document variables are left, and
            // a co-author corrected an affiliation there. The title page is recognised by its text instead.
            string stripped = Path.Combine(ws, "stripped.docx");
            File.WriteAllBytes(stripped, WithoutExportMarkers(File.ReadAllBytes(path), ("Some University", "Another University")));
            Assert.Contains("Lukas Olson1,*, Nicholas C. Coops2,1", DocxText(stripped));
            var unfiltered = DocxIngestionService.IngestDocx(stripped, doc);
            Assert.Contains(unfiltered.Blocks, b => b.Text.Contains("Lukas Olson")); // what happened before

            var filter = TitlePageFilter.For(context.Project, doc)!;
            var recognised = DocxIngestionService.IngestDocx(stripped, doc, titlePage: filter);
            Assert.DoesNotContain(recognised.Blocks, b => b.Text.Contains("Lukas Olson") || b.Text.Contains("Another University") || b.Text.Contains("lidar"));
            // The manuscript's paragraphs keep their ids. (Without the markers, rendered numbers and citations read
            // as edits, as in any copy saved by Google Docs.)
            Assert.Equal(doc.Blocks.Take(4).Select(b => b.ParagraphId), recognised.Blocks.Take(4).Select(b => b.ParagraphId));
            Assert.Equal(doc.Blocks[1].Role, recognised.Blocks[1].Role);
            Assert.StartsWith("Heights had an RMSE", recognised.Blocks[1].Text);
            Assert.Equal(5, filter.DroppedCount); // authors, 2 affiliations, corresponding author, keywords
            Assert.Contains("Another University", Assert.Single(filter.EditedLines));
            Assert.Contains("Paper panel", filter.Summary);

            // A manuscript that has its own author line keeps it.
            var own = doc.Clone();
            own.Blocks.Insert(1, new DocumentBlock { ParagraphId = "p-050", Role = BlockStyleRole.BodyParagraph, Text = "Lukas Olson^1,*^, Nicholas C. Coops^2,1^" });
            var (drop, _) = TitlePageText.Recognize(
                own.Blocks.Select(b => (b.Role, b.Text)).ToList(), front, "Keywords", own);
            Assert.Empty(drop);
        }
        finally
        {
            Directory.Delete(ws, recursive: true);
        }
    }

    /// <summary>A copy as Google Docs saves it: content controls unwrapped, bookmarks and document variables gone.</summary>
    private static byte[] WithoutExportMarkers(byte[] docx, (string From, string To) edit)
    {
        var w = System.Xml.Linq.XNamespace.Get("http://schemas.openxmlformats.org/wordprocessingml/2006/main");
        using var ms = new MemoryStream();
        ms.Write(docx);
        using (var zip = new System.IO.Compression.ZipArchive(ms, System.IO.Compression.ZipArchiveMode.Update, leaveOpen: true))
        {
            foreach (string part in new[] { "word/document.xml", "word/settings.xml" })
            {
                var entry = zip.GetEntry(part)!;
                System.Xml.Linq.XDocument xml;
                using (var s = entry.Open())
                {
                    xml = System.Xml.Linq.XDocument.Load(s);
                }
                foreach (var sdt in xml.Descendants(w + "sdt").Reverse().ToList())
                {
                    sdt.ReplaceWith(sdt.Element(w + "sdtContent")!.Nodes());
                }
                xml.Descendants().Where(e => e.Name == w + "bookmarkStart" || e.Name == w + "bookmarkEnd" || e.Name == w + "docVars").Remove();
                foreach (var t in xml.Descendants(w + "t"))
                {
                    t.Value = t.Value.Replace(edit.From, edit.To);
                }
                entry.Delete();
                using var output = zip.CreateEntry(part).Open();
                xml.Save(output);
            }
        }
        return ms.ToArray();
    }

    [Fact]
    public async Task GoogleDocsPull_LeavesOutTheTitlePageFromTheForm()
    {
        int port = 18000 + Random.Shared.Next(4000, 6000);
        string ws = Path.Combine(Path.GetTempPath(), $"RevEditorTest_PullTitle_{Guid.NewGuid():N}");
        var repo = new RevisionsEditor.Core.Git.GitRepositoryService(ws);
        repo.EnsureInitialized();
        try
        {
            SampleFrontMatter().Save(ws);
            repo.CommitSnapshot(new ManuscriptDocument
            {
                Blocks =
                [
                    new DocumentBlock { ParagraphId = "p-001", Role = BlockStyleRole.Title, Text = "Paper" },
                    new DocumentBlock { ParagraphId = "p-002", Role = BlockStyleRole.BodyParagraph, Text = "An abstract." },
                    new DocumentBlock { ParagraphId = "p-003", Role = BlockStyleRole.BodyParagraph, Text = "Body text." }
                ]
            }, "Start");

            using var bridge = new RevisionsEditor.Core.Bridge.LocalBridgeServer(repo, port);
            if (!bridge.TryStart())
            {
                return; // Port unavailable on this machine; nothing to verify.
            }
            var log = new List<string>();
            bridge.BridgeActivityLogged += log.Add;

            // A Word export uploaded to Google Docs, as the extension reads it: superscripts are plain text there.
            string[] pulled =
            [
                "Paper",
                "Lukas Olson1,*, Nicholas C. Coops2,1",
                "1 Department of Geography & Environment, Some University, Canada",
                "2 Integrated Remote Sensing Studio, Faculty of Forestry, University of British Columbia, Vancouver, BC, Canada",
                "* Corresponding author: Lukas Olson, author@example.org; ORCID 0000-0002-1825-0097",
                "An abstract.",
                "**Keywords:** lidar; tree height; leaf-off",
                "Body text, edited in Google Docs."
            ];
            using var http = new HttpClient();
            var request = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{port}/api/google-docs/pull")
            {
                Content = new StringContent(System.Text.Json.JsonSerializer.Serialize(new
                {
                    googleDocId = "DOC1",
                    title = "Paper",
                    blocks = pulled.Select((t, i) => new { role = i == 0 ? "Title" : "BodyParagraph", text = t }).ToArray()
                }), System.Text.Encoding.UTF8, "application/json")
            };
            request.Headers.Add("Origin", "chrome-extension://abcdefghijklmnop");
            var response = await http.SendAsync(request);
            Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());

            var merged = repo.LoadCurrentWorkingDocument();
            Assert.Equal(["Paper", "An abstract.", "Body text, edited in Google Docs."], merged.Blocks.Select(b => b.Text));
            Assert.Contains(log, l => l.Contains("Left out the title page (5 paragraphs)"));
            // The snapshot the patches are computed from leaves it out too, so nothing is sent back to delete it.
            bridge.RefreshPendingPatches();
            Assert.Empty(bridge.GetPendingPatches());
        }
        finally
        {
            TestWorkspaceBuilder.DeleteDirectory(ws);
        }
    }
}
