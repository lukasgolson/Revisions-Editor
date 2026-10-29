using System.Text.RegularExpressions;
using RevisionsEditor.Core.Formatting;
using RevisionsEditor.Core.Journals;
using RevisionsEditor.Core.Models;
using RevisionsEditor.Core.Scholarly;
using RevisionsEditor.Core.Serialization;

namespace RevisionsEditor.Core.Checks;

/// <summary>Word and character counts of some text.</summary>
public sealed record TextCounts(int Words, int Characters, int CharactersWithSpaces, int Paragraphs)
{
    public static TextCounts Zero { get; } = new(0, 0, 0, 0);

    public static TextCounts Of(IEnumerable<string> paragraphs)
    {
        int words = 0, characters = 0, withSpaces = 0, count = 0;
        foreach (string text in paragraphs)
        {
            if (text.Trim().Length == 0)
            {
                continue;
            }
            count++;
            words += ManuscriptStatistics.CountWords(text);
            withSpaces += text.Length;
            characters += text.Count(c => !char.IsWhiteSpace(c));
        }
        return new TextCounts(words, characters, withSpaces, count);
    }
}

/// <summary>Words in one section of the paper.</summary>
public sealed record SectionCount(string ParagraphId, string Title, int Level, int Words);

/// <summary>A sentence long enough to be worth splitting.</summary>
public sealed record LongSentence(string ParagraphId, string Text, int Words);

/// <summary>The journal's length limit and how the manuscript measures against it.</summary>
public sealed record WordLimit(int Limit, int Counted, string Scope)
{
    public bool Exceeded => Counted > Limit;
}

/// <summary>
/// Counts as they will appear in the published paper (citations, cross-references and linked numbers rendered,
/// pending changes accepted, markup removed), for the whole manuscript and its parts.
/// </summary>
public sealed class ManuscriptStatistics
{
    public const int LongSentenceWords = 40;

    // As Word counts: anything between spaces that contains a letter or a digit ("leaf-off", "0.42", "(2016)").
    private static readonly Regex WordRegex = new(@"[^\s]*[\p{L}\p{N}][^\s]*", RegexOptions.Compiled);
    private static readonly Regex SentenceRegex = new(@"[^.!?]+(?:[.!?]+(?=\s|$)|$)", RegexOptions.Compiled);
    private static readonly Regex AbbreviationDotRegex = new(@"\b(e\.g|i\.e|et al|cf|vs|approx|Fig|Figs|Eq|Eqs|Sect|no|pp?)\.", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public TextCounts Total { get; private init; } = TextCounts.Zero;
    /// <summary>From the first numbered section to the start of the back matter, without captions.</summary>
    public TextCounts MainText { get; private init; } = TextCounts.Zero;
    public TextCounts Abstract { get; private init; } = TextCounts.Zero;
    public TextCounts Captions { get; private init; } = TextCounts.Zero;
    public TextCounts References { get; private init; } = TextCounts.Zero;
    public TextCounts BackMatter { get; private init; } = TextCounts.Zero;
    public IReadOnlyList<SectionCount> Sections { get; private init; } = [];
    public IReadOnlyList<LongSentence> LongSentences { get; private init; } = [];
    public int Sentences { get; private init; }
    public double AverageSentenceWords { get; private init; }
    public int Figures { get; private init; }
    public int Tables { get; private init; }
    public int Equations { get; private init; }
    public int Citations { get; private init; }
    public int CitedReferences { get; private init; }
    public WordLimit? JournalLimit { get; private init; }
    public int? AbstractLimit { get; private init; }

    public static int CountWords(string text) => WordRegex.Count(text);

    /// <summary>Counts for blocks whose formatting rules have been applied (headings numbered).</summary>
    public static ManuscriptStatistics Compute(IReadOnlyList<DocumentBlock> blocks, ScholarlyContext? context, JournalProfile? journal = null)
    {
        string Published(DocumentBlock block)
        {
            string text = CriticMarkupSerializer.ResolveCriticMarkupText(block.Text, MarkupDisplayMode.NoMarkupFinal);
            text = block.Role switch
            {
                BlockStyleRole.Figure or BlockStyleRole.Table => ScholarlyBlocks.SplitCaption(text)?.Caption ?? text,
                BlockStyleRole.Equation => string.Empty,
                BlockStyleRole.Heading1 or BlockStyleRole.Heading2 or BlockStyleRole.Heading3 => ScholarlyBlocks.StripHeadingLabel(text),
                _ => text
            };
            if (context != null)
            {
                text = context.Render(text);
            }
            return Diffing.DiffEngineService.ToGoogleDocsText(InlineFormatting.StripMarkers(text));
        }

        var published = blocks.Select(b => (Block: b, Text: Published(b))).ToList();
        var structure = new ManuscriptStructure(blocks);
        var abstractIds = structure.AbstractBlocks().Select(b => b.ParagraphId).ToHashSet(StringComparer.Ordinal);

        // Main text: from the first section that is neither front nor back matter, up to the back matter.
        var main = new List<string>();
        var back = new List<string>();
        var sections = new List<SectionCount>();
        bool inMain = false, inBack = false;
        int sectionIndex = -1;
        foreach (var (block, text) in published)
        {
            bool heading = block.Role is BlockStyleRole.Heading1 or BlockStyleRole.Heading2 or BlockStyleRole.Heading3;
            if (heading && block.Role == BlockStyleRole.Heading1 || heading && !inMain)
            {
                if (FormattingRuleEngine.IsBackMatterHeading(block))
                {
                    inBack = true;
                    inMain = false;
                }
                else if (!FormattingRuleEngine.IsFrontMatterHeading(block) && !inBack)
                {
                    inMain = true;
                }
                else if (FormattingRuleEngine.IsFrontMatterHeading(block))
                {
                    inMain = false;
                }
            }
            if (heading)
            {
                int level = block.Role == BlockStyleRole.Heading1 ? 1 : block.Role == BlockStyleRole.Heading2 ? 2 : 3;
                sections.Add(new SectionCount(block.ParagraphId, (block.ComputedSectionNumber + text).Trim(), level, 0));
                sectionIndex = sections.Count - 1;
            }
            if (block.Role == BlockStyleRole.Abstract)
            {
                // The abstract (and keywords after it) belong to no section, even after a Highlights heading.
                sectionIndex = -1;
            }
            if (sectionIndex >= 0)
            {
                // A section's count includes its subsections (their headings too), as in the navigation pane of Word.
                int words = CountWords(text);
                for (int k = sectionIndex, level = sections[sectionIndex].Level; k >= 0; k--)
                {
                    if (sections[k].Level <= level)
                    {
                        sections[k] = sections[k] with { Words = sections[k].Words + words };
                        level = sections[k].Level - 1;
                        if (level < 1)
                        {
                            break;
                        }
                    }
                }
            }

            bool caption = block.Role is BlockStyleRole.Figure or BlockStyleRole.Table;
            if (inMain && !caption)
            {
                main.Add(text);
            }
            if (inBack && block.Role != BlockStyleRole.ReferenceItem)
            {
                back.Add(text);
            }
        }

        var referenceTexts = context?.Citations.References.Select(r => Diffing.DiffEngineService.ToGoogleDocsText(InlineFormatting.StripMarkers(r.Text))).ToList() ?? [];
        referenceTexts.AddRange(published.Where(p => p.Block.Role == BlockStyleRole.ReferenceItem).Select(p => p.Text));

        // Sentences of the prose (main text and abstract).
        var longSentences = new List<LongSentence>();
        int sentenceCount = 0, sentenceWords = 0;
        foreach (var (block, text) in published.Where(p => p.Block.Role is BlockStyleRole.BodyParagraph or BlockStyleRole.Abstract or BlockStyleRole.BlockQuote))
        {
            string protectedText = AbbreviationDotRegex.Replace(text, m => m.Value.Replace('.', '․'));
            foreach (Match m in SentenceRegex.Matches(protectedText))
            {
                int words = CountWords(m.Value);
                if (words == 0)
                {
                    continue;
                }
                sentenceCount++;
                sentenceWords += words;
                if (words > LongSentenceWords)
                {
                    longSentences.Add(new LongSentence(block.ParagraphId, m.Value.Replace('․', '.').Trim(), words));
                }
            }
        }

        var mainCounts = TextCounts.Of(main);
        WordLimit? limit = null;
        if (journal?.MaxWords is { } max)
        {
            limit = journal.MaxWordsScope == WordLimitScope.MainText
                ? new WordLimit(max, mainCounts.Words, "main text, Introduction to Conclusions")
                : new WordLimit(max, TextCounts.Of(published.Select(p => p.Text).Concat(referenceTexts)).Words, "whole manuscript, with references and captions");
        }

        var citationCount = blocks.Sum(b => Bibliography.CitationSyntax.Find(CriticMarkupSerializer.ResolveCriticMarkupText(b.Text, MarkupDisplayMode.NoMarkupFinal)).Count);
        return new ManuscriptStatistics
        {
            Total = TextCounts.Of(published.Select(p => p.Text).Concat(referenceTexts)),
            MainText = mainCounts,
            Abstract = TextCounts.Of(published.Where(p => abstractIds.Contains(p.Block.ParagraphId)).Select(p => p.Text)),
            Captions = TextCounts.Of(published.Where(p => p.Block.Role is BlockStyleRole.Figure or BlockStyleRole.Table or BlockStyleRole.Caption).Select(p => p.Text)),
            References = TextCounts.Of(referenceTexts),
            BackMatter = TextCounts.Of(back),
            Sections = sections,
            LongSentences = longSentences,
            Sentences = sentenceCount,
            AverageSentenceWords = sentenceCount == 0 ? 0 : (double)sentenceWords / sentenceCount,
            Figures = blocks.Count(b => b.Role == BlockStyleRole.Figure),
            Tables = blocks.Count(b => b.Role == BlockStyleRole.Table),
            Equations = blocks.Count(b => b.Role == BlockStyleRole.Equation),
            Citations = citationCount,
            CitedReferences = context?.Citations.CitedKeys.Count ?? 0,
            JournalLimit = limit,
            AbstractLimit = journal?.AbstractMaxWords
        };
    }
}
