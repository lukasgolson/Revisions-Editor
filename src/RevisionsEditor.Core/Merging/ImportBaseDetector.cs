using RevisionsEditor.Core.Models;
using RevisionsEditor.Core.Provenance;
using RevisionsEditor.Core.Serialization;

namespace RevisionsEditor.Core.Merging;

public sealed record ImportBaseCandidate(string CommitSha, double Score);

/// <summary>
/// Finds which committed version a reviewer's Word document started from. A document returned with tracked
/// changes still contains the text it was sent with (its "Original" view); that text is compared with the
/// signed snapshot of every commit, so copies sent out at different times can be imported in any order.
/// </summary>
public static class ImportBaseDetector
{
    /// <summary>The text of <paramref name="reviewed"/> before its tracked changes.</summary>
    public static ManuscriptDocument OriginalVersionOf(ManuscriptDocument reviewed)
    {
        var original = new ManuscriptDocument { Title = reviewed.Title };
        foreach (var block in reviewed.Blocks)
        {
            string text = CriticMarkupSerializer.ResolveCriticMarkupText(block.Text, MarkupDisplayMode.Original).Trim();
            if (text.Length > 0)
            {
                original.Blocks.Add(new DocumentBlock { ParagraphId = block.ParagraphId, Role = block.Role, Text = text });
            }
        }
        return original;
    }

    /// <summary>
    /// Commits ranked by how closely their text matches <paramref name="original"/>: share of identical
    /// paragraphs first, then of words; among equal matches the most recent commit comes first.
    /// </summary>
    public static List<ImportBaseCandidate> Rank(ManuscriptDocument original, IReadOnlyList<TextChainBlock> blocksInHistoryOrder)
    {
        string snapshot = TextCanonicalizer.SerializeCanonicalTextSnapshot(original);
        var lines = Lines(snapshot);
        var words = Words(snapshot);

        return blocksInHistoryOrder
            .Select((block, order) =>
            {
                double lineScore = Dice(lines, Lines(block.CanonicalManuscriptSnapshot));
                double wordScore = Dice(words, Words(block.CanonicalManuscriptSnapshot));
                return (block.CommitSha, Score: Math.Round(0.85 * lineScore + 0.15 * wordScore, 4), order);
            })
            .GroupBy(c => c.CommitSha, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderByDescending(c => c.order).First())
            .OrderByDescending(c => c.Score)
            .ThenByDescending(c => c.order)
            .Select(c => new ImportBaseCandidate(c.CommitSha, c.Score))
            .ToList();
    }

    private static List<string> Lines(string snapshot)
        => snapshot.Replace("\r\n", "\n").Split('\n', StringSplitOptions.RemoveEmptyEntries).ToList();

    private static List<string> Words(string snapshot)
        => snapshot.Split([' ', '\n'], StringSplitOptions.RemoveEmptyEntries).Select(w => w.ToLowerInvariant()).ToList();

    /// <summary>Dice coefficient over multisets.</summary>
    private static double Dice(List<string> a, List<string> b)
    {
        if (a.Count == 0 && b.Count == 0) return 1;
        if (a.Count == 0 || b.Count == 0) return 0;
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (string s in a) counts[s] = counts.GetValueOrDefault(s) + 1;
        int shared = 0;
        foreach (string s in b)
        {
            if (counts.TryGetValue(s, out int c) && c > 0)
            {
                counts[s] = c - 1;
                shared++;
            }
        }
        return 2.0 * shared / (a.Count + b.Count);
    }
}
