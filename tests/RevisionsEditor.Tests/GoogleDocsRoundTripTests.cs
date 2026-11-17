using System.IO;
using System.Net.Http;
using RevisionsEditor.Core.Bridge;
using RevisionsEditor.Core.Diffing;
using RevisionsEditor.Core.Git;
using RevisionsEditor.Core.Models;
using Xunit;

namespace RevisionsEditor.Tests;

/// <summary>
/// Round trips between the manuscript and a simulated Google Doc, through the bridge as the extension uses it:
/// pull the tab, apply the pending patches the way docs-apply.js does (plain text, emphasis left as it was),
/// then send the tab back as the new baseline. After every step both sides must agree.
/// </summary>
public sealed partial class RevisionsEditorTests
{
    /// <summary>A Google Docs tab as the extension reads it: role and text, with bold/italic as Markdown emphasis.</summary>
    private sealed class FakeGoogleDoc
    {
        public List<(string Role, string Text)> Paragraphs { get; } = [];

        private static string Key(string text) => DiffEngineService.GoogleDocsMatchKey(DiffEngineService.ToGoogleDocsText(text));

        public IReadOnlyList<string> Plain => Paragraphs.Select(p => DiffEngineService.ToGoogleDocsText(p.Text)).ToList();

        public int IndexOf(string plain) => Paragraphs.FindIndex(p => Key(p.Text) == Key(plain));

        public void Set(string oldPlain, string newText)
        {
            int i = IndexOf(oldPlain);
            Assert.True(i >= 0, $"No paragraph '{oldPlain}' in the Google Doc.");
            Paragraphs[i] = (Paragraphs[i].Role, newText);
        }

        /// <summary>Applies patches as docs-apply.js does: the merged plain text replaces the paragraph's words.</summary>
        public void Apply(IEnumerable<GoogleDocPatch> patches)
        {
            // Like planRequests, every paragraph and anchor is found in the document as it was read, before any edit.
            var slots = Paragraphs.Select(p => new List<(string Role, string Text)> { p }).ToList();
            var before = new List<(string Role, string Text)>(); // inserted at the very start
            foreach (var patch in patches)
            {
                switch (patch.Kind)
                {
                    case GoogleDocPatchKind.Modify:
                        int m = IndexOf(patch.BeforeText);
                        Assert.True(m >= 0, $"Patch {patch.PatchId}: '{patch.BeforeText}' not found.");
                        slots[m][0] = (Paragraphs[m].Role, KeepEmphasis(Paragraphs[m].Text, patch.AfterText));
                        break;
                    case GoogleDocPatchKind.Delete:
                        int d = IndexOf(patch.BeforeText);
                        Assert.True(d >= 0, $"Patch {patch.PatchId}: '{patch.BeforeText}' not found.");
                        slots[d][0] = (string.Empty, string.Empty);
                        break;
                    case GoogleDocPatchKind.Insert:
                        int a = patch.AnchorText.Length == 0 ? -1 : IndexOf(patch.AnchorText);
                        Assert.True(patch.AnchorText.Length == 0 || a >= 0, $"Patch {patch.PatchId}: anchor '{patch.AnchorText}' not found.");
                        (a < 0 ? before : slots[a]).Add((patch.Role.ToString(), patch.AfterText));
                        break;
                }
            }
            var result = before.Concat(slots.SelectMany(s => s)).Where(p => p.Role.Length > 0).ToList();
            Paragraphs.Clear();
            Paragraphs.AddRange(result);
        }

        /// <summary>Word-level edits keep the formatting of the words they do not touch, as in Google Docs.</summary>
        private static string KeepEmphasis(string current, string afterPlain)
        {
            var emphasized = System.Text.RegularExpressions.Regex.Matches(current, @"(\*{1,3})([^*]+?)\1")
                .Select(m => (Marker: m.Groups[1].Value, Words: m.Groups[2].Value)).ToList();
            string result = afterPlain;
            foreach (var (marker, words) in emphasized)
            {
                int at = result.IndexOf(words, StringComparison.Ordinal);
                if (at >= 0) result = result[..at] + marker + words + marker + result[(at + words.Length)..];
            }
            return result;
        }

        public string? TabId { get; set; }

        public object Payload() => new
        {
            googleDocId = "DOC1",
            title = "Paper",
            tabId = TabId,
            blocks = Paragraphs.Select(p => new { role = p.Role, text = p.Text }).ToArray()
        };
    }

    private sealed class BridgeHarness(LocalBridgeServer bridge, GitRepositoryService repo, int port) : IDisposable
    {
        private readonly HttpClient _http = new();
        public FakeGoogleDoc Docs { get; } = new();
        public GitRepositoryService Repo => repo;
        public LocalBridgeServer Bridge => bridge;
        public int Port => port;

        private async Task PostAsync(string path, object body)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{port}{path}")
            {
                Content = new StringContent(System.Text.Json.JsonSerializer.Serialize(body), System.Text.Encoding.UTF8, "application/json")
            };
            request.Headers.Add("Origin", "chrome-extension://abcdefghijklmnop");
            var response = await _http.SendAsync(request);
            Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        }

        public Task PullAsync() => PostAsync("/api/google-docs/pull", Docs.Payload());

        public List<GoogleDocPatch> Pending()
        {
            bridge.RefreshPendingPatches();
            return bridge.GetPendingPatches().Where(p => !p.AppliedInGoogleDocs).ToList();
        }

        /// <summary>"Apply all" in the panel: patches applied, acknowledged, then the tab sent as the new baseline.</summary>
        public async Task PushAsync()
        {
            var patches = Pending();
            Docs.Apply(patches);
            await PostAsync("/api/google-docs/ack-patch", new { patchIds = patches.Select(p => p.PatchId).ToArray() });
            await PostAsync("/api/google-docs/baseline", Docs.Payload());
        }

        /// <summary>"Copy merged" + "Mark applied": the text is pasted by hand, no baseline is sent.</summary>
        public async Task PushByHandAsync()
        {
            var patches = Pending();
            Docs.Apply(patches);
            await PostAsync("/api/google-docs/ack-patch", new { patchIds = patches.Select(p => p.PatchId).ToArray() });
        }

        public ManuscriptDocument Manuscript => repo.LoadCurrentWorkingDocument();

        public IReadOnlyList<string> ManuscriptPlain => Manuscript.Blocks.Select(b => DiffEngineService.ToGoogleDocsText(b.Text)).ToList();

        public void EditHere(Action<ManuscriptDocument> edit)
        {
            var doc = Manuscript;
            edit(doc);
            repo.CommitSnapshot(doc, "Edit in app");
        }

        public void AssertInSync(string step)
        {
            var here = ManuscriptPlain;
            var there = Docs.Plain;
            Assert.True(here.Select(DiffEngineService.GoogleDocsMatchKey).SequenceEqual(there.Select(DiffEngineService.GoogleDocsMatchKey)),
                $"{step}: manuscript and Google Doc differ.\n  Manuscript:\n    {string.Join("\n    ", Manuscript.Blocks.Select(b => b.Text))}\n  Google Doc:\n    {string.Join("\n    ", Docs.Paragraphs.Select(p => p.Text))}");
            var pending = Pending();
            Assert.True(pending.Count == 0, $"{step}: {pending.Count} patches still pending: {string.Join(" | ", pending.Select(p => $"{p.Kind} '{p.BeforeText}' -> '{p.AfterText}'"))}");
            Assert.True(Manuscript.Hunks.Count == 0, $"{step}: {Manuscript.Hunks.Count} pending changes: {string.Join(" | ", Manuscript.Hunks.Select(h => $"{h.HunkType} '{h.OriginalText}' -> '{h.ProposedText}' {h.CommentText}"))}");
        }

        public void Dispose() => _http.Dispose();
    }

    private static async Task WithBridgeAsync(string name, Func<BridgeHarness, Task> scenario)
    {
        int port = 18000 + Random.Shared.Next(4000, 6000);
        string tempRepo = Path.Combine(Path.GetTempPath(), $"RevEditorTest_{name}_{Guid.NewGuid():N}");
        var repo = new GitRepositoryService(tempRepo);
        repo.EnsureInitialized();
        try
        {
            using var bridge = new LocalBridgeServer(repo, port);
            if (!bridge.TryStart())
            {
                return; // Port unavailable on this machine; nothing to verify.
            }
            using var harness = new BridgeHarness(bridge, repo, port);
            await scenario(harness);
        }
        finally
        {
            TestWorkspaceBuilder.DeleteDirectory(tempRepo);
        }
    }

    private static void SeedDoc(FakeGoogleDoc docs)
    {
        docs.Paragraphs.AddRange([
            ("Title", "Paper"),
            ("Heading1", "Introduction"),
            ("BodyParagraph", "Alpha beta gamma delta."),
            ("BodyParagraph", "The second paragraph is here."),
            ("Heading1", "Methods"),
            ("BodyParagraph", "Methods text with an *italic* word and “curly quotes”."),
        ]);
    }

    [Fact]
    public Task GoogleDocsRoundTrip_EditsOnEitherSide_ConvergeAfterEveryPullAndPush() => WithBridgeAsync("RoundTrip", async h =>
    {
        SeedDoc(h.Docs);
        await h.PullAsync();
        h.AssertInSync("first pull");

        await h.PullAsync();
        h.AssertInSync("pull again without changes");

        // Edited in Google Docs only.
        h.Docs.Set("The second paragraph is here.", "The second paragraph is here, edited in Google Docs.");
        h.Docs.Paragraphs.Add(("BodyParagraph", "Added at the end in Google Docs."));
        await h.PullAsync();
        h.AssertInSync("pull of Google Docs edits");

        // Edited in the app only, then sent to Google Docs.
        h.EditHere(doc =>
        {
            doc.Blocks[2].Text = "Alpha beta gamma delta, edited in the app.";
            doc.Blocks.Insert(3, new DocumentBlock { ParagraphId = "p-new", Text = "A new paragraph with *emphasis* written in the app." });
        });
        Assert.Equal(2, h.Pending().Count);
        await h.PushAsync();
        h.AssertInSync("push of app edits");
        await h.PullAsync();
        h.AssertInSync("pull after push");
        Assert.Contains("A new paragraph with *emphasis* written in the app.", h.Manuscript.Blocks.Select(b => b.Text));

        // Both sides edit the same paragraph, different words.
        h.EditHere(doc => doc.Blocks.First(b => b.Text.StartsWith("Methods text")).Text = "Methods section with an *italic* word and “curly quotes”.");
        h.Docs.Set("Methods text with an italic word and “curly quotes”.", "Methods text with an *italic* term and “curly quotes”.");
        await h.PullAsync();
        Assert.Contains("Methods section with an *italic* term and \"curly quotes\".", h.Manuscript.Blocks.Select(b => DiffEngineService.GoogleDocsMatchKey(b.Text)));
        Assert.Empty(h.Manuscript.Hunks);
        await h.PushAsync();
        h.AssertInSync("push of combined edits");
        await h.PullAsync();
        h.AssertInSync("pull after combined push");
    });

    [Fact]
    public Task GoogleDocsRoundTrip_ChangesPastedByHand_DoNotConflictOnTheNextPull() => WithBridgeAsync("ByHand", async h =>
    {
        SeedDoc(h.Docs);
        await h.PullAsync();

        h.EditHere(doc => doc.Blocks[3].Text = "The second paragraph is here, edited in the app.");
        await h.PushByHandAsync();

        // Later someone edits another paragraph in Google Docs.
        h.Docs.Set("Alpha beta gamma delta.", "Alpha beta gamma delta epsilon.");
        await h.PullAsync();
        h.AssertInSync("pull after changes applied by hand");
    });

    [Fact]
    public Task GoogleDocsRoundTrip_TypographyChangedByGoogleDocs_IsNotAnEdit() => WithBridgeAsync("Typography", async h =>
    {
        SeedDoc(h.Docs);
        await h.PullAsync();

        // Edited here with straight quotes and a hyphen; Google Docs shows curly quotes and an en dash after a
        // user retypes nearby, and the next pull must not see that as a conflicting edit.
        h.EditHere(doc => doc.Blocks[2].Text = "Alpha beta \"gamma\" delta, pages 10-12.");
        await h.PushAsync();
        h.Docs.Set("Alpha beta \"gamma\" delta, pages 10-12.", "Alpha beta “gamma” delta, pages 10–12.");
        h.Docs.Set("The second paragraph is here.", "The second paragraph is here, edited.");
        await h.PullAsync();
        Assert.Empty(h.Manuscript.Hunks);
        Assert.Contains("The second paragraph is here, edited.", h.ManuscriptPlain);
    });

    [Fact]
    public Task GoogleDocsRoundTrip_AConflict_IsReportedOnce_AndLaterGoogleDocsEditsStillArrive() => WithBridgeAsync("Conflict", async h =>
    {
        SeedDoc(h.Docs);
        await h.PullAsync();

        h.EditHere(doc => doc.Blocks[2].Text = "Alpha beta gamma omega.");
        h.Docs.Set("Alpha beta gamma delta.", "Alpha beta gamma sigma.");
        await h.PullAsync();
        int conflicts = h.Manuscript.Hunks.Count;
        Assert.Equal(1, conflicts);

        // Pulling again without changes adds nothing.
        await h.PullAsync();
        Assert.Equal(conflicts, h.Manuscript.Hunks.Count);

        // A later Google Docs edit elsewhere in the same paragraph still arrives.
        h.Docs.Set("Alpha beta gamma sigma.", "Alpha BETA gamma sigma.");
        await h.PullAsync();
        var paragraph = h.Manuscript.Blocks[2];
        Assert.Contains("BETA", DiffEngineService.ToGoogleDocsText(paragraph.Text));
        // The pending change is still there to accept or reject, and still matches its hunk.
        Assert.Contains("{~~omega~>sigma~~}", paragraph.Text);
        Assert.Contains(h.Manuscript.Hunks, x => x.ParagraphId == paragraph.ParagraphId && x.OriginalText == "omega" && x.ProposedText == "sigma");
    });

    [Fact]
    public Task GoogleDocsRoundTrip_UncommittedEditsHere_AreMergedNotDiscarded() => WithBridgeAsync("Uncommitted", async h =>
    {
        SeedDoc(h.Docs);
        await h.PullAsync();

        // Typed in the app but not committed when the pull arrives.
        var typed = h.Manuscript;
        typed.Blocks[2].Text = "Alpha beta gamma delta, typed but not committed.";
        h.Bridge.UncommittedEditsProvider = () => typed;

        h.Docs.Set("The second paragraph is here.", "The second paragraph is here, edited in Google Docs.");
        await h.PullAsync();
        h.Bridge.UncommittedEditsProvider = null;

        Assert.Contains("Alpha beta gamma delta, typed but not committed.", h.ManuscriptPlain);
        Assert.Contains("The second paragraph is here, edited in Google Docs.", h.ManuscriptPlain);
        await h.PushAsync();
        h.AssertInSync("push after merging uncommitted edits");
    });

    [Fact]
    public Task GoogleDocsRoundTrip_FormattedParagraphPastedPlain_IsNotDuplicatedOrConflicted() => WithBridgeAsync("Pasted", async h =>
    {
        SeedDoc(h.Docs);
        await h.PullAsync();

        h.EditHere(doc =>
        {
            doc.Blocks[2].Text = "Alpha *beta* gamma \"delta\" - now edited.";
            doc.Blocks.Insert(4, new DocumentBlock { ParagraphId = "p-new", Text = "A **new** paragraph with *emphasis*." });
        });
        // Pasted into Google Docs by hand: plain text, curly quotes, an en dash; no baseline is sent.
        h.Docs.Set("Alpha beta gamma delta.", "Alpha beta gamma “delta” – now edited.");
        h.Docs.Paragraphs.Insert(4, ("BodyParagraph", "A new paragraph with emphasis."));
        h.Docs.Set("Methods", "Methods and data");
        await h.PullAsync();

        Assert.Empty(h.Manuscript.Hunks);
        Assert.Single(h.Manuscript.Blocks, b => b.Text.Contains("new", StringComparison.Ordinal) && b.Text.Contains("paragraph", StringComparison.Ordinal));
        Assert.Contains("A **new** paragraph with *emphasis*.", h.Manuscript.Blocks.Select(b => b.Text)); // formatting kept
        Assert.Contains("Alpha *beta* gamma", h.Manuscript.Blocks[2].Text);
        Assert.Contains("Methods and data", h.ManuscriptPlain);
    });

    [Fact]
    public Task GoogleDocsPull_FirstPullIntoAnExistingManuscript_MergesWithEditsMadeSince() => WithBridgeAsync("FirstPull", async h =>
    {
        // The manuscript was written here; a copy went to Google Docs; both were edited afterwards.
        h.Repo.CommitSnapshot(new ManuscriptDocument
        {
            Title = "Paper",
            Blocks = SeedBlocks().Select((p, i) => new DocumentBlock { ParagraphId = $"p-{i + 1:D3}", Role = Enum.Parse<BlockStyleRole>(p.Role), Text = p.Text }).ToList()
        }, "Written here");
        SeedDoc(h.Docs);
        h.EditHere(doc => doc.Blocks[2].Text = "Alpha beta gamma delta, edited here after sending.");
        h.Docs.Set("The second paragraph is here.", "The second paragraph is here, edited in Google Docs.");

        await h.PullAsync();

        Assert.Contains("Alpha beta gamma delta, edited here after sending.", h.ManuscriptPlain);
        Assert.Contains("The second paragraph is here, edited in Google Docs.", h.ManuscriptPlain);
        Assert.Empty(h.Manuscript.Hunks);
    });

    [Fact]
    public Task GoogleDocsPull_OfAnotherTab_IsNotMergedAgainstTheFirstTabsSnapshot() => WithBridgeAsync("OtherTab", async h =>
    {
        h.Docs.TabId = "t.first";
        SeedDoc(h.Docs);
        await h.PullAsync();

        // Another tab holds a later draft of the same paper, which also drops a paragraph.
        h.Docs.TabId = "t.second";
        h.Docs.Set("Alpha beta gamma delta.", "Alpha beta gamma delta, as in the second tab.");
        h.Docs.Paragraphs.RemoveAt(3);
        await h.PullAsync();

        h.AssertInSync("pull of another tab");
        Assert.Equal("t.second", h.Repo.LoadGoogleDocsBaseline()!.GoogleDocTabId);
    });

    private static List<(string Role, string Text)> SeedBlocks()
    {
        var docs = new FakeGoogleDoc();
        SeedDoc(docs);
        return docs.Paragraphs;
    }
}
