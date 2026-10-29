using System.Text.RegularExpressions;
using RevisionsEditor.Core.Models;
using RevisionsEditor.Core.Serialization;

namespace RevisionsEditor.Core.Diffing;

public enum DiffTokenKind
{
    Unchanged,
    Inserted,
    Deleted
}

public sealed record DiffToken(DiffTokenKind Kind, string Text);

public enum ParagraphDiffKind
{
    Unchanged,
    Modified,
    Added,
    Removed
}

public sealed class ParagraphDiffEntry
{
    public string ParagraphId { get; set; } = string.Empty;
    public BlockStyleRole Role { get; set; } = BlockStyleRole.BodyParagraph;
    public ParagraphDiffKind Kind { get; set; } = ParagraphDiffKind.Unchanged;
    public string BeforeText { get; set; } = string.Empty;
    public string AfterText { get; set; } = string.Empty;
    public List<DiffToken> InlineTokens { get; set; } = [];
    public int AddedWords { get; set; }
    public int RemovedWords { get; set; }
}

public sealed class DocumentDiffResult
{
    public string BaseLabel { get; set; } = "Base Commit";
    public string TargetLabel { get; set; } = "Target Commit";
    public List<ParagraphDiffEntry> Entries { get; set; } = [];
    public int TotalAddedWords => Entries.Sum(e => e.AddedWords);
    public int TotalRemovedWords => Entries.Sum(e => e.RemovedWords);
    public int ChangedParagraphCount => Entries.Count(e => e.Kind != ParagraphDiffKind.Unchanged);
}

public static class DiffEngineService
{
    private static readonly Regex WordTokenizerRegex = new(@"(\w+\s*|[^\s\w]+\s*|\s+)", RegexOptions.Compiled);
    private static readonly Regex WordCountRegex = new(@"\b\w+\b", RegexOptions.Compiled);

    /// <summary>
    /// Computes a word-and-punctuation LCS diff between two strings, coalescing adjacent operations.
    /// </summary>
    public static List<DiffToken> ComputeWordDiff(string beforeText, string afterText)
    {
        beforeText ??= string.Empty;
        afterText ??= string.Empty;

        if (string.Equals(beforeText, afterText, StringComparison.Ordinal))
        {
            return string.IsNullOrEmpty(beforeText)
                ? []
                : [new DiffToken(DiffTokenKind.Unchanged, beforeText)];
        }

        string[] a = TokenizeWords(beforeText);
        string[] b = TokenizeWords(afterText);

        // Typical edits touch a few words in the middle of a paragraph: strip the shared prefix and suffix
        // so the O(n*m) LCS table only covers the region that actually changed.
        int prefix = 0;
        while (prefix < a.Length && prefix < b.Length && string.Equals(a[prefix], b[prefix], StringComparison.Ordinal))
        {
            prefix++;
        }

        int suffix = 0;
        while (suffix < a.Length - prefix && suffix < b.Length - prefix
            && string.Equals(a[a.Length - 1 - suffix], b[b.Length - 1 - suffix], StringComparison.Ordinal))
        {
            suffix++;
        }

        int n = a.Length - prefix - suffix;
        int m = b.Length - prefix - suffix;
        int width = m + 1;

        // Flat (n+1)*(m+1) table; dp[i*width + j] = LCS length of a[prefix+i..] and b[prefix+j..] within the middle region.
        int[] dp = new int[(n + 1) * width];
        for (int i = n - 1; i >= 0; i--)
        {
            for (int j = m - 1; j >= 0; j--)
            {
                dp[i * width + j] = string.Equals(a[prefix + i], b[prefix + j], StringComparison.Ordinal)
                    ? 1 + dp[(i + 1) * width + j + 1]
                    : Math.Max(dp[(i + 1) * width + j], dp[i * width + j + 1]);
            }
        }

        var rawTokens = new List<DiffToken>(a.Length + b.Length);
        for (int k = 0; k < prefix; k++)
        {
            rawTokens.Add(new DiffToken(DiffTokenKind.Unchanged, a[k]));
        }

        int x = 0, y = 0;
        while (x < n && y < m)
        {
            if (string.Equals(a[prefix + x], b[prefix + y], StringComparison.Ordinal))
            {
                rawTokens.Add(new DiffToken(DiffTokenKind.Unchanged, a[prefix + x]));
                x++;
                y++;
            }
            else if (dp[(x + 1) * width + y] >= dp[x * width + y + 1])
            {
                rawTokens.Add(new DiffToken(DiffTokenKind.Deleted, a[prefix + x]));
                x++;
            }
            else
            {
                rawTokens.Add(new DiffToken(DiffTokenKind.Inserted, b[prefix + y]));
                y++;
            }
        }

        while (x < n)
        {
            rawTokens.Add(new DiffToken(DiffTokenKind.Deleted, a[prefix + x++]));
        }

        while (y < m)
        {
            rawTokens.Add(new DiffToken(DiffTokenKind.Inserted, b[prefix + y++]));
        }

        for (int k = a.Length - suffix; k < a.Length; k++)
        {
            rawTokens.Add(new DiffToken(DiffTokenKind.Unchanged, a[k]));
        }

        return CoalesceTokens(rawTokens);
    }

    private static List<DiffToken> CoalesceTokens(List<DiffToken> raw)
    {
        if (raw.Count == 0)
        {
            return [];
        }

        var merged = new List<DiffToken> { raw[0] };
        for (int i = 1; i < raw.Count; i++)
        {
            var last = merged[^1];
            if (last.Kind == raw[i].Kind)
            {
                merged[^1] = new DiffToken(last.Kind, last.Text + raw[i].Text);
            }
            else
            {
                merged.Add(raw[i]);
            }
        }

        return merged;
    }

    /// <summary>Splits text into word, punctuation and whitespace tokens (each word keeps its trailing space).</summary>
    public static string[] TokenizeWords(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return [];
        }

        return WordTokenizerRegex.Matches(text).Select(m => m.Value).ToArray();
    }

    public static int CountWords(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return 0;
        }

        return WordCountRegex.Count(text);
    }

    /// <summary>
    /// Computes a full paragraph-aligned and inline-word diff between two manuscript documents or Markdown snapshots.
    /// </summary>
    public static DocumentDiffResult ComputeDocumentDiff(
        ManuscriptDocument baseDoc,
        ManuscriptDocument targetDoc,
        string baseLabel = "Base",
        string targetLabel = "Target")
    {
        var result = new DocumentDiffResult
        {
            BaseLabel = baseLabel,
            TargetLabel = targetLabel
        };

        var baseBlocks = baseDoc.Blocks;
        var targetBlocks = targetDoc.Blocks;

        int maxCount = Math.Max(baseBlocks.Count, targetBlocks.Count);
        for (int i = 0; i < maxCount; i++)
        {
            var bBlock = i < baseBlocks.Count ? baseBlocks[i] : null;
            var tBlock = i < targetBlocks.Count ? targetBlocks[i] : null;

            string bText = bBlock != null
                ? CriticMarkupSerializer.ResolveCriticMarkupText(bBlock.Text, MarkupDisplayMode.NoMarkupFinal)
                : string.Empty;
            string tText = tBlock != null
                ? CriticMarkupSerializer.ResolveCriticMarkupText(tBlock.Text, MarkupDisplayMode.NoMarkupFinal)
                : string.Empty;

            if (bBlock != null && tBlock != null)
            {
                var tokens = ComputeWordDiff(bText, tText);
                bool isIdentical = string.Equals(bText, tText, StringComparison.Ordinal);

                result.Entries.Add(new ParagraphDiffEntry
                {
                    ParagraphId = tBlock.ParagraphId,
                    Role = tBlock.Role,
                    Kind = isIdentical ? ParagraphDiffKind.Unchanged : ParagraphDiffKind.Modified,
                    BeforeText = bText,
                    AfterText = tText,
                    InlineTokens = tokens,
                    AddedWords = tokens.Where(tok => tok.Kind == DiffTokenKind.Inserted).Sum(tok => CountWords(tok.Text)),
                    RemovedWords = tokens.Where(tok => tok.Kind == DiffTokenKind.Deleted).Sum(tok => CountWords(tok.Text))
                });
            }
            else if (bBlock == null && tBlock != null)
            {
                result.Entries.Add(new ParagraphDiffEntry
                {
                    ParagraphId = tBlock.ParagraphId,
                    Role = tBlock.Role,
                    Kind = ParagraphDiffKind.Added,
                    BeforeText = string.Empty,
                    AfterText = tText,
                    InlineTokens = [new DiffToken(DiffTokenKind.Inserted, tText)],
                    AddedWords = CountWords(tText),
                    RemovedWords = 0
                });
            }
            else if (bBlock != null && tBlock == null)
            {
                result.Entries.Add(new ParagraphDiffEntry
                {
                    ParagraphId = bBlock.ParagraphId,
                    Role = bBlock.Role,
                    Kind = ParagraphDiffKind.Removed,
                    BeforeText = bText,
                    AfterText = string.Empty,
                    InlineTokens = [new DiffToken(DiffTokenKind.Deleted, bText)],
                    AddedWords = 0,
                    RemovedWords = CountWords(bText)
                });
            }
        }

        return result;
    }

    /// <summary>
    /// Detects any untracked changes made in an imported .docx by comparing the baseline commit text
    /// against the .docx pre-tracked-changes text, creating UntrackedEdit RevisionHunk items.
    /// </summary>
    public static void DetectUntrackedEdits(
        ManuscriptDocument baselineDoc,
        ManuscriptDocument importedDocxDoc,
        string supervisorAuthor = "Supervisor (Untracked)")
    {
        int count = Math.Min(baselineDoc.Blocks.Count, importedDocxDoc.Blocks.Count);
        for (int i = 0; i < count; i++)
        {
            var baseBlock = baselineDoc.Blocks[i];
            var docxBlock = importedDocxDoc.Blocks[i];

            string baseClean = CriticMarkupSerializer.ResolveCriticMarkupText(baseBlock.Text, MarkupDisplayMode.NoMarkupFinal).Trim();
            // Pre-tracked-changes state of the imported docx block:
            string docxOriginalClean = CriticMarkupSerializer.ResolveCriticMarkupText(docxBlock.Text, MarkupDisplayMode.Original).Trim();

            if (!string.Equals(baseClean, docxOriginalClean, StringComparison.Ordinal) && !string.IsNullOrEmpty(baseClean))
            {
                // Extract word-level diff spans that were modified without Track Changes
                var tokens = ComputeWordDiff(baseClean, docxOriginalClean);
                int idx = 0;
                while (idx < tokens.Count)
                {
                    if (tokens[idx].Kind == DiffTokenKind.Deleted
                        && idx + 1 < tokens.Count
                        && tokens[idx + 1].Kind == DiffTokenKind.Inserted)
                    {
                        string oldSpan = tokens[idx].Text.Trim();
                        string newSpan = tokens[idx + 1].Text.Trim();
                        if (!string.IsNullOrEmpty(oldSpan) || !string.IsNullOrEmpty(newSpan))
                        {
                            var untrackedHunk = new RevisionHunk
                            {
                                HunkId = $"{docxBlock.ParagraphId}-ut{idx + 1}",
                                ParagraphId = docxBlock.ParagraphId,
                                HunkType = RevisionHunkType.UntrackedEdit,
                                OriginalText = oldSpan,
                                ProposedText = newSpan,
                                Author = supervisorAuthor,
                                TimestampUtc = DateTimeOffset.UtcNow,
                                Status = RevisionHunkStatus.Pending,
                                ContextSnippet = docxOriginalClean
                            };
                            importedDocxDoc.Hunks.Add(untrackedHunk);

                            // Embed as CriticMarkup replacement inside docxBlock.Text so it can be accepted/rejected
                            if (!string.IsNullOrEmpty(newSpan) && docxBlock.Text.Contains(newSpan, StringComparison.Ordinal))
                            {
                                int pos = docxBlock.Text.IndexOf(newSpan, StringComparison.Ordinal);
                                docxBlock.Text = string.Concat(
                                    docxBlock.Text.AsSpan(0, pos),
                                    $"{{~~{oldSpan}~>{newSpan}~~}}",
                                    docxBlock.Text.AsSpan(pos + newSpan.Length));
                            }
                        }
                        idx += 2;
                    }
                    else
                    {
                        idx++;
                    }
                }
            }
        }
    }

    /// <summary>
    /// Text of a block as it appears in Google Docs: CriticMarkup resolved and Markdown emphasis markers removed
    /// (bold and italic are formatting there, not characters).
    /// </summary>
    public static string ToGoogleDocsText(string blockText)
    {
        string text = CriticMarkupSerializer.ResolveCriticMarkupText(blockText, MarkupDisplayMode.NoMarkupFinal);
        // Twice: runs imported before markers were kept tight can nest ("***a **b***") or touch.
        text = EmphasisMarkerRegex.Replace(EmphasisMarkerRegex.Replace(text, "$2"), "$2");
        return MultiWhitespaceRegex.Replace(text, " ").Trim();
    }

    /// <summary>
    /// Comparison key that ignores differences Google Docs and the formatting rules introduce on their own:
    /// curly versus straight quotes, dash variants, non-breaking spaces and repeated whitespace.
    /// </summary>
    public static string GoogleDocsMatchKey(string plainText)
    {
        var sb = new System.Text.StringBuilder(plainText.Length);
        foreach (char c in plainText.Normalize(System.Text.NormalizationForm.FormC))
        {
            sb.Append(c switch
            {
                '\u2018' or '\u2019' or '\u201A' or '\u201B' or '\u2032' => '\'',
                '\u201C' or '\u201D' or '\u201E' or '\u201F' or '\u2033' => '"',
                '\u2013' or '\u2014' => '-',
                '\u00A0' or '\u000B' or '\u2028' or '\u2029' or '\t' => ' ',
                _ => c
            });
        }
        return MultiWhitespaceRegex.Replace(sb.ToString(), " ").Trim();
    }

    // Emphasis written by the Word importer, including runs that end in a space ("**bold **").
    private static readonly Regex EmphasisMarkerRegex = new(@"(\*{1,3})([^*\n]+?)\1", RegexOptions.Compiled);
    private static readonly Regex MultiWhitespaceRegex = new(@"\s+", RegexOptions.Compiled);

    /// <summary>
    /// Generates paragraph-level patches that turn the last Google Docs snapshot into the merged manuscript.
    /// Paragraphs are aligned by content (a paragraph-level LCS), so adding or removing one paragraph yields one
    /// insert or delete patch instead of shifting every later paragraph into a "modified" one.
    /// </summary>
    /// <remarks>
    /// Figures, tables and equations are not sent: in Google Docs they are an image, a table or an equation, not
    /// text, so their syntax must never be typed into the document. Their captions stay as they are there.
    /// </remarks>
    public static List<GoogleDocPatch> GenerateGoogleDocPatches(
        ManuscriptDocument googleDocsBaseline,
        ManuscriptDocument mergedMainDoc)
    {
        static bool IsText(DocumentBlock b) => b.Role is not (BlockStyleRole.Figure or BlockStyleRole.Table or BlockStyleRole.Equation);
        var baseBlocks = googleDocsBaseline.Blocks.Where(IsText).ToList();
        var mergedBlocks = mergedMainDoc.Blocks.Where(IsText).ToList();
        var baseTexts = baseBlocks.Select(b => ToGoogleDocsText(b.Text)).ToList();
        var mergedTexts = mergedBlocks.Select(b => ToGoogleDocsText(b.Text)).ToList();
        var baseKeys = baseTexts.Select(GoogleDocsMatchKey).ToList();
        var mergedKeys = mergedTexts.Select(GoogleDocsMatchKey).ToList();
        var baseSections = SectionHeadings(baseBlocks, googleDocsBaseline.Title);
        var mergedSections = SectionHeadings(mergedBlocks, mergedMainDoc.Title);

        int n = baseKeys.Count, m = mergedKeys.Count, width = m + 1;
        int[] dp = new int[(n + 1) * width];
        for (int i = n - 1; i >= 0; i--)
        {
            for (int j = m - 1; j >= 0; j--)
            {
                dp[i * width + j] = baseKeys[i] == mergedKeys[j]
                    ? 1 + dp[(i + 1) * width + j + 1]
                    : Math.Max(dp[(i + 1) * width + j], dp[i * width + j + 1]);
            }
        }

        var patches = new List<GoogleDocPatch>();
        var usedIds = new HashSet<string>(StringComparer.Ordinal);
        var removedRun = new List<int>();
        var addedRun = new List<int>();
        string anchor = string.Empty; // Google Docs text of the last unchanged paragraph

        void FlushRun()
        {
            int pairs = Math.Min(removedRun.Count, addedRun.Count);
            for (int k = 0; k < pairs; k++)
            {
                int bi = removedRun[k], mi = addedRun[k];
                AddPatch(GoogleDocPatchKind.Modify, mergedBlocks[mi], mergedSections[mi], baseTexts[bi], mergedTexts[mi], string.Empty);
            }
            for (int k = pairs; k < removedRun.Count; k++)
            {
                int bi = removedRun[k];
                AddPatch(GoogleDocPatchKind.Delete, baseBlocks[bi], baseSections[bi], baseTexts[bi], string.Empty, string.Empty);
            }
            // Extra new paragraphs follow the last paragraph of this run that already exists in Google Docs.
            string insertAnchor = pairs > 0 ? baseTexts[removedRun[pairs - 1]] : anchor;
            for (int k = pairs; k < addedRun.Count; k++)
            {
                int mi = addedRun[k];
                AddPatch(GoogleDocPatchKind.Insert, mergedBlocks[mi], mergedSections[mi], string.Empty, mergedTexts[mi], insertAnchor);
            }
            if (removedRun.Count > 0)
            {
                anchor = baseTexts[removedRun[^1]];
            }
            removedRun.Clear();
            addedRun.Clear();
        }

        void AddPatch(GoogleDocPatchKind kind, DocumentBlock block, string section, string before, string after, string anchorText)
        {
            var tokens = ComputeWordDiff(before, after);
            int added = tokens.Where(t => t.Kind == DiffTokenKind.Inserted).Sum(t => CountWords(t.Text));
            int removed = tokens.Where(t => t.Kind == DiffTokenKind.Deleted).Sum(t => CountWords(t.Text));

            // Content-derived id: stable across regenerations, unique within the list.
            string baseId = "patch-" + Provenance.TextCanonicalizer.ComputeSha256Hex($"{kind}\u001F{before}\u001F{after}\u001F{anchorText}")[..10];
            string id = baseId;
            for (int suffix = 2; !usedIds.Add(id); suffix++)
            {
                id = $"{baseId}-{suffix}";
            }

            patches.Add(new GoogleDocPatch
            {
                PatchId = id,
                Kind = kind,
                ParagraphId = block.ParagraphId,
                Role = block.Role,
                SectionHeading = section,
                BeforeText = before,
                AfterText = after,
                AnchorText = anchorText,
                ChangeSummary = kind switch
                {
                    GoogleDocPatchKind.Insert => $"New paragraph, {added} words",
                    GoogleDocPatchKind.Delete => $"Removed paragraph, {removed} words",
                    _ => $"+{added} / -{removed} words"
                }
            });
        }

        int x = 0, y = 0;
        while (x < n || y < m)
        {
            if (x < n && y < m && baseKeys[x] == mergedKeys[y])
            {
                FlushRun();
                anchor = baseTexts[x];
                x++;
                y++;
            }
            else if (y >= m || (x < n && dp[(x + 1) * width + y] >= dp[x * width + y + 1]))
            {
                removedRun.Add(x++);
            }
            else
            {
                addedRun.Add(y++);
            }
        }
        FlushRun();

        return patches;
    }

    private static List<string> SectionHeadings(List<DocumentBlock> blocks, string title)
    {
        var sections = new List<string>(blocks.Count);
        string current = title;
        foreach (var block in blocks)
        {
            if (block.Role is BlockStyleRole.Heading1 or BlockStyleRole.Heading2 or BlockStyleRole.Heading3)
            {
                current = ToGoogleDocsText(block.Text);
            }
            sections.Add(current);
        }
        return sections;
    }
}
