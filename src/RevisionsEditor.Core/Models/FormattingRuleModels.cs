namespace RevisionsEditor.Core.Models;

public sealed class FormattingRuleProfile
{
    public string ProfileName { get; set; } = "Academic Manuscript (Times 12pt Double)";
    public string Description { get; set; } = "Standard journal submission layout: Times New Roman 12pt, 2.0x line spacing, 0.5in first-line indent, numbered headings.";
    public string BodyFontFamily { get; set; } = "Times New Roman";
    public string HeadingFontFamily { get; set; } = "Times New Roman";
    public double BodyFontSizePt { get; set; } = 12.0;
    public double TitleFontSizePt { get; set; } = 18.0;
    public double Heading1FontSizePt { get; set; } = 14.0;
    public double Heading2FontSizePt { get; set; } = 12.5;
    public double Heading3FontSizePt { get; set; } = 12.0;
    public double LineSpacingMultiplier { get; set; } = 2.0;
    public double FirstLineIndentInches { get; set; } = 0.5;
    public double ParagraphSpacingAfterPt { get; set; } = 6.0;
    public double PageMarginInches { get; set; } = 1.0;
    public bool AutoNumberHeadings { get; set; } = true;
    public bool NormalizeWhitespace { get; set; } = true;
    public bool EnforceSingleSpaceAfterPeriod { get; set; } = true;
    public bool ConvertStraightQuotesToSmart { get; set; } = false;
    public bool StripForeignInlineStyles { get; set; } = true;

    public FormattingRuleProfile Clone() => new()
    {
        ProfileName = ProfileName,
        Description = Description,
        BodyFontFamily = BodyFontFamily,
        HeadingFontFamily = HeadingFontFamily,
        BodyFontSizePt = BodyFontSizePt,
        TitleFontSizePt = TitleFontSizePt,
        Heading1FontSizePt = Heading1FontSizePt,
        Heading2FontSizePt = Heading2FontSizePt,
        Heading3FontSizePt = Heading3FontSizePt,
        LineSpacingMultiplier = LineSpacingMultiplier,
        FirstLineIndentInches = FirstLineIndentInches,
        ParagraphSpacingAfterPt = ParagraphSpacingAfterPt,
        PageMarginInches = PageMarginInches,
        AutoNumberHeadings = AutoNumberHeadings,
        NormalizeWhitespace = NormalizeWhitespace,
        EnforceSingleSpaceAfterPeriod = EnforceSingleSpaceAfterPeriod,
        ConvertStraightQuotesToSmart = ConvertStraightQuotesToSmart,
        StripForeignInlineStyles = StripForeignInlineStyles
    };

    public static List<FormattingRuleProfile> GetBuiltInProfiles() =>
    [
        new FormattingRuleProfile
        {
            ProfileName = "Academic Manuscript (Times 12pt Double)",
            Description = "Standard journal & PhD supervisor review layout: Times New Roman 12pt, double-spaced, 0.5\" first-line indent, numbered sections.",
            BodyFontFamily = "Times New Roman",
            HeadingFontFamily = "Times New Roman",
            BodyFontSizePt = 12.0,
            TitleFontSizePt = 17.0,
            Heading1FontSizePt = 14.0,
            Heading2FontSizePt = 12.5,
            Heading3FontSizePt = 12.0,
            LineSpacingMultiplier = 2.0,
            FirstLineIndentInches = 0.5,
            ParagraphSpacingAfterPt = 4.0,
            PageMarginInches = 1.0,
            AutoNumberHeadings = true
        },
        new FormattingRuleProfile
        {
            ProfileName = "Modern Preprint / Nature Style (Cambria 11pt)",
            Description = "Clean modern preprint style: Cambria/Segoe UI, 11pt body, 1.25x line spacing, block paragraphs with 8pt spacing, numbered headings.",
            BodyFontFamily = "Cambria",
            HeadingFontFamily = "Segoe UI Semibold",
            BodyFontSizePt = 11.0,
            TitleFontSizePt = 18.0,
            Heading1FontSizePt = 14.0,
            Heading2FontSizePt = 12.0,
            Heading3FontSizePt = 11.0,
            LineSpacingMultiplier = 1.25,
            FirstLineIndentInches = 0.0,
            ParagraphSpacingAfterPt = 8.0,
            PageMarginInches = 0.9,
            AutoNumberHeadings = true
        },
        new FormattingRuleProfile
        {
            ProfileName = "IEEE / ACM Camera-Ready (10pt Compact)",
            Description = "Compact conference style: Georgia/Times 10pt, 1.1x tight line spacing, 0.25\" indent, numbered headings.",
            BodyFontFamily = "Georgia",
            HeadingFontFamily = "Georgia",
            BodyFontSizePt = 10.0,
            TitleFontSizePt = 15.0,
            Heading1FontSizePt = 12.0,
            Heading2FontSizePt = 11.0,
            Heading3FontSizePt = 10.0,
            LineSpacingMultiplier = 1.1,
            FirstLineIndentInches = 0.25,
            ParagraphSpacingAfterPt = 3.0,
            PageMarginInches = 0.75,
            AutoNumberHeadings = true
        },
        new FormattingRuleProfile
        {
            ProfileName = "APA 7th Edition Thesis",
            Description = "APA 7th compliant layout: Times New Roman 12pt, 2.0x spacing, unnumbered headings, 0.5\" indent.",
            BodyFontFamily = "Times New Roman",
            HeadingFontFamily = "Times New Roman",
            BodyFontSizePt = 12.0,
            TitleFontSizePt = 14.0,
            Heading1FontSizePt = 12.0,
            Heading2FontSizePt = 12.0,
            Heading3FontSizePt = 12.0,
            LineSpacingMultiplier = 2.0,
            FirstLineIndentInches = 0.5,
            ParagraphSpacingAfterPt = 0.0,
            PageMarginInches = 1.0,
            AutoNumberHeadings = false
        }
    ];
}
