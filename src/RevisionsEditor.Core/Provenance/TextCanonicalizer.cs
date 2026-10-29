using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using RevisionsEditor.Core.Models;
using RevisionsEditor.Core.Serialization;

namespace RevisionsEditor.Core.Provenance;

public static class TextCanonicalizer
{
    private static readonly Regex MultiWhitespaceRegex = new(@"\s+", RegexOptions.Compiled);
    private static readonly Regex LeadingSectionNumberRegex = new(@"^\d+(\.\d+)*\.?\s+", RegexOptions.Compiled);
    private static readonly Regex SentenceSplitRegex = new(@"(?<=[\.\!\?])\s+(?=[A-Z0-9""'\(\[])", RegexOptions.Compiled);
    private static readonly Regex MarkdownInlineFormattingRegex = new(@"(\*\*|\*|__|_|`)", RegexOptions.Compiled);
    private static readonly Regex WordRegex = new(@"\b\w+\b", RegexOptions.Compiled);

    /// <summary>
    /// Normalizes a raw sentence or paragraph string so that presentation-only differences
    /// (smart vs straight quotes, double spaces, heading numbers, inline bold/italic markers)
    /// do not change the canonical text content hash.
    /// </summary>
    public static string CanonicalizeSentenceText(string rawText)
    {
        if (string.IsNullOrWhiteSpace(rawText))
        {
            return string.Empty;
        }

        string resolved = CriticMarkupSerializer.ResolveCriticMarkupText(rawText, MarkupDisplayMode.NoMarkupFinal);
        resolved = resolved.Normalize(NormalizationForm.FormC);

        // Normalize curly/smart quotes and dashes to canonical ASCII equivalents so typographic rules don't alter text hashes
        resolved = resolved
            .Replace('\u2018', '\'')
            .Replace('\u2019', '\'')
            .Replace('\u201C', '"')
            .Replace('\u201D', '"')
            .Replace("\u2014", "--")
            .Replace("\u2013", "-");

        // Strip purely visual inline markdown emphasis markers
        resolved = MarkdownInlineFormattingRegex.Replace(resolved, string.Empty);

        // Strip auto-generated heading numbers (e.g. "1.2 Introduction" -> "Introduction")
        resolved = LeadingSectionNumberRegex.Replace(resolved.Trim(), string.Empty);

        // Collapse redundant whitespace
        resolved = MultiWhitespaceRegex.Replace(resolved, " ").Trim();
        return resolved;
    }

    public static List<string> SplitIntoSentences(string blockText)
    {
        string resolved = CriticMarkupSerializer.ResolveCriticMarkupText(blockText, MarkupDisplayMode.NoMarkupFinal).Trim();
        if (string.IsNullOrWhiteSpace(resolved))
        {
            return [];
        }

        string[] parts = SentenceSplitRegex.Split(resolved);
        return parts
            .Select(CanonicalizeSentenceText)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .ToList();
    }

    /// <summary>
    /// Extracts all canonical sentences across a ManuscriptDocument in deterministic order.
    /// </summary>
    public static List<CanonicalSentenceLeaf> ExtractCanonicalLeaves(ManuscriptDocument document)
    {
        var leaves = new List<CanonicalSentenceLeaf>();
        int pOrder = 0;

        foreach (var block in document.Blocks)
        {
            pOrder++;
            var sentences = SplitIntoSentences(block.Text);
            int sOrder = 0;
            foreach (string canonical in sentences)
            {
                sOrder++;
                string leafPayload = $"{pOrder}:{sOrder}:{canonical}";
                leaves.Add(new CanonicalSentenceLeaf
                {
                    SentenceId = $"{block.ParagraphId}-s{sOrder:D2}",
                    ParagraphId = block.ParagraphId,
                    ParagraphOrder = pOrder,
                    SentenceOrder = sOrder,
                    CanonicalText = canonical,
                    LeafHash = ComputeSha256Hex(leafPayload)
                });
            }
        }

        return leaves;
    }

    /// <summary>
    /// Produces a canonical multi-line plain-text snapshot of the document's pure text content (one canonical paragraph per line).
    /// </summary>
    public static string SerializeCanonicalTextSnapshot(ManuscriptDocument document)
    {
        var lines = new List<string>();
        foreach (var block in document.Blocks)
        {
            var sentences = SplitIntoSentences(block.Text);
            if (sentences.Count > 0)
            {
                lines.Add(string.Join(" ", sentences));
            }
        }
        return string.Join("\n", lines);
    }

    /// <summary>
    /// Computes the binary Merkle Root over the canonical sentences in a canonical text snapshot or ManuscriptDocument.
    /// </summary>
    public static string ComputeMerkleRootFromCanonicalSnapshot(string canonicalSnapshot)
    {
        if (string.IsNullOrWhiteSpace(canonicalSnapshot))
        {
            return ComputeSha256Hex("EMPTY_MANUSCRIPT");
        }

        string[] lines = canonicalSnapshot.Replace("\r\n", "\n").Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var leafHashes = new List<string>();
        int pOrder = 0;
        foreach (string line in lines)
        {
            pOrder++;
            var sentences = SplitIntoSentences(line);
            int sOrder = 0;
            foreach (string s in sentences)
            {
                sOrder++;
                leafHashes.Add(ComputeSha256Hex($"{pOrder}:{sOrder}:{s}"));
            }
        }

        return ComputeMerkleRoot(leafHashes);
    }

    public static string ComputeMerkleRoot(IReadOnlyList<string> leafHashes)
    {
        if (leafHashes.Count == 0)
        {
            return ComputeSha256Hex("EMPTY_MANUSCRIPT");
        }

        var currentLayer = leafHashes.ToList();
        while (currentLayer.Count > 1)
        {
            var nextLayer = new List<string>((currentLayer.Count + 1) / 2);
            for (int i = 0; i < currentLayer.Count; i += 2)
            {
                string left = currentLayer[i];
                string right = (i + 1 < currentLayer.Count) ? currentLayer[i + 1] : left;
                nextLayer.Add(ComputeSha256Hex(left + right));
            }
            currentLayer = nextLayer;
        }

        return currentLayer[0];
    }

    public static string ComputeSha256Hex(string input)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(input ?? string.Empty);
        byte[] hash = SHA256.HashData(bytes);
        return Convert.ToHexStringLower(hash);
    }

    /// <summary>
    /// Computes word-level Jaccard/Dice similarity (0.0 .. 1.0) between two canonical sentences for tracking sentence evolution across edits.
    /// </summary>
    public static double ComputeSentenceSimilarity(string sentenceA, string sentenceB)
    {
        if (string.Equals(sentenceA, sentenceB, StringComparison.OrdinalIgnoreCase))
        {
            return 1.0;
        }

        return ComputeWordSetSimilarity(ExtractWordSet(sentenceA), ExtractWordSet(sentenceB));
    }

    /// <summary>
    /// Dice coefficient over two precomputed word sets (see <see cref="ExtractWordSet"/>).
    /// </summary>
    public static double ComputeWordSetSimilarity(HashSet<string> wordsA, HashSet<string> wordsB)
    {
        if (wordsA.Count == 0 || wordsB.Count == 0)
        {
            return 0.0;
        }

        var (smaller, larger) = wordsA.Count <= wordsB.Count ? (wordsA, wordsB) : (wordsB, wordsA);
        int intersection = 0;
        foreach (string word in smaller)
        {
            if (larger.Contains(word))
            {
                intersection++;
            }
        }
        return (2.0 * intersection) / (wordsA.Count + wordsB.Count);
    }

    public static HashSet<string> ExtractWordSet(string text)
    {
        var words = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in WordRegex.Matches(text ?? string.Empty))
        {
            words.Add(m.Value);
        }
        return words;
    }
}
