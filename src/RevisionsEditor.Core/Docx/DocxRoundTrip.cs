using System.IO.Compression;
using System.Text.Json;
using System.Xml.Linq;
using RevisionsEditor.Core.Models;

namespace RevisionsEditor.Core.Docx;

/// <summary>What the export recorded about a paragraph, read back from its hidden bookmark.</summary>
internal sealed record BlockMarker(bool Skip, string Prefix, string Suffix, BlockStyleRole? Role, bool HasText);

/// <summary>
/// Reads the markers <see cref="DocxExportService"/> leaves in a document so a copy reviewed in Word imports
/// back to the source it was made from: rendered citations, numbers and inline equations are locked content
/// controls whose source is a document variable; generated text (caption labels, the reference list, table
/// cells) is skipped; figure, table and equation paragraphs carry their syntax in a hidden bookmark.
/// </summary>
internal static class DocxRoundTrip
{
    private static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";

    public static Dictionary<string, string> ReadDocumentVariables(ZipArchive archive)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var entry = archive.GetEntry("word/settings.xml");
        if (entry == null)
        {
            return result;
        }
        using var stream = entry.Open();
        var xml = XDocument.Load(stream);
        foreach (var v in xml.Descendants(W + "docVar"))
        {
            string? name = v.Attribute(W + "name")?.Value;
            if (name != null)
            {
                result[name] = v.Attribute(W + "val")?.Value ?? string.Empty;
            }
        }
        return result;
    }

    private static string? TagOf(XElement sdt) => sdt.Element(W + "sdtPr")?.Element(W + "tag")?.Attribute(W + "val")?.Value;

    /// <summary>True for paragraphs inside generated content (the reference list, table cells).</summary>
    public static bool IsInsideSkippedContent(XElement paragraph)
        => paragraph.Ancestors(W + "sdt").Any(sdt => TagOf(sdt) == DocxExportService.SkipTag);

    public static BlockMarker? BlockMarkerOf(XElement paragraph, IReadOnlyDictionary<string, string> variables)
    {
        foreach (var bookmark in paragraph.Descendants(W + "bookmarkStart"))
        {
            string name = bookmark.Attribute(W + "name")?.Value ?? string.Empty;
            if (!name.StartsWith(DocxExportService.BlockBookmarkPrefix, StringComparison.Ordinal)
                || !variables.TryGetValue("reB" + name[DocxExportService.BlockBookmarkPrefix.Length..], out var json))
            {
                continue;
            }
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (root.TryGetProperty("k", out var k) && k.GetString() == "skip")
                {
                    return new BlockMarker(true, string.Empty, string.Empty, null, false);
                }
                BlockStyleRole? role = root.TryGetProperty("r", out var r) && Enum.TryParse<BlockStyleRole>(r.GetString(), out var parsed) ? parsed : null;
                return new BlockMarker(
                    false,
                    root.TryGetProperty("p", out var p) ? p.GetString() ?? string.Empty : string.Empty,
                    root.TryGetProperty("s", out var s) ? s.GetString() ?? string.Empty : string.Empty,
                    role,
                    !root.TryGetProperty("t", out _));
            }
            catch (JsonException)
            {
                return null;
            }
        }
        return null;
    }

    /// <summary>Restores the paragraph's syntax around the (possibly edited) caption text, and its style.</summary>
    public static void ApplyBlockMarker(DocumentBlock block, List<RevisionHunk> hunks, BlockMarker marker)
    {
        if (!marker.HasText)
        {
            // An equation: Word cannot carry LaTeX edits, so the source is kept as exported.
            block.Text = marker.Prefix;
            hunks.RemoveAll(h => h.HunkType != RevisionHunkType.Comment);
        }
        else
        {
            block.Text = marker.Prefix + block.Text + marker.Suffix;
        }
        if (marker.Role is { } role)
        {
            block.Role = role;
        }
    }

    /// <summary>
    /// For a content control the export made: <paramref name="replacement"/> is a run with the source text it was
    /// rendered from (or null for a generated label). Returns false for other content controls, and for rendered
    /// text a reviewer changed with tracked changes, which is then read as written.
    /// </summary>
    public static bool ReplacementForContentControl(XElement sdt, IReadOnlyDictionary<string, string> variables, out XElement? replacement)
    {
        replacement = null;
        string? tag = TagOf(sdt);
        if (tag == DocxExportService.LabelTag)
        {
            return true;
        }
        if (tag == null || !tag.StartsWith(DocxExportService.InlineTagPrefix, StringComparison.Ordinal)
            || !variables.TryGetValue("reI" + tag[DocxExportService.InlineTagPrefix.Length..], out var source))
        {
            return false;
        }
        var content = sdt.Element(W + "sdtContent");
        if (content != null && content.Descendants().Any(e => e.Name == W + "ins" || e.Name == W + "del"))
        {
            return false;
        }
        // An editable one (a placeholder) typed over without tracking changes: read what is there now.
        if (variables.TryGetValue("reT" + tag[DocxExportService.InlineTagPrefix.Length..], out var written)
            && string.Concat(content?.Descendants(W + "t").Select(t => t.Value) ?? []) != written)
        {
            return false;
        }
        replacement = new XElement(W + "r", new XElement(W + "t", new XAttribute(XNamespace.Xml + "space", "preserve"), source));
        return true;
    }
}
