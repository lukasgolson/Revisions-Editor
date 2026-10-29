using System.Text;
using System.Text.RegularExpressions;
using RevisionsEditor.Core.Diffing;
using RevisionsEditor.Core.Merging;
using RevisionsEditor.Core.Models;
using RevisionsEditor.Core.Scholarly;
using RevisionsEditor.Core.Serialization;

namespace RevisionsEditor.Core.Reviews;

/// <summary>
/// Builds the documents a revision is resubmitted with: the marked-up manuscript (every change since the
/// submitted version as a tracked change) and the point-by-point response letter.
/// </summary>
public static class RevisionDocuments
{
    // Citations, cross-references, placeholders, inline math and emphasis markers are compared as single
    // tokens, so a changed citation is shown as a whole and the markup is never split by a revision mark.
    private static readonly Regex AtomicMarkupRegex = new(
        @"\[[^\[\]]*@[^\[\]]*\]|(?<![\w@\\])@[A-Za-z0-9_][\w:.#$%&+?<>~/-]*[A-Za-z0-9]|\{\{[^{}]*\}\}|(?<![\\$])\$[^$\n]+\$|\{#[^{}]*\}|\^[^\s^]+\^",
        RegexOptions.Compiled);

    private static readonly Regex AtomStandInRegex = new(@"ATOM(\d+)Q", RegexOptions.Compiled);

    private static readonly Regex SentenceRegex = new(@"[^.!?]+(?:[.!?]+(?=\s|$)|$)\s*", RegexOptions.Compiled);

    /// <summary>
    /// The revised manuscript with every change since <paramref name="submitted"/> marked as a pending change
    /// by <paramref name="author"/>; export it with tracked changes (or highlighted changes).
    /// </summary>
    public static ManuscriptDocument BuildMarkedUpManuscript(ManuscriptDocument submitted, ManuscriptDocument revised, string author, DateTimeOffset when)
    {
        var before = Final(submitted);
        var after = Final(revised);
        var marked = new ManuscriptDocument
        {
            DocumentId = revised.DocumentId,
            Title = revised.Title,
            ActiveFormattingProfile = revised.ActiveFormattingProfile
        };

        int[] map = ManuscriptMerger.Align(before.Blocks, after.Blocks);
        var matchedFrom = new int[after.Blocks.Count];
        Array.Fill(matchedFrom, -1);
        for (int i = 0; i < map.Length; i++)
        {
            if (map[i] >= 0)
            {
                matchedFrom[map[i]] = i;
            }
        }

        int nextOld = 0;
        int hunkSeq = 0;
        for (int j = 0; j < after.Blocks.Count; j++)
        {
            // Paragraphs removed before this one are shown struck through in place.
            if (matchedFrom[j] >= 0)
            {
                for (; nextOld < matchedFrom[j]; nextOld++)
                {
                    if (map[nextOld] < 0)
                    {
                        AddRemoved(marked, before.Blocks[nextOld], author, when, ref hunkSeq);
                    }
                }
                nextOld = matchedFrom[j] + 1;
            }

            var block = after.Blocks[j].Clone();
            string oldText = matchedFrom[j] >= 0 ? before.Blocks[matchedFrom[j]].Text : string.Empty;
            if (matchedFrom[j] < 0)
            {
                if (block.Role is not BlockStyleRole.Figure and not BlockStyleRole.Table and not BlockStyleRole.Equation)
                {
                    block.Text = Mark(marked, block.ParagraphId, string.Empty, block.Text, author, when, ref hunkSeq);
                }
            }
            else if (oldText != block.Text)
            {
                block.Text = MarkBlock(marked, block, oldText, author, when, ref hunkSeq);
            }
            marked.Blocks.Add(block);
        }
        for (; nextOld < before.Blocks.Count; nextOld++)
        {
            if (map[nextOld] < 0)
            {
                AddRemoved(marked, before.Blocks[nextOld], author, when, ref hunkSeq);
            }
        }
        return marked;
    }

    private static ManuscriptDocument Final(ManuscriptDocument doc)
    {
        var copy = doc.Clone();
        foreach (var block in copy.Blocks)
        {
            block.Text = CriticMarkupSerializer.ResolveCriticMarkupText(block.Text, MarkupDisplayMode.NoMarkupFinal);
        }
        copy.Hunks.Clear();
        return copy;
    }

    private static void AddRemoved(ManuscriptDocument marked, DocumentBlock removed, string author, DateTimeOffset when, ref int seq)
    {
        if (removed.Role is BlockStyleRole.Figure or BlockStyleRole.Table or BlockStyleRole.Equation)
        {
            return; // a removed figure cannot be shown struck through; the response letter says so
        }
        var block = removed.Clone();
        block.ParagraphId = $"{removed.ParagraphId}-removed";
        block.Text = Mark(marked, block.ParagraphId, removed.Text, string.Empty, author, when, ref seq);
        marked.Blocks.Add(block);
    }

    private static string MarkBlock(ManuscriptDocument marked, DocumentBlock block, string oldText, string author, DateTimeOffset when, ref int seq)
    {
        if (block.Role is BlockStyleRole.Figure or BlockStyleRole.Table)
        {
            // Only the caption is marked; the figure or table syntax around it must stay intact.
            var oldSplit = ScholarlyBlocks.SplitCaption(oldText);
            var newSplit = ScholarlyBlocks.SplitCaption(block.Text);
            if (oldSplit == null || newSplit == null)
            {
                return block.Text;
            }
            return newSplit.Value.Prefix + Mark(marked, block.ParagraphId, oldSplit.Value.Caption, newSplit.Value.Caption, author, when, ref seq) + newSplit.Value.Suffix;
        }
        if (block.Role == BlockStyleRole.Equation)
        {
            return block.Text;
        }
        return Mark(marked, block.ParagraphId, oldText, block.Text, author, when, ref seq);
    }

    /// <summary>The new text with CriticMarkup for each difference from the old, adding a hunk per mark.</summary>
    private static string Mark(ManuscriptDocument marked, string paragraphId, string oldText, string newText, string author, DateTimeOffset when, ref int seq)
    {
        var atoms = new List<string>();
        string Protect(string text) => AtomicMarkupRegex.Replace(text, m =>
        {
            int index = atoms.IndexOf(m.Value);
            if (index < 0)
            {
                atoms.Add(m.Value);
                index = atoms.Count - 1;
            }
            // A word-like stand-in, so the diff treats the markup as one word.
            return $"ATOM{index}Q";
        });
        string Restore(string text) => AtomStandInRegex.Replace(text, m =>
            int.TryParse(m.Groups[1].Value, out int index) && index < atoms.Count ? atoms[index] : m.Value);

        var tokens = DiffEngineService.ComputeWordDiff(Protect(oldText), Protect(newText));
        var result = new StringBuilder();
        for (int i = 0; i < tokens.Count; i++)
        {
            var token = tokens[i];
            if (token.Kind == DiffTokenKind.Unchanged)
            {
                result.Append(Restore(token.Text));
                continue;
            }

            string text = Restore(token.Text);
            // Keep the space after a change outside the mark, as Word does.
            string trailing = text[text.TrimEnd().Length..];
            string core = text[..(text.Length - trailing.Length)];
            if (token.Kind == DiffTokenKind.Deleted && i + 1 < tokens.Count && tokens[i + 1].Kind == DiffTokenKind.Inserted)
            {
                string inserted = Restore(tokens[i + 1].Text);
                string insTrailing = inserted[inserted.TrimEnd().Length..];
                string insCore = inserted[..(inserted.Length - insTrailing.Length)];
                var hunk = AddHunk(marked, paragraphId, RevisionHunkType.Replacement, core, insCore, author, when, ref seq);
                result.Append(CriticMarkupSerializer.FormatHunkAsCriticMarkup(hunk)).Append(insTrailing);
                i++;
            }
            else if (core.Length == 0)
            {
                result.Append(token.Kind == DiffTokenKind.Inserted ? trailing : string.Empty);
            }
            else if (token.Kind == DiffTokenKind.Inserted)
            {
                var hunk = AddHunk(marked, paragraphId, RevisionHunkType.Insertion, string.Empty, core, author, when, ref seq);
                result.Append(CriticMarkupSerializer.FormatHunkAsCriticMarkup(hunk)).Append(trailing);
            }
            else
            {
                var hunk = AddHunk(marked, paragraphId, RevisionHunkType.Deletion, core, string.Empty, author, when, ref seq);
                result.Append(CriticMarkupSerializer.FormatHunkAsCriticMarkup(hunk));
            }
        }
        return result.ToString();
    }

    private static RevisionHunk AddHunk(ManuscriptDocument doc, string paragraphId, RevisionHunkType type, string original, string proposed, string author, DateTimeOffset when, ref int seq)
    {
        var hunk = new RevisionHunk
        {
            HunkId = $"rev-{++seq:D4}",
            ParagraphId = paragraphId,
            HunkType = type,
            OriginalText = original,
            ProposedText = proposed,
            Author = author,
            TimestampUtc = when
        };
        doc.Hunks.Add(hunk);
        return hunk;
    }

    /// <summary>
    /// Where each paragraph is in the manuscript by structure: "Section 2.3 (Study area), paragraph 2". Used
    /// when Word is not available to give page and line numbers.
    /// </summary>
    public static Dictionary<string, string> StructuralLocations(ManuscriptDocument formatted)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        string section = "the front matter";
        int paragraph = 0;
        foreach (var block in formatted.Blocks)
        {
            switch (block.Role)
            {
                case BlockStyleRole.Heading1 or BlockStyleRole.Heading2 or BlockStyleRole.Heading3:
                    string title = Checks.ManuscriptStructure.PlainText(block.Text, heading: true);
                    string number = block.ComputedSectionNumber.Trim().TrimEnd('.');
                    section = number.Length > 0 ? $"Section {number} ({title})" : $"“{title}”";
                    paragraph = 0;
                    result[block.ParagraphId] = $"heading of {section}";
                    break;
                case BlockStyleRole.Abstract:
                    result[block.ParagraphId] = "the Abstract";
                    break;
                case BlockStyleRole.Title:
                    result[block.ParagraphId] = "the title";
                    break;
                default:
                    paragraph++;
                    result[block.ParagraphId] = block.Role switch
                    {
                        BlockStyleRole.Figure => $"figure caption in {section}",
                        BlockStyleRole.Table => $"table caption in {section}",
                        BlockStyleRole.Equation => $"equation in {section}",
                        _ => $"{section}, paragraph {paragraph}"
                    };
                    break;
            }
        }
        return result;
    }

    /// <summary>
    /// The response letter: each comment (in italics), the response, and for each linked paragraph the revised
    /// text with its location. Only the sentences that changed are quoted when the paragraph existed before.
    /// </summary>
    public static ManuscriptDocument BuildResponseLetter(
        ReviewRound round,
        ManuscriptDocument? submitted,
        ManuscriptDocument revised,
        IReadOnlyDictionary<string, string> locations,
        string paperTitle)
    {
        var letter = new ManuscriptDocument { DocumentId = "response-letter", Title = $"Response to reviewers: {paperTitle}" };
        int id = 0;
        void Add(BlockStyleRole role, string text) => letter.Blocks.Add(new DocumentBlock { ParagraphId = $"l-{++id:D3}", Role = role, Text = text });

        Add(BlockStyleRole.Title, "Response to reviewers");
        string reference = round.ManuscriptNumber.Length > 0 ? $"Manuscript {round.ManuscriptNumber}: " : string.Empty;
        Add(BlockStyleRole.BodyParagraph, $"{reference}*{paperTitle}*");
        Add(BlockStyleRole.BodyParagraph, round.LetterIntroduction.Trim().Length > 0
            ? round.LetterIntroduction.Trim()
            : "We thank the editor and the reviewers for their careful reading of the manuscript and their constructive comments. "
              + "We respond to each point below. The comments are reproduced in italics, followed by our response and the revised text with its location in the revised manuscript.");

        var revisedById = revised.Blocks.ToDictionary(b => b.ParagraphId, StringComparer.Ordinal);
        var submittedById = submitted?.Blocks.ToDictionary(b => b.ParagraphId, StringComparer.Ordinal) ?? new Dictionary<string, DocumentBlock>();
        var order = revised.Blocks.Select((b, i) => (b.ParagraphId, i)).ToDictionary(x => x.ParagraphId, x => x.i, StringComparer.Ordinal);

        foreach (var group in round.Comments.GroupBy(c => c.Reviewer))
        {
            Add(BlockStyleRole.Heading1, group.Key);
            foreach (var comment in group)
            {
                var paragraphs = comment.Text.Split("\n\n", StringSplitOptions.RemoveEmptyEntries);
                for (int i = 0; i < paragraphs.Length; i++)
                {
                    Add(BlockStyleRole.BlockQuote, (i == 0 ? $"**Comment {comment.Id}.** " : string.Empty) + paragraphs[i].Trim().Replace('\n', ' '));
                }

                string response = comment.Response.Trim().Length > 0 ? comment.Response.Trim() : "[Response to be written.]";
                var responseParagraphs = response.Split("\n\n", StringSplitOptions.RemoveEmptyEntries);
                for (int i = 0; i < responseParagraphs.Length; i++)
                {
                    Add(BlockStyleRole.BodyParagraph, (i == 0 ? "**Response:** " : string.Empty) + responseParagraphs[i].Trim().Replace('\n', ' '));
                }

                foreach (string paragraphId in comment.ParagraphIds.Where(revisedById.ContainsKey).OrderBy(p => order[p]))
                {
                    var block = revisedById[paragraphId];
                    string newText = CriticMarkupSerializer.ResolveCriticMarkupText(block.Text, MarkupDisplayMode.NoMarkupFinal);
                    string? oldText = submittedById.TryGetValue(paragraphId, out var old)
                        ? CriticMarkupSerializer.ResolveCriticMarkupText(old.Text, MarkupDisplayMode.NoMarkupFinal)
                        : null;
                    string location = locations.TryGetValue(paragraphId, out var loc) ? loc : "the revised manuscript";
                    string quoted = block.Role switch
                    {
                        BlockStyleRole.Figure or BlockStyleRole.Table => ScholarlyBlocks.SplitCaption(newText)?.Caption ?? newText,
                        BlockStyleRole.Equation => newText,
                        _ => ChangedSentences(oldText, newText)
                    };
                    Add(BlockStyleRole.BodyParagraph, $"Revised text ({location}):");
                    Add(BlockStyleRole.BlockQuote, $"“{quoted.Trim()}”");
                }
            }
        }
        return letter;
    }

    /// <summary>The sentences of <paramref name="newText"/> that changed, joined with ellipses (all of it for a new paragraph).</summary>
    public static string ChangedSentences(string? oldText, string newText)
    {
        if (oldText == null || oldText == newText)
        {
            return newText;
        }
        var oldSentences = SentenceRegex.Matches(oldText).Select(m => m.Value.Trim()).ToHashSet(StringComparer.Ordinal);
        var sentences = SentenceRegex.Matches(newText).Select(m => m.Value.Trim()).Where(s => s.Length > 0).ToList();
        var parts = new List<string>();
        bool skipped = false;
        for (int i = 0; i < sentences.Count; i++)
        {
            if (oldSentences.Contains(sentences[i]))
            {
                skipped = true;
                continue;
            }
            if (skipped && parts.Count > 0)
            {
                parts.Add("…");
            }
            else if (i > 0 && parts.Count == 0)
            {
                parts.Add("…");
            }
            parts.Add(sentences[i]);
            skipped = false;
        }
        if (parts.Count == 0)
        {
            return newText;
        }
        if (skipped)
        {
            parts.Add("…");
        }
        return string.Join(" ", parts);
    }
}
