using System.Text.RegularExpressions;
using RevisionsEditor.Core.Models;

namespace RevisionsEditor.Core.Formatting;

public static class FormattingRuleEngine
{
    private static readonly Regex MultiSpaceRegex = new(@"[ \t]{2,}", RegexOptions.Compiled);
    private static readonly Regex DoubleSpaceAfterPeriodRegex = new(@"([\.\!\?])[ \t]{2,}", RegexOptions.Compiled);
    private static readonly Regex ExistingHeadingNumberPrefixRegex = new(@"^\d+(\.\d+)*\.?\s+", RegexOptions.Compiled);
    private static readonly Regex StraightDoubleQuotePairRegex = new(@"""([^""]+)""", RegexOptions.Compiled);
    private static readonly Regex HtmlOrInlineStyleGarbageRegex = new(@"<span[^>]*>|</span>|style=""[^""]*""", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Applies the active FormattingRuleProfile to a ManuscriptDocument:
    /// - Strips foreign inline style tags/artifacts
    /// - Normalizes whitespace & typography rules
    /// - Computes hierarchical section numbering (1., 1.1, 1.1.1) without mutating the canonical text content hash
    /// </summary>
    public static void ApplyFormattingRules(ManuscriptDocument document, FormattingRuleProfile profile)
    {
        document.ActiveFormattingProfile = profile.ProfileName;

        int h1 = 0, h2 = 0, h3 = 0;

        foreach (var block in document.Blocks)
        {
            string cleaned = block.Text;

            if (profile.StripForeignInlineStyles)
            {
                cleaned = HtmlOrInlineStyleGarbageRegex.Replace(cleaned, string.Empty);
            }

            if (profile.NormalizeWhitespace)
            {
                cleaned = MultiSpaceRegex.Replace(cleaned, " ").Trim();
            }

            if (profile.EnforceSingleSpaceAfterPeriod)
            {
                cleaned = DoubleSpaceAfterPeriodRegex.Replace(cleaned, "$1 ");
            }

            if (profile.ConvertStraightQuotesToSmart)
            {
                cleaned = ApplySmartQuotesOutsideCriticMarkup(cleaned);
            }
            else
            {
                cleaned = cleaned
                    .Replace('\u2018', '\'')
                    .Replace('\u2019', '\'')
                    .Replace('\u201C', '"')
                    .Replace('\u201D', '"');
            }

            if (block.Role is BlockStyleRole.Heading1 or BlockStyleRole.Heading2 or BlockStyleRole.Heading3)
            {
                cleaned = ExistingHeadingNumberPrefixRegex.Replace(cleaned, string.Empty);
            }

            block.Text = cleaned;

            if (profile.AutoNumberHeadings && IsUnnumberedHeading(block))
            {
                block.ComputedSectionNumber = string.Empty;
            }
            else if (profile.AutoNumberHeadings)
            {
                switch (block.Role)
                {
                    case BlockStyleRole.Heading1:
                        h1++;
                        h2 = 0;
                        h3 = 0;
                        block.ComputedSectionNumber = $"{h1}. ";
                        break;
                    case BlockStyleRole.Heading2:
                        if (h1 == 0) h1 = 1;
                        h2++;
                        h3 = 0;
                        block.ComputedSectionNumber = $"{h1}.{h2} ";
                        break;
                    case BlockStyleRole.Heading3:
                        if (h1 == 0) h1 = 1;
                        if (h2 == 0) h2 = 1;
                        h3++;
                        block.ComputedSectionNumber = $"{h1}.{h2}.{h3} ";
                        break;
                    default:
                        block.ComputedSectionNumber = string.Empty;
                        break;
                }
            }
            else
            {
                block.ComputedSectionNumber = string.Empty;
            }
        }
    }

    private static readonly Regex UnnumberedHeadingRegex = new(
        @"^(abstract|highlights|graphical abstract|keywords|key words|index terms|acknowledge?ments?|funding( statement| sources| details)?|role of the funding source|data availability( statement)?|data statement|code availability|credit authorship contribution statement|author contributions?( statement)?|declaration of (competing )?interests?|declaration of competing interest|competing interests?( statement)?|conflicts? of interest|disclosure statement|declaration of generative ai.*|institutional review board statement|informed consent statement|supplementary materials?|appendix( [a-z0-9]+)?(.*)?|references|bibliography|literature cited|works cited|nomenclature|abbreviations)$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex FrontMatterHeadingRegex = new(
        @"^(abstract|highlights|graphical abstract|keywords|key words|index terms|nomenclature|abbreviations)$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Front and back matter (Abstract, Acknowledgements, References…) is not numbered, as journals expect.</summary>
    private static bool IsUnnumberedHeading(DocumentBlock block)
        => HeadingText(block) is { } text && UnnumberedHeadingRegex.IsMatch(text);

    /// <summary>A heading of the front matter: Abstract, Highlights, Keywords, Nomenclature.</summary>
    public static bool IsFrontMatterHeading(DocumentBlock block)
        => HeadingText(block) is { } text && FrontMatterHeadingRegex.IsMatch(text);

    /// <summary>A heading of the back matter: Acknowledgements, statements, Appendix, References.</summary>
    public static bool IsBackMatterHeading(DocumentBlock block)
        => HeadingText(block) is { } text && UnnumberedHeadingRegex.IsMatch(text) && !FrontMatterHeadingRegex.IsMatch(text);

    private static string? HeadingText(DocumentBlock block)
    {
        if (block.Role is not (BlockStyleRole.Heading1 or BlockStyleRole.Heading2 or BlockStyleRole.Heading3))
        {
            return null;
        }
        string text = Scholarly.ScholarlyBlocks.StripHeadingLabel(Serialization.CriticMarkupSerializer.ResolveCriticMarkupText(block.Text, MarkupDisplayMode.NoMarkupFinal));
        return text.Replace("*", string.Empty).Trim().TrimEnd(':', '.');
    }

    private static string ApplySmartQuotesOutsideCriticMarkup(string input)
    {
        if (string.IsNullOrEmpty(input))
        {
            return string.Empty;
        }

        // Convert opening and closing straight double quotes to curly quotes
        return StraightDoubleQuotePairRegex.Replace(input, "\u201C$1\u201D");
    }

    public static string GetDisplayHeadingText(DocumentBlock block)
    {
        if (!string.IsNullOrEmpty(block.ComputedSectionNumber)
            && block.Role is BlockStyleRole.Heading1 or BlockStyleRole.Heading2 or BlockStyleRole.Heading3)
        {
            return block.ComputedSectionNumber + block.Text;
        }
        return block.Text;
    }
}
