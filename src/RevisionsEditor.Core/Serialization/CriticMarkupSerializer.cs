using System.Text;
using System.Text.RegularExpressions;
using RevisionsEditor.Core.Models;

namespace RevisionsEditor.Core.Serialization;

public static partial class CriticMarkupSerializer
{
    // Matches CriticMarkup tokens in order:
    // 1. Replacement: {~~old~>new~~}
    // 2. Insertion:   {++ins++}
    // 3. Deletion:    {--del--}
    // 4. Highlight+Comment: {==anchor==}{>>author: comment<<} or standalone {>>comment<<}
    private static readonly Regex CriticTokenRegex = new(
        @"(\{~~(?<repOld>.*?)~>(?<repNew>.*?)~~\})|(\{\+\+(?<ins>.*?)\+\+\})|(\{--(?<del>.*?)--\})|(\{==(?<hl>.*?)==\}\{>>(?<hlComment>.*?)<<\})|(\{>>(?<comment>.*?)<<\})",
        RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex ParagraphSeparatorRegex = new(@"\n\s*\n", RegexOptions.Compiled);

    private static readonly Regex AnchorTagRegex = new(
        @"^<!--\s*id:(?<id>[a-zA-Z0-9_\-]+)\s*(?:role:(?<role>[a-zA-Z0-9]+))?\s*-->\s*",
        RegexOptions.Compiled);

    /// <summary>
    /// Serializes a ManuscriptDocument into canonical Markdown with CriticMarkup for any unresolved hunks.
    /// </summary>
    public static string ToMarkdown(ManuscriptDocument document, bool includeAnchorComments = false)
    {
        var sb = new StringBuilder();
        var hunksByParagraph = GroupHunksByParagraph(document.Hunks);

        foreach (var block in document.Blocks)
        {
            if (includeAnchorComments)
            {
                sb.AppendLine($"<!-- id:{block.ParagraphId} role:{block.Role} -->");
            }

            string prefix = MarkdownPrefix(block.Role);

            string renderedBlockText = RenderBlockWithHunks(block, HunksFor(hunksByParagraph, block.ParagraphId));
            sb.AppendLine(prefix + renderedBlockText);
            sb.AppendLine();
        }

        return sb.ToString().TrimEnd() + Environment.NewLine;
    }

    /// <summary>Figures, tables and equations have no prefix: their own syntax identifies them.</summary>
    private static string MarkdownPrefix(BlockStyleRole role) => role switch
    {
        BlockStyleRole.Title => "# ",
        BlockStyleRole.Abstract => "## Abstract: ",
        BlockStyleRole.Heading1 => "# ",
        BlockStyleRole.Heading2 => "## ",
        BlockStyleRole.Heading3 => "### ",
        BlockStyleRole.BlockQuote => "> ",
        BlockStyleRole.Caption => "*Caption:* ",
        BlockStyleRole.ReferenceItem => "- [Ref] ",
        _ => string.Empty
    };

    /// <summary>
    /// Groups hunks by paragraph once (preserving document order) so per-block lookups are O(1) instead of a full scan.
    /// </summary>
    public static Dictionary<string, List<RevisionHunk>> GroupHunksByParagraph(IEnumerable<RevisionHunk> hunks)
    {
        var result = new Dictionary<string, List<RevisionHunk>>(StringComparer.Ordinal);
        foreach (var hunk in hunks)
        {
            if (!result.TryGetValue(hunk.ParagraphId, out var list))
            {
                list = [];
                result[hunk.ParagraphId] = list;
            }
            list.Add(hunk);
        }
        return result;
    }

    public static IReadOnlyList<RevisionHunk> HunksFor(Dictionary<string, List<RevisionHunk>> hunksByParagraph, string paragraphId)
        => hunksByParagraph.TryGetValue(paragraphId, out var list) ? list : [];

    /// <summary>
    /// Renders a block's text embedding CriticMarkup tokens for Pending hunks, applying Accepted hunks, and reverting Rejected hunks.
    /// </summary>
    public static string RenderBlockWithHunks(DocumentBlock block, IReadOnlyList<RevisionHunk> blockHunks)
    {
        string text = block.Text;
        if (blockHunks.Count == 0)
        {
            return text;
        }

        // If the block.Text already contains raw CriticMarkup, resolve accepted/rejected tokens first
        foreach (var hunk in blockHunks)
        {
            text = ApplySingleHunkToCriticText(text, hunk);
        }

        return text;
    }

    private static string ApplySingleHunkToCriticText(string currentText, RevisionHunk hunk)
    {
        string criticToken = FormatHunkAsCriticMarkup(hunk);

        // If token is already embedded in currentText:
        if (!string.IsNullOrEmpty(criticToken) && currentText.Contains(criticToken, StringComparison.Ordinal))
        {
            return hunk.Status switch
            {
                RevisionHunkStatus.Accepted => currentText.Replace(criticToken, GetAcceptedReplacement(hunk), StringComparison.Ordinal),
                RevisionHunkStatus.Rejected => currentText.Replace(criticToken, GetRejectedReplacement(hunk), StringComparison.Ordinal),
                _ => currentText
            };
        }

        // Otherwise, if Pending and not yet embedded in currentText, embed it at OriginalText or end of block
        if (hunk.Status == RevisionHunkStatus.Pending)
        {
            if (hunk.HunkType is RevisionHunkType.Deletion or RevisionHunkType.Replacement or RevisionHunkType.UntrackedEdit
                && !string.IsNullOrEmpty(hunk.OriginalText))
            {
                int idx = currentText.IndexOf(hunk.OriginalText, StringComparison.Ordinal);
                if (idx >= 0)
                {
                    return string.Concat(
                        currentText.AsSpan(0, idx),
                        criticToken,
                        currentText.AsSpan(idx + hunk.OriginalText.Length));
                }
            }
            else if (hunk.HunkType == RevisionHunkType.Comment && !string.IsNullOrEmpty(hunk.OriginalText))
            {
                int idx = currentText.IndexOf(hunk.OriginalText, StringComparison.Ordinal);
                if (idx >= 0)
                {
                    return string.Concat(
                        currentText.AsSpan(0, idx),
                        criticToken,
                        currentText.AsSpan(idx + hunk.OriginalText.Length));
                }
            }
        }
        else if (hunk.Status == RevisionHunkStatus.Accepted)
        {
            if (hunk.HunkType is RevisionHunkType.Deletion or RevisionHunkType.Replacement or RevisionHunkType.UntrackedEdit
                && !string.IsNullOrEmpty(hunk.OriginalText)
                && currentText.Contains(hunk.OriginalText, StringComparison.Ordinal))
            {
                int idx = currentText.IndexOf(hunk.OriginalText, StringComparison.Ordinal);
                return string.Concat(
                    currentText.AsSpan(0, idx),
                    GetAcceptedReplacement(hunk),
                    currentText.AsSpan(idx + hunk.OriginalText.Length));
            }
        }

        return currentText;
    }

    public static string FormatHunkAsCriticMarkup(RevisionHunk hunk)
    {
        return hunk.HunkType switch
        {
            RevisionHunkType.Insertion => $"{{++{hunk.ProposedText}++}}",
            RevisionHunkType.Deletion => $"{{--{hunk.OriginalText}--}}",
            RevisionHunkType.Replacement or RevisionHunkType.UntrackedEdit => $"{{~~{hunk.OriginalText}~>{hunk.ProposedText}~~}}",
            RevisionHunkType.Comment => !string.IsNullOrEmpty(hunk.OriginalText)
                ? $"{{=={hunk.OriginalText}==}}{{>>{hunk.Author}: {hunk.CommentText}<<}}"
                : $"{{>>{hunk.Author}: {hunk.CommentText}<<}}",
            _ => string.Empty
        };
    }

    public static string GetAcceptedReplacement(RevisionHunk hunk) => hunk.HunkType switch
    {
        RevisionHunkType.Insertion => hunk.ProposedText,
        RevisionHunkType.Deletion => string.Empty,
        RevisionHunkType.Replacement or RevisionHunkType.UntrackedEdit => hunk.ProposedText,
        RevisionHunkType.Comment => hunk.OriginalText,
        _ => hunk.ProposedText
    };

    public static string GetRejectedReplacement(RevisionHunk hunk) => hunk.HunkType switch
    {
        RevisionHunkType.Insertion => string.Empty,
        RevisionHunkType.Deletion => hunk.OriginalText,
        RevisionHunkType.Replacement or RevisionHunkType.UntrackedEdit => hunk.OriginalText,
        RevisionHunkType.Comment => hunk.OriginalText,
        _ => hunk.OriginalText
    };

    /// <summary>
    /// Resolves a string containing CriticMarkup tokens into plain text according to the specified display mode.
    /// </summary>
    public static string ResolveCriticMarkupText(string textWithCriticMarkup, MarkupDisplayMode mode)
    {
        if (string.IsNullOrEmpty(textWithCriticMarkup))
        {
            return string.Empty;
        }

        if (mode == MarkupDisplayMode.AllMarkup)
        {
            return textWithCriticMarkup;
        }

        return CriticTokenRegex.Replace(textWithCriticMarkup, match =>
        {
            if (match.Groups["repOld"].Success)
            {
                return mode == MarkupDisplayMode.Original
                    ? match.Groups["repOld"].Value
                    : match.Groups["repNew"].Value;
            }
            if (match.Groups["ins"].Success)
            {
                return mode == MarkupDisplayMode.Original
                    ? string.Empty
                    : match.Groups["ins"].Value;
            }
            if (match.Groups["del"].Success)
            {
                return mode == MarkupDisplayMode.Original
                    ? match.Groups["del"].Value
                    : string.Empty;
            }
            if (match.Groups["hl"].Success)
            {
                return match.Groups["hl"].Value;
            }
            if (match.Groups["comment"].Success)
            {
                return string.Empty;
            }
            return match.Value;
        });
    }

    /// <summary>
    /// Parses a Markdown + CriticMarkup document into a structured ManuscriptDocument with blocks and extracted RevisionHunk items.
    /// </summary>
    public static ManuscriptDocument ParseMarkdown(string markdown, string defaultAuthor = "Supervisor")
    {
        var doc = new ManuscriptDocument();
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return doc;
        }

        string normalized = markdown.Replace("\r\n", "\n");
        string[] rawParagraphs = ParagraphSeparatorRegex.Split(normalized);

        int blockIndex = 0;
        string? pendingId = null;
        BlockStyleRole? pendingRole = null;

        foreach (string rawPara in rawParagraphs)
        {
            string trimmed = rawPara.Trim();
            if (string.IsNullOrEmpty(trimmed))
            {
                continue;
            }

            var anchorMatch = AnchorTagRegex.Match(trimmed);
            if (anchorMatch.Success)
            {
                pendingId = anchorMatch.Groups["id"].Value;
                if (Enum.TryParse<BlockStyleRole>(anchorMatch.Groups["role"].Value, true, out var parsedRole))
                {
                    pendingRole = parsedRole;
                }
                trimmed = trimmed[anchorMatch.Length..].Trim();
                if (string.IsNullOrEmpty(trimmed))
                {
                    continue;
                }
            }

            blockIndex++;
            string paragraphId = pendingId ?? $"p-{blockIndex:D3}";
            BlockStyleRole role = pendingRole ?? BlockStyleRole.BodyParagraph;
            BlockStyleRole? anchoredRole = pendingRole;
            pendingId = null;
            pendingRole = null;

            string content = trimmed;
            if (anchoredRole is { } knownRole)
            {
                // The id comment states the style: strip only that style's own prefix, so a section's first
                // heading is not mistaken for the document title and body text starting with "# " survives.
                string prefix = MarkdownPrefix(knownRole);
                if (prefix.Length > 0 && content.StartsWith(prefix, StringComparison.Ordinal))
                {
                    content = content[prefix.Length..].Trim();
                }
                if (knownRole == BlockStyleRole.Title)
                {
                    doc.Title = ResolveCriticMarkupText(content, MarkupDisplayMode.NoMarkupFinal);
                }
            }
            else if (Scholarly.ScholarlyBlocks.DetectRole(content) is { } scholarlyRole)
            {
                // Figures, tables and equations are recognised by their whole-paragraph syntax, which stays in the text.
                role = scholarlyRole;
            }
            else if (content.StartsWith("### ", StringComparison.Ordinal))
            {
                role = BlockStyleRole.Heading3;
                content = content[4..].Trim();
            }
            else if (content.StartsWith("## Abstract:", StringComparison.OrdinalIgnoreCase))
            {
                role = BlockStyleRole.Abstract;
                content = content["## Abstract:".Length..].Trim();
            }
            else if (content.StartsWith("## ", StringComparison.Ordinal))
            {
                role = BlockStyleRole.Heading2;
                content = content[3..].Trim();
            }
            else if (content.StartsWith("# ", StringComparison.Ordinal))
            {
                role = blockIndex == 1 ? BlockStyleRole.Title : BlockStyleRole.Heading1;
                content = content[2..].Trim();
                if (role == BlockStyleRole.Title)
                {
                    doc.Title = ResolveCriticMarkupText(content, MarkupDisplayMode.NoMarkupFinal);
                }
            }
            else if (content.StartsWith("> ", StringComparison.Ordinal))
            {
                role = BlockStyleRole.BlockQuote;
                content = content[2..].Trim();
            }
            else if (content.StartsWith("*Caption:*", StringComparison.OrdinalIgnoreCase))
            {
                role = BlockStyleRole.Caption;
                content = content["*Caption:*".Length..].Trim();
            }
            else if (content.StartsWith("- [Ref]", StringComparison.OrdinalIgnoreCase))
            {
                role = BlockStyleRole.ReferenceItem;
                content = content["- [Ref]".Length..].Trim();
            }

            var block = new DocumentBlock
            {
                ParagraphId = paragraphId,
                Role = role,
                Text = content
            };
            doc.Blocks.Add(block);

            // Extract any CriticMarkup tokens inside content into structured RevisionHunks
            int hunkSeq = 0;
            string? contextSnippet = null;
            foreach (Match m in CriticTokenRegex.Matches(content))
            {
                hunkSeq++;
                contextSnippet ??= ResolveCriticMarkupText(content, MarkupDisplayMode.NoMarkupFinal);
                var hunk = new RevisionHunk
                {
                    HunkId = $"{paragraphId}-h{hunkSeq:D2}",
                    ParagraphId = paragraphId,
                    Author = defaultAuthor,
                    TimestampUtc = DateTimeOffset.UtcNow,
                    Status = RevisionHunkStatus.Pending,
                    ContextSnippet = contextSnippet
                };

                if (m.Groups["repOld"].Success)
                {
                    hunk.HunkType = RevisionHunkType.Replacement;
                    hunk.OriginalText = m.Groups["repOld"].Value;
                    hunk.ProposedText = m.Groups["repNew"].Value;
                }
                else if (m.Groups["ins"].Success)
                {
                    hunk.HunkType = RevisionHunkType.Insertion;
                    hunk.ProposedText = m.Groups["ins"].Value;
                }
                else if (m.Groups["del"].Success)
                {
                    hunk.HunkType = RevisionHunkType.Deletion;
                    hunk.OriginalText = m.Groups["del"].Value;
                }
                else if (m.Groups["hl"].Success)
                {
                    hunk.HunkType = RevisionHunkType.Comment;
                    hunk.OriginalText = m.Groups["hl"].Value;
                    ParseAuthorAndComment(m.Groups["hlComment"].Value, defaultAuthor, out string author, out string commentBody);
                    hunk.Author = author;
                    hunk.CommentText = commentBody;
                }
                else if (m.Groups["comment"].Success)
                {
                    hunk.HunkType = RevisionHunkType.Comment;
                    ParseAuthorAndComment(m.Groups["comment"].Value, defaultAuthor, out string author, out string commentBody);
                    hunk.Author = author;
                    hunk.CommentText = commentBody;
                }

                doc.Hunks.Add(hunk);
            }
        }

        return doc;
    }

    private static void ParseAuthorAndComment(string rawComment, string fallbackAuthor, out string author, out string commentBody)
    {
        int colonIdx = rawComment.IndexOf(':');
        if (colonIdx > 0 && colonIdx < 40)
        {
            author = rawComment[..colonIdx].Trim();
            commentBody = rawComment[(colonIdx + 1)..].Trim();
        }
        else
        {
            author = fallbackAuthor;
            commentBody = rawComment.Trim();
        }
    }

    /// <summary>
    /// Permanently bakes Accepted hunks and reverts Rejected hunks inside the document blocks,
    /// returning a clean ManuscriptDocument ready to commit or merge.
    /// </summary>
    public static ManuscriptDocument BakeResolvedHunks(ManuscriptDocument source, bool acceptRemainingPending = false)
    {
        var copy = source.Clone();
        foreach (var hunk in copy.Hunks)
        {
            if (hunk.Status == RevisionHunkStatus.Pending && acceptRemainingPending)
            {
                hunk.Status = RevisionHunkStatus.Accepted;
            }
        }

        var hunksByParagraph = GroupHunksByParagraph(copy.Hunks);
        foreach (var block in copy.Blocks)
        {
            block.Text = RenderBlockWithHunks(block, HunksFor(hunksByParagraph, block.ParagraphId));
        }

        copy.Hunks.RemoveAll(h => h.Status != RevisionHunkStatus.Pending);
        return copy;
    }
}
