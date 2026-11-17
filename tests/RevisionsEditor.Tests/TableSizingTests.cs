using System.IO;
using RevisionsEditor.Core.Formatting;
using RevisionsEditor.Core.Latex;
using RevisionsEditor.Core.Models;
using RevisionsEditor.Core.Scholarly;
using Xunit;

namespace RevisionsEditor.Tests;

public sealed partial class RevisionsEditorTests
{
    private static readonly string[][] NarrowTable =
    [
        ["Site", "RMSE (m)", "Bias (m)"],
        ["Grimston", "0.42", "-0.05"],
        ["Kwantlen", "0.51", "0.03"]
    ];

    private static readonly string[][] ManyColumnsTable =
    [
        ["Configuration", "AUC@5°", "AUC@10°", "AUC@20°", "Matches", "Inliers", "Runtime (ms)"],
        ["DeDoDe V2 + LightGlue", "41.2", "57.9", "71.3", "1843", "1021", "88.4"],
        ["+ DINOv3 adaptor", "44.7", "61.0", "74.2", "1910", "1102", "97.1"]
    ];

    private static readonly string[][] TextTable =
    [
        ["Dataset", "Environment", "Description of the imagery and how it was collected", "Images"],
        ["Tanks and Temples", "Built and natural", "Handheld video of large outdoor scenes, statues, vehicles and buildings, with ground-truth laser scans for the training scenes", "2060"],
        ["ETH3D", "Built", "High-resolution DSLR images of indoor and outdoor scenes, with laser-scanned geometry for every scene", "454"],
        ["Intra-canopy flights", "Hardwood forest", "Drone video flown under the canopy of a northern hardwood stand across four treatment areas in Centre-du-Québec", "3120"]
    ];

    private static bool[] NumericColumns(string[][] rows)
        => Enumerable.Range(0, rows[0].Length).Select(c => c > 0 && rows.Skip(1).All(r => double.TryParse(r[c].Replace("-", ""), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _))).ToArray();

    [Fact]
    public void TableSizing_FitsAColumn_SpansTheTwoColumns_OrWrapsText_ByContent()
    {
        var narrow = TableSizing.Plan(NarrowTable, NumericColumns(NarrowTable), LatexTemplate.JournalTwoColumn, twoColumn: true);
        Assert.False(narrow.Wide);
        Assert.False(narrow.Wraps);

        // Seven columns of numbers: too wide for a column, fine across the page, and numbers never wrap.
        var many = TableSizing.Plan(ManyColumnsTable, NumericColumns(ManyColumnsTable), LatexTemplate.JournalTwoColumn, twoColumn: true);
        Assert.True(many.Wide);
        Assert.False(many.Wraps);

        // Long descriptions: across the page, the description column wraps and takes most of the space.
        var text = TableSizing.Plan(TextTable, NumericColumns(TextTable), LatexTemplate.JournalTwoColumn, twoColumn: true);
        Assert.True(text.Wide);
        Assert.True(text.Wraps);
        Assert.Null(text.WrapShare[3]);
        Assert.True(text.WrapShare[2] > 1);
        Assert.Equal(text.WrapShare.Count(s => s != null), text.WrapShare.Where(s => s != null).Sum()!.Value, 2);

        // One column of a review copy: never "wide", the text wraps within the page.
        var review = TableSizing.Plan(TextTable, NumericColumns(TextTable), LatexTemplate.ReviewManuscript, twoColumn: false);
        Assert.False(review.Wide);
        Assert.True(review.Wraps);

        // span= decides when given.
        Assert.False(TableSizing.Plan(ManyColumnsTable, NumericColumns(ManyColumnsTable), LatexTemplate.JournalTwoColumn, true, "column").Wide);
        Assert.True(TableSizing.Plan(NarrowTable, NumericColumns(NarrowTable), LatexTemplate.JournalTwoColumn, true, "full").Wide);
    }

    [Fact]
    public async Task LatexExport_SizesTablesByContent_AndTypesets()
    {
        var (ws, doc) = ScholarlyWorkspace();
        try
        {
            int n = 0;
            foreach (var (name, rows) in new[] { ("narrow", NarrowTable), ("many", ManyColumnsTable), ("text", TextTable) })
            {
                File.WriteAllText(Path.Combine(ws, "tables", $"{name}.csv"), string.Join("\n", rows.Select(r => string.Join(",", r.Select(c => $"\"{c}\"")))) + "\n");
                doc.Blocks.Add(new DocumentBlock
                {
                    ParagraphId = $"p-1{n++:D2}",
                    Role = BlockStyleRole.Table,
                    Text = ScholarlyBlocks.FormatTable($"The {name} table.", $"tbl:{name}", $"tables/{name}.csv")
                });
            }
            var context = ContextFor(doc, ws, "rse");
            var profile = FormattingRuleProfile.GetBuiltInProfiles()[0];
            var latex = LatexExportService.Build(doc, context, profile, new LatexExportOptions { WorkspacePath = ws });
            string tex = latex.Source;
            AssertWellFormedLatex(tex);

            string Env(string label)
            {
                int at = tex.IndexOf($@"\label{{tbl:{label}}}", StringComparison.Ordinal);
                int begin = tex.LastIndexOf(@"\begin{table", at, StringComparison.Ordinal);
                return tex.Substring(begin, tex.IndexOf(@"\end{table", at, StringComparison.Ordinal) - begin);
            }
            Assert.StartsWith(@"\begin{table}", Env("narrow"));
            Assert.StartsWith(@"\begin{table*}", Env("many"));
            Assert.Contains(@"\begin{tabular}", Env("many"));
            Assert.StartsWith(@"\begin{table*}", Env("text"));
            Assert.Contains(@"\begin{tabularx}{\textwidth}", Env("text"));
            Assert.Contains(@"\raggedright\arraybackslash\hsize=", Env("text"));

            if (LatexCompiler.FindEngine() is { } engine)
            {
                string path = LatexExportService.WriteTo(latex, Path.Combine(ws, "latex-out"));
                var compiled = await LatexCompiler.CompileAsync(path, engine, TimeSpan.FromMinutes(8));
                Assert.True(compiled.Success, string.Join("\n", compiled.Errors));
                if (Environment.GetEnvironmentVariable("DIAGRAM_OUT") is { } keep && compiled.PdfPath != null)
                {
                    File.Copy(compiled.PdfPath, Path.Combine(keep, "latex-tables.pdf"), overwrite: true);
                }
            }
        }
        finally
        {
            Directory.Delete(ws, recursive: true);
        }
    }
}
