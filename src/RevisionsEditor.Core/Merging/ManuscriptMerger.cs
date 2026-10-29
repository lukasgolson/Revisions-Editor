using System.Text;
using System.Text.RegularExpressions;
using RevisionsEditor.Core.Diffing;
using RevisionsEditor.Core.Models;
using RevisionsEditor.Core.Provenance;
using RevisionsEditor.Core.Serialization;

namespace RevisionsEditor.Core.Merging;

public sealed class ManuscriptMergeResult
{
    public ManuscriptDocument Document { get; init; } = new();
    /// <summary>Paragraphs taken from the other side because only it changed them.</summary>
    public int TakenFromTheirs { get; set; }
    /// <summary>Paragraphs changed on both sides whose edits did not overlap and were combined.</summary>
    public int Combined { get; set; }
    /// <summary>Conflicting edits, added to the manuscript as pending changes to accept or reject.</summary>
    public int Conflicts { get; set; }
    /// <summary>What happened to the paper's other files (bibliography, figures, results, review responses).</summary>
    public List<string> Notes { get; } = [];

    public string Summary => (Conflicts == 0
        ? $"{TakenFromTheirs + Combined} paragraphs merged automatically, no conflicts."
        : $"{TakenFromTheirs + Combined} paragraphs merged automatically, {Conflicts} conflicting edit{(Conflicts == 1 ? "" : "s")} added as pending changes.")
        + (Notes.Count > 0 ? " " + string.Join(" ", Notes) : string.Empty);
}

/// <summary>
/// Three-way merge of manuscripts at paragraph level, with word-level merging inside paragraphs.
///
/// Both versions are compared with the version they started from (the base). Paragraphs are aligned by
/// content, not position, so inserted or removed paragraphs do not shift the comparison. A paragraph changed
/// on one side only takes that side's version; changed on both sides, non-overlapping edits are combined.
/// Edits that genuinely conflict are not resolved silently: they become pending changes (keeping "ours" as
/// the current text, with "theirs" proposed) to accept or reject in the Changes pane.
/// </summary>
public static class ManuscriptMerger
{
    private static readonly Regex CriticTokenRegex = new(@"\{(~~|\+\+|--|==|>>)", RegexOptions.Compiled);
    private const double PairingThreshold = 0.5;

    public static ManuscriptMergeResult Merge(
        ManuscriptDocument baseDoc,
        ManuscriptDocument ours,
        ManuscriptDocument theirs,
        string theirsAuthor)
    {
        var baseBlocks = baseDoc.Blocks;
        var oursMatch = Align(baseBlocks, ours.Blocks);
        var theirsMatch = Align(baseBlocks, theirs.Blocks);
        var oursInserts = InsertionsByAnchor(oursMatch, ours.Blocks.Count);
        var theirsInserts = InsertionsByAnchor(theirsMatch, theirs.Blocks.Count);

        var merged = new ManuscriptDocument
        {
            DocumentId = ours.DocumentId,
            Title = ours.Title,
            GoogleDocId = ours.GoogleDocId ?? theirs.GoogleDocId,
            GoogleDocTabId = ours.GoogleDocTabId ?? theirs.GoogleDocTabId,
            GoogleDocTabTitle = ours.GoogleDocTabTitle ?? theirs.GoogleDocTabTitle,
            ActiveFormattingProfile = ours.ActiveFormattingProfile
        };
        var result = new ManuscriptMergeResult { Document = merged };
        var builder = new Builder(merged, ours, theirs, theirsAuthor);

        void EmitInsertions(int anchor)
        {
            var fromOurs = oursInserts.TryGetValue(anchor, out var o) ? o : [];
            var fromTheirs = theirsInserts.TryGetValue(anchor, out var t) ? t : [];
            foreach (int index in fromOurs)
            {
                builder.AddFromOurs(ours.Blocks[index]);
            }
            foreach (int index in fromTheirs)
            {
                // The same paragraph added on both sides appears once.
                if (fromOurs.Any(oi => Key(ours.Blocks[oi].Text) == Key(theirs.Blocks[index].Text)))
                {
                    continue;
                }
                builder.AddFromTheirs(theirs.Blocks[index]);
                result.TakenFromTheirs++;
            }
        }

        EmitInsertions(-1);
        for (int i = 0; i < baseBlocks.Count; i++)
        {
            var b = baseBlocks[i];
            var o = oursMatch[i] >= 0 ? ours.Blocks[oursMatch[i]] : null;
            var t = theirsMatch[i] >= 0 ? theirs.Blocks[theirsMatch[i]] : null;
            bool oursChanged = o == null || !SameBlock(o, b);
            bool theirsChanged = t == null || !SameBlock(t, b);

            if (o == null && t == null)
            {
                // Removed on both sides.
            }
            else if (o == null)
            {
                if (theirsChanged)
                {
                    // Removed here, edited there: keep their edit, proposed for insertion, so removing it
                    // stays a deliberate choice.
                    builder.AddConflictKeepingTheirs(t!, "removed in this version");
                    result.Conflicts++;
                }
            }
            else if (t == null)
            {
                if (oursChanged)
                {
                    builder.AddConflictProposingRemoval(o);
                    result.Conflicts++;
                }
                // Otherwise removed there and unchanged here: removed.
            }
            else if (!theirsChanged || SameBlock(o, t))
            {
                builder.AddFromOurs(o);
            }
            else if (!oursChanged)
            {
                builder.AddFromTheirs(t, keepIdOf: o);
                result.TakenFromTheirs++;
            }
            else
            {
                var role = o.Role != b.Role ? o.Role : t.Role;
                if (HasMarkup(b.Text) || HasMarkup(o.Text) || HasMarkup(t.Text))
                {
                    // Pending review markup cannot be merged word by word; keep ours and note their version.
                    builder.AddWithComment(o, role, $"Changed in both versions. Other version: {DiffEngineService.ToGoogleDocsText(t.Text)}");
                    result.Conflicts++;
                }
                else
                {
                    var (text, conflicts) = MergeWords(b.Text, o.Text, t.Text);
                    builder.AddMerged(o, role, text, conflicts);
                    if (conflicts.Count > 0) result.Conflicts += conflicts.Count;
                    else result.Combined++;
                }
            }

            EmitInsertions(i);
        }

        return result;
    }

    // ---- Paragraph alignment --------------------------------------------------------------------------

    private static string Key(string text) => Regex.Replace(text.Trim(), @"\s+", " ");

    private static bool SameBlock(DocumentBlock a, DocumentBlock b) => a.Role == b.Role && Key(a.Text) == Key(b.Text);

    private static bool HasMarkup(string text) => CriticTokenRegex.IsMatch(text);

    /// <summary>
    /// For each paragraph of <paramref name="from"/>, the index of the same paragraph in <paramref name="to"/>,
    /// or -1 if it was removed. Unchanged paragraphs are matched by an LCS over their text; between those,
    /// edited paragraphs are paired by similarity, keeping their order.
    /// </summary>
    public static int[] Align(IReadOnlyList<DocumentBlock> from, IReadOnlyList<DocumentBlock> to)
    {
        int n = from.Count, m = to.Count, width = m + 1;
        var fromKeys = from.Select(b => Key(b.Text)).ToArray();
        var toKeys = to.Select(b => Key(b.Text)).ToArray();
        var dp = new int[(n + 1) * width];
        for (int i = n - 1; i >= 0; i--)
        {
            for (int j = m - 1; j >= 0; j--)
            {
                dp[i * width + j] = fromKeys[i] == toKeys[j]
                    ? 1 + dp[(i + 1) * width + j + 1]
                    : Math.Max(dp[(i + 1) * width + j], dp[i * width + j + 1]);
            }
        }

        var match = Enumerable.Repeat(-1, n).ToArray();
        var gapFrom = new List<int>();
        var gapTo = new List<int>();
        int x = 0, y = 0;
        while (x < n || y < m)
        {
            if (x < n && y < m && fromKeys[x] == toKeys[y])
            {
                PairBySimilarity(from, to, gapFrom, gapTo, match);
                match[x++] = y++;
            }
            else if (y >= m || (x < n && dp[(x + 1) * width + y] >= dp[x * width + y + 1]))
            {
                gapFrom.Add(x++);
            }
            else
            {
                gapTo.Add(y++);
            }
        }
        PairBySimilarity(from, to, gapFrom, gapTo, match);
        return match;
    }

    /// <summary>Order-preserving pairing of edited paragraphs within a gap, maximizing total similarity.</summary>
    private static void PairBySimilarity(IReadOnlyList<DocumentBlock> from, IReadOnlyList<DocumentBlock> to, List<int> gapFrom, List<int> gapTo, int[] match)
    {
        int p = gapFrom.Count, q = gapTo.Count;
        if (p > 0 && q > 0)
        {
            var sim = new double[p, q];
            for (int a = 0; a < p; a++)
                for (int c = 0; c < q; c++)
                    sim[a, c] = Similarity(from[gapFrom[a]], to[gapTo[c]]);

            var best = new double[p + 1, q + 1];
            for (int a = p - 1; a >= 0; a--)
            {
                for (int c = q - 1; c >= 0; c--)
                {
                    double take = sim[a, c] >= PairingThreshold ? sim[a, c] + best[a + 1, c + 1] : double.MinValue;
                    best[a, c] = Math.Max(take, Math.Max(best[a + 1, c], best[a, c + 1]));
                }
            }

            int i = 0, j = 0;
            while (i < p && j < q)
            {
                if (sim[i, j] >= PairingThreshold && best[i, j] == sim[i, j] + best[i + 1, j + 1])
                {
                    match[gapFrom[i]] = gapTo[j];
                    i++;
                    j++;
                }
                else if (best[i, j] == best[i + 1, j]) i++;
                else j++;
            }
        }
        gapFrom.Clear();
        gapTo.Clear();
    }

    /// <summary>Word overlap, or character-pair overlap for short paragraphs such as headings.</summary>
    private static double Similarity(DocumentBlock a, DocumentBlock b)
    {
        string ta = DiffEngineService.ToGoogleDocsText(a.Text);
        string tb = DiffEngineService.ToGoogleDocsText(b.Text);
        double score = TextCanonicalizer.ComputeSentenceSimilarity(ta, tb);
        if (ta.Length < 60 || tb.Length < 60)
        {
            score = Math.Max(score, BigramSimilarity(ta, tb));
        }
        if (a.ParagraphId == b.ParagraphId && a.Role == b.Role)
        {
            score += 0.15;
        }
        return score;
    }

    private static double BigramSimilarity(string a, string b)
    {
        static List<string> Bigrams(string s)
        {
            s = s.ToLowerInvariant();
            var list = new List<string>();
            for (int i = 0; i + 1 < s.Length; i++) list.Add(s.Substring(i, 2));
            return list;
        }
        var x = Bigrams(a);
        var y = Bigrams(b);
        if (x.Count == 0 || y.Count == 0) return 0;
        var counts = x.GroupBy(s => s).ToDictionary(g => g.Key, g => g.Count());
        int shared = 0;
        foreach (string s in y)
        {
            if (counts.TryGetValue(s, out int c) && c > 0)
            {
                counts[s] = c - 1;
                shared++;
            }
        }
        return 2.0 * shared / (x.Count + y.Count);
    }

    /// <summary>Paragraphs added on one side, keyed by the base paragraph they follow (-1 = start).</summary>
    private static Dictionary<int, List<int>> InsertionsByAnchor(int[] match, int otherCount)
    {
        var matchedOther = new int[otherCount];
        Array.Fill(matchedOther, -1);
        for (int i = 0; i < match.Length; i++)
        {
            if (match[i] >= 0) matchedOther[match[i]] = i;
        }

        var result = new Dictionary<int, List<int>>();
        int anchor = -1;
        for (int j = 0; j < otherCount; j++)
        {
            if (matchedOther[j] >= 0)
            {
                anchor = matchedOther[j];
                continue;
            }
            if (!result.TryGetValue(anchor, out var list))
            {
                list = [];
                result[anchor] = list;
            }
            list.Add(j);
        }
        return result;
    }

    // ---- Word-level merge within a paragraph -----------------------------------------------------------

    public sealed record WordConflict(string Ours, string Theirs);

    /// <summary>
    /// diff3 over words. Returns the merged text in which every conflicting region is written as a pending
    /// CriticMarkup change from ours to theirs, plus the list of those conflicts.
    /// </summary>
    public static (string Text, List<WordConflict> Conflicts) MergeWords(string baseText, string oursText, string theirsText)
    {
        var b = DiffEngineService.TokenizeWords(baseText);
        var o = DiffEngineService.TokenizeWords(oursText);
        var t = DiffEngineService.TokenizeWords(theirsText);
        var bo = MatchTokens(b, o);
        var bt = MatchTokens(b, t);

        var sb = new StringBuilder();
        var conflicts = new List<WordConflict>();
        int i = 0, io = 0, it = 0;
        while (true)
        {
            // Next base token kept unchanged on both sides.
            int next = i;
            while (next < b.Length && !(bo[next] >= io && bt[next] >= it)) next++;

            int oEnd = next < b.Length ? bo[next] : o.Length;
            int tEnd = next < b.Length ? bt[next] : t.Length;
            string baseChunk = string.Concat(b[i..next]);
            string oursChunk = string.Concat(o[io..oEnd]);
            string theirsChunk = string.Concat(t[it..tEnd]);

            if (oursChunk == baseChunk) sb.Append(theirsChunk);
            else if (theirsChunk == baseChunk || theirsChunk == oursChunk) sb.Append(oursChunk);
            else
            {
                conflicts.Add(new WordConflict(oursChunk, theirsChunk));
                sb.Append(ConflictToken(oursChunk, theirsChunk));
            }

            if (next >= b.Length) break;
            sb.Append(b[next]);
            i = next + 1;
            io = bo[next] + 1;
            it = bt[next] + 1;
        }
        return (sb.ToString(), conflicts);
    }

    /// <summary>ours → theirs as CriticMarkup; accepting takes theirs, rejecting keeps ours.</summary>
    private static string ConflictToken(string ours, string theirs)
    {
        // Keep the whitespace around the token outside it so accepting or rejecting leaves clean spacing.
        string lead = LeadingSpace(ours.Length > 0 ? ours : theirs);
        string trail = TrailingSpace(ours.Length > 0 ? ours : theirs);
        string o = ours.Trim(), th = theirs.Trim();
        if (o.Length == 0) return $"{lead}{{++{th}++}}{trail}";
        if (th.Length == 0) return $"{lead}{{--{o}--}}{trail}";
        return $"{lead}{{~~{o}~>{th}~~}}{trail}";
    }

    private static string LeadingSpace(string s) => s[..(s.Length - s.TrimStart().Length)];
    private static string TrailingSpace(string s) => s[s.TrimEnd().Length..];

    /// <summary>For each token of <paramref name="a"/>, its index in <paramref name="c"/> on an LCS, or -1.</summary>
    private static int[] MatchTokens(string[] a, string[] c)
    {
        int n = a.Length, m = c.Length, width = m + 1;
        var dp = new int[(n + 1) * width];
        for (int i = n - 1; i >= 0; i--)
            for (int j = m - 1; j >= 0; j--)
                dp[i * width + j] = a[i] == c[j] ? 1 + dp[(i + 1) * width + j + 1] : Math.Max(dp[(i + 1) * width + j], dp[i * width + j + 1]);

        var map = Enumerable.Repeat(-1, n).ToArray();
        int x = 0, y = 0;
        while (x < n && y < m)
        {
            if (a[x] == c[y]) { map[x++] = y++; }
            else if (dp[(x + 1) * width + y] >= dp[x * width + y + 1]) x++;
            else y++;
        }
        return map;
    }

    // ---- Building the merged manuscript ----------------------------------------------------------------

    /// <summary>Adds blocks with their pending hunks, keeping paragraph and hunk ids unique.</summary>
    private sealed class Builder(ManuscriptDocument merged, ManuscriptDocument ours, ManuscriptDocument theirs, string theirsAuthor)
    {
        private readonly HashSet<string> _ids = new(StringComparer.Ordinal);
        private readonly HashSet<string> _hunkIds = new(StringComparer.Ordinal);
        private int _next = 1;

        public void AddFromOurs(DocumentBlock block) => Add(block, ours, block.ParagraphId);

        public void AddFromTheirs(DocumentBlock block, DocumentBlock? keepIdOf = null) => Add(block, theirs, keepIdOf?.ParagraphId ?? block.ParagraphId);

        public void AddMerged(DocumentBlock oursBlock, BlockStyleRole role, string text, List<WordConflict> conflicts)
        {
            string id = Claim(oursBlock.ParagraphId);
            merged.Blocks.Add(new DocumentBlock { ParagraphId = id, Role = role, Text = text });
            int n = 0;
            foreach (var c in conflicts)
            {
                string o = c.Ours.Trim(), t = c.Theirs.Trim();
                AddHunk(new RevisionHunk
                {
                    HunkId = $"{id}-m{++n:D2}",
                    ParagraphId = id,
                    HunkType = o.Length == 0 ? RevisionHunkType.Insertion : t.Length == 0 ? RevisionHunkType.Deletion : RevisionHunkType.Replacement,
                    OriginalText = o,
                    ProposedText = t,
                    Author = theirsAuthor,
                    Status = RevisionHunkStatus.Pending,
                    ContextSnippet = DiffEngineService.ToGoogleDocsText(text)
                });
            }
        }

        public void AddConflictKeepingTheirs(DocumentBlock theirsBlock, string reason)
        {
            string id = Claim(theirsBlock.ParagraphId);
            string text = theirsBlock.Text.Trim();
            if (HasMarkup(text))
            {
                merged.Blocks.Add(new DocumentBlock { ParagraphId = id, Role = theirsBlock.Role, Text = text });
                CopyHunks(theirs, theirsBlock.ParagraphId, id);
                AddComment(id, $"This paragraph was {reason} but edited in the other version.");
                return;
            }
            merged.Blocks.Add(new DocumentBlock { ParagraphId = id, Role = theirsBlock.Role, Text = $"{{++{text}++}}" });
            AddHunk(new RevisionHunk { HunkId = $"{id}-m01", ParagraphId = id, HunkType = RevisionHunkType.Insertion, ProposedText = text, Author = theirsAuthor, ContextSnippet = DiffEngineService.ToGoogleDocsText(text) });
        }

        public void AddConflictProposingRemoval(DocumentBlock oursBlock)
        {
            string id = Claim(oursBlock.ParagraphId);
            string text = oursBlock.Text.Trim();
            if (HasMarkup(text))
            {
                merged.Blocks.Add(new DocumentBlock { ParagraphId = id, Role = oursBlock.Role, Text = text });
                CopyHunks(ours, oursBlock.ParagraphId, id);
                AddComment(id, "This paragraph was removed in the other version but edited in this one.");
                return;
            }
            merged.Blocks.Add(new DocumentBlock { ParagraphId = id, Role = oursBlock.Role, Text = $"{{--{text}--}}" });
            AddHunk(new RevisionHunk { HunkId = $"{id}-m01", ParagraphId = id, HunkType = RevisionHunkType.Deletion, OriginalText = text, Author = theirsAuthor, ContextSnippet = DiffEngineService.ToGoogleDocsText(text) });
        }

        public void AddWithComment(DocumentBlock oursBlock, BlockStyleRole role, string comment)
        {
            string id = Claim(oursBlock.ParagraphId);
            merged.Blocks.Add(new DocumentBlock { ParagraphId = id, Role = role, Text = oursBlock.Text.Trim() });
            CopyHunks(ours, oursBlock.ParagraphId, id);
            AddComment(id, comment);
        }

        private void Add(DocumentBlock block, ManuscriptDocument source, string preferredId)
        {
            string id = Claim(preferredId);
            var copy = block.Clone();
            copy.ParagraphId = id;
            merged.Blocks.Add(copy);
            CopyHunks(source, block.ParagraphId, id);
        }

        private void CopyHunks(ManuscriptDocument source, string fromId, string toId)
        {
            foreach (var hunk in source.Hunks.Where(h => h.ParagraphId == fromId && h.Status == RevisionHunkStatus.Pending))
            {
                var copy = hunk.Clone();
                copy.ParagraphId = toId;
                AddHunk(copy);
            }
        }

        private void AddComment(string paragraphId, string comment)
        {
            var block = merged.Blocks.Last(b => b.ParagraphId == paragraphId);
            string safe = comment.Replace("<<", "«").Replace("}", ")");
            block.Text = $"{block.Text} {{>>{theirsAuthor}: {safe}<<}}";
            AddHunk(new RevisionHunk { HunkId = $"{paragraphId}-mc", ParagraphId = paragraphId, HunkType = RevisionHunkType.Comment, CommentText = safe, Author = theirsAuthor, ContextSnippet = DiffEngineService.ToGoogleDocsText(block.Text) });
        }

        private void AddHunk(RevisionHunk hunk)
        {
            string id = hunk.HunkId;
            for (int n = 2; !_hunkIds.Add(id); n++) id = $"{hunk.HunkId}-{n}";
            hunk.HunkId = id;
            hunk.Status = RevisionHunkStatus.Pending;
            merged.Hunks.Add(hunk);
        }

        private string Claim(string preferred)
        {
            if (!string.IsNullOrEmpty(preferred) && _ids.Add(preferred)) return preferred;
            var taken = new HashSet<string>(ours.Blocks.Select(b => b.ParagraphId).Concat(theirs.Blocks.Select(b => b.ParagraphId)), StringComparer.Ordinal);
            while (true)
            {
                string id = $"p-m{_next++:D3}";
                if (!taken.Contains(id) && _ids.Add(id)) return id;
            }
        }
    }
}
