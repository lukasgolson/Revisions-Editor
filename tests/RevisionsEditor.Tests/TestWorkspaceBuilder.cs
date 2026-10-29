using System.IO;
using RevisionsEditor.Core.Docx;
using RevisionsEditor.Core.Git;
using RevisionsEditor.Core.Models;

namespace RevisionsEditor.Tests;

/// <summary>
/// Builds a small but complete review workflow in a repository for end-to-end tests:
/// two author commits on main (the second recorded as the Google Docs baseline), a reviewer's .docx with
/// tracked replacements, insertions, deletions, a comment and an untracked edit, and that .docx ingested
/// onto the branch 'review/supervisor-round1'.
/// </summary>
internal static class TestWorkspaceBuilder
{
    public const string ReviewBranch = "review/supervisor-round1";

    public static string SeedReviewWorkflow(GitRepositoryService repo)
    {
        repo.EnsureInitialized();

        var profile = FormattingRuleProfile.GetBuiltInProfiles()[0];
        repo.SaveFormattingProfile(profile);

        var draft1 = new ManuscriptDocument
        {
            DocumentId = "doc-test",
            Title = "Provenance in Collaborative Writing",
            ActiveFormattingProfile = profile.ProfileName,
            Blocks =
            [
                new DocumentBlock { ParagraphId = "p-001", Role = BlockStyleRole.Title, Text = "Provenance in Collaborative Writing" },
                new DocumentBlock { ParagraphId = "p-002", Role = BlockStyleRole.Abstract, Text = "Collaborative authoring spans cloud editors and desktop word processors. This fragmentation forces authors to reconcile tracked changes by hand while losing authorship provenance." },
                new DocumentBlock { ParagraphId = "p-003", Role = BlockStyleRole.Heading1, Text = "Introduction" },
                new DocumentBlock { ParagraphId = "p-004", Role = BlockStyleRole.BodyParagraph, Text = "Manuscripts undergo numerous iterative revisions between authors and reviewers. Cloud editors excel at drafting, while many reviewers rely on Track Changes." },
                new DocumentBlock { ParagraphId = "p-005", Role = BlockStyleRole.Heading1, Text = "Method" },
                new DocumentBlock { ParagraphId = "p-006", Role = BlockStyleRole.BodyParagraph, Text = "We propose a canonical Markdown representation paired with a formatting-independent hash chain. Every commit hashes canonicalized sentences and signs the block header." }
            ]
        };

        repo.CommitSnapshot(draft1, "Initial draft", "Test Author", AuthorshipOriginSource.HandWrittenInGoogleDocs);

        var draft2 = draft1.Clone();
        draft2.Blocks[3].Text = "Manuscripts undergo numerous iterative revisions between authors and reviewers. Cloud editors excel at drafting, while reviewers prefer Track Changes for structural feedback.";
        draft2.Blocks.Add(new DocumentBlock { ParagraphId = "p-007", Role = BlockStyleRole.Heading1, Text = "Evaluation" });
        draft2.Blocks.Add(new DocumentBlock { ParagraphId = "p-008", Role = BlockStyleRole.BodyParagraph, Text = "Across 24 revision rounds, automated reconciliation reduced manual copying by 78% and eliminated style drift." });

        repo.SaveGoogleDocsBaseline(draft2);
        repo.CommitSnapshot(draft2, "Add evaluation section", "Test Author", AuthorshipOriginSource.HandWrittenInGoogleDocs);

        var reviewed = draft2.Clone();
        reviewed.Blocks[1].Text = "Collaborative authoring spans cloud editors and desktop word processors. This {~~fragmentation~>toolchain divide~~} forces authors to reconcile tracked changes by hand while {++inadvertently ++}losing authorship provenance.";
        reviewed.Blocks[3].Text = "Manuscripts undergo {--numerous --}iterative revisions between authors and reviewers. {==Cloud editors excel at drafting, while reviewers prefer Track Changes for structural feedback.==}{>>Test Reviewer: Please cite a survey here.<<}";
        reviewed.Blocks[5].Text = "We propose a CriticMarkup-enhanced Markdown AST paired with a formatting-independent hash chain. Every commit hashes canonicalized sentences and signs the block header.";
        reviewed.Blocks[7].Text = "Across {~~24~>36 controlled~~} revision rounds, automated reconciliation reduced manual copying by {~~78%~>84.5%~~} and eliminated style drift.";

        string docxPath = Path.Combine(repo.WorkspacePath, "Reviewed.docx");
        DocxExportService.ExportToDocx(reviewed, docxPath, profile, includeTrackedChanges: true);

        repo.CreateAndCheckoutBranch(ReviewBranch, "main");
        var ingested = DocxIngestionService.IngestDocx(docxPath, draft2, profile);
        repo.CommitSnapshot(ingested, "Import reviewed document", "Test Reviewer", AuthorshipOriginSource.SupervisorTrackedEdit);

        return docxPath;
    }

    public static void DeleteDirectory(string path)
    {
        if (!Directory.Exists(path))
        {
            return;
        }

        try
        {
            // Git marks pack files read-only.
            foreach (var file in Directory.GetFiles(path, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }
            Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
