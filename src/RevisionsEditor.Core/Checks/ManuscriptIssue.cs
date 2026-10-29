namespace RevisionsEditor.Core.Checks;

/// <summary>How serious a manuscript issue is.</summary>
public enum IssueSeverity { Info, Warning, Error }

/// <summary>A problem found in the manuscript. Start/Length index into DocumentBlock.Text (-1 = whole paragraph).</summary>
public sealed record ManuscriptIssue(
    string Category,            // "Citations", "Cross-references", "Numbers", "Submission", "Style"
    IssueSeverity Severity,
    string Message,             // one sentence, plain English, for an academic author
    string? ParagraphId = null,
    int Start = -1,
    int Length = 0,
    string? Fix = null,         // replacement text for [Start, Start+Length) when a one-click fix exists
    string? FixLabel = null);   // e.g. "Change to “10 m”"
