using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Xml.Linq;
using RevisionsEditor.App;
using RevisionsEditor.Core.Bridge;
using RevisionsEditor.Core.Diffing;
using RevisionsEditor.Core.Docx;
using RevisionsEditor.Core.Formatting;
using RevisionsEditor.Core.Git;
using RevisionsEditor.Core.Models;
using RevisionsEditor.Core.Provenance;
using RevisionsEditor.Core.Serialization;
using Xunit;

namespace RevisionsEditor.Tests;

public sealed partial class RevisionsEditorTests
{
    [Fact]
    public void BlockchainTextChain_VerifiesValidBlocks_IgnoresFormattingChanges_AndDetectsTampering()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"RevEditorTest_Chain_{Guid.NewGuid():N}");
        try
        {
            var ledger = ProvenanceChainService.LoadOrCreateLedger(tempDir, "doc-test");

            var docV1 = new ManuscriptDocument
            {
                DocumentId = "doc-test",
                Title = "Quantum Error Correction in Neutral Atom Arrays",
                Blocks =
                [
                    new DocumentBlock { ParagraphId = "p-001", Role = BlockStyleRole.Title, Text = "Quantum Error Correction in Neutral Atom Arrays" },
                    new DocumentBlock { ParagraphId = "p-002", Role = BlockStyleRole.Heading1, Text = "Introduction" },
                    new DocumentBlock { ParagraphId = "p-003", Role = BlockStyleRole.BodyParagraph, Text = "Neutral atom arrays provide a scalable architecture for fault-tolerant quantum computation. Coherence times exceed several seconds." }
                ]
            };

            var b0 = ProvenanceChainService.AppendSignedBlock(
                tempDir, ledger, docV1, "1111111a", "Initial draft", "Author",
                AuthorshipOriginSource.HandWrittenInWpf);

            var docV2 = docV1.Clone();
            docV2.Blocks[2].Text = "Neutral atom arrays provide a highly scalable architecture for fault-tolerant quantum computation. Coherence times routinely exceed several seconds in optical tweezers.";

            var b1 = ProvenanceChainService.AppendSignedBlock(
                tempDir, ledger, docV2, "2222222b", "Refine introduction", "Author",
                AuthorshipOriginSource.HandWrittenInGoogleDocs);

            // 1. Verify intact chain passes
            var check1 = ProvenanceChainService.VerifyChainIntegrity(ledger);
            Assert.True(check1.IsValid);
            Assert.Equal(2, check1.VerifiedBlockCount);
            Assert.Null(check1.TamperedBlockIndex);
            Assert.Equal(b0.BlockHash, b1.PreviousBlockHash);

            // 2. Apply radical formatting rule changes (smart quotes, auto-numbered headings, IEEE compact style)
            // and verify the canonical text Merkle root is 100% invariant!
            string merkleBeforeFormatting = TextCanonicalizer.ComputeMerkleRootFromCanonicalSnapshot(
                TextCanonicalizer.SerializeCanonicalTextSnapshot(docV2));

            var ieeeProfile = FormattingRuleProfile.GetBuiltInProfiles()[2];
            ieeeProfile.AutoNumberHeadings = true;
            ieeeProfile.ConvertStraightQuotesToSmart = true;
            FormattingRuleEngine.ApplyFormattingRules(docV2, ieeeProfile);

            string merkleAfterFormatting = TextCanonicalizer.ComputeMerkleRootFromCanonicalSnapshot(
                TextCanonicalizer.SerializeCanonicalTextSnapshot(docV2));

            Assert.Equal(merkleBeforeFormatting, merkleAfterFormatting);

            // 3. Simulate retroactive text tampering in Block #0
            ledger.Blocks[0].CanonicalManuscriptSnapshot += " [FALSIFIED CLAIM]";
            var tamperedCheck = ProvenanceChainService.VerifyChainIntegrity(ledger);
            Assert.False(tamperedCheck.IsValid);
            Assert.Equal(0, tamperedCheck.TamperedBlockIndex);
            Assert.False(tamperedCheck.BlockStatuses[0].MerkleRootMatches);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Fact]
    public void SentenceProvenance_TracksEvolutionAcrossEdits_AndRecordsWhereTextCameFrom()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"RevEditorTest_Prov_{Guid.NewGuid():N}");
        try
        {
            var ledger = ProvenanceChainService.LoadOrCreateLedger(tempDir, "doc-prov");

            var doc1 = new ManuscriptDocument
            {
                Blocks =
                [
                    new DocumentBlock
                    {
                        ParagraphId = "p-001",
                        Role = BlockStyleRole.BodyParagraph,
                        Text = "We measured the latency of distributed consensus across five continental regions."
                    }
                ]
            };

            ProvenanceChainService.AppendSignedBlock(
                tempDir, ledger, doc1, "aaaa111", "Draft sentence", "Author",
                AuthorshipOriginSource.HandWrittenInWpf);

            Assert.Single(ledger.Sentences);
            string originalSentenceId = ledger.Sentences[0].SentenceId;
            Assert.Equal(AuthorshipOriginSource.HandWrittenInWpf, ledger.Sentences[0].OriginSource);

            // Evolve the first sentence and add a second pasted sentence
            var doc2 = new ManuscriptDocument
            {
                Blocks =
                [
                    new DocumentBlock
                    {
                        ParagraphId = "p-001",
                        Role = BlockStyleRole.BodyParagraph,
                        Text = "We systematically measured the tail latency of distributed consensus across five continental cloud regions. This second sentence was pasted wholesale from an external source."
                    }
                ]
            };

            ProvenanceChainService.AppendSignedBlock(
                tempDir, ledger, doc2, "bbbb222", "Revise first sentence and paste second", "Author",
                AuthorshipOriginSource.ExternalPaste);

            Assert.Equal(2, ledger.Sentences.Count);
            var evolvedSentence = ledger.Sentences.First(s => s.SentenceId == originalSentenceId);
            Assert.Equal(2, evolvedSentence.TotalRevisions);
            Assert.Contains("tail latency", evolvedSentence.CurrentCanonicalText);

            var pastedSentence = ledger.Sentences.First(s => s.SentenceId != originalSentenceId);
            Assert.Equal(AuthorshipOriginSource.ExternalPaste, pastedSentence.OriginSource);
            Assert.Equal(1, pastedSentence.TotalRevisions);
            Assert.Equal("Author", Assert.Single(pastedSentence.RevisionHistory).Author);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Fact]
    public void DocxTrackChanges_RoundTripsInsertionsDeletionsComments_AndDetectsUntrackedSupervisorEdits()
    {
        string tempFile = Path.Combine(Path.GetTempPath(), $"RevEditorTest_{Guid.NewGuid():N}.docx");
        try
        {
            var baselineDoc = new ManuscriptDocument
            {
                Title = "Baseline Paper",
                Blocks =
                [
                    new DocumentBlock { ParagraphId = "p-001", Role = BlockStyleRole.Title, Text = "Baseline Paper" },
                    new DocumentBlock { ParagraphId = "p-002", Role = BlockStyleRole.BodyParagraph, Text = "Our initial algorithm achieves linear convergence on convex objectives." },
                    new DocumentBlock { ParagraphId = "p-003", Role = BlockStyleRole.BodyParagraph, Text = "Experimental results confirm the theoretical bound across all benchmarks." }
                ]
            };

            var supervisorEditedDoc = baselineDoc.Clone();
            // Paragraph 2 has tracked replacement, insertion, and supervisor comment:
            supervisorEditedDoc.Blocks[1].Text =
                "Our {~~initial~>proposed proximal~~} algorithm achieves {++super-++}linear convergence on {==convex objectives.==}{>>Prof. Smith: Specify strong convexity parameter mu here.<<}";
            // Paragraph 3 has an UNTRACKED direct edit ("all benchmarks" -> "twelve synthetic datasets"):
            supervisorEditedDoc.Blocks[2].Text =
                "Experimental results confirm the theoretical bound across twelve synthetic datasets.";

            DocxExportService.ExportToDocx(supervisorEditedDoc, tempFile, includeTrackedChanges: true);
            Assert.True(File.Exists(tempFile));

            var ingested = DocxIngestionService.IngestDocx(tempFile, baselineDoc);

            Assert.Equal(3, ingested.Blocks.Count);
            Assert.Contains(ingested.Hunks, h => h.HunkType == RevisionHunkType.Replacement && h.OriginalText == "initial" && h.ProposedText == "proposed proximal");
            Assert.Contains(ingested.Hunks, h => h.HunkType == RevisionHunkType.Insertion && h.ProposedText == "super-");
            Assert.Contains(ingested.Hunks, h => h.HunkType == RevisionHunkType.Comment && h.CommentText.Contains("strong convexity"));
            Assert.Contains(ingested.Hunks, h => h.HunkType == RevisionHunkType.UntrackedEdit && h.OriginalText.Contains("all benchmarks") && h.ProposedText.Contains("twelve synthetic datasets"));
        }
        finally
        {
            if (File.Exists(tempFile))
            {
                File.Delete(tempFile);
            }
        }
    }

    [Fact]
    public void FormattingRuleEngine_SanitizesForeignStyles_ComputesSectionNumbers_AndGeneratesCleanWordStylesXml()
    {
        var doc = new ManuscriptDocument
        {
            Title = "Formatted Paper",
            Blocks =
            [
                new DocumentBlock { ParagraphId = "p-001", Role = BlockStyleRole.Title, Text = "Formatted Paper" },
                new DocumentBlock { ParagraphId = "p-002", Role = BlockStyleRole.Heading1, Text = "1.   Background  <span style=\"font-family:Comic Sans MS\">Study</span>" },
                new DocumentBlock { ParagraphId = "p-003", Role = BlockStyleRole.Heading2, Text = "Prior   Literature" },
                new DocumentBlock { ParagraphId = "p-004", Role = BlockStyleRole.BodyParagraph, Text = "First sentence.   Second sentence with \"quoted terms\"." }
            ]
        };

        var profile = FormattingRuleProfile.GetBuiltInProfiles()[1]; // Cambria 11pt Modern Preprint
        profile.AutoNumberHeadings = true;
        profile.ConvertStraightQuotesToSmart = true;

        FormattingRuleEngine.ApplyFormattingRules(doc, profile);

        Assert.Equal("Background Study", doc.Blocks[1].Text);
        Assert.Equal("1. ", doc.Blocks[1].ComputedSectionNumber);
        Assert.Equal("1.1 ", doc.Blocks[2].ComputedSectionNumber);
        Assert.Equal("First sentence. Second sentence with \u201Cquoted terms\u201D.", doc.Blocks[3].Text);

        using var ms = new MemoryStream();
        DocxExportService.ExportToDocx(doc, ms, profile, includeTrackedChanges: false);
        ms.Position = 0;

        using var zip = new ZipArchive(ms, ZipArchiveMode.Read);
        var stylesEntry = zip.GetEntry("word/styles.xml");
        Assert.NotNull(stylesEntry);

        using var sStream = stylesEntry!.Open();
        var stylesXml = XDocument.Load(sStream);
        string xmlString = stylesXml.ToString();
        Assert.Contains("Cambria", xmlString);
        Assert.DoesNotContain("Comic Sans", xmlString);
    }

    [Fact]
    public void EndToEndGitWorkflow_SeedsBranches_ResolvesSupervisorHunks_MergesIntoMain_AndGeneratesGoogleDocPatches()
    {
        string tempRepo = Path.Combine(Path.GetTempPath(), $"RevEditorTest_Git_{Guid.NewGuid():N}");
        try
        {
            var gitService = new GitRepositoryService(tempRepo);
            string reviewedDocx = TestWorkspaceBuilder.SeedReviewWorkflow(gitService);

            Assert.True(File.Exists(reviewedDocx));
            Assert.Equal(TestWorkspaceBuilder.ReviewBranch, gitService.GetCurrentBranch());

            var reviewDoc = gitService.LoadCurrentWorkingDocument();
            Assert.NotEmpty(reviewDoc.Hunks);

            // Accept all hunks except reject the deletion in p-004
            foreach (var h in reviewDoc.Hunks)
            {
                h.Status = h.HunkType == RevisionHunkType.Deletion
                    ? RevisionHunkStatus.Rejected
                    : RevisionHunkStatus.Accepted;
            }

            var resolvedDoc = CriticMarkupSerializer.BakeResolvedHunks(reviewDoc, acceptRemainingPending: true);
            Assert.Empty(resolvedDoc.Hunks);

            var (mergeSha, signedBlock, _) = gitService.MergeBranchIntoMain(
                TestWorkspaceBuilder.ReviewBranch,
                resolvedDoc,
                "Merge supervisor round 1 review into main");

            Assert.NotEmpty(mergeSha);
            Assert.Equal("main", gitService.GetCurrentBranch());

            var graph = gitService.GetCommitGraph();
            Assert.True(graph.Count >= 4);
            Assert.Equal(2, graph[0].ParentShas.Count); // True 2-parent Git merge commit!

            var baseline = gitService.LoadGoogleDocsBaseline()!;
            var patches = DiffEngineService.GenerateGoogleDocPatches(baseline, resolvedDoc);
            Assert.NotEmpty(patches);
            Assert.Contains(patches, p => p.AfterText.Contains("toolchain divide"));
        }
        finally
        {
            TestWorkspaceBuilder.DeleteDirectory(tempRepo);
        }
    }

    [Fact]
    public void GitCommit_PreservesQuotesInMessages_AndKeepsSigningKeyOutOfHistory()
    {
        string tempRepo = Path.Combine(Path.GetTempPath(), $"RevEditorTest_Quote_{Guid.NewGuid():N}");
        try
        {
            var gitService = new GitRepositoryService(tempRepo);
            var doc = new ManuscriptDocument
            {
                Blocks = [new DocumentBlock { ParagraphId = "p-001", Role = BlockStyleRole.BodyParagraph, Text = "A sentence." }]
            };

            const string message = "Use \"smart\" quotes; don't break --author or $HOME";
            gitService.CommitSnapshot(doc, message);

            var graph = gitService.GetCommitGraph();
            Assert.Single(graph);
            Assert.Equal(message, graph[0].Message);
            Assert.True(graph[0].IsCryptographicallyVerified);

            string gitignore = File.ReadAllText(Path.Combine(tempRepo, ".gitignore"));
            Assert.Contains(".revisions/author-key.json", gitignore);
        }
        finally
        {
            TestWorkspaceBuilder.DeleteDirectory(tempRepo);
        }
    }

    [Fact]
    public void DocxExport_AttributesTrackedChangesToTheirAuthors_AndOmitsResolvedChanges()
    {
        var timestamp = new DateTimeOffset(2026, 3, 14, 9, 30, 0, TimeSpan.Zero);
        var doc = new ManuscriptDocument
        {
            Blocks =
            [
                new DocumentBlock { ParagraphId = "p-001", Role = BlockStyleRole.BodyParagraph, Text = "Results are {++very ++}{~~good~>strong~~}." }
            ],
            Hunks =
            [
                new RevisionHunk { HunkId = "h1", ParagraphId = "p-001", HunkType = RevisionHunkType.Insertion, ProposedText = "very ", Author = "Dr. Rivera", TimestampUtc = timestamp },
                new RevisionHunk { HunkId = "h2", ParagraphId = "p-001", HunkType = RevisionHunkType.Replacement, OriginalText = "good", ProposedText = "strong", Author = "Dr. Rivera", Status = RevisionHunkStatus.Accepted }
            ]
        };

        // The app bakes accepted/rejected changes before exporting, so only the pending insertion remains tracked.
        var resolved = CriticMarkupSerializer.BakeResolvedHunks(doc, acceptRemainingPending: false);
        using var ms = new MemoryStream();
        DocxExportService.ExportToDocx(resolved, ms, includeTrackedChanges: true);
        ms.Position = 0;

        using var zip = new ZipArchive(ms, ZipArchiveMode.Read);
        using var docStream = zip.GetEntry("word/document.xml")!.Open();
        var xml = XDocument.Load(docStream);
        XNamespace w = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";

        var insertion = Assert.Single(xml.Descendants(w + "ins"));
        Assert.Equal("Dr. Rivera", insertion.Attribute(w + "author")?.Value);
        Assert.Equal("2026-03-14T09:30:00Z", insertion.Attribute(w + "date")?.Value);
        Assert.Empty(xml.Descendants(w + "del"));
        Assert.Contains("strong", string.Concat(xml.Descendants(w + "t").Select(t => t.Value)));
    }

    [Fact]
    public void WorkspaceFromEarlierVersion_MovesToSectionFilesAndNotes_AndItsChainStillVerifiesAndGrows()
    {
        string tempRepo = Path.Combine(Path.GetTempPath(), $"RevEditorTest_Legacy_{Guid.NewGuid():N}");
        try
        {
            // Build a workspace the way earlier versions stored it: manuscript.md, document-state.json and a
            // linear signed ledger file (with typing counts) on one computer.
            Directory.CreateDirectory(tempRepo);
            RunGitIn(tempRepo, "init", "-q", "-b", "main");
            RunGitIn(tempRepo, "config", "user.name", "Author");
            RunGitIn(tempRepo, "config", "user.email", "author@example.com");
            string revisions = Path.Combine(tempRepo, ".revisions");
            var doc = new ManuscriptDocument
            {
                Title = "Legacy paper",
                Blocks =
                [
                    new DocumentBlock { ParagraphId = "p-001", Role = BlockStyleRole.Title, Text = "Legacy paper" },
                    new DocumentBlock { ParagraphId = "p-002", Role = BlockStyleRole.BodyParagraph, Text = "A sentence written earlier." }
                ]
            };
            var legacyLedger = ProvenanceChainService.LoadOrCreateLedger(revisions, "doc-main");
            var shas = new List<string>();
            for (int i = 0; i < 2; i++)
            {
                if (i == 1) doc.Blocks[1].Text = "A sentence written earlier, then revised.";
                File.WriteAllText(Path.Combine(tempRepo, "manuscript.md"), CriticMarkupSerializer.ToMarkdown(doc, includeAnchorComments: true));
                File.WriteAllText(Path.Combine(revisions, "document-state.json"), System.Text.Json.JsonSerializer.Serialize(doc));
                RunGitIn(tempRepo, "add", "manuscript.md", ".revisions/document-state.json");
                RunGitIn(tempRepo, "commit", "-q", "-m", $"Version {i + 1}");
                string sha = RunGitIn(tempRepo, "rev-parse", "HEAD").Trim();
                shas.Add(sha);
                var v1 = LegacyBlock(revisions, legacyLedger, doc, sha, i);
                legacyLedger.Blocks.Add(v1);
            }
            string ledgerPath = Path.Combine(revisions, ProvenanceChainService.LegacyLedgerFileName);
            var json = System.Text.Json.Nodes.JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(legacyLedger))!;
            json["Blocks"]![0]!["Telemetry"] = System.Text.Json.Nodes.JsonNode.Parse("""{ "KeystrokeCount": 320, "BackspaceCount": 45 }""");
            File.WriteAllText(ledgerPath, json.ToJsonString());

            // Opening it moves the chain into Git notes; both earlier blocks still verify.
            var repo = new GitRepositoryService(tempRepo);
            var ledger = repo.LoadProvenanceLedger();
            Assert.False(File.Exists(ledgerPath));
            Assert.True(File.Exists(ledgerPath + ".migrated"));
            var check = ProvenanceChainService.VerifyChainIntegrity(ledger);
            Assert.True(check.IsValid, check.SummaryMessage);
            Assert.Equal(2, check.VerifiedBlockCount);
            Assert.Equal("A sentence written earlier, then revised.", repo.LoadCurrentWorkingDocument().Blocks[1].Text);
            Assert.Equal("A sentence written earlier.", repo.LoadDocumentAtCommit(shas[0]).Blocks[1].Text);

            // The next commit switches to section files, removes the old files from the repository, and
            // links its new-style block to the last earlier block.
            var current = repo.LoadCurrentWorkingDocument();
            current.Blocks.Add(new DocumentBlock { ParagraphId = "p-003", Role = BlockStyleRole.Heading1, Text = "Methods" });
            current.Blocks.Add(new DocumentBlock { ParagraphId = "p-004", Role = BlockStyleRole.BodyParagraph, Text = "We describe the method." });
            var (newSha, block) = repo.CommitSnapshot(current, "Add methods");
            Assert.Equal(2, block.Version);
            Assert.Equal([ledger.Blocks[^1].BlockHash], block.ParentBlockHashes);

            string tracked = RunGitIn(tempRepo, "ls-tree", "-r", "--name-only", "HEAD");
            Assert.Contains("manuscript/00-front-matter.md", tracked);
            Assert.Contains("manuscript/01-methods.md", tracked);
            Assert.Contains(".revisions/manuscript.json", tracked);
            var trackedFiles = tracked.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            Assert.DoesNotContain("manuscript.md", trackedFiles);
            Assert.DoesNotContain(".revisions/document-state.json", trackedFiles);
            Assert.False(File.Exists(Path.Combine(tempRepo, "manuscript.md")));

            var reloaded = new GitRepositoryService(tempRepo).LoadProvenanceLedger();
            var check2 = ProvenanceChainService.VerifyChainIntegrity(reloaded);
            Assert.True(check2.IsValid, check2.SummaryMessage);
            Assert.Equal(3, check2.VerifiedBlockCount);
            Assert.Equal(2, reloaded.Sentences.First(s => s.CurrentCanonicalText.StartsWith("A sentence")).TotalRevisions);
            Assert.Equal(4, new GitRepositoryService(tempRepo).LoadDocumentAtCommit(newSha).Blocks.Count);
        }
        finally
        {
            TestWorkspaceBuilder.DeleteDirectory(tempRepo);
        }
    }

    /// <summary>A block signed the way earlier versions did: version 1 header, linear chain.</summary>
    private static TextChainBlock LegacyBlock(string revisionsDir, ProvenanceLedger ledger, ManuscriptDocument doc, string sha, int index)
    {
        var (signer, _, fingerprint) = ProvenanceChainService.LoadOrCreateAuthorKey(revisionsDir);
        using (signer)
        {
            string snapshot = TextCanonicalizer.SerializeCanonicalTextSnapshot(doc);
            var block = new TextChainBlock
            {
                BlockIndex = index,
                CommitSha = sha,
                CommitMessage = $"Version {index + 1}",
                TimestampUtc = DateTimeOffset.UtcNow,
                AuthorName = "Author",
                AuthorKeyFingerprint = fingerprint,
                PreviousBlockHash = index == 0 ? new string('0', 64) : ledger.Blocks[^1].BlockHash,
                NormalizedTextMerkleRoot = TextCanonicalizer.ComputeMerkleRootFromCanonicalSnapshot(snapshot),
                SentenceProvenanceRoot = "legacy",
                CanonicalManuscriptSnapshot = snapshot
            };
            block.BlockHash = ProvenanceChainService.ComputeBlockHeaderHash(block);
            block.SignatureBase64 = Convert.ToBase64String(signer.SignData(System.Text.Encoding.UTF8.GetBytes(block.BlockHash), System.Security.Cryptography.HashAlgorithmName.SHA256));
            return block;
        }
    }

    private static string RunGitIn(string dir, params string[] args)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("git") { WorkingDirectory = dir, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (string a in args) psi.ArgumentList.Add(a);
        using var p = System.Diagnostics.Process.Start(psi)!;
        var err = p.StandardError.ReadToEndAsync();
        string output = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0) throw new InvalidOperationException($"git {string.Join(' ', args)}: {err.Result}");
        return output;
    }

    [Theory]
    [InlineData("The quick brown fox jumps.", "The quick red fox jumps.")]
    [InlineData("Alpha beta gamma.", "Alpha beta gamma delta.")]
    [InlineData("Remove this word please.", "Remove word please.")]
    [InlineData("", "Entirely new text.")]
    [InlineData("Same same same.", "Same same same same.")]
    public void WordDiff_ReconstructsBothVersions(string before, string after)
    {
        var tokens = DiffEngineService.ComputeWordDiff(before, after);

        string rebuiltBefore = string.Concat(tokens.Where(t => t.Kind != DiffTokenKind.Inserted).Select(t => t.Text));
        string rebuiltAfter = string.Concat(tokens.Where(t => t.Kind != DiffTokenKind.Deleted).Select(t => t.Text));

        Assert.Equal(before, rebuiltBefore);
        Assert.Equal(after, rebuiltAfter);
    }

    /// <summary>
    /// A minimal .docx shaped like a Google Docs export: link text inside w:hyperlink, explicit w:val="0"
    /// formatting toggles, a tab between words, and a paragraph inside a table.
    /// </summary>
    private static byte[] BuildGoogleDocsStyleDocx()
    {
        const string documentXml = """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"
                        xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">
              <w:body>
                <w:p><w:pPr><w:pStyle w:val="Title"/></w:pPr><w:r><w:rPr><w:b w:val="0"/></w:rPr><w:t>Linked Manuscript</w:t></w:r></w:p>
                <w:p>
                  <w:r><w:rPr><w:b w:val="0"/><w:i w:val="0"/></w:rPr><w:t xml:space="preserve">See the </w:t></w:r>
                  <w:hyperlink r:id="rId5"><w:r><w:rPr><w:u w:val="single"/></w:rPr><w:t>project website</w:t></w:r></w:hyperlink>
                  <w:r><w:t xml:space="preserve"> for data.</w:t></w:r>
                  <w:r><w:tab/><w:t>Then</w:t></w:r>
                  <w:r><w:rPr><w:b w:val="1"/></w:rPr><w:t>bold</w:t></w:r>
                </w:p>
                <w:tbl><w:tr><w:tc><w:p><w:r><w:t>Table cell text</w:t></w:r></w:p></w:tc></w:tr></w:tbl>
              </w:body>
            </w:document>
            """;

        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            using var writer = new StreamWriter(zip.CreateEntry("word/document.xml").Open());
            writer.Write(documentXml);
        }
        return ms.ToArray();
    }

    [Fact]
    public void DocxIngestion_KeepsHyperlinkTableAndTabText_FromGoogleDocsExports()
    {
        using var ms = new MemoryStream(BuildGoogleDocsStyleDocx());
        var doc = DocxIngestionService.IngestDocx(ms, "Fallback");

        Assert.Equal("Linked Manuscript", doc.Title);
        Assert.Equal(3, doc.Blocks.Count);
        Assert.Equal("See the project website for data. Then**bold**", doc.Blocks[1].Text);
        Assert.Equal("Table cell text", doc.Blocks[2].Text);
    }

    [Fact]
    public async Task LocalBridge_PullFromExtension_CommitsGoogleDocToWorkspace()
    {
        int port = 18000 + Random.Shared.Next(2000, 4000);
        string tempRepo = Path.Combine(Path.GetTempPath(), $"RevEditorTest_Pull_{Guid.NewGuid():N}");
        var repo = new GitRepositoryService(tempRepo);
        repo.EnsureInitialized();

        try
        {
            using var bridge = new LocalBridgeServer(repo, port);
            if (!bridge.TryStart())
            {
                return; // Port unavailable on this machine; nothing to verify.
            }

            // The same request the extension's service worker sends.
            using var http = new HttpClient();
            var request = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{port}/api/google-docs/pull")
            {
                Content = new StringContent(System.Text.Json.JsonSerializer.Serialize(new
                {
                    googleDocId = "1AbC_test",
                    title = "Linked Manuscript",
                    docxBase64 = Convert.ToBase64String(BuildGoogleDocsStyleDocx()),
                    // Sent by extension versions before 1.3.1; now ignored.
                    telemetry = new { keystrokeCount = 12, backspaceCount = 2, editBurstCount = 1, sourceEnvironment = "Google Docs Chrome Extension" }
                }), System.Text.Encoding.UTF8, "application/json")
            };
            request.Headers.Add("Origin", "chrome-extension://abcdefghijklmnop");

            var response = await http.SendAsync(request);
            string body = await response.Content.ReadAsStringAsync();
            Assert.True(response.IsSuccessStatusCode, body);
            Assert.Contains("committed", body);

            var committed = repo.LoadCurrentWorkingDocument();
            Assert.Equal("1AbC_test", committed.GoogleDocId);
            Assert.Contains(committed.Blocks, b => b.Text.Contains("project website"));
            Assert.Single(repo.GetCommitGraph());
            Assert.NotNull(repo.LoadGoogleDocsBaseline());
        }
        finally
        {
            TestWorkspaceBuilder.DeleteDirectory(tempRepo);
        }
    }

    private static ManuscriptDocument Doc(params (BlockStyleRole Role, string Text)[] blocks) => new()
    {
        Blocks = blocks.Select((b, i) => new DocumentBlock { ParagraphId = $"p-{i + 1:D3}", Role = b.Role, Text = b.Text }).ToList()
    };

    [Fact]
    public void GoogleDocsText_HasNoStrayEmphasisMarkers_WhenBoldRunsEndInSpaces()
    {
        // Google's export ends bold runs with the following space: <w:r><w:b/><w:t>bold </w:t></w:r>.
        const string documentXml = """
            <w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"><w:body>
              <w:p><w:r><w:t xml:space="preserve">A </w:t></w:r><w:r><w:rPr><w:b/></w:rPr><w:t xml:space="preserve">bold </w:t></w:r><w:r><w:rPr><w:i/></w:rPr><w:t xml:space="preserve"> italic</w:t></w:r><w:r><w:rPr><w:b/></w:rPr><w:t xml:space="preserve"> </w:t></w:r><w:r><w:t>end.</w:t></w:r></w:p>
            </w:body></w:document>
            """;
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        using (var writer = new StreamWriter(zip.CreateEntry("word/document.xml").Open()))
        {
            writer.Write(documentXml);
        }
        ms.Position = 0;

        var block = Assert.Single(DocxIngestionService.IngestDocx(ms).Blocks);
        Assert.Equal("A **bold** *italic* end.", block.Text);                           // markers hug the words
        Assert.Equal("A bold italic end.", DiffEngineService.ToGoogleDocsText(block.Text));

        // Snapshots imported before this fix contain "**bold **"; their Google Docs text must be clean too.
        Assert.Equal("A bold italic end.", DiffEngineService.ToGoogleDocsText("A **bold ***italic* ** **end."));
    }

    [Fact]
    public void GoogleDocPatches_AlignParagraphsByContent_AndUseStableIds()
    {
        var googleDocs = Doc(
            (BlockStyleRole.Title, "A Study"),
            (BlockStyleRole.BodyParagraph, "First “quoted” paragraph."),
            (BlockStyleRole.BodyParagraph, "Second paragraph stays."),
            (BlockStyleRole.BodyParagraph, "Old closing remark."),
            (BlockStyleRole.BodyParagraph, "Paragraph to remove."));
        var merged = Doc(
            (BlockStyleRole.Title, "A Study"),
            (BlockStyleRole.BodyParagraph, "First \"quoted\" paragraph."),         // only quote style differs: no patch
            (BlockStyleRole.Heading1, "A **new** section"),                         // inserted, with emphasis markers
            (BlockStyleRole.BodyParagraph, "Second paragraph stays."),
            (BlockStyleRole.BodyParagraph, "New closing {++remark++}."));

        var patches = DiffEngineService.GenerateGoogleDocPatches(googleDocs, merged);

        // One insertion, one modification, one deletion: the inserted paragraph does not shift the others.
        Assert.Equal(3, patches.Count);
        var insert = Assert.Single(patches, p => p.Kind == GoogleDocPatchKind.Insert);
        Assert.Equal("A new section", insert.AfterText);                          // plain text, no ** markers
        Assert.Equal("First “quoted” paragraph.", insert.AnchorText);   // Google Docs text it follows
        Assert.Equal(BlockStyleRole.Heading1, insert.Role);

        var modify = Assert.Single(patches, p => p.Kind == GoogleDocPatchKind.Modify);
        Assert.Equal("Old closing remark.", modify.BeforeText);
        Assert.Equal("New closing remark.", modify.AfterText);

        var delete = Assert.Single(patches, p => p.Kind == GoogleDocPatchKind.Delete);
        Assert.Equal("Paragraph to remove.", delete.BeforeText);

        // Ids depend on content only, so regenerating keeps them (acknowledgements stay attached).
        var again = DiffEngineService.GenerateGoogleDocPatches(googleDocs, merged);
        Assert.Equal(patches.Select(p => p.PatchId), again.Select(p => p.PatchId));
        Assert.Equal(3, patches.Select(p => p.PatchId).Distinct().Count());
    }

    [Fact]
    public async Task LocalBridge_PullOfOneDocumentTab_RecordsTheTab_AndReportsItWithPatches()
    {
        int port = 18000 + Random.Shared.Next(6000, 8000);
        string tempRepo = Path.Combine(Path.GetTempPath(), $"RevEditorTest_Tab_{Guid.NewGuid():N}");
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
            async Task<string> PostAsync(string endpoint, object payload)
            {
                var request = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{port}{endpoint}")
                {
                    Content = new StringContent(System.Text.Json.JsonSerializer.Serialize(payload), System.Text.Encoding.UTF8, "application/json")
                };
                request.Headers.Add("Origin", "chrome-extension://abcdefghijklmnop");
                var response = await http.SendAsync(request);
                string body = await response.Content.ReadAsStringAsync();
                Assert.True(response.IsSuccessStatusCode, body);
                return body;
            }

            // The extension sends only the "Revision" tab, read through the Docs API.
            await PostAsync("/api/google-docs/pull", new
            {
                googleDocId = "DOC1",
                title = "Thesis",
                tabId = "t.b",
                tabTitle = "Revision",
                blocks = new object[]
                {
                    new { role = "Title", text = "Revision Title" },
                    new { role = "BodyParagraph", text = "A **bold** claim." },
                    new { role = "Heading2", text = "Methods" }
                }
            });

            var committed = repo.LoadCurrentWorkingDocument();
            Assert.Equal(["Revision Title", "A **bold** claim.", "Methods"], committed.Blocks.Select(b => b.Text));
            Assert.Equal([BlockStyleRole.Title, BlockStyleRole.BodyParagraph, BlockStyleRole.Heading2], committed.Blocks.Select(b => b.Role));
            Assert.Equal("Revision Title", committed.Title);
            var baseline = repo.LoadGoogleDocsBaseline()!;
            Assert.Equal("t.b", baseline.GoogleDocTabId);
            Assert.Equal("Revision", baseline.GoogleDocTabTitle);
            Assert.Contains("tab 'Revision'", repo.GetCommitGraph()[0].Message);

            // A later change in the app is reported as a patch for that tab.
            var edited = repo.LoadCurrentWorkingDocument();
            edited.Blocks[1].Text = "A **strong** claim.";
            repo.CommitSnapshot(edited, "Edit in app");

            string pending = await http.GetStringAsync($"http://127.0.0.1:{port}/api/google-docs/pending-patches");
            using var json = System.Text.Json.JsonDocument.Parse(pending);
            Assert.Equal("t.b", json.RootElement.GetProperty("tabId").GetString());
            Assert.Equal("Revision", json.RootElement.GetProperty("tabTitle").GetString());
            var patch = Assert.Single(json.RootElement.GetProperty("patches").EnumerateArray());
            Assert.Equal("A bold claim.", patch.GetProperty("beforeText").GetString());
            Assert.Equal("A strong claim.", patch.GetProperty("afterText").GetString());
        }
        finally
        {
            TestWorkspaceBuilder.DeleteDirectory(tempRepo);
        }
    }

    [Fact]
    public async Task LocalBridge_BaselineUpdateAfterApplying_ClearsAppliedPatches()
    {
        int port = 18000 + Random.Shared.Next(4000, 6000);
        string tempRepo = Path.Combine(Path.GetTempPath(), $"RevEditorTest_Baseline_{Guid.NewGuid():N}");
        var repo = new GitRepositoryService(tempRepo);
        try
        {
            // Google Docs snapshot is older than the merged manuscript in the repository.
            var merged = Doc((BlockStyleRole.Title, "Linked Manuscript"), (BlockStyleRole.BodyParagraph, "See the project website for data. Then**bold**"));
            repo.CommitSnapshot(merged, "Merged review");
            repo.SaveGoogleDocsBaseline(Doc((BlockStyleRole.Title, "Linked Manuscript"), (BlockStyleRole.BodyParagraph, "See the website.")));

            using var bridge = new LocalBridgeServer(repo, port);
            if (!bridge.TryStart())
            {
                return; // Port unavailable on this machine; nothing to verify.
            }
            bridge.RefreshPendingPatches();
            var patch = Assert.Single(bridge.GetPendingPatches());
            Assert.Equal("See the project website for data. Thenbold", patch.AfterText);

            // After the extension applies the change, it posts a fresh export of the document.
            using var http = new HttpClient();
            var request = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{port}/api/google-docs/baseline")
            {
                Content = new StringContent(System.Text.Json.JsonSerializer.Serialize(new
                {
                    googleDocId = "1AbC_test",
                    title = "Linked Manuscript",
                    docxBase64 = Convert.ToBase64String(BuildGoogleDocsStyleDocx())
                }), System.Text.Encoding.UTF8, "application/json")
            };
            request.Headers.Add("Origin", "chrome-extension://abcdefghijklmnop");
            var response = await http.SendAsync(request);
            string body = await response.Content.ReadAsStringAsync();
            Assert.True(response.IsSuccessStatusCode, body);

            // The fixture document has the merged paragraph plus a table cell, so only that extra cell remains.
            var remaining = bridge.GetPendingPatches();
            Assert.DoesNotContain(remaining, p => p.PatchId == patch.PatchId);
            Assert.Single(repo.GetCommitGraph()); // updating the snapshot never commits
        }
        finally
        {
            TestWorkspaceBuilder.DeleteDirectory(tempRepo);
        }
    }

    [Fact]
    public async Task LocalBridge_RejectsRequestsFromOtherWebsites()
    {
        int port = 18000 + Random.Shared.Next(0, 2000);
        using var bridge = new LocalBridgeServer(null, port);
        if (!bridge.TryStart())
        {
            return; // Port unavailable on this machine; nothing to verify.
        }

        using var http = new HttpClient();

        var foreign = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{port}/api/google-docs/pull")
        {
            Content = new StringContent("{}")
        };
        foreign.Headers.Add("Origin", "https://attacker.example");
        Assert.Equal(System.Net.HttpStatusCode.Forbidden, (await http.SendAsync(foreign)).StatusCode);

        var extension = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{port}/api/status");
        extension.Headers.Add("Origin", "chrome-extension://abcdefghijklmnop");
        var response = await http.SendAsync(extension);
        Assert.Equal(System.Net.HttpStatusCode.ServiceUnavailable, response.StatusCode); // allowed, but no workspace is open
        Assert.Equal("chrome-extension://abcdefghijklmnop", response.Headers.GetValues("Access-Control-Allow-Origin").Single());
    }

    [Fact]
    public void WpfMainWindow_StartsWithoutWorkspace_OpensSeededWorkspace_AndRendersAllTabs()
        => RunOnStaThread(StartsWithoutWorkspace_OpensSeededWorkspace_AndRendersAllTabs);

    /// <summary>
    /// WPF needs an STA thread; the VSTest runner uses MTA threads, so run the body on a dedicated STA thread.
    /// </summary>
    private static void RunOnStaThread(Action body)
    {
        if (Thread.CurrentThread.GetApartmentState() == ApartmentState.STA)
        {
            body();
            return;
        }

        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                body();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure != null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    [Fact]
    public void DocumentEditor_ReadsBackTheSameManuscript_WithFormattingRelativeToParagraphStyle()
        => RunOnStaThread(() =>
        {
            var profile = FormattingRuleProfile.GetBuiltInProfiles()[0];
            var manuscript = new ManuscriptDocument
            {
                Blocks =
                [
                    new DocumentBlock { ParagraphId = "p-001", Role = BlockStyleRole.Title, Text = "A Title" },
                    new DocumentBlock { ParagraphId = "p-002", Role = BlockStyleRole.Heading1, Text = "Introduction" },
                    new DocumentBlock { ParagraphId = "p-003", Role = BlockStyleRole.BodyParagraph, Text = "Plain, **bold**, *italic* and ***both*** words." },
                    new DocumentBlock { ParagraphId = "p-004", Role = BlockStyleRole.Heading2, Text = "Methods" }
                ]
            };

            var page = RevisionsEditor.App.Rendering.FlowDocumentBuilder.BuildManuscriptEditorDocument(manuscript, profile, MarkupDisplayMode.AllMarkup);
            var read = RevisionsEditor.App.Rendering.ManuscriptEditorReader.Read(page, []);

            // Same ids, styles and text; the heading's "1. " number label is not part of its text, and heading
            // text (bold by style) gets no emphasis markers.
            Assert.Equal(
                manuscript.Blocks.Select(b => (b.ParagraphId, b.Role, b.Text)),
                read.Blocks.Select(b => (b.ParagraphId, b.Role, b.Text)));
            // The title is read-only on the page (it is edited in the Paper panel's form) and read back unchanged.
            Assert.IsType<System.Windows.Documents.BlockUIContainer>(page.Blocks.FirstBlock);
            Assert.Equal(["p-001"], read.LockedParagraphIds);

            // Bold added in the editor becomes Markdown emphasis, with the spaces outside the markers.
            var body = page.Blocks.OfType<System.Windows.Documents.Paragraph>().ElementAt(1);
            body.Inlines.Add(new System.Windows.Documents.Run(" new term ") { FontWeight = FontWeights.Bold });
            body.Inlines.Add(new System.Windows.Documents.Run("here."));
            Assert.EndsWith("words. **new term** here.", RevisionsEditor.App.Rendering.ManuscriptEditorReader.ReadParagraphText(body));
        });

    [Fact]
    public void DocumentEditor_GivesNewParagraphsIds_AndKeepsParagraphsWithPendingChangesUnchanged()
        => RunOnStaThread(() =>
        {
            var profile = FormattingRuleProfile.GetBuiltInProfiles()[0];
            var manuscript = new ManuscriptDocument
            {
                Blocks =
                [
                    new DocumentBlock { ParagraphId = "p-001", Role = BlockStyleRole.BodyParagraph, Text = "Editable paragraph." },
                    new DocumentBlock { ParagraphId = "p-002", Role = BlockStyleRole.BodyParagraph, Text = "Under {~~review~>revision~~}." }
                ],
                Hunks =
                [
                    new RevisionHunk { HunkId = "h1", ParagraphId = "p-002", HunkType = RevisionHunkType.Replacement, OriginalText = "review", ProposedText = "revision" }
                ]
            };

            var page = RevisionsEditor.App.Rendering.FlowDocumentBuilder.BuildManuscriptEditorDocument(manuscript, profile, MarkupDisplayMode.AllMarkup);
            Assert.IsType<System.Windows.Documents.BlockUIContainer>(page.Blocks.ElementAt(1)); // read-only

            // A paragraph typed by the user (no tag) and a split copy of an existing paragraph's tag.
            var first = (System.Windows.Documents.Paragraph)page.Blocks.FirstBlock;
            page.Blocks.InsertAfter(first, new System.Windows.Documents.Paragraph(new System.Windows.Documents.Run("Typed paragraph.")));
            page.Blocks.Add(new System.Windows.Documents.Paragraph(new System.Windows.Documents.Run("Second half.")) { Tag = first.Tag });

            var read = RevisionsEditor.App.Rendering.ManuscriptEditorReader.Read(page, ["p-001", "p-002"]);
            Assert.Equal(["p-001", "p-003", "p-002", "p-004"], read.Blocks.Select(b => b.ParagraphId));
            Assert.Equal("Under {~~review~>revision~~}.", read.Blocks[2].Text);     // pending markup kept verbatim
            Assert.Equal(["p-002"], read.LockedParagraphIds);
            Assert.All(read.Blocks, b => Assert.Equal(BlockStyleRole.BodyParagraph, b.Role));
        });

    private static void StartsWithoutWorkspace_OpensSeededWorkspace_AndRendersAllTabs()
    {
        string artifactDir = Path.Combine(Path.GetTempPath(), $"RevEditorTest_Screens_{Guid.NewGuid():N}");
        Directory.CreateDirectory(artifactDir);

        if (Application.Current == null)
        {
            var app = new RevisionsEditor.App.App();
            app.InitializeComponent();
        }

        MainWindow.UseSavedLayout = false; // never read or overwrite the user's window layout
        var window = new MainWindow();
        string freshWorkspace = Path.Combine(Path.GetTempPath(), $"RevEditorTest_UI_{Guid.NewGuid():N}");
        try
        {
            // No sample data: a new window has no workspace and no content.
            Assert.False(window.ViewModel.HasWorkspace);
            Assert.Empty(window.ViewModel.CommitGraph);
            Assert.Empty(window.ViewModel.FilteredHunks);

            TestWorkspaceBuilder.SeedReviewWorkflow(new GitRepositoryService(freshWorkspace));
            RunOnDispatcher(() => window.ViewModel.OpenWorkspaceAsync(freshWorkspace));
            Assert.True(window.ViewModel.HasWorkspace);
            Assert.True(window.ViewModel.IsOnReviewBranch);
            Assert.NotEmpty(window.ViewModel.CommitGraph);
            Assert.NotEmpty(window.ViewModel.FilteredHunks);
            Assert.NotEmpty(window.ViewModel.Sentences);
            Assert.NotEmpty(window.ViewModel.ChainBlocks);
            Assert.True(window.ViewModel.IsChainValid);

            // Typing on the Document page reaches the manuscript and the Markdown source, and the pending
            // review changes (in read-only paragraphs) are kept.
            window.SelectWorkspaceTab(0);
            var editor = (System.Windows.Controls.RichTextBox)window.FindName("ManuscriptEditor");
            int pendingBefore = window.ViewModel.PendingHunkCount;
            var editable = editor.Document.Blocks.OfType<System.Windows.Documents.Paragraph>().First();
            string editableId = ((RevisionsEditor.App.Rendering.EditorBlockTag)editable.Tag).ParagraphId!;
            string editableText = window.ViewModel.CurrentDocument.Blocks.First(b => b.ParagraphId == editableId).Text;
            editable.Inlines.Add(new System.Windows.Documents.Run(" Revised"));
            window.ViewModel.FlushPendingMarkdownSync(); // what every command does first
            Assert.Equal(editableText + " Revised", window.ViewModel.CurrentDocument.Blocks.First(b => b.ParagraphId == editableId).Text);
            Assert.Contains(editableText + " Revised", window.ViewModel.RawMarkdownText);
            Assert.Equal(pendingBefore, window.ViewModel.PendingHunkCount);

            // The title is read-only on the page (it is edited in the Paper panel's title page form); deleting it
            // puts it back, and the form changes it.
            var titleBlock = (System.Windows.Documents.BlockUIContainer)editor.Document.Blocks.FirstBlock;
            Assert.Equal(BlockStyleRole.Title, ((RevisionsEditor.App.Rendering.EditorBlockTag)titleBlock.Tag).Role);
            editor.Document.Blocks.Remove(titleBlock);
            window.ViewModel.FlushPendingMarkdownSync();
            Assert.Equal("Provenance in Collaborative Writing", window.ViewModel.CurrentDocument.Blocks[0].Text);
            window.ViewModel.Paper.PaperTitle = "Provenance in Collaborative Writing Revised";
            Assert.Equal("Provenance in Collaborative Writing Revised", window.ViewModel.CurrentDocument.Blocks[0].Text);
            Assert.Contains("# Provenance in Collaborative Writing Revised", window.ViewModel.RawMarkdownText);

            // Deleting a read-only paragraph with pending changes (e.g. via a selection spanning it) puts it back.
            int paragraphsBefore = window.ViewModel.CurrentDocument.Blocks.Count;
            var lockedBlock = editor.Document.Blocks.OfType<System.Windows.Documents.BlockUIContainer>()
                .First(b => b.Tag is RevisionsEditor.App.Rendering.EditorBlockTag { Role: not BlockStyleRole.Title, LockedBlock: not null });
            string lockedId = ((RevisionsEditor.App.Rendering.EditorBlockTag)lockedBlock.Tag).ParagraphId!;
            editor.Document.Blocks.Remove(lockedBlock);
            window.ViewModel.FlushPendingMarkdownSync();
            Assert.Equal(paragraphsBefore, window.ViewModel.CurrentDocument.Blocks.Count);
            Assert.Contains(window.ViewModel.CurrentDocument.Blocks, b => b.ParagraphId == lockedId);
            Assert.Equal(pendingBefore, window.ViewModel.PendingHunkCount);
            Assert.Contains(editor.Document.Blocks.OfType<System.Windows.Documents.BlockUIContainer>(),
                b => ((RevisionsEditor.App.Rendering.EditorBlockTag)b.Tag).ParagraphId == lockedId);

            var rootElement = (FrameworkElement)window.Content;
            rootElement.Width = 1440;
            rootElement.Height = 900;

            for (int i = 0; i < 9; i++)
            {
                if (i == 4)
                {
                    // Merge first so the Google Docs patch list is populated.
                    RunOnDispatcher(window.ViewModel.MergeReviewBranchToMainAsync);
                    Assert.Equal("main", window.ViewModel.CurrentBranch);
                    Assert.NotEmpty(window.ViewModel.GoogleDocPatches);
                }

                window.SelectWorkspaceTab(i);
                rootElement.Measure(new Size(1440, 900));
                rootElement.Arrange(new Rect(0, 0, 1440, 900));
                rootElement.UpdateLayout();

                var rtb = new RenderTargetBitmap(1440, 900, 96, 96, PixelFormats.Pbgra32);
                rtb.Render(rootElement);

                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(rtb));

                string outPath = Path.Combine(artifactDir, $"tab{i + 1}.png");
                using (var fs = File.Create(outPath))
                {
                    encoder.Save(fs);
                }
                Assert.True(new FileInfo(outPath).Length > 0);
            }
        }
        finally
        {
            window.ViewModel.Dispose();
            TestWorkspaceBuilder.DeleteDirectory(freshWorkspace);
            TestWorkspaceBuilder.DeleteDirectory(artifactDir);
        }
    }

    /// <summary>
    /// Runs an async view-model operation to completion on this STA thread, pumping the dispatcher so
    /// continuations that update bound collections run on the UI thread as they do in the app.
    /// </summary>
    private static void RunOnDispatcher(Func<Task> operation)
    {
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        var task = operation();
        var frame = new DispatcherFrame();
        task.ContinueWith(_ => frame.Continue = false, TaskScheduler.Default);
        Dispatcher.PushFrame(frame);
        task.GetAwaiter().GetResult();
    }
}

