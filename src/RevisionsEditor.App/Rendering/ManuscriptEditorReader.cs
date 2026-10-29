using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Documents;
using RevisionsEditor.Core.Models;

namespace RevisionsEditor.App.Rendering;

/// <summary>
/// Reads the editor's page back into manuscript blocks. Bold and italic become Markdown emphasis (the form
/// the rest of the app stores), read-only paragraphs with pending changes are returned unchanged, and
/// section-number labels are ignored because numbering is computed.
/// </summary>
public static class ManuscriptEditorReader
{
    private static readonly Regex ParagraphNumberRegex = new(@"^p-(\d+)$", RegexOptions.Compiled);

    public sealed record Result(List<DocumentBlock> Blocks, HashSet<string> LockedParagraphIds);

    /// <param name="reservedIds">Paragraph ids already in use (for example by hunks), never reused for new paragraphs.</param>
    public static Result Read(FlowDocument document, IEnumerable<string> reservedIds)
    {
        var blocks = new List<DocumentBlock>();
        var locked = new HashSet<string>(StringComparer.Ordinal);
        var used = new HashSet<string>(StringComparer.Ordinal);
        var reserved = new HashSet<string>(reservedIds, StringComparer.Ordinal);
        int nextNumber = reserved.Select(id => ParagraphNumberRegex.Match(id)).Where(m => m.Success).Select(m => int.Parse(m.Groups[1].Value)).DefaultIfEmpty(0).Max() + 1;

        string NewId()
        {
            string id;
            do
            {
                id = $"p-{nextNumber++:D3}";
            }
            while (reserved.Contains(id) || used.Contains(id));
            return id;
        }

        EditorBlockTag? previousTag = null;
        Paragraph? previousParagraph = null;

        void Visit(IEnumerable<Block> content)
        {
            foreach (var block in content)
            {
                switch (block)
                {
                    case BlockUIContainer { Tag: EditorBlockTag { LockedBlock: { } original } }:
                        if (used.Add(original.ParagraphId))
                        {
                            blocks.Add(original.Clone());
                            locked.Add(original.ParagraphId);
                        }
                        previousTag = null;
                        previousParagraph = null;
                        break;

                    case Paragraph paragraph:
                        var tag = paragraph.Tag as EditorBlockTag;
                        var role = tag?.Role ?? InferRoleOfSplitParagraph(paragraph, previousParagraph, previousTag);
                        string text = ReadParagraphText(paragraph);
                        // Splitting a paragraph (Enter) copies its tag: the second half gets its own id.
                        string id = tag?.ParagraphId is { Length: > 0 } existing && !used.Contains(existing) ? existing : NewId();
                        if (text.Trim().Length > 0)
                        {
                            used.Add(id);
                            blocks.Add(new DocumentBlock { ParagraphId = id, Role = role, Text = text.Trim() });
                        }
                        previousTag = tag ?? new EditorBlockTag { ParagraphId = id, Role = role };
                        previousParagraph = paragraph;
                        break;

                    case List list:
                        foreach (var item in list.ListItems)
                        {
                            Visit(item.Blocks);
                        }
                        break;

                    case Table table:
                        foreach (var cell in table.RowGroups.SelectMany(g => g.Rows).SelectMany(r => r.Cells))
                        {
                            Visit(cell.Blocks);
                        }
                        break;

                    case Section section:
                        Visit(section.Blocks);
                        break;
                }
            }
        }

        Visit(document.Blocks);
        return new Result(blocks, locked);
    }

    /// <summary>
    /// A paragraph without a tag was created by the editor. If it still has the formatting of the paragraph
    /// before it, it is the second half of a split and keeps that style; otherwise it is body text.
    /// </summary>
    private static BlockStyleRole InferRoleOfSplitParagraph(Paragraph paragraph, Paragraph? previous, EditorBlockTag? previousTag)
    {
        if (previous != null && previousTag != null
            && paragraph.FontSize.Equals(previous.FontSize)
            && paragraph.FontWeight == previous.FontWeight
            && paragraph.FontStyle == previous.FontStyle
            && paragraph.TextAlignment == previous.TextAlignment)
        {
            return previousTag.Role;
        }
        return BlockStyleRole.BodyParagraph;
    }

    /// <summary>
    /// The paragraph's text with bold and italic written as Markdown emphasis, relative to the paragraph's
    /// own style (a heading's text is bold by style, not emphasized).
    /// </summary>
    public static string ReadParagraphText(Paragraph paragraph)
    {
        var segments = new List<(string Text, bool Bold, bool Italic)>();
        bool baseBold = paragraph.FontWeight.ToOpenTypeWeight() >= FontWeights.Bold.ToOpenTypeWeight();
        bool baseItalic = paragraph.FontStyle != FontStyles.Normal;

        void Collect(InlineCollection inlines)
        {
            foreach (var inline in inlines)
            {
                switch (inline)
                {
                    case Run run:
                        bool bold = !baseBold && run.FontWeight.ToOpenTypeWeight() >= FontWeights.Bold.ToOpenTypeWeight();
                        bool italic = !baseItalic && run.FontStyle != FontStyles.Normal;
                        segments.Add((run.Text.Replace('\t', ' ').Replace('\u000B', ' '), bold, italic));
                        break;
                    case LineBreak:
                        segments.Add((" ", false, false));
                        break;
                    case Span span: // Bold, Italic, Underline and Hyperlink are spans
                        Collect(span.Inlines);
                        break;
                    // InlineUIContainer: section-number labels and other non-text content are not part of the text.
                }
            }
        }

        Collect(paragraph.Inlines);

        // Merge neighbouring runs with the same formatting, then mark emphasis around the words only.
        var sb = new StringBuilder();
        int i = 0;
        while (i < segments.Count)
        {
            var (_, bold, italic) = segments[i];
            var merged = new StringBuilder();
            while (i < segments.Count && segments[i].Bold == bold && segments[i].Italic == italic)
            {
                merged.Append(segments[i].Text);
                i++;
            }
            sb.Append(Emphasize(merged.ToString(), bold, italic));
        }
        return sb.ToString();
    }

    private static string Emphasize(string text, bool bold, bool italic)
    {
        string marker = bold && italic ? "***" : bold ? "**" : italic ? "*" : string.Empty;
        string core = text.Trim();
        if (marker.Length == 0 || core.Length == 0)
        {
            return text;
        }
        string leading = text[..(text.Length - text.TrimStart().Length)];
        string trailing = text[text.TrimEnd().Length..];
        return $"{leading}{marker}{core}{marker}{trailing}";
    }
}
