using System.Net;
using System.Text;
using System.Text.Json;
using RevisionsEditor.Core.Diffing;
using RevisionsEditor.Core.Docx;
using RevisionsEditor.Core.Formatting;
using RevisionsEditor.Core.Git;
using RevisionsEditor.Core.Merging;
using RevisionsEditor.Core.Models;
using RevisionsEditor.Core.Scholarly;
using RevisionsEditor.Core.Serialization;

namespace RevisionsEditor.Core.Bridge;

public sealed class GoogleDocsPullPayload
{
    public string? GoogleDocId { get; set; }
    public string? Title { get; set; }
    /// <summary>Document tab the content came from; content is limited to that tab.</summary>
    public string? TabId { get; set; }
    public string? TabTitle { get; set; }
    /// <summary>Paragraphs read from one tab through the Google Docs API (preferred over DocxBase64).</summary>
    public List<GoogleDocsPulledBlock>? Blocks { get; set; }
    public string? MarkdownContent { get; set; }
    public string? DocxBase64 { get; set; }
}

public sealed class GoogleDocsPulledBlock
{
    /// <summary>A <see cref="BlockStyleRole"/> name such as "Title", "Heading1" or "BodyParagraph".</summary>
    public string? Role { get; set; }
    /// <summary>Paragraph text, with bold and italic written as Markdown emphasis like the Word importer does.</summary>
    public string? Text { get; set; }
    /// <summary>For an image (Role "Figure"): its bytes, as the extension downloaded them.</summary>
    public string? ImageBase64 { get; set; }
    public string? MimeType { get; set; }
    /// <summary>For an image the extension could not download: why.</summary>
    public string? ImageError { get; set; }
    /// <summary>For a table (Role "Table"): its cells, row by row.</summary>
    public List<List<string>>? Rows { get; set; }
}

public sealed class LocalBridgeServer : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private readonly HttpListener _listener = new();
    private readonly SemaphoreSlim _requestGate = new(1, 1);
    private CancellationTokenSource? _cts;
    private GitRepositoryService? _repoService;
    private List<GoogleDocPatch> _pendingPatches = [];
    private string? _baselineTabId;
    private string? _baselineTabTitle;

    public int Port { get; }
    public bool IsRunning { get; private set; }
    public string? ConnectedGoogleDocTitle { get; private set; }
    public DateTimeOffset? LastPingUtc { get; private set; }

    public event Action<string>? BridgeActivityLogged;
    public event Action? SnapshotPulledFromGoogleDocs;
    public event Action? PatchAcknowledgedFromGoogleDocs;

    public LocalBridgeServer(GitRepositoryService? repoService, int port = 17420)
    {
        _repoService = repoService;
        Port = port;
    }

    /// <summary>
    /// Switches the bridge to another workspace. Pending patches are recalculated on the next refresh.
    /// </summary>
    public void UpdateRepository(GitRepositoryService? repoService)
    {
        _repoService = repoService;
        _pendingPatches = [];
    }

    public IReadOnlyList<GoogleDocPatch> GetPendingPatches() => _pendingPatches;

    /// <summary>
    /// Marks patches as applied in Google Docs. Returns how many of the ids were pending.
    /// </summary>
    public int AcknowledgePatches(IEnumerable<string> patchIds)
    {
        var ids = new HashSet<string>(patchIds, StringComparer.Ordinal);
        var matches = _pendingPatches.Where(p => ids.Contains(p.PatchId) && !p.AppliedInGoogleDocs).ToList();
        foreach (var patch in matches)
        {
            patch.AppliedInGoogleDocs = true;
        }

        if (matches.Count > 0)
        {
            BridgeActivityLogged?.Invoke(matches.Count == 1
                ? $"Applied patch {matches[0].PatchId} ({matches[0].SectionHeading}) in Google Docs."
                : $"Applied {matches.Count} patches in Google Docs.");
            PatchAcknowledgedFromGoogleDocs?.Invoke();
        }
        return matches.Count;
    }

    /// <summary>
    /// Builds the manuscript sent by the extension: paragraphs of one document tab read through the Docs API
    /// when available, otherwise a Word export or Markdown. A title page written from the form (pasted in, or
    /// in a Word export uploaded to Google Docs) is left out: Google Docs keeps none of the export's markers, so it
    /// is recognised by its text.
    /// </summary>
    /// <remarks>
    /// Images become figures (saved under figures/) and tables become table blocks (their cells saved as CSV under
    /// tables/), captioned by the "Figure 2." paragraph under an image or the "Table 1." paragraph above a table.
    /// </remarks>
    private static ManuscriptDocument BuildPulledDocument(GoogleDocsPullPayload payload, GitRepositoryService repo, out string? note)
    {
        string title = payload.Title ?? "Google Doc Manuscript";
        var profile = repo.LoadFormattingProfile();
        ManuscriptDocument doc;
        TitlePageFilter? titlePage;
        ImportedObjects objects;
        lock (repo.SyncRoot)
        {
            var current = repo.HasCommits() ? repo.LoadCurrentWorkingDocument() : null;
            titlePage = current != null ? TitlePageFilter.For(ScholarlyProject.Load(repo.WorkspacePath), current) : null;
            objects = new ImportedObjects(current, repo.WorkspacePath);
        }
        var notes = new List<string>();

        if (payload.Blocks != null)
        {
            doc = new ManuscriptDocument { Title = title };
            var pulled = payload.Blocks
                .Select(b => (
                    Block: b,
                    Role: Enum.TryParse<BlockStyleRole>(b.Role, ignoreCase: true, out var parsed) ? parsed : BlockStyleRole.BodyParagraph,
                    Text: (b.Text ?? string.Empty).Trim()))
                .Where(b => b.Text.Length > 0 || b.Role is BlockStyleRole.Figure or BlockStyleRole.Table)
                .ToList();
            var leftOut = titlePage?.Apply(pulled.Select(b => (b.Role, b.Text)).ToList()) ?? [];
            var items = pulled.Where((_, i) => !leftOut.Contains(i)).ToList();
            bool IsText(int k) => k >= 0 && k < items.Count && items[k].Role is not (BlockStyleRole.Figure or BlockStyleRole.Table);

            // Captions: the paragraph under an image; the paragraph above a table (else under it).
            var captionOf = new Dictionary<int, int>(); // caption index → object index
            var captionFor = new Dictionary<int, int>(); // object index → caption index
            for (int i = 0; i < items.Count; i++)
            {
                int k = items[i].Role == BlockStyleRole.Figure
                    ? (IsText(i + 1) && ImportedObjects.TryFigureCaption(items[i + 1].Text, out _) ? i + 1 : -1)
                    : items[i].Role == BlockStyleRole.Table
                        ? (IsText(i - 1) && !captionOf.ContainsKey(i - 1) && ImportedObjects.TryTableCaption(items[i - 1].Text, out _) ? i - 1
                            : IsText(i + 1) && ImportedObjects.TryTableCaption(items[i + 1].Text, out _) ? i + 1 : -1)
                        : -1;
                if (k >= 0)
                {
                    captionOf[k] = i;
                    captionFor[i] = k;
                }
            }

            var emitted = new HashSet<int>();
            void Add(BlockStyleRole role, string text) => doc.Blocks.Add(new DocumentBlock { ParagraphId = $"p-{doc.Blocks.Count + 1:D3}", Role = role, Text = text });
            void AddObject(int i)
            {
                if (!emitted.Add(i))
                {
                    return;
                }
                var item = items[i];
                string caption = string.Empty;
                if (captionFor.TryGetValue(i, out int c))
                {
                    _ = item.Role == BlockStyleRole.Figure ? ImportedObjects.TryFigureCaption(items[c].Text, out caption) : ImportedObjects.TryTableCaption(items[c].Text, out caption);
                }
                if (item.Role == BlockStyleRole.Table)
                {
                    var rows = (item.Block.Rows ?? []).Select(r => (IReadOnlyList<string>)r.Select(cell => (cell ?? string.Empty).Trim()).ToList()).Where(r => r.Count > 0).ToList();
                    if (rows.Count > 0)
                    {
                        Add(BlockStyleRole.Table, objects.Table(rows, caption));
                    }
                    return;
                }
                if (string.IsNullOrEmpty(item.Block.ImageBase64))
                {
                    notes.Add($"An image{(caption.Length > 0 ? $" (\"{caption}\")" : string.Empty)} could not be read from Google Docs{(item.Block.ImageError is { Length: > 0 } e ? $": {e}" : string.Empty)}.");
                    if (captionFor.ContainsKey(i))
                    {
                        Add(BlockStyleRole.BodyParagraph, items[c].Text); // keep the caption text
                    }
                    return;
                }
                byte[] bytes = Convert.FromBase64String(item.Block.ImageBase64);
                Add(BlockStyleRole.Figure, objects.Figure(bytes, ImportedObjects.ExtensionFor(item.Block.MimeType, bytes), caption));
            }

            for (int i = 0; i < items.Count; i++)
            {
                if (captionOf.TryGetValue(i, out int target))
                {
                    if (target > i)
                    {
                        AddObject(target); // a table under its caption
                    }
                    continue;
                }
                if (items[i].Role is BlockStyleRole.Figure or BlockStyleRole.Table)
                {
                    AddObject(i);
                    continue;
                }
                Add(items[i].Role, items[i].Text);
            }
            var titleBlock = doc.Blocks.FirstOrDefault(b => b.Role == BlockStyleRole.Title);
            if (titleBlock != null)
            {
                doc.Title = DiffEngineService.ToGoogleDocsText(titleBlock.Text);
            }
            FormattingRuleEngine.ApplyFormattingRules(doc, profile);
        }
        else if (!string.IsNullOrWhiteSpace(payload.DocxBase64))
        {
            using var ms = new MemoryStream(Convert.FromBase64String(payload.DocxBase64));
            doc = DocxIngestionService.IngestDocx(ms, title, null, profile, titlePage, objects);
        }
        else
        {
            doc = CriticMarkupSerializer.ParseMarkdown(payload.MarkdownContent ?? string.Empty, repo.AuthorName);
            doc.Title = title;
        }

        lock (repo.SyncRoot)
        {
            objects.WriteTo(repo.WorkspacePath);
        }
        if (objects.FigureCount + objects.TableCount > 0)
        {
            notes.Insert(0, $"{objects.FigureCount} figure{(objects.FigureCount == 1 ? string.Empty : "s")} and {objects.TableCount} table{(objects.TableCount == 1 ? string.Empty : "s")} read as figure and table blocks"
                + (objects.Files.Count > 0 ? $" ({objects.Files.Count} file{(objects.Files.Count == 1 ? string.Empty : "s")} saved under figures/ and tables/)." : "."));
        }
        if (titlePage?.Summary is { } titlePageNote)
        {
            notes.Insert(0, titlePageNote);
        }

        doc.GoogleDocId = payload.GoogleDocId;
        doc.GoogleDocTabId = string.IsNullOrWhiteSpace(payload.TabId) ? null : payload.TabId;
        doc.GoogleDocTabTitle = string.IsNullOrWhiteSpace(payload.TabTitle) ? null : payload.TabTitle;
        note = notes.Count > 0 ? string.Join(" ", notes) : null;
        return doc;
    }

    public void RefreshPendingPatches()
    {
        var repo = _repoService;
        if (repo == null)
        {
            _pendingPatches = [];
            return;
        }

        ManuscriptDocument? baseline;
        ManuscriptDocument current;
        lock (repo.SyncRoot)
        {
            baseline = repo.LoadGoogleDocsBaseline();
            _baselineTabId = baseline?.GoogleDocTabId;
            _baselineTabTitle = baseline?.GoogleDocTabTitle;
            if (baseline == null)
            {
                _pendingPatches = [];
                return;
            }
            current = CriticMarkupSerializer.BakeResolvedHunks(repo.LoadCurrentWorkingDocument(), acceptRemainingPending: false);
        }

        var fresh = DiffEngineService.GenerateGoogleDocPatches(baseline, current);
        foreach (var p in fresh)
        {
            var prev = _pendingPatches.FirstOrDefault(old => old.PatchId == p.PatchId);
            if (prev != null)
            {
                p.AppliedInGoogleDocs = prev.AppliedInGoogleDocs;
            }
        }
        _pendingPatches = fresh;
    }

    public bool TryStart()
    {
        if (IsRunning)
        {
            return true;
        }

        try
        {
            _listener.Prefixes.Clear();
            _listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
            _listener.Start();
            _cts = new CancellationTokenSource();
            IsRunning = true;
            _ = AcceptLoopAsync(_cts.Token);
            return true;
        }
        catch
        {
            IsRunning = false;
            return false;
        }
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _listener.IsListening)
        {
            try
            {
                var ctx = await _listener.GetContextAsync().WaitAsync(ct);
                _ = Task.Run(() => HandleRequestAsync(ctx), ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                // Ignore transient listener errors
            }
        }
    }

    public async Task HandleRequestAsync(HttpListenerContext ctx)
    {
        var req = ctx.Request;
        var res = ctx.Response;

        // Only the companion extension (and its Google Docs content script) may call the bridge. Browsers always
        // send Origin on cross-site requests, so this stops arbitrary web pages from committing to the repository.
        string? origin = req.Headers["Origin"];
        if (!string.IsNullOrEmpty(origin))
        {
            if (!IsAllowedOrigin(origin))
            {
                res.StatusCode = 403;
                res.Close();
                return;
            }

            res.Headers["Access-Control-Allow-Origin"] = origin;
            res.Headers["Vary"] = "Origin";
            res.Headers["Access-Control-Allow-Methods"] = "GET, POST, OPTIONS";
            res.Headers["Access-Control-Allow-Headers"] = "Content-Type";

            // Chrome's private/local network access preflight for an allowed origin reaching 127.0.0.1.
            if (string.Equals(req.Headers["Access-Control-Request-Private-Network"], "true", StringComparison.OrdinalIgnoreCase))
            {
                res.Headers["Access-Control-Allow-Private-Network"] = "true";
            }
        }

        if (string.Equals(req.HttpMethod, "OPTIONS", StringComparison.OrdinalIgnoreCase))
        {
            res.StatusCode = 204;
            res.Close();
            return;
        }

        LastPingUtc = DateTimeOffset.UtcNow;
        string path = req.Url?.AbsolutePath?.TrimEnd('/') ?? string.Empty;

        // Requests mutate the repository and shared state, so handle them one at a time.
        await _requestGate.WaitAsync();
        try
        {
            var repo = _repoService;
            if (repo == null)
            {
                await WriteJsonResponseAsync(res, 503, new { error = "No workspace is open in Revisions Editor." });
                return;
            }

            if (req.HttpMethod == "GET" && path == "/api/status")
            {
                RefreshPendingPatches();
                ManuscriptDocument doc;
                string branch;
                lock (repo.SyncRoot)
                {
                    doc = repo.LoadCurrentWorkingDocument();
                    branch = repo.GetCurrentBranch();
                }
                await WriteJsonResponseAsync(res, 200, new
                {
                    status = "ok",
                    workspace = repo.WorkspacePath,
                    branch,
                    documentTitle = doc.Title,
                    pendingPatchCount = _pendingPatches.Count(p => !p.AppliedInGoogleDocs)
                });
                return;
            }

            if (req.HttpMethod == "POST" && path == "/api/google-docs/pull")
            {
                using var reader = new StreamReader(req.InputStream, Encoding.UTF8);
                string body = await reader.ReadToEndAsync();
                var payload = JsonSerializer.Deserialize<GoogleDocsPullPayload>(body, JsonOptions)
                    ?? throw new InvalidOperationException("Invalid Google Docs pull payload.");

                ConnectedGoogleDocTitle = payload.Title ?? "Google Doc Manuscript";
                var pulledDoc = BuildPulledDocument(payload, repo, out string? titlePageNote);
                if (titlePageNote != null)
                {
                    BridgeActivityLogged?.Invoke($"Google Docs: {titlePageNote}");
                }
                string tabNote = string.IsNullOrWhiteSpace(payload.TabTitle) ? string.Empty : $", tab '{payload.TabTitle}'";

                string sha;
                TextChainBlock block;
                ManuscriptMergeResult? merge = null;
                lock (repo.SyncRoot)
                {
                    var toCommit = pulledDoc;
                    if (repo.HasCommits())
                    {
                        repo.CheckoutBranch(repo.GetMainBranchName());

                        // The Google Doc and the manuscript may both have changed since the last pull (edits
                        // made here, or merged from another computer). Merge three ways against the version
                        // last seen in Google Docs so neither side's edits are lost.
                        var baseline = repo.LoadGoogleDocsBaseline();
                        if (baseline != null)
                        {
                            var current = CriticMarkupSerializer.BakeResolvedHunks(repo.LoadCurrentWorkingDocument(), acceptRemainingPending: false);
                            merge = ManuscriptMerger.Merge(baseline, current, pulledDoc, "Google Docs");
                            toCommit = merge.Document;
                            toCommit.GoogleDocId = pulledDoc.GoogleDocId;
                            toCommit.GoogleDocTabId = pulledDoc.GoogleDocTabId;
                            toCommit.GoogleDocTabTitle = pulledDoc.GoogleDocTabTitle;
                            var titleBlock = toCommit.Blocks.FirstOrDefault(b => b.Role == BlockStyleRole.Title);
                            toCommit.Title = titleBlock != null ? DiffEngineService.ToGoogleDocsText(titleBlock.Text) : pulledDoc.Title;
                        }
                    }
                    repo.SaveGoogleDocsBaseline(pulledDoc);
                    string conflictNote = merge is { Conflicts: > 0 } ? $", {merge.Conflicts} conflicting edits left as pending changes" : string.Empty;
                    (sha, block) = repo.CommitSnapshot(
                        toCommit,
                        $"Pull snapshot from Google Docs ('{ConnectedGoogleDocTitle}'{tabNote}{conflictNote})",
                        repo.AuthorName,
                        AuthorshipOriginSource.HandWrittenInGoogleDocs);
                }

                RefreshPendingPatches();
                string mergeNote = merge != null ? $" — {merge.Summary}" : string.Empty;
                BridgeActivityLogged?.Invoke($"Pulled '{ConnectedGoogleDocTitle}'{tabNote} from Google Docs -> Commit {sha[..7]} (Block #{block.BlockIndex}){mergeNote}");
                SnapshotPulledFromGoogleDocs?.Invoke();

                await WriteJsonResponseAsync(res, 200, new
                {
                    status = "committed",
                    commitSha = sha,
                    blockIndex = block.BlockIndex,
                    blockHash = block.BlockHash
                });
                return;
            }

            if (req.HttpMethod == "GET" && path == "/api/google-docs/pending-patches")
            {
                RefreshPendingPatches();
                await WriteJsonResponseAsync(res, 200, new
                {
                    patches = _pendingPatches,
                    // The document tab the patches were prepared from; they apply to that tab only.
                    tabId = _baselineTabId,
                    tabTitle = _baselineTabTitle
                });
                return;
            }

            if (req.HttpMethod == "POST" && path == "/api/google-docs/ack-patch")
            {
                using var reader = new StreamReader(req.InputStream, Encoding.UTF8);
                string body = await reader.ReadToEndAsync();
                using var doc = JsonDocument.Parse(body);

                // Accepts {"patchId": "..."} or {"patchIds": ["...", ...]}.
                var patchIds = new List<string>();
                if (doc.RootElement.TryGetProperty("patchId", out var pProp) && pProp.GetString() is { } single)
                {
                    patchIds.Add(single);
                }
                if (doc.RootElement.TryGetProperty("patchIds", out var listProp) && listProp.ValueKind == JsonValueKind.Array)
                {
                    patchIds.AddRange(listProp.EnumerateArray().Select(e => e.GetString()).OfType<string>());
                }

                int acknowledged = AcknowledgePatches(patchIds);
                await WriteJsonResponseAsync(res, 200, new { status = "acknowledged", acknowledged, patchIds });
                return;
            }

            if (req.HttpMethod == "POST" && path == "/api/google-docs/baseline")
            {
                // After merged changes were applied in Google Docs, the extension sends the tab's current content
                // so the baseline matches the document again. Nothing is committed: the text came from the repository.
                using var reader = new StreamReader(req.InputStream, Encoding.UTF8);
                string body = await reader.ReadToEndAsync();
                var payload = JsonSerializer.Deserialize<GoogleDocsPullPayload>(body, JsonOptions);
                if (payload == null || (payload.Blocks == null && string.IsNullOrWhiteSpace(payload.DocxBase64)))
                {
                    await WriteJsonResponseAsync(res, 400, new { error = "The document content is required." });
                    return;
                }

                lock (repo.SyncRoot)
                {
                    repo.SaveGoogleDocsBaseline(BuildPulledDocument(payload, repo, out _));
                }

                var previous = _pendingPatches;
                RefreshPendingPatches();
                int remaining = _pendingPatches.Count(p => !p.AppliedInGoogleDocs);
                BridgeActivityLogged?.Invoke(
                    $"Updated the Google Docs snapshot after applying changes ({previous.Count - _pendingPatches.Count} applied, {remaining} remaining).");
                PatchAcknowledgedFromGoogleDocs?.Invoke();

                await WriteJsonResponseAsync(res, 200, new { status = "updated", pendingPatchCount = remaining });
                return;
            }

            await WriteJsonResponseAsync(res, 404, new { error = "Endpoint not found" });
        }
        catch (Exception ex)
        {
            await WriteJsonResponseAsync(res, 500, new { error = ex.Message });
        }
        finally
        {
            _requestGate.Release();
        }
    }

    private static bool IsAllowedOrigin(string origin)
        => origin.StartsWith("chrome-extension://", StringComparison.OrdinalIgnoreCase)
            || string.Equals(origin, "https://docs.google.com", StringComparison.OrdinalIgnoreCase);

    private static async Task WriteJsonResponseAsync(HttpListenerResponse res, int statusCode, object payload)
    {
        res.StatusCode = statusCode;
        res.ContentType = "application/json; charset=utf-8";
        byte[] bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload, JsonOptions));
        res.ContentLength64 = bytes.Length;
        await res.OutputStream.WriteAsync(bytes);
        res.Close();
    }

    public void Dispose()
    {
        try
        {
            _cts?.Cancel();
            if (_listener.IsListening)
            {
                _listener.Stop();
            }
            _listener.Close();
        }
        catch
        {
            // Ignore shutdown exceptions
        }
    }
}
