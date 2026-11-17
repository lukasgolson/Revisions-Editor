using System.Windows.Input;

namespace RevisionsEditor.App;

/// <summary>
/// Application commands. Routed commands give menus their shortcut text, keep shortcuts working from any
/// focused control, and disable menu items and toolbar buttons automatically when they cannot run.
/// </summary>
public static class AppCommands
{
    public static readonly RoutedUICommand OpenWorkspace = Create("Open Workspace...", nameof(OpenWorkspace), Key.O, ModifierKeys.Control);
    public static readonly RoutedUICommand OpenRecentWorkspace = Create("Open Recent Workspace", nameof(OpenRecentWorkspace));
    public static readonly RoutedUICommand CloseWorkspace = Create("Close Workspace", nameof(CloseWorkspace));
    public static readonly RoutedUICommand ImportDocx = Create("Import Word Document...", nameof(ImportDocx), Key.I, ModifierKeys.Control | ModifierKeys.Shift);
    public static readonly RoutedUICommand ExportCleanDocx = Create("Export Word Document...", nameof(ExportCleanDocx), Key.E, ModifierKeys.Control);
    public static readonly RoutedUICommand ExportTrackedDocx = Create("Export Word Document with Tracked Changes...", nameof(ExportTrackedDocx), Key.E, ModifierKeys.Control | ModifierKeys.Shift);
    public static readonly RoutedUICommand ExportCertificate = Create("Export Provenance Certificate...", nameof(ExportCertificate));
    public static readonly RoutedUICommand Exit = Create("Exit", nameof(Exit));

    public static readonly RoutedUICommand InsertCriticMarkup = Create("Insert Markup", nameof(InsertCriticMarkup));

    public static readonly RoutedUICommand SetMarkupMode = Create("Markup Mode", nameof(SetMarkupMode));

    public static readonly RoutedUICommand Commit = Create("Commit...", nameof(Commit), Key.S, ModifierKeys.Control);
    public static readonly RoutedUICommand MergeIntoMain = Create("Merge Review Branch into Main", nameof(MergeIntoMain), Key.M, ModifierKeys.Control);
    public static readonly RoutedUICommand CheckoutBranch = Create("Check Out Branch", nameof(CheckoutBranch));
    public static readonly RoutedUICommand CheckoutCommit = Create("Check Out Commit", nameof(CheckoutCommit));
    public static readonly RoutedUICommand CompareWithHead = Create("Compare with Latest", nameof(CompareWithHead));

    public static readonly RoutedUICommand SyncWithGitHub = Create("Sync with GitHub", nameof(SyncWithGitHub), Key.Y, ModifierKeys.Control | ModifierKeys.Shift);
    public static readonly RoutedUICommand ConnectGitHub = Create("Connect to GitHub Repository...", nameof(ConnectGitHub));
    public static readonly RoutedUICommand CloneFromGitHub = Create("Open from GitHub...", nameof(CloneFromGitHub));

    public static readonly RoutedUICommand AcceptChange = Create("Accept", nameof(AcceptChange));
    public static readonly RoutedUICommand RejectChange = Create("Reject", nameof(RejectChange));
    public static readonly RoutedUICommand ResetChange = Create("Mark as Pending", nameof(ResetChange));
    public static readonly RoutedUICommand AcceptAllChanges = Create("Accept All Changes", nameof(AcceptAllChanges));
    public static readonly RoutedUICommand RejectAllChanges = Create("Reject All Changes", nameof(RejectAllChanges));

    public static readonly RoutedUICommand VerifyChain = Create("Verify Provenance Chain", nameof(VerifyChain), Key.F5, ModifierKeys.None);
    public static readonly RoutedUICommand TestTamperDetection = Create("Test Tamper Detection", nameof(TestTamperDetection));
    public static readonly RoutedUICommand ApplyFormatting = Create("Apply Formatting Rules", nameof(ApplyFormatting));

    public static readonly RoutedUICommand CopyPatchText = Create("Copy Merged Text", nameof(CopyPatchText));
    public static readonly RoutedUICommand MarkPatchApplied = Create("Mark as Applied", nameof(MarkPatchApplied));

    public static readonly RoutedUICommand InsertCitation = Create("Citation...", nameof(InsertCitation), Key.C, ModifierKeys.Control | ModifierKeys.Alt);
    public static readonly RoutedUICommand InsertCrossReference = Create("Cross-Reference...", nameof(InsertCrossReference), Key.R, ModifierKeys.Control | ModifierKeys.Alt);
    public static readonly RoutedUICommand InsertFigure = Create("Figure...", nameof(InsertFigure), Key.F, ModifierKeys.Control | ModifierKeys.Alt);
    public static readonly RoutedUICommand InsertTable = Create("Table...", nameof(InsertTable), Key.T, ModifierKeys.Control | ModifierKeys.Alt);
    public static readonly RoutedUICommand InsertDiagram = Create("Diagram...", nameof(InsertDiagram), Key.G, ModifierKeys.Control | ModifierKeys.Alt);
    public static readonly RoutedUICommand InsertPlaceholder = Create("Placeholder...", nameof(InsertPlaceholder), Key.P, ModifierKeys.Control | ModifierKeys.Alt);
    public static readonly RoutedUICommand NewNote = Create("New Note...", nameof(NewNote), Key.M, ModifierKeys.Control | ModifierKeys.Alt);
    public static readonly RoutedUICommand EditNote = Create("Edit Note...", nameof(EditNote));
    public static readonly RoutedUICommand DeleteNote = Create("Delete Note", nameof(DeleteNote));
    public static readonly RoutedUICommand GoToNoteParagraph = Create("Go to the Paragraph", nameof(GoToNoteParagraph));
    public static readonly RoutedUICommand InsertEquation = Create("Equation...", nameof(InsertEquation), Key.Q, ModifierKeys.Control | ModifierKeys.Alt);
    public static readonly RoutedUICommand InsertNumber = Create("Linked Number...", nameof(InsertNumber), Key.N, ModifierKeys.Control | ModifierKeys.Alt);
    public static readonly RoutedUICommand EditScholarlyItem = Create("Edit", nameof(EditScholarlyItem));
    public static readonly RoutedUICommand RemoveScholarlyItem = Create("Remove", nameof(RemoveScholarlyItem));
    public static readonly RoutedUICommand TogglePreview = Create("Preview as Published", nameof(TogglePreview), Key.F8, ModifierKeys.None);

    public static readonly RoutedUICommand CheckManuscript = Create("Check Manuscript", nameof(CheckManuscript), Key.F7, ModifierKeys.None);
    public static readonly RoutedUICommand GoToIssue = Create("Go to Paragraph", nameof(GoToIssue));
    public static readonly RoutedUICommand FixIssue = Create("Fix", nameof(FixIssue));
    public static readonly RoutedUICommand DismissIssue = Create("Dismiss", nameof(DismissIssue));
    public static readonly RoutedUICommand RestoreDismissedIssues = Create("Show Dismissed Issues Again", nameof(RestoreDismissedIssues));
    public static readonly RoutedUICommand AllowAbbreviation = Create("Allow This Abbreviation Without a Definition", nameof(AllowAbbreviation));

    public static readonly RoutedUICommand AddReferencesByDoi = Create("Add References by DOI...", nameof(AddReferencesByDoi));
    public static readonly RoutedUICommand AddReferencesFromZotero = Create("Add Cited References from Zotero", nameof(AddReferencesFromZotero));
    public static readonly RoutedUICommand CheckReferencesOnline = Create("Check DOIs and Retractions Online", nameof(CheckReferencesOnline));
    public static readonly RoutedUICommand OpenBibliography = Create("Open Bibliography File", nameof(OpenBibliography));
    public static readonly RoutedUICommand CiteSelectedReferences = Create("Cite", nameof(CiteSelectedReferences));

    public static readonly RoutedUICommand AddResultsFile = Create("Add Results File...", nameof(AddResultsFile));
    public static readonly RoutedUICommand RefreshResults = Create("Refresh Results", nameof(RefreshResults));
    public static readonly RoutedUICommand InsertSelectedValue = Create("Insert in Text", nameof(InsertSelectedValue));

    public static readonly RoutedUICommand ExportReviewCopy = Create("Export Review Copy for the Journal...", nameof(ExportReviewCopy));
    public static readonly RoutedUICommand ExportSubmissionCopy = Create("Export Submission Copy...", nameof(ExportSubmissionCopy));
    public static readonly RoutedUICommand ExportHighlights = Create("Export Highlights File...", nameof(ExportHighlights));
    public static readonly RoutedUICommand AddRequiredSections = Create("Add Required Sections...", nameof(AddRequiredSections));
    public static readonly RoutedUICommand MarkSubmitted = Create("Mark Current Version as Submitted", nameof(MarkSubmitted));

    public static readonly RoutedUICommand AddReviewComments = Create("Add Reviewer Comments...", nameof(AddReviewComments));
    public static readonly RoutedUICommand LinkCommentToParagraph = Create("Link to Paragraph at Cursor", nameof(LinkCommentToParagraph));
    public static readonly RoutedUICommand UnlinkComment = Create("Remove Links", nameof(UnlinkComment));
    public static readonly RoutedUICommand ExportRevision = Create("Export Revision (Response Letter and Marked-Up Manuscript)...", nameof(ExportRevision));

    public static readonly RoutedUICommand ToggleLineNumbers = Create("Line Numbers", nameof(ToggleLineNumbers), Key.L, ModifierKeys.Control | ModifierKeys.Alt);
    public static readonly RoutedUICommand GoToLine = Create("Go to Line...", nameof(GoToLine), Key.G, ModifierKeys.Control);
    public static readonly RoutedUICommand FindNext = Create("Find Next", nameof(FindNext), Key.F3);
    public static readonly RoutedUICommand FindPrevious = Create("Find Previous", nameof(FindPrevious), Key.F3, ModifierKeys.Shift);
    public static readonly RoutedUICommand WordCount = Create("Word Count...", nameof(WordCount), Key.G, ModifierKeys.Control | ModifierKeys.Shift);
    public static readonly RoutedUICommand GoToReviewerLine = Create("Go to the Line the Reviewer Means", nameof(GoToReviewerLine));

    public static readonly RoutedUICommand ExportPdf = Create("Export PDF with LaTeX...", nameof(ExportPdf), Key.P, ModifierKeys.Control | ModifierKeys.Shift);
    public static readonly RoutedUICommand ExportLatexSource = Create("Export LaTeX Source...", nameof(ExportLatexSource));
    public static readonly RoutedUICommand ImportKeywords = Create("Take Keywords from the Manuscript", nameof(ImportKeywords));

    public static readonly RoutedUICommand ResetLayout = Create("Reset Window Layout", nameof(ResetLayout));
    public static readonly RoutedUICommand ShowPanel = Create("Show Panel", nameof(ShowPanel));
    public static readonly RoutedUICommand ClosePanel = Create("Close Panel", nameof(ClosePanel), Key.W, ModifierKeys.Control);

    public static readonly RoutedUICommand About = Create("About Revisions Editor", nameof(About));

    private static RoutedUICommand Create(string text, string name, Key key = Key.None, ModifierKeys modifiers = ModifierKeys.None)
    {
        var gestures = new InputGestureCollection();
        if (key != Key.None)
        {
            gestures.Add(new KeyGesture(key, modifiers));
        }
        return new RoutedUICommand(text, name, typeof(AppCommands), gestures);
    }
}
