using System.Text.Json.Serialization;

namespace RevisionsEditor.Core.Models;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum BlockStyleRole
{
    Title,
    Abstract,
    Heading1,
    Heading2,
    Heading3,
    BodyParagraph,
    BlockQuote,
    Caption,
    ReferenceItem,
    /// <summary>An image with its caption: <c>![Caption](figures/file.png){#fig:label}</c>.</summary>
    Figure,
    /// <summary>A table whose cells come from a CSV file: <c>Table: Caption {#tbl:label file=tables/file.csv}</c>.</summary>
    Table,
    /// <summary>A display equation in LaTeX: <c>$ ... $ {#eq:label}</c>.</summary>
    Equation
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum RevisionHunkType
{
    Insertion,
    Deletion,
    Replacement,
    Comment,
    UntrackedEdit
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum RevisionHunkStatus
{
    Pending,
    Accepted,
    Rejected
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MarkupDisplayMode
{
    AllMarkup,
    SimpleMarkup,
    NoMarkupFinal,
    Original
}

public sealed class DocumentBlock
{
    public string ParagraphId { get; set; } = $"p-{Guid.NewGuid():N}"[..10];
    public BlockStyleRole Role { get; set; } = BlockStyleRole.BodyParagraph;
    public string Text { get; set; } = string.Empty;
    public string ComputedSectionNumber { get; set; } = string.Empty;

    public DocumentBlock Clone() => new()
    {
        ParagraphId = ParagraphId,
        Role = Role,
        Text = Text,
        ComputedSectionNumber = ComputedSectionNumber
    };
}

public sealed class RevisionHunk
{
    public string HunkId { get; set; } = $"h-{Guid.NewGuid():N}"[..10];
    public string ParagraphId { get; set; } = string.Empty;
    public RevisionHunkType HunkType { get; set; } = RevisionHunkType.Insertion;
    public string OriginalText { get; set; } = string.Empty;
    public string ProposedText { get; set; } = string.Empty;
    public string CommentText { get; set; } = string.Empty;
    public string ContextSnippet { get; set; } = string.Empty;
    public string Author { get; set; } = "Supervisor";
    public DateTimeOffset TimestampUtc { get; set; } = DateTimeOffset.UtcNow;
    public RevisionHunkStatus Status { get; set; } = RevisionHunkStatus.Pending;

    public RevisionHunk Clone() => new()
    {
        HunkId = HunkId,
        ParagraphId = ParagraphId,
        HunkType = HunkType,
        OriginalText = OriginalText,
        ProposedText = ProposedText,
        CommentText = CommentText,
        ContextSnippet = ContextSnippet,
        Author = Author,
        TimestampUtc = TimestampUtc,
        Status = Status
    };
}

public sealed class ManuscriptDocument
{
    public string DocumentId { get; set; } = "doc-main";
    public string Title { get; set; } = "Untitled Academic Manuscript";
    public string? GoogleDocId { get; set; }
    /// <summary>The Google Docs document tab this manuscript was taken from (for example "t.0"), when known.</summary>
    public string? GoogleDocTabId { get; set; }
    public string? GoogleDocTabTitle { get; set; }
    public string ActiveFormattingProfile { get; set; } = "Academic Manuscript (Times 12pt Double)";
    public List<DocumentBlock> Blocks { get; set; } = [];
    public List<RevisionHunk> Hunks { get; set; } = [];

    public ManuscriptDocument Clone() => new()
    {
        DocumentId = DocumentId,
        Title = Title,
        GoogleDocId = GoogleDocId,
        GoogleDocTabId = GoogleDocTabId,
        GoogleDocTabTitle = GoogleDocTabTitle,
        ActiveFormattingProfile = ActiveFormattingProfile,
        Blocks = Blocks.Select(b => b.Clone()).ToList(),
        Hunks = Hunks.Select(h => h.Clone()).ToList()
    };
}

public sealed class GitCommitNode
{
    public string Sha { get; set; } = string.Empty;
    public string ShortSha => Sha.Length >= 7 ? Sha[..7] : Sha;
    public List<string> ParentShas { get; set; } = [];
    public List<string> BranchNames { get; set; } = [];
    public string Author { get; set; } = string.Empty;
    public DateTimeOffset Timestamp { get; set; }
    public string Message { get; set; } = string.Empty;
    public int LaneIndex { get; set; }
    public bool IsCryptographicallyVerified { get; set; }
    public string BlockHashShort { get; set; } = string.Empty;
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum GoogleDocPatchKind
{
    /// <summary>Replace the text of an existing paragraph (BeforeText becomes AfterText).</summary>
    Modify,
    /// <summary>Insert a new paragraph (AfterText) after the paragraph whose text is AnchorText, or at the start when it is empty.</summary>
    Insert,
    /// <summary>Remove the paragraph whose text is BeforeText.</summary>
    Delete
}

public sealed class GoogleDocPatch
{
    /// <summary>Derived from the patch content, so it stays the same when the list is regenerated.</summary>
    public string PatchId { get; set; } = $"patch-{Guid.NewGuid():N}"[..12];
    public GoogleDocPatchKind Kind { get; set; } = GoogleDocPatchKind.Modify;
    public string ParagraphId { get; set; } = string.Empty;
    public BlockStyleRole Role { get; set; } = BlockStyleRole.BodyParagraph;
    public string SectionHeading { get; set; } = string.Empty;
    /// <summary>Plain text currently in Google Docs (empty for an insertion).</summary>
    public string BeforeText { get; set; } = string.Empty;
    /// <summary>Plain text the paragraph should have (empty for a deletion).</summary>
    public string AfterText { get; set; } = string.Empty;
    /// <summary>For insertions: the Google Docs paragraph the new one follows; empty means the start of the document.</summary>
    public string AnchorText { get; set; } = string.Empty;
    public string ChangeSummary { get; set; } = string.Empty;
    public bool AppliedInGoogleDocs { get; set; }
}
