using RevisionsEditor.Core.Scholarly;

namespace RevisionsEditor.Core.Journals;

/// <summary>A section or statement the journal asks for, recognised by its heading.</summary>
public sealed record RequiredSection(
    string Heading,
    IReadOnlyList<string> Aliases,
    bool Required,
    string Notes,
    string? Template = null);

/// <summary>What a journal's word limit counts.</summary>
public enum WordLimitScope
{
    /// <summary>Everything: abstract, main text, captions, tables, references and appendices.</summary>
    WholeManuscript,
    /// <summary>From the Introduction to the end of the Discussion or Conclusions.</summary>
    MainText
}

/// <summary>A journal's submission requirements, as far as they can be checked in the manuscript.</summary>
public sealed class JournalProfile
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string Publisher { get; init; } = string.Empty;
    public string GuideUrl { get; init; } = string.Empty;
    public required string CitationStyleId { get; init; }
    public CrossReferenceNames CrossReferenceNames { get; init; } = CrossReferenceNames.Default;
    /// <summary>Formatting rules to use for this journal's review copy, by profile name (null keeps the current one).</summary>
    public string? FormattingProfileName { get; init; }

    public int? AbstractMaxWords { get; init; }
    public string AbstractNotes { get; init; } = string.Empty;
    public bool HighlightsRequired { get; init; }
    public int? HighlightsMin { get; init; }
    public int? HighlightsMax { get; init; }
    public int? HighlightMaxCharacters { get; init; }
    public int? KeywordsMin { get; init; }
    public int? KeywordsMax { get; init; }
    public string KeywordsHeading { get; init; } = "Keywords";
    /// <summary>"required", "optional" or "none".</summary>
    public string GraphicalAbstract { get; init; } = "optional";
    public string? LengthLimit { get; init; }
    /// <summary>Word limit, when the journal states one, counted over <see cref="MaxWordsScope"/>.</summary>
    public int? MaxWords { get; init; }
    public WordLimitScope MaxWordsScope { get; init; } = WordLimitScope.WholeManuscript;
    public IReadOnlyList<RequiredSection> RequiredSections { get; init; } = [];
    public bool LineNumbersForReview { get; init; } = true;
    public bool DoubleSpacingForReview { get; init; } = true;
    public IReadOnlyList<string> OtherRequirements { get; init; } = [];
    /// <summary>How sure we are that the requirements above are current; always check the guide before submitting.</summary>
    public string Confidence { get; init; } = string.Empty;

    public override string ToString() => Name;
}

/// <summary>Built-in journals for forest remote sensing and related fields.</summary>
public static partial class JournalProfiles
{
    public static JournalProfile Default => All[0];

    public static JournalProfile? Find(string? id)
        => All.FirstOrDefault(j => string.Equals(j.Id, id, StringComparison.OrdinalIgnoreCase));
}
