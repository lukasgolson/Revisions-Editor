using System.Text.RegularExpressions;
using RevisionsEditor.Core.Models;
using RevisionsEditor.Core.Scholarly;
using RevisionsEditor.Core.Serialization;

namespace RevisionsEditor.Core.Checks;

/// <summary>A heading and the paragraphs under it (up to the next heading of the same or a higher level).</summary>
public sealed record ManuscriptSection(DocumentBlock? Heading, string Title, IReadOnlyList<DocumentBlock> Content);

/// <summary>Finds the parts of a paper a journal asks about: abstract, keywords, highlights and named sections.</summary>
public sealed class ManuscriptStructure
{
    private static readonly Regex KeywordsPrefixRegex = new(
        @"^\s*\**\s*(key\s*words|keywords|index terms)\s*\**\s*[:—–-]\s*\**",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex BulletPrefixRegex = new(@"^\s*([•●▪‣⁃∙*•-]|\d+[.)])\s+", RegexOptions.Compiled);

    public IReadOnlyList<DocumentBlock> Blocks { get; }
    public List<ManuscriptSection> Sections { get; } = [];

    public ManuscriptStructure(IReadOnlyList<DocumentBlock> blocks)
    {
        Blocks = blocks;
        for (int i = 0; i < blocks.Count; i++)
        {
            var block = blocks[i];
            int level = HeadingLevel(block.Role);
            if (level == 0)
            {
                continue;
            }
            var content = new List<DocumentBlock>();
            for (int j = i + 1; j < blocks.Count; j++)
            {
                int next = HeadingLevel(blocks[j].Role);
                if (next > 0 && next <= level)
                {
                    break;
                }
                content.Add(blocks[j]);
            }
            Sections.Add(new ManuscriptSection(block, PlainText(block.Text, heading: true), content));
        }
    }

    private static int HeadingLevel(BlockStyleRole role) => role switch
    {
        BlockStyleRole.Heading1 => 1,
        BlockStyleRole.Heading2 => 2,
        BlockStyleRole.Heading3 => 3,
        _ => 0
    };

    /// <summary>Text as it reads in the paper: pending changes accepted, markers and labels removed.</summary>
    public static string PlainText(string text, bool heading = false)
    {
        string t = CriticMarkupSerializer.ResolveCriticMarkupText(text, MarkupDisplayMode.NoMarkupFinal);
        if (heading)
        {
            t = ScholarlyBlocks.StripHeadingLabel(t);
        }
        return Diffing.DiffEngineService.ToGoogleDocsText(InlineFormatting.StripMarkers(t));
    }

    /// <summary>The section whose heading is <paramref name="title"/> or one of the aliases (case-insensitive, ignoring punctuation).</summary>
    public ManuscriptSection? FindSection(string title, IEnumerable<string>? aliases = null)
    {
        var names = new[] { title }.Concat(aliases ?? []).Select(NormalizeHeading).ToList();
        return Sections.FirstOrDefault(s => names.Contains(NormalizeHeading(s.Title)))
            ?? Sections.FirstOrDefault(s => names.Any(n => n.Length >= 8 && NormalizeHeading(s.Title).StartsWith(n, StringComparison.Ordinal)));
    }

    public static string NormalizeHeading(string heading)
    {
        string lower = heading.ToLowerInvariant().Replace('’', '\'');
        lower = Regex.Replace(lower, @"^\s*[\d.]+\s*", string.Empty);
        lower = Regex.Replace(lower, @"[^a-z0-9' ]+", " ");
        return Regex.Replace(lower, @"\s+", " ").Trim();
    }

    /// <summary>The abstract's paragraphs: blocks styled Abstract, or the paragraphs under an "Abstract" heading.</summary>
    public List<DocumentBlock> AbstractBlocks()
    {
        var styled = Blocks.Where(b => b.Role == BlockStyleRole.Abstract).ToList();
        if (styled.Count > 0)
        {
            return styled;
        }
        return FindSection("Abstract")?.Content.Where(b => b.Role is BlockStyleRole.BodyParagraph).ToList() ?? [];
    }

    /// <summary>The keywords (from "Keywords: a; b; c" or a Keywords heading), with the block they are in.</summary>
    public (DocumentBlock? Block, List<string> Keywords) Keywords()
    {
        foreach (var block in Blocks.Take(60))
        {
            string text = PlainText(block.Text);
            var m = KeywordsPrefixRegex.Match(text);
            if (m.Success && block.Role is not BlockStyleRole.Heading1 and not BlockStyleRole.Heading2 and not BlockStyleRole.Heading3)
            {
                return (block, SplitKeywords(text[m.Length..]));
            }
        }
        var section = FindSection("Keywords", ["key words", "index terms"]);
        var first = section?.Content.FirstOrDefault();
        return first != null ? (first, SplitKeywords(PlainText(first.Text))) : (null, []);
    }

    private static List<string> SplitKeywords(string text)
    {
        char separator = text.Contains(';') ? ';' : text.Contains('·') ? '·' : ',';
        return text.Split(separator).Select(k => k.Trim().TrimEnd('.').Trim()).Where(k => k.Length > 0).ToList();
    }

    /// <summary>The highlight bullets under a "Highlights" heading (one paragraph each), or null when there is no such section.</summary>
    /// <param name="render">Renders citations and linked numbers, so lengths are those of the published text.</param>
    public List<(DocumentBlock Block, string Text)>? Highlights(Func<string, string>? render = null)
    {
        var section = FindSection("Highlights", ["research highlights"]);
        if (section == null)
        {
            return null;
        }
        var bullets = new List<(DocumentBlock, string)>();
        // The bullets are the ordinary paragraphs straight after the heading; the abstract or keywords end them.
        foreach (var block in section.Content.TakeWhile(b => b.Role == BlockStyleRole.BodyParagraph && !KeywordsPrefixRegex.IsMatch(PlainText(b.Text))))
        {
            string raw = CriticMarkupSerializer.ResolveCriticMarkupText(block.Text, MarkupDisplayMode.NoMarkupFinal);
            string text = render != null
                ? Diffing.DiffEngineService.ToGoogleDocsText(InlineFormatting.StripMarkers(render(raw)))
                : PlainText(block.Text);
            // MDPI's two prompts are headings within the highlights, not bullets.
            if (text.EndsWith('?') || text.Length == 0)
            {
                continue;
            }
            bullets.Add((block, BulletPrefixRegex.Replace(text, string.Empty).Trim()));
        }
        return bullets;
    }
}
