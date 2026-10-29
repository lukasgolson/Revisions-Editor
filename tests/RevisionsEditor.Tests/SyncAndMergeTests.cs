using System.IO;
using System.Net.Http;
using RevisionsEditor.Core.Bridge;
using RevisionsEditor.Core.Docx;
using RevisionsEditor.Core.Git;
using RevisionsEditor.Core.Merging;
using RevisionsEditor.Core.Models;
using RevisionsEditor.Core.Provenance;
using RevisionsEditor.Core.Serialization;
using RevisionsEditor.Core.Storage;
using Xunit;

namespace RevisionsEditor.Tests;

public sealed partial class RevisionsEditorTests
{
    private static ManuscriptDocument Paper(params (string Id, BlockStyleRole Role, string Text)[] blocks) => new()
    {
        Title = "A Study",
        Blocks = blocks.Select(b => new DocumentBlock { ParagraphId = b.Id, Role = b.Role, Text = b.Text }).ToList()
    };

    private static ManuscriptDocument BasePaper() => Paper(
        ("p-001", BlockStyleRole.Title, "A Study"),
        ("p-002", BlockStyleRole.Heading1, "Introduction"),
        ("p-003", BlockStyleRole.BodyParagraph, "Alpha beta gamma delta."),
        ("p-004", BlockStyleRole.BodyParagraph, "The second paragraph is here."),
        ("p-005", BlockStyleRole.BodyParagraph, "Third paragraph to remove."),
        ("p-006", BlockStyleRole.Heading1, "Methods"),
        ("p-007", BlockStyleRole.BodyParagraph, "Methods text."));

    [Fact]
    public void ManuscriptMerger_CombinesSeparateEdits_AndTurnsOverlappingOnesIntoPendingChanges()
    {
        var baseDoc = BasePaper();

        var ours = BasePaper();
        ours.Blocks[2].Text = "Alpha BETA gamma delta.";
        ours.Blocks[3].Text = "The second paragraph is right here.";
        ours.Blocks.Add(new DocumentBlock { ParagraphId = "p-008", Role = BlockStyleRole.BodyParagraph, Text = "Ours added a closing paragraph." });

        // The other version has positional ids, as a Google Docs pull does; alignment is by content.
        var theirs = BasePaper();
        theirs.Blocks[2].Text = "Alpha beta gamma DELTA.";
        theirs.Blocks[3].Text = "The second paragraph is over there.";
        theirs.Blocks.RemoveAt(4);
        theirs.Blocks.Insert(2, new DocumentBlock { Role = BlockStyleRole.BodyParagraph, Text = "Theirs added an opening paragraph." });
        for (int i = 0; i < theirs.Blocks.Count; i++) theirs.Blocks[i].ParagraphId = $"t-{i:D2}";

        var result = ManuscriptMerger.Merge(baseDoc, ours, theirs, "Other computer");
        var merged = result.Document;

        Assert.Equal(
        [
            "A Study",
            "Introduction",
            "Theirs added an opening paragraph.",
            "Alpha BETA gamma DELTA.",                                     // different words: combined
            "The second paragraph is {~~right here~>over there~~}.",       // same words: a pending change
            "Methods",
            "Methods text.",
            "Ours added a closing paragraph."
        ], merged.Blocks.Select(b => b.Text));
        Assert.Equal(1, result.Conflicts);
        Assert.Equal("p-004", merged.Blocks[4].ParagraphId);              // our ids are kept
        Assert.Equal(merged.Blocks.Count, merged.Blocks.Select(b => b.ParagraphId).Distinct().Count());

        var conflict = Assert.Single(merged.Hunks);
        Assert.Equal(RevisionHunkType.Replacement, conflict.HunkType);
        Assert.Equal("Other computer", conflict.Author);
        Assert.Equal(RevisionHunkStatus.Pending, conflict.Status);

        conflict.Status = RevisionHunkStatus.Accepted;
        Assert.Equal("The second paragraph is over there.", CriticMarkupSerializer.BakeResolvedHunks(merged).Blocks[4].Text);
        conflict.Status = RevisionHunkStatus.Rejected;
        Assert.Equal("The second paragraph is right here.", CriticMarkupSerializer.BakeResolvedHunks(merged).Blocks[4].Text);
    }

    [Fact]
    public void ManuscriptMerger_KeepsAParagraphEditedOnOneSideAndRemovedOnTheOther_AsAPendingChange()
    {
        var baseDoc = BasePaper();
        var ours = BasePaper();
        ours.Blocks.RemoveAt(4);                                             // removed here
        var theirs = BasePaper();
        theirs.Blocks[4].Text = "Third paragraph, which the other computer improved.";

        var result = ManuscriptMerger.Merge(baseDoc, ours, theirs, "Other computer");

        Assert.Equal(1, result.Conflicts);
        var kept = Assert.Single(result.Document.Blocks, b => b.Text.Contains("improved"));
        Assert.Equal("{++Third paragraph, which the other computer improved.++}", kept.Text);
        Assert.Equal(RevisionHunkType.Insertion, Assert.Single(result.Document.Hunks).HunkType);

        // Unchanged on both sides except for the removal: removed without asking.
        var plainRemoval = ManuscriptMerger.Merge(baseDoc, BasePaper(), ours, "Other computer");
        Assert.Equal(0, plainRemoval.Conflicts);
        Assert.DoesNotContain(plainRemoval.Document.Blocks, b => b.Text.StartsWith("Third"));
    }

    [Fact]
    public void SectionStore_WritesOneFilePerSection_AndReadsBackTheSameManuscript()
    {
        var doc = Paper(
            ("p-001", BlockStyleRole.Title, "A Study"),
            ("p-002", BlockStyleRole.Abstract, "We study things."),
            ("p-003", BlockStyleRole.Heading1, "Introduction"),
            ("p-004", BlockStyleRole.BodyParagraph, "Text with a {++pending++} insertion."),
            ("p-005", BlockStyleRole.Heading2, "Background"),
            ("p-006", BlockStyleRole.Heading1, "Résumé & Results"),
            ("p-007", BlockStyleRole.BodyParagraph, "Results text."),
            ("p-008", BlockStyleRole.Heading1, "Introduction"),
            ("p-009", BlockStyleRole.ReferenceItem, "Doe, J. (2020). A reference."));
        doc.GoogleDocId = "DOC1";
        doc.Hunks.Add(new RevisionHunk
        {
            HunkId = "h-1", ParagraphId = "p-004", HunkType = RevisionHunkType.Insertion, ProposedText = "pending",
            Author = "Dr. Rivera", TimestampUtc = new DateTimeOffset(2026, 3, 14, 9, 30, 0, TimeSpan.Zero)
        });

        var files = ManuscriptSectionStore.Write(doc);
        Assert.Equal(
        [
            ".revisions/manuscript.json",
            "manuscript/00-front-matter.md",
            "manuscript/01-introduction.md",
            "manuscript/02-resume-results.md",
            "manuscript/03-introduction.md"
        ], files.Keys.Order(StringComparer.Ordinal));
        Assert.Contains("## Background", files["manuscript/01-introduction.md"]);

        var read = ManuscriptSectionStore.Read(files);
        Assert.Equal(doc.Blocks.Select(b => (b.ParagraphId, b.Role, b.Text)), read.Blocks.Select(b => (b.ParagraphId, b.Role, b.Text)));
        Assert.Equal("A Study", read.Title);
        Assert.Equal("DOC1", read.GoogleDocId);
        var hunk = Assert.Single(read.Hunks);
        Assert.Equal("Dr. Rivera", hunk.Author);                           // restored from the metadata
        Assert.Equal(2026, hunk.TimestampUtc.Year);
    }

    [Fact]
    public void GitHubSync_TwoComputersEditingDifferentSections_AreMergedAndBothChainsVerify()
    {
        string root = Path.Combine(Path.GetTempPath(), $"RevEditorTest_Sync_{Guid.NewGuid():N}");
        try
        {
            // A local bare repository stands in for the private GitHub repository.
            string remote = Path.Combine(root, "github.git");
            Directory.CreateDirectory(remote);
            RunGitIn(remote, "init", "-q", "--bare", "-b", "main");

            var laptop = new GitRepositoryService(Path.Combine(root, "laptop"));
            laptop.CommitSnapshot(BasePaper(), "First draft");
            laptop.SetRemoteUrl(remote);
            var first = laptop.SyncWithRemote();
            Assert.Equal(1, first.BranchesPushed);

            var desktop = GitRepositoryService.CloneFromRemote(remote, Path.Combine(root, "desktop"));
            Assert.Equal("Alpha beta gamma delta.", desktop.LoadCurrentWorkingDocument().Blocks[2].Text);
            Assert.Single(desktop.LoadProvenanceLedger().Blocks);             // the provenance came along

            // Both computers edit different sections before syncing.
            var onLaptop = laptop.LoadCurrentWorkingDocument();
            onLaptop.Blocks[2].Text = "Alpha beta gamma delta, revised on the laptop.";
            laptop.CommitSnapshot(onLaptop, "Revise introduction");
            laptop.SyncWithRemote();

            var onDesktop = desktop.LoadCurrentWorkingDocument();
            onDesktop.Blocks[6].Text = "Methods text, revised on the desktop.";
            desktop.CommitSnapshot(onDesktop, "Revise methods");
            var merged = desktop.SyncWithRemote();
            Assert.Equal(1, merged.BranchesMerged);
            Assert.Equal(0, merged.Conflicts);

            var laptopAgain = laptop.SyncWithRemote();
            Assert.Equal(1, laptopAgain.BranchesUpdated);
            var final = laptop.LoadCurrentWorkingDocument();
            Assert.Equal("Alpha beta gamma delta, revised on the laptop.", final.Blocks[2].Text);
            Assert.Equal("Methods text, revised on the desktop.", final.Blocks[6].Text);
            Assert.Empty(final.Hunks);

            // The history holds a two-parent merge, and blocks signed on both computers verify on each.
            var graph = laptop.GetCommitGraph();
            Assert.Equal(2, graph[0].ParentShas.Count);
            foreach (var repo in new[] { laptop, new GitRepositoryService(Path.Combine(root, "desktop")) })
            {
                var ledger = repo.LoadProvenanceLedger();
                var check = ProvenanceChainService.VerifyChainIntegrity(ledger);
                Assert.True(check.IsValid, check.SummaryMessage);
                Assert.Equal(4, check.VerifiedBlockCount);
                Assert.Equal(2, ledger.Blocks.Select(b => b.AuthorKeyFingerprint).Distinct().Count());
                Assert.Equal(2, ledger.Blocks[^1].ParentBlockHashes.Count);
            }

            // Removing a block from the history is noticed by the blocks that link to it.
            var tampered = laptop.LoadProvenanceLedger();
            tampered.Blocks.RemoveAll(b => b.CommitMessage == "Revise introduction");
            Assert.False(ProvenanceChainService.VerifyChainIntegrity(tampered).IsValid);

            // Edits to the same words on both computers become a pending change instead of being lost.
            var a = laptop.LoadCurrentWorkingDocument();
            a.Blocks[3].Text = "The second paragraph is right here.";
            laptop.CommitSnapshot(a, "Laptop wording");
            laptop.SyncWithRemote();
            var b = desktop.LoadCurrentWorkingDocument();
            b.Blocks[3].Text = "The second paragraph is over there.";
            desktop.CommitSnapshot(b, "Desktop wording");
            var conflicted = desktop.SyncWithRemote();
            Assert.Equal(1, conflicted.Conflicts);
            var withConflict = desktop.LoadCurrentWorkingDocument();
            Assert.Equal("The second paragraph is {~~over there~>right here~~}.", withConflict.Blocks[3].Text);
            Assert.Equal("main (from GitHub)", Assert.Single(withConflict.Hunks).Author);
        }
        finally
        {
            TestWorkspaceBuilder.DeleteDirectory(root);
        }
    }

    [Fact]
    public void ReviewedDocument_FromAnOlderVersion_IsBasedOnThatVersion_AndMergingKeepsLaterEdits()
    {
        string tempRepo = Path.Combine(Path.GetTempPath(), $"RevEditorTest_OutOfOrder_{Guid.NewGuid():N}");
        try
        {
            var repo = new GitRepositoryService(tempRepo);
            var (sentSha, _) = repo.CommitSnapshot(BasePaper(), "Version sent to the reviewer");

            // The reviewer marks up the copy they were sent...
            var reviewed = BasePaper();
            reviewed.Blocks[6].Text = "Methods {++and materials ++}text.";
            reviewed.Hunks.Add(new RevisionHunk { HunkId = "r1", ParagraphId = "p-007", HunkType = RevisionHunkType.Insertion, ProposedText = "and materials ", Author = "Reviewer" });
            string docx = Path.Combine(tempRepo, "reviewed.docx");
            using (var fs = File.Create(docx))
            {
                DocxExportService.ExportToDocx(reviewed, fs, includeTrackedChanges: true);
            }

            // ...while the author keeps writing on main.
            var later = repo.LoadCurrentWorkingDocument();
            later.Blocks[2].Text = "Alpha beta gamma delta, written after the copy was sent.";
            repo.CommitSnapshot(later, "Keep writing");

            var profile = FormattingRuleProfile.GetBuiltInProfiles()[0];
            var original = ImportBaseDetector.OriginalVersionOf(DocxIngestionService.IngestDocx(docx, null, profile));
            var ranked = ImportBaseDetector.Rank(original, repo.LoadProvenanceLedger().Blocks);
            Assert.Equal(sentSha, ranked[0].CommitSha);
            Assert.Equal(1.0, ranked[0].Score);

            // Import onto a branch from that version (as the app does), accept the change, merge into main.
            var baseline = repo.LoadDocumentAtCommit(sentSha);
            var ingested = DocxIngestionService.IngestDocx(docx, baseline, profile);
            Assert.DoesNotContain(ingested.Hunks, h => h.HunkType == RevisionHunkType.UntrackedEdit);
            repo.CreateAndCheckoutBranch("review/reviewer", sentSha);
            repo.CommitSnapshot(ingested, "Import review", "Reviewer", AuthorshipOriginSource.SupervisorTrackedEdit);

            var resolved = CriticMarkupSerializer.BakeResolvedHunks(repo.LoadCurrentWorkingDocument(), acceptRemainingPending: true);
            var outcome = repo.MergeBranchIntoMain("review/reviewer", resolved, "Merge review");

            Assert.NotNull(outcome.Merge);
            Assert.Equal(0, outcome.Merge!.Conflicts);
            var mainDoc = repo.LoadCurrentWorkingDocument();
            Assert.Equal("Alpha beta gamma delta, written after the copy was sent.", mainDoc.Blocks[2].Text);  // kept
            Assert.Equal("Methods and materials text.", mainDoc.Blocks[6].Text);                                // review applied
            Assert.True(ProvenanceChainService.VerifyChainIntegrity(repo.LoadProvenanceLedger()).IsValid);
        }
        finally
        {
            TestWorkspaceBuilder.DeleteDirectory(tempRepo);
        }
    }

    [Fact]
    public async Task LocalBridge_PullAfterEditsHere_MergesThemWithTheGoogleDocEdits()
    {
        int port = 18000 + Random.Shared.Next(2000, 4000);
        string tempRepo = Path.Combine(Path.GetTempPath(), $"RevEditorTest_PullMerge_{Guid.NewGuid():N}");
        var repo = new GitRepositoryService(tempRepo);
        repo.EnsureInitialized();
        try
        {
            using var bridge = new LocalBridgeServer(repo, port);
            if (!bridge.TryStart())
            {
                return; // Port unavailable on this machine; nothing to verify.
            }

            using var http = new HttpClient();
            async Task PullAsync(params string[] paragraphs)
            {
                var request = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{port}/api/google-docs/pull")
                {
                    Content = new StringContent(System.Text.Json.JsonSerializer.Serialize(new
                    {
                        googleDocId = "DOC1",
                        title = "Paper",
                        blocks = paragraphs.Select((t, i) => new { role = i == 0 ? "Title" : "BodyParagraph", text = t }).ToArray()
                    }), System.Text.Encoding.UTF8, "application/json")
                };
                request.Headers.Add("Origin", "chrome-extension://abcdefghijklmnop");
                var response = await http.SendAsync(request);
                Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
            }

            await PullAsync("Paper", "First paragraph.", "Second paragraph.");

            // Edited here (or merged from another computer) before the next pull...
            var here = repo.LoadCurrentWorkingDocument();
            here.Blocks[1].Text = "First paragraph, edited in the app.";
            repo.CommitSnapshot(here, "Edit in app");

            // ...while someone edited a different paragraph in Google Docs.
            await PullAsync("Paper", "First paragraph.", "Second paragraph, edited in Google Docs.");

            var merged = repo.LoadCurrentWorkingDocument();
            Assert.Equal(["Paper", "First paragraph, edited in the app.", "Second paragraph, edited in Google Docs."], merged.Blocks.Select(b => b.Text));

            // The app's edit is still waiting to be sent to Google Docs.
            bridge.RefreshPendingPatches();
            var patch = Assert.Single(bridge.GetPendingPatches());
            Assert.Equal("First paragraph, edited in the app.", patch.AfterText);
        }
        finally
        {
            TestWorkspaceBuilder.DeleteDirectory(tempRepo);
        }
    }
}
