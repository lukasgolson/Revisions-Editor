using RevisionsEditor.Core.Models;
using RevisionsEditor.Core.Scholarly;
using RevisionsEditor.Core.Serialization;

namespace RevisionsEditor.Core.Merging;

public sealed class GoogleDocsReplaceResult
{
    public ManuscriptDocument Document { get; set; } = new();
    /// <summary>Figures in the Google Doc shown with the manuscript's own figure (its file, label and options).</summary>
    public int FiguresMatched { get; set; }
    /// <summary>Figures (and equations) the Google Doc does not have, kept where they were.</summary>
    public int ObjectsKept { get; set; }
    /// <summary>Images only in the Google Doc, added as new figures.</summary>
    public int FiguresAdded { get; set; }
    /// <summary>
    /// Tables whose cells came from the Google Doc into a new file, to move onto the manuscript's own table file
    /// (keeping its label): from → to, relative to the workspace.
    /// </summary>
    public List<(string From, string To)> TableFileMoves { get; } = [];

    public string Summary
    {
        get
        {
            var parts = new List<string> { "text replaced with the Google Doc" };
            if (FiguresMatched > 0) parts.Add($"{FiguresMatched} figure{(FiguresMatched == 1 ? string.Empty : "s")} kept as drawn here");
            if (ObjectsKept > 0) parts.Add($"{ObjectsKept} figure{(ObjectsKept == 1 ? string.Empty : "s")} or equation{(ObjectsKept == 1 ? string.Empty : "s")} missing from the Google Doc kept in place");
            if (FiguresAdded > 0) parts.Add($"{FiguresAdded} new image{(FiguresAdded == 1 ? string.Empty : "s")} from the Google Doc added");
            return string.Join("; ", parts) + ".";
        }
    }
}

/// <summary>
/// Makes the manuscript the Google Doc: its text, headings, captions and tables exactly as they are there, with
/// no merge, while the figures stay the manuscript's own. Google Docs only holds pictures of the figures (and
/// the app never sends figures or equations there), so:
/// <list type="bullet">
/// <item>an image in the Google Doc is shown with the manuscript's figure it stands for (matched by caption,
/// otherwise by order), keeping its file (SVG, PDF or diagram), label and options, with the caption from the
/// Google Doc;</item>
/// <item>figures and equations the Google Doc does not have are kept, after the text they followed;</item>
/// <item>an image only in the Google Doc becomes a new figure;</item>
/// <item>a table keeps the manuscript's label and file, with the cells from the Google Doc.</item>
/// </list>
/// Pending changes are dropped (the replaced version stays in the history).
/// </summary>
public static class GoogleDocsReplace
{
    public static GoogleDocsReplaceResult Replace(ManuscriptDocument current, ManuscriptDocument pulled)
    {
        var result = new GoogleDocsReplaceResult();
        var currentBlocks = current.Blocks.Select(b => Final(b)).ToList();
        var pulledBlocks = pulled.Blocks.Select(b => b.Clone()).ToList();

        // ---- Figures and tables of the Google Doc paired with the manuscript's -------------------------
        var figurePairs = PairObjects(currentBlocks, pulledBlocks, BlockStyleRole.Figure);
        var tablePairs = PairObjects(currentBlocks, pulledBlocks, BlockStyleRole.Table);

        var output = new List<DocumentBlock>();
        var placedCurrent = new HashSet<int>();      // manuscript blocks with a place in the result
        var resultIndexOfCurrent = new Dictionary<int, int>();
        for (int i = 0; i < pulledBlocks.Count; i++)
        {
            var p = pulledBlocks[i];
            if (p.Role == BlockStyleRole.Figure)
            {
                if (figurePairs.TryGetValue(i, out int c))
                {
                    var mine = currentBlocks[c].Clone();
                    var ours = ScholarlyBlocks.ParseFigure(mine.Text);
                    var theirs = ScholarlyBlocks.ParseFigure(p.Text);
                    if (ours != null && theirs != null && theirs.Caption.Trim().Length > 0)
                    {
                        mine.Text = ScholarlyBlocks.FormatFigure(theirs.Caption, ours.ImagePath, ours.Label, ours.Attributes);
                    }
                    Add(mine, c);
                    result.FiguresMatched++;
                }
                else
                {
                    Add(p, null);
                    result.FiguresAdded++;
                }
                continue;
            }
            if (p.Role == BlockStyleRole.Table && tablePairs.TryGetValue(i, out int t))
            {
                var mine = ScholarlyBlocks.ParseTable(currentBlocks[t].Text);
                var theirs = ScholarlyBlocks.ParseTable(p.Text);
                if (mine != null && theirs != null)
                {
                    if (mine.CsvPath != null && theirs.CsvPath != null && !string.Equals(mine.CsvPath, theirs.CsvPath, StringComparison.OrdinalIgnoreCase))
                    {
                        result.TableFileMoves.Add((theirs.CsvPath, mine.CsvPath));
                    }
                    var attributes = mine.Attributes.Where(a => !a.Key.Equals("file", StringComparison.OrdinalIgnoreCase)).ToDictionary(a => a.Key, a => a.Value);
                    p = new DocumentBlock { ParagraphId = p.ParagraphId, Role = p.Role, Text = ScholarlyBlocks.FormatTable(theirs.Caption, mine.Label, mine.CsvPath ?? theirs.CsvPath, attributes) };
                }
                Add(p, t);
                continue;
            }
            Add(p, null);
        }

        // Text paragraphs keep the manuscript's paragraph ids where they are the same paragraph (so notes and
        // review links stay attached).
        var textCurrent = currentBlocks.Select((b, i) => (b, i)).Where(x => !IsObject(x.b)).ToList();
        var textOutput = output.Select((b, i) => (b, i)).Where(x => !IsObject(x.b)).ToList();
        int[] map = ManuscriptMerger.Align(textCurrent.Select(x => x.b).ToList(), textOutput.Select(x => x.b).ToList(), trustParagraphIds: false);
        for (int k = 0; k < map.Length; k++)
        {
            if (map[k] < 0) continue;
            int ci = textCurrent[k].i, oi = textOutput[map[k]].i;
            output[oi].ParagraphId = currentBlocks[ci].ParagraphId;
            placedCurrent.Add(ci);
            resultIndexOfCurrent[ci] = oi;
        }

        // ---- Figures and equations the Google Doc does not have: after the block they followed --------
        for (int ci = 0; ci < currentBlocks.Count; ci++)
        {
            var block = currentBlocks[ci];
            if (placedCurrent.Contains(ci) || block.Role is not (BlockStyleRole.Figure or BlockStyleRole.Equation))
            {
                continue;
            }
            int anchor = -1;
            for (int j = ci - 1; j >= 0; j--)
            {
                if (resultIndexOfCurrent.TryGetValue(j, out int at))
                {
                    anchor = at;
                    break;
                }
            }
            output.Insert(anchor + 1, block.Clone());
            // Later positions shift by one.
            foreach (var key in resultIndexOfCurrent.Keys.ToList())
            {
                if (resultIndexOfCurrent[key] > anchor) resultIndexOfCurrent[key]++;
            }
            resultIndexOfCurrent[ci] = anchor + 1;
            placedCurrent.Add(ci);
            result.ObjectsKept++;
        }

        // Unique paragraph ids (the Google Doc's are positions, and may repeat the manuscript's).
        var used = new HashSet<string>(StringComparer.Ordinal);
        var all = new HashSet<string>(output.Select(b => b.ParagraphId).Concat(currentBlocks.Select(b => b.ParagraphId)), StringComparer.Ordinal);
        int next = 1;
        foreach (var block in output)
        {
            if (used.Add(block.ParagraphId)) continue;
            string id;
            do id = $"p-g{next++:D3}"; while (all.Contains(id));
            all.Add(id);
            used.Add(id);
            block.ParagraphId = id;
        }

        var titleBlock = output.FirstOrDefault(b => b.Role == BlockStyleRole.Title);
        var document = new ManuscriptDocument
        {
            DocumentId = current.DocumentId,
            Title = titleBlock != null ? Diffing.DiffEngineService.ToGoogleDocsText(titleBlock.Text) : pulled.Title,
            GoogleDocId = pulled.GoogleDocId ?? current.GoogleDocId,
            GoogleDocTabId = pulled.GoogleDocTabId ?? current.GoogleDocTabId,
            GoogleDocTabTitle = pulled.GoogleDocTabTitle ?? current.GoogleDocTabTitle,
            ActiveFormattingProfile = current.ActiveFormattingProfile,
            Blocks = output
        };
        result.Document = document;
        return result;

        void Add(DocumentBlock block, int? fromCurrent)
        {
            output.Add(block);
            if (fromCurrent is { } c)
            {
                placedCurrent.Add(c);
                resultIndexOfCurrent[c] = output.Count - 1;
            }
        }
    }

    private static bool IsObject(DocumentBlock b) => b.Role is BlockStyleRole.Figure or BlockStyleRole.Table or BlockStyleRole.Equation;

    /// <summary>The block as it reads with its pending changes accepted (the Google Doc shows that version).</summary>
    private static DocumentBlock Final(DocumentBlock b)
    {
        var copy = b.Clone();
        copy.Text = CriticMarkupSerializer.ResolveCriticMarkupText(b.Text, MarkupDisplayMode.NoMarkupFinal).Trim();
        return copy;
    }

    /// <summary>
    /// Pulled figures (or tables) paired with the manuscript's: the same file or label first (the import already
    /// recognised it), then by caption, then the rest in order between those pairs. pulled index → current index.
    /// </summary>
    private static Dictionary<int, int> PairObjects(List<DocumentBlock> current, List<DocumentBlock> pulled, BlockStyleRole role)
    {
        var mine = current.Select((b, i) => (b, i)).Where(x => x.b.Role == role).ToList();
        var theirs = pulled.Select((b, i) => (b, i)).Where(x => x.b.Role == role).ToList();
        var pairs = new Dictionary<int, int>(); // theirs position → mine position (within the lists above)

        string? Identity(DocumentBlock b) => role == BlockStyleRole.Figure
            ? ScholarlyBlocks.ParseFigure(b.Text) is { } f ? f.Label ?? f.ImagePath : null
            : ScholarlyBlocks.ParseTable(b.Text) is { } t ? t.Label ?? t.CsvPath : null;
        string Caption(DocumentBlock b) => (role == BlockStyleRole.Figure ? ScholarlyBlocks.ParseFigure(b.Text)?.Caption : ScholarlyBlocks.ParseTable(b.Text)?.Caption) ?? string.Empty;

        // Recognised by the import (same image bytes, same table cells): the same label or file.
        for (int k = 0; k < theirs.Count; k++)
        {
            string? id = Identity(theirs[k].b);
            int m = id == null ? -1 : mine.FindIndex(x => Identity(x.b) == id);
            if (m >= 0 && !pairs.ContainsValue(m)) pairs[k] = m;
        }
        // By caption, keeping order.
        var mineLeft = mine.Select((x, m) => (x, m)).Where(y => !pairs.ContainsValue(y.m)).ToList();
        var theirsLeft = theirs.Select((x, k) => (x, k)).Where(y => !pairs.ContainsKey(y.k)).ToList();
        if (mineLeft.Count > 0 && theirsLeft.Count > 0)
        {
            int[] byCaption = ManuscriptMerger.Align(
                mineLeft.Select(y => new DocumentBlock { ParagraphId = $"m{y.m}", Text = Caption(y.x.b) }).ToList(),
                theirsLeft.Select(y => new DocumentBlock { ParagraphId = $"t{y.k}", Text = Caption(y.x.b) }).ToList(),
                trustParagraphIds: false);
            for (int a = 0; a < byCaption.Length; a++)
            {
                if (byCaption[a] >= 0 && Caption(mineLeft[a].x.b).Trim().Length > 0)
                {
                    pairs[theirsLeft[byCaption[a]].k] = mineLeft[a].m;
                }
            }
        }
        // The rest in order, between the pairs already made: an image whose caption was lost or reworded stands for
        // the figure in the same place. Only where both sides have the same number left there, and the captions
        // are not plainly about something else; otherwise the image is new and the figure is kept as well.
        var pairedTheirs = pairs.Keys.OrderBy(k => k).ToList();
        var bounds = new List<(int Theirs, int Mine)> { (-1, -1) };
        bounds.AddRange(pairedTheirs.Select(k => (k, pairs[k])));
        bounds.Add((theirs.Count, mine.Count));
        for (int g = 0; g + 1 < bounds.Count; g++)
        {
            var gapTheirs = Enumerable.Range(bounds[g].Theirs + 1, Math.Max(0, bounds[g + 1].Theirs - bounds[g].Theirs - 1)).Where(k => !pairs.ContainsKey(k)).ToList();
            var gapMine = Enumerable.Range(bounds[g].Mine + 1, Math.Max(0, bounds[g + 1].Mine - bounds[g].Mine - 1)).Where(m => !pairs.ContainsValue(m)).ToList();
            if (gapTheirs.Count == 0 || gapTheirs.Count != gapMine.Count) continue;
            for (int x = 0; x < gapTheirs.Count; x++)
            {
                string a = Caption(theirs[gapTheirs[x]].b), b = Caption(mine[gapMine[x]].b);
                if (a.Trim().Length == 0 || Provenance.TextCanonicalizer.ComputeSentenceSimilarity(a, b) >= 0.25)
                {
                    pairs[gapTheirs[x]] = gapMine[x];
                }
            }
        }
        return pairs.ToDictionary(kv => theirs[kv.Key].i, kv => mine[kv.Value].i);
    }
}
