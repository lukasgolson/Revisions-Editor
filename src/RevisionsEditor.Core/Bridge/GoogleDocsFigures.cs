using System.Security.Cryptography;
using RevisionsEditor.Core.Diffing;
using RevisionsEditor.Core.Models;
using RevisionsEditor.Core.Scholarly;

namespace RevisionsEditor.Core.Bridge;

/// <summary>
/// Figures sent to Google Docs with the text changes: a figure whose file changed since Google Docs last showed it
/// (a diagram edited, a plot saved again) replaces its image there, and a figure Google Docs does not have is
/// inserted after the text it follows, with its "Figure N." caption.
/// <para>
/// Each figure of the Google Docs snapshot carries a fingerprint of the file its image shows (shown=…), taken when
/// the document is read; a changed fingerprint is what makes an image out of date.
/// </para>
/// </summary>
public static class GoogleDocsFigures
{
    public const string ShownAttribute = "shown";

    /// <summary>Records in a pulled document which version of each figure's file Google Docs shows.</summary>
    public static void MarkShown(ManuscriptDocument pulled, string workspace)
    {
        foreach (var block in pulled.Blocks.Where(b => b.Role == BlockStyleRole.Figure))
        {
            if (ScholarlyBlocks.ParseFigure(block.Text) is { } figure && Fingerprint(workspace, figure.ImagePath) is { } print)
            {
                var attributes = new Dictionary<string, string>(figure.Attributes, StringComparer.OrdinalIgnoreCase) { [ShownAttribute] = print };
                block.Text = ScholarlyBlocks.FormatFigure(figure.Caption, figure.ImagePath, figure.Label, attributes);
            }
        }
    }

    /// <summary>A snapshot as merges compare it: without the fingerprints, which only the image patches use.</summary>
    public static ManuscriptDocument WithoutShown(ManuscriptDocument snapshot)
    {
        var copy = snapshot.Clone();
        foreach (var block in copy.Blocks.Where(b => b.Role == BlockStyleRole.Figure))
        {
            if (ScholarlyBlocks.ParseFigure(block.Text) is { } figure && figure.Attributes.ContainsKey(ShownAttribute))
            {
                var attributes = figure.Attributes.Where(a => !a.Key.Equals(ShownAttribute, StringComparison.OrdinalIgnoreCase)).ToDictionary(a => a.Key, a => a.Value);
                block.Text = ScholarlyBlocks.FormatFigure(figure.Caption, figure.ImagePath, figure.Label, attributes);
            }
        }
        return copy;
    }

    /// <summary>A short fingerprint of a file's content, or null when it is missing.</summary>
    public static string? Fingerprint(string workspace, string relative)
    {
        string full = Path.Combine(workspace, relative.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(full)) return null;
        using var stream = File.OpenRead(full);
        return Convert.ToHexString(SHA256.HashData(stream))[..16].ToLowerInvariant();
    }

    /// <summary>The image patches that bring Google Docs' figures up to date with the manuscript's.</summary>
    public static List<GoogleDocPatch> Patches(ManuscriptDocument googleDocs, ManuscriptDocument manuscript, string workspace)
    {
        var patches = new List<GoogleDocPatch>();
        var shown = googleDocs.Blocks.Where(b => b.Role == BlockStyleRole.Figure)
            .Select(b => ScholarlyBlocks.ParseFigure(b.Text))
            .ToList();
        var docsText = googleDocs.Blocks.Where(b => b.Role is not (BlockStyleRole.Figure or BlockStyleRole.Table or BlockStyleRole.Equation))
            .Select(b => DiffEngineService.ToGoogleDocsText(b.Text))
            .ToList();
        var docsKeys = docsText.Select(DiffEngineService.GoogleDocsMatchKey).ToHashSet(StringComparer.Ordinal);
        var used = new HashSet<int>();
        int number = 0;

        for (int i = 0; i < manuscript.Blocks.Count; i++)
        {
            var block = manuscript.Blocks[i];
            if (block.Role != BlockStyleRole.Figure) continue;
            number++;
            var figure = ScholarlyBlocks.ParseFigure(Serialization.CriticMarkupSerializer.ResolveCriticMarkupText(block.Text, MarkupDisplayMode.NoMarkupFinal));
            if (figure == null || Fingerprint(workspace, figure.ImagePath) is not { } print) continue;

            // The image in Google Docs that stands for this figure: by label, else by file.
            int ordinal = -1;
            for (int k = 0; k < shown.Count && ordinal < 0; k++)
            {
                if (shown[k] is { } s && !used.Contains(k)
                    && ((figure.Label != null && s.Label == figure.Label) || string.Equals(s.ImagePath, figure.ImagePath, StringComparison.OrdinalIgnoreCase)))
                {
                    ordinal = k;
                }
            }
            string caption = $"Figure {number}. {DiffEngineService.ToGoogleDocsText(figure.Caption)}".Trim();
            string section = SectionOf(manuscript.Blocks, i, manuscript.Title);
            if (ordinal >= 0)
            {
                used.Add(ordinal);
                var docs = shown[ordinal]!;
                bool sameFile = string.Equals(docs.ImagePath, figure.ImagePath, StringComparison.OrdinalIgnoreCase);
                // Snapshots taken before fingerprints were kept: an unchanged file name counts as up to date.
                string? docsPrint = docs.Attributes.GetValueOrDefault(ShownAttribute);
                if (sameFile && (docsPrint == null || docsPrint == print)) continue;
                patches.Add(Patch(GoogleDocPatchKind.ReplaceImage, block, figure, print, caption, section, workspace, ordinal, string.Empty));
            }
            else
            {
                // After the nearest text before it that Google Docs has.
                string anchor = string.Empty;
                for (int k = i - 1; k >= 0; k--)
                {
                    var before = manuscript.Blocks[k];
                    if (before.Role is BlockStyleRole.Figure or BlockStyleRole.Table or BlockStyleRole.Equation) continue;
                    string text = DiffEngineService.ToGoogleDocsText(before.Text);
                    if (docsKeys.Contains(DiffEngineService.GoogleDocsMatchKey(text)))
                    {
                        anchor = docsText.First(t => DiffEngineService.GoogleDocsMatchKey(t) == DiffEngineService.GoogleDocsMatchKey(text));
                        break;
                    }
                }
                patches.Add(Patch(GoogleDocPatchKind.InsertImage, block, figure, print, caption, section, workspace, -1, anchor));
            }
        }
        return patches;
    }

    private static GoogleDocPatch Patch(GoogleDocPatchKind kind, DocumentBlock block, FigureBlock figure, string print, string caption, string section, string workspace, int ordinal, string anchor)
    {
        var size = FigureImages.NaturalSizePoints(Path.Combine(workspace, figure.ImagePath.Replace('/', Path.DirectorySeparatorChar)));
        // At most the text width of a page (6.5 in), at the share of it the figure asks for.
        double width = Math.Min(468 * (figure.WidthFraction ?? 1.0), size?.Width ?? 468);
        return new GoogleDocPatch
        {
            PatchId = $"patch-img-{print}-{(kind == GoogleDocPatchKind.ReplaceImage ? "r" + ordinal : "i")}",
            Kind = kind,
            ParagraphId = block.ParagraphId,
            Role = BlockStyleRole.Figure,
            SectionHeading = section,
            AfterText = caption,
            AnchorText = anchor,
            ImageOrdinal = ordinal,
            ImagePath = figure.ImagePath,
            ImageWidthPt = Math.Round(Math.Max(72, width), 1),
            ChangeSummary = kind == GoogleDocPatchKind.ReplaceImage ? "Updated figure image" : "New figure"
        };
    }

    private static string SectionOf(IReadOnlyList<DocumentBlock> blocks, int index, string title)
    {
        for (int k = index; k >= 0; k--)
        {
            if (blocks[k].Role is BlockStyleRole.Heading1 or BlockStyleRole.Heading2 or BlockStyleRole.Heading3)
            {
                return DiffEngineService.ToGoogleDocsText(blocks[k].Text);
            }
        }
        return title;
    }

    /// <summary>A figure's image as Google Docs takes it: PNG or JPEG as it is, anything else (SVG, PDF, TIFF…) drawn as PNG.</summary>
    public static (byte[] Bytes, string ContentType)? ImageFor(string workspace, string relative)
    {
        string full = Path.Combine(workspace, relative.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(full)) return null;
        string extension = Path.GetExtension(full).ToLowerInvariant();
        if (extension is ".png") return (File.ReadAllBytes(full), "image/png");
        if (extension is ".jpg" or ".jpeg") return (File.ReadAllBytes(full), "image/jpeg");
        return FigureImages.RenderPng(full, dpi: 200, maxPixels: 3000) is { } png ? (png, "image/png") : null;
    }
}
