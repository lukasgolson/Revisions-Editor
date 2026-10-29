using RevisionsEditor.Core.Scholarly;

namespace RevisionsEditor.Core.Journals;

// Requirements gathered from the journals' guides for authors (archived copies 2022–2025; live Elsevier pages
// could not be read). They change: the checklist always points to the guide.
public static partial class JournalProfiles
{
    private const string ElsevierAiTemplate =
        "During the preparation of this work the author(s) used [NAME TOOL / SERVICE] in order to [REASON]. After using this tool/service, the author(s) reviewed and edited the content as needed and take(s) full responsibility for the content of the publication.";

    private const string MdpiAiTemplate =
        "During the preparation of this manuscript/study, the author(s) used [tool name, version information] for the purposes of [description of use]. The authors have reviewed and edited the output and take full responsibility for the content of this publication.";

    private const string CreditRoles =
        "Conceptualization; Data curation; Formal analysis; Funding acquisition; Investigation; Methodology; Project administration; Resources; Software; Supervision; Validation; Visualization; Writing – original draft; Writing – review & editing.";

    private static readonly CrossReferenceNames ElsevierNames = new()
    {
        Figure = "Fig.", Figures = "Figs.", FigureAtSentenceStart = "Fig.", FiguresAtSentenceStart = "Figs.",
        Equation = "Eq.", Equations = "Eqs.", EquationAtSentenceStart = "Eq.", EquationsAtSentenceStart = "Eqs.",
        FigureCaptionLabel = "Fig.", TableCaptionLabel = "Table"
    };

    private static readonly CrossReferenceNames MdpiNames = new()
    {
        Figure = "Figure", Figures = "Figures", FigureAtSentenceStart = "Figure", FiguresAtSentenceStart = "Figures",
        Equation = "Equation", Equations = "Equations", EquationAtSentenceStart = "Equation", EquationsAtSentenceStart = "Equations",
        FigureCaptionLabel = "Figure", TableCaptionLabel = "Table"
    };

    private static readonly CrossReferenceNames IeeeNames = new()
    {
        Figure = "Fig.", Figures = "Figs.", FigureAtSentenceStart = "Fig.", FiguresAtSentenceStart = "Figs.",
        Equation = string.Empty, Equations = string.Empty, EquationAtSentenceStart = "Equation", EquationsAtSentenceStart = "Equations",
        FigureCaptionLabel = "Fig.", TableCaptionLabel = "Table", RomanTableNumbers = true, UppercaseTableCaptionLabel = true
    };

    private static readonly CrossReferenceNames CspNames = new()
    {
        Figure = "Figure", Figures = "Figures", FigureAtSentenceStart = "Figure", FiguresAtSentenceStart = "Figures",
        Equation = "Eq.", Equations = "Eqs.", EquationAtSentenceStart = "Equation", EquationsAtSentenceStart = "Equations",
        FigureCaptionLabel = "Fig.", TableCaptionLabel = "Table"
    };

    private static readonly CrossReferenceNames TaylorFrancisNames = new()
    {
        Figure = "Figure", Figures = "Figures", FigureAtSentenceStart = "Figure", FiguresAtSentenceStart = "Figures",
        Equation = "Equation", Equations = "Equations", EquationAtSentenceStart = "Equation", EquationsAtSentenceStart = "Equations",
        FigureCaptionLabel = "Figure", TableCaptionLabel = "Table"
    };

    private static IReadOnlyList<RequiredSection> ElsevierSections(bool dataAvailability = true) =>
    [
        new("CRediT authorship contribution statement", ["author contributions", "authorship contribution statement", "author contribution statement"], true,
            "Give each author's roles using CRediT terms: " + CreditRoles),
        new("Declaration of competing interest", ["competing interests", "declaration of interests", "conflict of interest", "conflicts of interest", "declaration of competing interests"], true,
            "Required even when there is nothing to declare; also submitted with Elsevier's declaration tool.",
            "The authors declare that they have no known competing financial interests or personal relationships that could have appeared to influence the work reported in this paper."),
        .. (dataAvailability
            ? new RequiredSection[] { new("Data availability", ["data availability statement", "data statement", "availability of data"], true,
                "State where the data and code are available, or why they cannot be shared.") }
            : []),
        new("Declaration of generative AI and AI-assisted technologies in the writing process", ["declaration of generative ai", "generative ai", "use of ai", "ai-assisted technologies"], false,
            "Needed only if generative AI was used in writing (not for spelling or grammar checkers). Placed before the references.", ElsevierAiTemplate),
        new("Funding", ["funding sources", "role of the funding source", "funding statement"], false,
            "Name the funders and grant numbers; state if there was no specific funding."),
        new("Acknowledgements", ["acknowledgments", "acknowledgement"], false, "A separate section before the references.")
    ];

    private static IReadOnlyList<RequiredSection> MdpiSections(bool highlights) =>
    [
        .. (highlights
            ? new RequiredSection[] { new("Highlights", [], true, "Between the affiliations and the abstract: up to 2 bullets under each of the two questions.",
                "What are the main findings?\n• First bullet.\n• Second bullet.\nWhat are the implications of the main findings?\n• First bullet.\n• Second bullet.") }
            : []),
        new("Author Contributions", ["author contribution", "credit authorship contribution statement", "authors' contributions"], true,
            "One paragraph with each author's CRediT roles by initials.",
            "Conceptualization, X.X. and Y.Y.; methodology, X.X.; software, X.X.; validation, X.X., Y.Y. and Z.Z.; formal analysis, X.X.; investigation, X.X.; resources, X.X.; data curation, X.X.; writing—original draft preparation, X.X.; writing—review and editing, X.X.; visualization, X.X.; supervision, X.X.; project administration, X.X.; funding acquisition, Y.Y. All authors have read and agreed to the published version of the manuscript."),
        new("Funding", ["funding statement"], true, "Funders with grant numbers, and who funded the APC.",
            "This research received no external funding."),
        new("Data Availability Statement", ["data availability"], true, "Where the data can be found (see MDPI Research Data Policies)."),
        new("Conflicts of Interest", ["conflict of interest", "competing interests", "declaration of competing interest"], true, "Required.",
            "The authors declare no conflicts of interest."),
        new("Acknowledgments", ["acknowledgements"], false,
            "Declare generative-AI use here (and in Materials and Methods) when it went beyond superficial editing.", MdpiAiTemplate),
        new("Institutional Review Board Statement", ["irb statement"], false, "Only for studies involving humans or animals; otherwise “Not applicable.”"),
        new("Informed Consent Statement", [], false, "Only for studies involving human participants; otherwise “Not applicable.”")
    ];

    public static IReadOnlyList<JournalProfile> All { get; } =
    [
        new JournalProfile
        {
            Id = "rse",
            Name = "Remote Sensing of Environment",
            Publisher = "Elsevier",
            GuideUrl = "https://www.sciencedirect.com/journal/remote-sensing-of-environment/publish/guide-for-authors",
            CitationStyleId = "elsevier-harvard",
            CrossReferenceNames = ElsevierNames,
            AbstractMaxWords = 400,
            AbstractNotes = "Recommended at most 400 words; no references or non-standard abbreviations.",
            HighlightsRequired = true, HighlightsMin = 3, HighlightsMax = 5, HighlightMaxCharacters = 85,
            GraphicalAbstract = "optional",
            LengthLimit = "Research articles 15,000 words including references and figure captions; reviews 20,000; short communications 5,000.",
            MaxWords = 15000,
            RequiredSections = ElsevierSections(),
            LineNumbersForReview = true,
            DoubleSpacingForReview = true,
            OtherRequirements =
            [
                "Double spacing, 2.5 cm margins, single column, page numbers and continuous line numbers.",
                "Table captions above tables, figure captions below figures; figures placed near the text that cites them.",
                "Highlights are submitted as a separate file.",
                "A statistically sound accuracy assessment or validation is expected."
            ],
            Confidence = "Medium-low: from the 2022 guide for authors; check the current guide."
        },
        new JournalProfile
        {
            Id = "isprs-jprs",
            Name = "ISPRS Journal of Photogrammetry and Remote Sensing",
            Publisher = "Elsevier",
            GuideUrl = "https://www.sciencedirect.com/journal/isprs-journal-of-photogrammetry-and-remote-sensing/publish/guide-for-authors",
            CitationStyleId = "elsevier-harvard",
            CrossReferenceNames = ElsevierNames,
            AbstractNotes = "Concise and factual; no stated word limit.",
            HighlightsRequired = false, HighlightsMin = 3, HighlightsMax = 5, HighlightMaxCharacters = 85,
            KeywordsMax = 6,
            GraphicalAbstract = "optional",
            RequiredSections = ElsevierSections(dataAvailability: false),
            LineNumbersForReview = false,
            DoubleSpacingForReview = false,
            OtherRequirements =
            [
                "“Your Paper Your Way”: any consistent layout is accepted at first submission.",
                "Groups of citations are listed alphabetically, then chronologically."
            ],
            Confidence = "Medium: from the April 2024 guide for authors."
        },
        new JournalProfile
        {
            Id = "jag",
            Name = "International Journal of Applied Earth Observation and Geoinformation",
            Publisher = "Elsevier",
            GuideUrl = "https://www.sciencedirect.com/journal/international-journal-of-applied-earth-observation-and-geoinformation/publish/guide-for-authors",
            CitationStyleId = "elsevier-harvard",
            CrossReferenceNames = ElsevierNames,
            AbstractNotes = "Concise and factual; no stated word limit.",
            HighlightsRequired = true, HighlightsMin = 3, HighlightsMax = 5, HighlightMaxCharacters = 85,
            KeywordsMax = 6,
            GraphicalAbstract = "optional",
            LengthLimit = "Research papers 8,000 words for the whole submission (abstract, tables, references and appendices included).",
            MaxWords = 8000,
            RequiredSections = ElsevierSections(),
            LineNumbersForReview = false,
            DoubleSpacingForReview = false,
            OtherRequirements = ["Highlights are submitted as a separate file.", "Short communications: 2,500 words, 1 table and 3 figures at most."],
            Confidence = "Medium: from the December 2023 guide for authors."
        },
        new JournalProfile
        {
            Id = "fem",
            Name = "Forest Ecology and Management",
            Publisher = "Elsevier",
            GuideUrl = "https://www.sciencedirect.com/journal/forest-ecology-and-management/publish/guide-for-authors",
            CitationStyleId = "elsevier-harvard",
            CrossReferenceNames = ElsevierNames,
            AbstractMaxWords = 400,
            AbstractNotes = "Not longer than 400 words; no references.",
            HighlightsRequired = true, HighlightsMin = 3, HighlightsMax = 5, HighlightMaxCharacters = 85,
            GraphicalAbstract = "optional",
            RequiredSections = ElsevierSections(),
            LineNumbersForReview = true,
            DoubleSpacingForReview = true,
            OtherRequirements = ["Double spacing and consecutive line numbering are “an essential peer review requirement”.", "Highlights are submitted as a separate file."],
            Confidence = "Medium: from the October 2023 guide for authors."
        },
        new JournalProfile
        {
            Id = "mdpi-remote-sensing",
            Name = "Remote Sensing (MDPI)",
            Publisher = "MDPI",
            GuideUrl = "https://www.mdpi.com/journal/remotesensing/instructions",
            CitationStyleId = "mdpi",
            CrossReferenceNames = MdpiNames,
            AbstractMaxWords = 300,
            AbstractNotes = "One paragraph of about 300 words; self-contained, acronyms defined.",
            HighlightsRequired = true, HighlightsMin = 2, HighlightsMax = 4,
            KeywordsMin = 3, KeywordsMax = 10,
            GraphicalAbstract = "optional",
            RequiredSections = MdpiSections(highlights: true),
            LineNumbersForReview = false,
            DoubleSpacingForReview = false,
            OtherRequirements =
            [
                "Highlights (required since September 2025): “What are the main findings?” and “What are the implications of the main findings?”, up to 2 bullets each.",
                "Maps and images need a scale bar, a north arrow and coordinates.",
                "The cover letter must confirm the manuscript is not under consideration elsewhere and that all authors approved it."
            ],
            Confidence = "High: from the December 2025 instructions for authors."
        },
        new JournalProfile
        {
            Id = "mdpi-forests",
            Name = "Forests (MDPI)",
            Publisher = "MDPI",
            GuideUrl = "https://www.mdpi.com/journal/forests/instructions",
            CitationStyleId = "mdpi",
            CrossReferenceNames = MdpiNames,
            AbstractMaxWords = 200,
            AbstractNotes = "About 200 words at most; a structured abstract is encouraged.",
            KeywordsMin = 3, KeywordsMax = 10,
            GraphicalAbstract = "optional",
            RequiredSections = MdpiSections(highlights: false),
            LineNumbersForReview = false,
            DoubleSpacingForReview = false,
            OtherRequirements = ["Give the Latin binomial and authority after each common name at first mention."],
            Confidence = "High: from the September 2025 instructions for authors."
        },
        new JournalProfile
        {
            Id = "cjfr",
            Name = "Canadian Journal of Forest Research",
            Publisher = "Canadian Science Publishing",
            GuideUrl = "https://cdnsciencepub.com/journal/cjfr/authors",
            CitationStyleId = "csp",
            CrossReferenceNames = CspNames,
            AbstractMaxWords = 200,
            AbstractNotes = "One paragraph, at most 200 words (150 for Notes); no citations, figures or acronyms.",
            GraphicalAbstract = "optional",
            LengthLimit = "Research articles: typically 6,000 words from the Introduction to the Discussion and 50 references.",
            MaxWords = 6000,
            MaxWordsScope = WordLimitScope.MainText,
            RequiredSections =
            [
                new("Competing interests statement", ["competing interests", "declaration of competing interest", "conflict of interest"], true, "Also given at submission.",
                    "The authors declare there are no competing interests."),
                new("Author contribution statement", ["author contributions", "credit authorship contribution statement"], true, "Authors by initials with their CRediT roles."),
                new("Funding statement", ["funding"], true, "Funders in full with grant numbers.", "The authors declare no specific funding for this work."),
                new("Data availability statement", ["data availability"], true, "Whether the data are available, with repository and persistent identifier, or the restrictions."),
                new("Acknowledgements", ["acknowledgments"], false, "Name any AI tool used for writing assistance and how it was used.")
            ],
            LineNumbersForReview = true,
            DoubleSpacingForReview = true,
            OtherRequirements = ["Continuous line numbers, double spacing and page numbers are required.", "Map captions must credit base map and data sources, projection and coordinate system."],
            Confidence = "High: from the July 2025 author guidelines."
        },
        new JournalProfile
        {
            Id = "ieee-tgrs",
            Name = "IEEE Transactions on Geoscience and Remote Sensing",
            Publisher = "IEEE",
            GuideUrl = "https://www.grss-ieee.org/publications/transactions-on-geoscience-and-remote-sensing/",
            CitationStyleId = "ieee",
            CrossReferenceNames = IeeeNames,
            AbstractMaxWords = 250,
            AbstractNotes = "One paragraph, up to 250 words, without abbreviations, references or equations.",
            KeywordsMin = 3, KeywordsMax = 5,
            KeywordsHeading = "Index Terms",
            GraphicalAbstract = "optional",
            LengthLimit = "6 printed pages; pages beyond carry an overlength charge.",
            RequiredSections =
            [
                new("Acknowledgment", ["acknowledgments", "acknowledgement", "acknowledgements"], false,
                    "Disclose AI-generated content here: the system used, the sections affected and the extent of use.")
            ],
            LineNumbersForReview = false,
            DoubleSpacingForReview = false,
            OtherRequirements = ["Submit in IEEE two-column format; line numbers are added by the submission system.", "ORCID iDs are required for all authors.", "Funding goes in the unnumbered first footnote."],
            Confidence = "Medium-high: from the December 2023 Information for Authors."
        },
        new JournalProfile
        {
            Id = "cjrs",
            Name = "Canadian Journal of Remote Sensing",
            Publisher = "Taylor & Francis",
            GuideUrl = "https://www.tandfonline.com/action/authorSubmission?show=instructions&journalCode=ujrs20",
            CitationStyleId = "csp",
            CrossReferenceNames = TaylorFrancisNames,
            AbstractMaxWords = 200,
            AbstractNotes = "100–200 words in non-specialist language; no references. A French title and abstract are welcome.",
            GraphicalAbstract = "none",
            RequiredSections =
            [
                new("Disclosure statement", ["competing interests", "declaration of interest", "conflict of interest"], true, "After the acknowledgments, before the references.",
                    "The authors report there are no competing interests to declare."),
                new("Funding", ["funding details"], false, "Agency and grant numbers."),
                new("Data availability statement", ["data availability"], false, "Where the data set is, with a link or persistent identifier."),
                new("Acknowledgments", ["acknowledgements"], false, "Before the disclosure statement.")
            ],
            LineNumbersForReview = false,
            DoubleSpacingForReview = false,
            OtherRequirements = ["Figures are submitted as separate files, not embedded.", "Uses an author-date reference style close to the one used here (CSP); check the journal's reference guide."],
            Confidence = "Medium: from the August 2023 instructions."
        },
        new JournalProfile
        {
            Id = "elsevier-generic",
            Name = "Other Elsevier journal",
            Publisher = "Elsevier",
            CitationStyleId = "elsevier-harvard",
            CrossReferenceNames = ElsevierNames,
            AbstractMaxWords = 250,
            HighlightsRequired = true, HighlightsMin = 3, HighlightsMax = 5, HighlightMaxCharacters = 85,
            KeywordsMax = 6,
            RequiredSections = ElsevierSections(),
            Confidence = "Generic Elsevier defaults; check the journal's guide for authors."
        }
    ];
}
