using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using RevisionsEditor.Core.Formatting;
using RevisionsEditor.Core.Merging;
using RevisionsEditor.Core.Models;
using RevisionsEditor.Core.Provenance;
using RevisionsEditor.Core.Serialization;
using RevisionsEditor.Core.Storage;
using LibGit2Sharp; // extension methods (Lookup<T>, CreateBranch); its types are written Git2.X
using Git2 = LibGit2Sharp;

namespace RevisionsEditor.Core.Git;

/// <summary>A merge commit and its signed block; <see cref="Merge"/> is set when both sides had changes.</summary>
public sealed record MergeOutcome(string CommitSha, TextChainBlock SignedBlock, ManuscriptMergeResult? Merge);

/// <summary>What a sync with the remote repository did.</summary>
public sealed class SyncReport
{
    public List<string> Lines { get; } = [];
    public int BranchesUpdated { get; set; }
    public int BranchesPushed { get; set; }
    public int BranchesMerged { get; set; }
    public int Conflicts { get; set; }

    public string Summary
    {
        get
        {
            if (BranchesUpdated + BranchesPushed + BranchesMerged == 0)
            {
                return "Already up to date with GitHub.";
            }
            var parts = new List<string>();
            if (BranchesUpdated > 0) parts.Add($"{BranchesUpdated} branch{(BranchesUpdated == 1 ? "" : "es")} updated from GitHub");
            if (BranchesMerged > 0) parts.Add($"{BranchesMerged} merged");
            if (BranchesPushed > 0) parts.Add($"{BranchesPushed} sent to GitHub");
            string text = string.Join(", ", parts) + ".";
            return Conflicts > 0 ? $"{text} {Conflicts} conflicting edit{(Conflicts == 1 ? "" : "s")} added as pending changes." : text;
        }
    }
}

public sealed class GitRepositoryService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private static readonly UTF8Encoding Utf8NoBom = new(false);
    private const string AuthorKeyIgnoreEntry = ".revisions/author-key.json";
    private const string LegacyManuscriptPath = "manuscript.md";
    private const string LegacyStatePath = ".revisions/document-state.json";

    /// <summary>Signed provenance blocks are stored as Git notes on their commits under this ref.</summary>
    public const string ProvenanceNotesRef = "refs/notes/revisions-provenance";
    public const string RemoteName = "origin";
    private const string RemoteNotesRef = "refs/notes/remotes/origin/revisions-provenance";

    // Commit snapshots are immutable, so documents loaded by full SHA can be cached for the lifetime of the service.
    private readonly ConcurrentDictionary<string, ManuscriptDocument> _documentAtCommitCache = new(StringComparer.OrdinalIgnoreCase);
    private ProvenanceLedger? _ledgerCache;
    private bool _initialized;
    private string? _authorName;
    private string? _authorEmail;

    /// <summary>
    /// Lock for multi-step repository operations (checkout + write + commit). The UI and the Google Docs
    /// bridge both take it so their Git sequences never interleave.
    /// </summary>
    public object SyncRoot { get; } = new();

    public string WorkspacePath { get; }
    public string RevisionsDir => Path.Combine(WorkspacePath, ".revisions");
    public string SectionsDir => Path.Combine(WorkspacePath, ManuscriptSectionStore.SectionFolder);
    public string FormattingRulesFilePath => Path.Combine(RevisionsDir, "formatting-rules.json");
    public string GoogleDocsBaselineFilePath => Path.Combine(RevisionsDir, "gdocs-baseline.json");

    public GitRepositoryService(string workspacePath)
    {
        WorkspacePath = Path.GetFullPath(workspacePath);
    }

    /// <summary>
    /// The local author's name, taken from the Git configuration (falls back to the Windows user name).
    /// </summary>
    public string AuthorName
    {
        get
        {
            EnsureInitialized();
            return _authorName ?? Environment.UserName;
        }
    }

    public void EnsureInitialized()
    {
        if (_initialized)
        {
            return;
        }

        Directory.CreateDirectory(WorkspacePath);
        Directory.CreateDirectory(RevisionsDir);

        if (!Directory.Exists(Path.Combine(WorkspacePath, ".git")))
        {
            Git2.Repository.Init(WorkspacePath);
            WithRepo(r => r.Refs.UpdateTarget("HEAD", "refs/heads/main"));
        }

        // Only fill in values Git does not have, so commits never fail on a fresh machine.
        var (name, email) = ReadGitIdentity();
        if (name == null)
        {
            name = Environment.UserName;
            WithRepo(r => r.Config.Set("user.name", name));
        }
        if (email == null)
        {
            email = $"{Environment.UserName}@{Environment.MachineName}";
            WithRepo(r => r.Config.Set("user.email", email));
        }
        _authorName = name;
        _authorEmail = email;

        EnsureAuthorKeyIsIgnored();
        _initialized = true;
    }

    private (string? Name, string? Email) ReadGitIdentity() => WithRepo(r =>
    {
        static string? Value(Git2.ConfigurationEntry<string>? entry) => entry?.Value is { } v && v.Trim().Length > 0 ? v.Trim() : null;
        return (Value(r.Config.Get<string>("user.name")), Value(r.Config.Get<string>("user.email")));
    });

    private void EnsureAuthorKeyIsIgnored()
    {
        // The signing key must never be committed, including in repositories created outside this app.
        string gitignorePath = Path.Combine(WorkspacePath, ".gitignore");
        string existing = File.Exists(gitignorePath) ? File.ReadAllText(gitignorePath) : string.Empty;
        bool alreadyIgnored = existing
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Any(line => string.Equals(line.Trim(), AuthorKeyIgnoreEntry, StringComparison.Ordinal));

        if (!alreadyIgnored)
        {
            string prefix = existing.Length > 0 && !existing.EndsWith('\n') ? "\n" : string.Empty;
            File.AppendAllText(gitignorePath, $"{prefix}{AuthorKeyIgnoreEntry}\n*.tmp\n", Utf8NoBom);
        }
    }

    public bool HasCommits()
    {
        EnsureInitialized();
        return WithRepo(r => r.Head.Tip != null);
    }

    public string GetCurrentBranch()
    {
        EnsureInitialized();
        // "HEAD" when detached, as git rev-parse --abbrev-ref HEAD says; the branch name even before its first commit.
        return WithRepo(r => r.Info.IsHeadDetached ? "HEAD" : r.Head.FriendlyName is { Length: > 0 } name ? name : "main");
    }

    /// <summary>
    /// Lists local branches and the checked-out branch with a single Git process.
    /// The current branch is "HEAD" when detached, matching <c>git rev-parse --abbrev-ref HEAD</c>.
    /// </summary>
    public (List<string> Branches, string CurrentBranch) GetBranchesAndCurrent()
    {
        EnsureInitialized();
        var branches = LocalBranches();
        string current = GetCurrentBranch();
        if (current != "HEAD" && !branches.Contains(current, StringComparer.Ordinal))
        {
            branches.Add(current); // unborn branch (no commits yet)
        }
        return (branches, current);
    }

    public List<string> GetBranches()
    {
        EnsureInitialized();
        var branches = LocalBranches();
        return branches.Count > 0 ? branches : [GetCurrentBranch()];
    }

    private List<string> LocalBranches() => WithRepo(r => r.Branches
        .Where(b => !b.IsRemote)
        .Select(b => b.FriendlyName)
        .OrderBy(n => n, StringComparer.Ordinal)
        .ToList());

    /// <summary>
    /// Returns the repository's trunk branch: "main", or "master" for repositories that use it.
    /// </summary>
    public string GetMainBranchName() => ResolveMainBranchName(GetBranches());

    public static string ResolveMainBranchName(IReadOnlyCollection<string> branches)
        => !branches.Contains("main", StringComparer.Ordinal) && branches.Contains("master", StringComparer.Ordinal)
            ? "master"
            : "main";

    public void SaveFormattingProfile(FormattingRuleProfile profile)
    {
        Directory.CreateDirectory(RevisionsDir);
        File.WriteAllText(FormattingRulesFilePath, JsonSerializer.Serialize(profile, JsonOptions));
    }

    public FormattingRuleProfile LoadFormattingProfile()
    {
        if (File.Exists(FormattingRulesFilePath))
        {
            var loaded = JsonSerializer.Deserialize<FormattingRuleProfile>(File.ReadAllText(FormattingRulesFilePath), JsonOptions);
            if (loaded != null)
            {
                return loaded;
            }
        }
        return FormattingRuleProfile.GetBuiltInProfiles()[0];
    }

    public void SaveGoogleDocsBaseline(ManuscriptDocument baselineDoc)
    {
        Directory.CreateDirectory(RevisionsDir);
        File.WriteAllText(GoogleDocsBaselineFilePath, JsonSerializer.Serialize(baselineDoc, JsonOptions));
    }

    public ManuscriptDocument? LoadGoogleDocsBaseline()
    {
        if (File.Exists(GoogleDocsBaselineFilePath))
        {
            return JsonSerializer.Deserialize<ManuscriptDocument>(File.ReadAllText(GoogleDocsBaselineFilePath), JsonOptions);
        }
        return null;
    }

    // ---- Committing ------------------------------------------------------------------------------------

    /// <summary>
    /// Saves the manuscript as one Markdown file per section (plus pending-change details), commits it, and
    /// signs a provenance block for the commit, stored with it as a Git note.
    /// </summary>
    public (string CommitSha, TextChainBlock SignedBlock) CommitSnapshot(
        ManuscriptDocument document,
        string commitMessage,
        string? authorName = null,
        AuthorshipOriginSource originSource = AuthorshipOriginSource.HandWrittenInWpf)
    {
        EnsureInitialized();
        authorName ??= AuthorName;

        var profile = LoadFormattingProfile();
        FormattingRuleEngine.ApplyFormattingRules(document, profile);
        StageDocument(document);

        // Imported reviewer edits are attributed to the reviewer; the committer stays the local user.
        var committer = Identity();
        var author = string.Equals(authorName, AuthorName, StringComparison.Ordinal) ? committer : new Git2.Signature(authorName, ReviewerEmail(authorName), committer.When);
        var (sha, parents) = WithRepo(r =>
        {
            var commit = r.Commit(commitMessage, author, committer, new Git2.CommitOptions { AllowEmptyCommit = true });
            return (commit.Sha, commit.Parents.Select(p => p.Sha).ToList());
        });
        RememberDocumentAtCommit(sha, document);
        var block = SignCommit(document, sha, parents, commitMessage, authorName, originSource);
        return (sha, block);
    }

    /// <summary>Writes the section files and stages them (and the removal of the earlier single-file layout).</summary>
    private void StageDocument(ManuscriptDocument document)
    {
        bool legacyRemoved = WriteWorkingFiles(document);
        var paths = new List<string> { ManuscriptSectionStore.SectionFolder, ManuscriptSectionStore.MetadataPath, ".gitignore" };
        paths.AddRange(ExistingProjectPaths());
        WithRepo(r =>
        {
            // Added, changed and deleted files under these paths, as git add -A does (ignored files stay out).
            Git2.Commands.Stage(r, paths);
            if (legacyRemoved)
            {
                foreach (string legacy in new[] { LegacyManuscriptPath, LegacyStatePath })
                {
                    if (r.Index[legacy] != null) r.Index.Remove(legacy);
                }
                r.Index.Write();
            }
        });
    }

    /// <summary>Writes the manuscript's files; returns true when files of the earlier layout were removed.</summary>
    private bool WriteWorkingFiles(ManuscriptDocument document)
    {
        var files = ManuscriptSectionStore.Write(document);
        Directory.CreateDirectory(SectionsDir);

        foreach (string existing in Directory.EnumerateFiles(SectionsDir, "*.md"))
        {
            string relative = $"{ManuscriptSectionStore.SectionFolder}/{Path.GetFileName(existing)}";
            if (!files.ContainsKey(relative))
            {
                File.Delete(existing);
            }
        }

        foreach (var (relative, content) in files)
        {
            string path = Path.Combine(WorkspacePath, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if (!File.Exists(path) || File.ReadAllText(path) != content)
            {
                File.WriteAllText(path, content, Utf8NoBom);
            }
        }

        bool legacyRemoved = false;
        foreach (string legacy in new[] { LegacyManuscriptPath, LegacyStatePath })
        {
            string path = Path.Combine(WorkspacePath, legacy.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(path))
            {
                File.Delete(path);
                legacyRemoved = true;
            }
        }
        return legacyRemoved;
    }

    public string GetHeadCommitSha()
    {
        return WithRepo(r => r.Head.Tip?.Sha ?? string.Empty);
    }

    private (string Sha, List<string> Parents) GetCommitAndParents(string rev)
    {
        return WithRepo(r =>
        {
            var commit = r.Lookup<Git2.Commit>(rev) ?? throw new InvalidOperationException($"No commit {rev}.");
            return (commit.Sha, commit.Parents.Select(p => p.Sha).ToList());
        });
    }

    private string? ResolveCommit(string rev)
    {
        return WithRepo(r =>
        {
            try
            {
                return r.Lookup(rev)?.Peel<Git2.Commit>()?.Sha;
            }
            catch (Git2.LibGit2SharpException)
            {
                return null; // not a valid name
            }
        });
    }

    private string? GetMergeBase(string a, string b)
    {
        return WithRepo(r => r.Lookup<Git2.Commit>(a) is { } ca && r.Lookup<Git2.Commit>(b) is { } cb
            ? r.ObjectDatabase.FindMergeBase(ca, cb)?.Sha
            : null);
    }

    public void CreateAndCheckoutBranch(string branchName, string? baseRef = null)
    {
        EnsureInitialized();
        string targetBase = string.IsNullOrWhiteSpace(baseRef) ? "HEAD" : baseRef;
        WithRepo(r =>
        {
            var branch = r.Branches[branchName];
            if (branch == null || branch.IsRemote)
            {
                var start = r.Lookup(targetBase)?.Peel<Git2.Commit>() ?? throw new InvalidOperationException($"No commit {targetBase} to branch from.");
                branch = r.CreateBranch(branchName, start);
            }
            Git2.Commands.Checkout(r, branch);
        });
    }

    public void CheckoutBranch(string branchName)
    {
        EnsureInitialized();
        Checkout(branchName);
    }

    /// <summary>Checks a branch out (or a commit, detached), as git checkout does: changes in the working tree that do not conflict are kept.</summary>
    private void Checkout(string branchOrCommit) => WithRepo(r =>
    {
        if (r.Branches[branchOrCommit] is { IsRemote: false } branch)
        {
            if (!branch.IsCurrentRepositoryHead) Git2.Commands.Checkout(r, branch);
        }
        else
        {
            var commit = r.Lookup(branchOrCommit)?.Peel<Git2.Commit>() ?? throw new InvalidOperationException($"No branch or commit {branchOrCommit}.");
            Git2.Commands.Checkout(r, commit);
        }
    });

    /// <summary>
    /// Merges a review branch into main with a true two-parent merge commit. If main has changed since the
    /// review branched off, the reviewed manuscript is merged three ways with main (the version the review
    /// started from is the base), so edits made on main in the meantime are kept; edits that conflict become
    /// pending changes.
    /// </summary>
    public MergeOutcome MergeBranchIntoMain(
        string reviewBranchName,
        ManuscriptDocument resolvedDocument,
        string mergeCommitMessage,
        string? authorName = null)
    {
        EnsureInitialized();
        authorName ??= AuthorName;
        string mainBranch = GetMainBranchName();

        string reviewTipSha = ResolveCommit(reviewBranchName) ?? string.Empty;
        Checkout(mainBranch);
        string mainTipSha = GetHeadCommitSha();

        var final = resolvedDocument;
        ManuscriptMergeResult? merge = null;
        string? baseSha = reviewTipSha.Length > 0 ? GetMergeBase(mainTipSha, reviewTipSha) : null;
        if (baseSha != null && !string.Equals(baseSha, mainTipSha, StringComparison.OrdinalIgnoreCase))
        {
            merge = ManuscriptMerger.Merge(LoadDocumentAtCommit(baseSha), LoadDocumentAtCommit(mainTipSha), resolvedDocument, reviewBranchName);
            final = merge.Document;
            if (merge.Conflicts > 0)
            {
                mergeCommitMessage += $" ({merge.Conflicts} conflicting edits left as pending changes)";
            }
        }

        var parents = new List<string> { mainTipSha };
        if (reviewTipSha.Length > 0 && !string.Equals(mainTipSha, reviewTipSha, StringComparison.OrdinalIgnoreCase))
        {
            parents.Add(reviewTipSha);
        }
        string mergeSha = CommitTree(mainBranch, final, parents, mergeCommitMessage);
        var block = SignCommit(final, mergeSha, parents, mergeCommitMessage, authorName, AuthorshipOriginSource.HandWrittenInWpf);
        return new MergeOutcome(mergeSha, block, merge);
    }

    /// <summary>Commits <paramref name="document"/> on the checked-out <paramref name="branch"/> with the given parents.</summary>
    private string CommitTree(string branch, ManuscriptDocument document, IReadOnlyList<string> parents, string message)
    {
        FormattingRuleEngine.ApplyFormattingRules(document, LoadFormattingProfile());
        StageDocument(document);
        var identity = Identity();
        string sha = WithRepo(r =>
        {
            var tree = r.ObjectDatabase.CreateTree(r.Index);
            var parentCommits = parents.Select(p => r.Lookup<Git2.Commit>(p) ?? throw new InvalidOperationException($"No commit {p}.")).ToList();
            var commit = r.ObjectDatabase.CreateCommit(identity, identity, message, tree, parentCommits, prettifyMessage: true);
            // The index and working files already match the new commit, so moving the branch is enough.
            r.Refs.UpdateTarget($"refs/heads/{branch}", commit.Sha);
            return commit.Sha;
        });
        RememberDocumentAtCommit(sha, document);
        return sha;
    }

    // ---- Reading manuscripts ---------------------------------------------------------------------------

    public ManuscriptDocument LoadCurrentWorkingDocument()
    {
        EnsureInitialized();
        if (Directory.Exists(SectionsDir))
        {
            var files = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (string path in Directory.EnumerateFiles(SectionsDir, "*.md"))
            {
                files[$"{ManuscriptSectionStore.SectionFolder}/{Path.GetFileName(path)}"] = File.ReadAllText(path);
            }
            if (files.Count > 0)
            {
                string metadataPath = Path.Combine(WorkspacePath, ManuscriptSectionStore.MetadataPath.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(metadataPath))
                {
                    files[ManuscriptSectionStore.MetadataPath] = File.ReadAllText(metadataPath);
                }
                return ManuscriptSectionStore.Read(files);
            }
        }

        // Earlier versions kept the manuscript in one file (it moves to section files on the next commit).
        string statePath = Path.Combine(WorkspacePath, LegacyStatePath.Replace('/', Path.DirectorySeparatorChar));
        if (File.Exists(statePath))
        {
            var doc = JsonSerializer.Deserialize<ManuscriptDocument>(File.ReadAllText(statePath), JsonOptions);
            if (doc != null)
            {
                return doc;
            }
        }

        string markdownPath = Path.Combine(WorkspacePath, LegacyManuscriptPath);
        if (File.Exists(markdownPath))
        {
            return CriticMarkupSerializer.ParseMarkdown(File.ReadAllText(markdownPath));
        }

        return new ManuscriptDocument();
    }

    public ManuscriptDocument LoadDocumentAtCommit(string commitSha)
    {
        EnsureInitialized();
        if (string.IsNullOrWhiteSpace(commitSha))
        {
            return LoadCurrentWorkingDocument();
        }

        bool cacheable = commitSha.Length == 40;
        if (cacheable && _documentAtCommitCache.TryGetValue(commitSha, out var cached))
        {
            return cached.Clone();
        }

        var loaded = ReadDocumentAtCommit(commitSha);
        if (cacheable)
        {
            _documentAtCommitCache[commitSha] = loaded.Clone();
        }
        return loaded;
    }

    /// <summary>
    /// Seeds the commit cache with a document this service just committed, saving a later read.
    /// </summary>
    private void RememberDocumentAtCommit(string sha, ManuscriptDocument document)
    {
        if (sha.Length == 40)
        {
            _documentAtCommitCache[sha] = document.Clone();
        }
    }

    private ManuscriptDocument ReadDocumentAtCommit(string commitSha)
    {
        var blobByPath = ListBlobs(commitSha, [ManuscriptSectionStore.SectionFolder, ManuscriptSectionStore.MetadataPath, LegacyStatePath, LegacyManuscriptPath]);
        if (blobByPath.Count == 0)
        {
            return new ManuscriptDocument();
        }

        var contents = ReadBlobs(blobByPath.Values);
        var files = blobByPath
            .Where(kv => contents.ContainsKey(kv.Value))
            .ToDictionary(kv => kv.Key, kv => contents[kv.Value], StringComparer.Ordinal);

        if (files.Keys.Any(ManuscriptSectionStore.IsSectionFile))
        {
            return ManuscriptSectionStore.Read(files);
        }
        if (files.TryGetValue(LegacyStatePath, out var stateJson) && !string.IsNullOrWhiteSpace(stateJson)
            && JsonSerializer.Deserialize<ManuscriptDocument>(stateJson, JsonOptions) is { } parsed)
        {
            return parsed;
        }
        if (files.TryGetValue(LegacyManuscriptPath, out var markdown) && !string.IsNullOrWhiteSpace(markdown))
        {
            return CriticMarkupSerializer.ParseMarkdown(markdown);
        }
        return new ManuscriptDocument();
    }

    /// <summary>
    /// The files (path → blob id) at a commit under the given paths, each a file or a folder (read recursively), as
    /// git ls-tree -r lists them. Empty when the commit does not exist.
    /// </summary>
    private Dictionary<string, string> ListBlobs(string commitSha, IEnumerable<string> paths) => WithRepo(r =>
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (r.Lookup<Git2.Commit>(commitSha) is not { } commit)
        {
            return result;
        }
        void Walk(Git2.Tree tree, string prefix)
        {
            foreach (var entry in tree)
            {
                string path = prefix + entry.Name;
                if (entry.Target is Git2.Tree sub) Walk(sub, path + "/");
                else if (entry.Target is Git2.Blob) result[path] = entry.Target.Sha;
            }
        }
        foreach (string path in paths.Distinct(StringComparer.Ordinal))
        {
            switch (commit[path]?.Target)
            {
                case Git2.Blob blob:
                    result[path] = blob.Sha;
                    break;
                case Git2.Tree tree:
                    Walk(tree, path.TrimEnd('/') + "/");
                    break;
            }
        }
        return result;
    });

    /// <summary>Reads many blobs (as stored, without line-ending conversion).</summary>
    private Dictionary<string, string> ReadBlobs(IEnumerable<string> blobShas)
        => ReadBlobBytes(blobShas).ToDictionary(kv => kv.Key, kv => Encoding.UTF8.GetString(kv.Value), StringComparer.OrdinalIgnoreCase);

    private Dictionary<string, byte[]> ReadBlobBytes(IEnumerable<string> blobShas)
    {
        var shas = blobShas.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var result = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        if (shas.Count == 0)
        {
            return result;
        }
        return WithRepo(r =>
        {
            foreach (string sha in shas)
            {
                if (r.Lookup<Git2.Blob>(sha) is { } blob)
                {
                    using var stream = blob.GetContentStream();
                    using var buffer = new MemoryStream();
                    stream.CopyTo(buffer);
                    result[sha] = buffer.ToArray();
                }
            }
            return result;
        });
    }

    // ---- Provenance blocks (Git notes) -----------------------------------------------------------------

    /// <summary>
    /// The provenance ledger assembled from the blocks stored with the commits of every branch (including
    /// those fetched from GitHub). A ledger file from earlier versions is moved into the repository first.
    /// </summary>
    public ProvenanceLedger LoadProvenanceLedger(string documentId = "doc-main")
    {
        EnsureInitialized();
        _ledgerCache ??= BuildLedgerFromRepository(documentId);
        return CloneLedger(_ledgerCache);
    }

    private static ProvenanceLedger CloneLedger(ProvenanceLedger ledger)
        => JsonSerializer.Deserialize<ProvenanceLedger>(JsonSerializer.Serialize(ledger))!;

    private ProvenanceLedger BuildLedgerFromRepository(string documentId)
    {
        MigrateLegacyLedgerFile();

        // Parents before children, from every branch, GitHub branch and tag (not the notes refs).
        var commits = WithRepo(r => History(r, Git2.CommitSortStrategies.Topological | Git2.CommitSortStrategies.Reverse)
            .Select(c => (c.Sha, c.Parents.Select(p => p.Sha).ToList()))
            .ToList());

        return ProvenanceChainService.BuildLedger(RevisionsDir, documentId, commits, ReadProvenanceNotes());
    }

    private Dictionary<string, TextChainBlock> ReadProvenanceNotes()
    {
        var blocks = new Dictionary<string, TextChainBlock>(StringComparer.OrdinalIgnoreCase);
        var notes = WithRepo(r => r.Refs[ProvenanceNotesRef] == null
            ? []
            : r.Notes[ProvenanceNotesNamespace].Select(n => (Commit: n.TargetObjectId.Sha, Json: n.Message)).ToList());
        foreach (var (commit, json) in notes)
        {
            if (ProvenanceChainService.DeserializeBlock(json.Trim()) is { } block
                && string.Equals(block.CommitSha, commit, StringComparison.OrdinalIgnoreCase))
            {
                blocks[commit] = block;
            }
        }
        return blocks;
    }

    /// <summary>Signs <paramref name="document"/> at a new commit and stores the block as the commit's note.</summary>
    private TextChainBlock SignCommit(
        ManuscriptDocument document,
        string sha,
        IReadOnlyList<string> parents,
        string message,
        string author,
        AuthorshipOriginSource origin)
    {
        // The links to earlier blocks are computed from the history this ledger knows, so make sure it
        // knows the new commit's parents (commits made outside the app may be newer than the cache).
        if (_ledgerCache == null || parents.Any(p => !_ledgerCache.CommitParents.ContainsKey(p)))
        {
            _ledgerCache = BuildLedgerFromRepository(document.DocumentId);
        }

        var block = ProvenanceChainService.AppendSignedBlock(RevisionsDir, _ledgerCache, document, sha, message, author, origin, parents);
        var identity = Identity();
        WithRepo(r => r.Notes.Add(new Git2.ObjectId(sha), ProvenanceChainService.SerializeBlock(block), identity, identity, ProvenanceNotesNamespace));
        return block;
    }

    /// <summary>
    /// Earlier versions kept the signed chain in .revisions/provenance-chain.json on one computer. Its blocks
    /// are stored with their commits (so they sync), and the file is kept renamed as a backup.
    /// </summary>
    private void MigrateLegacyLedgerFile()
    {
        string path = Path.Combine(RevisionsDir, ProvenanceChainService.LegacyLedgerFileName);
        if (!File.Exists(path))
        {
            return;
        }

        var legacy = ProvenanceChainService.LoadOrCreateLedger(RevisionsDir);
        var existingNotes = ReadProvenanceNotes();
        var shas = legacy.Blocks.Select(b => b.CommitSha).Where(s => s.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var present = CommitsThatExist(shas);

        var notes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var block in legacy.Blocks)
        {
            if (!present.TryGetValue(block.CommitSha, out var fullSha) || existingNotes.ContainsKey(fullSha))
            {
                continue;
            }
            block.Version = 1;
            block.CommitSha = fullSha;
            block.AuthorPublicKeyPem = legacy.AuthorPublicKeyPem;
            // Where the text came from was recorded per sentence; keep it on the block for the rebuilt history.
            block.OriginSource = legacy.Sentences
                .SelectMany(s => s.RevisionHistory)
                .Where(step => step.BlockIndex == block.BlockIndex)
                .GroupBy(step => step.Source)
                .OrderByDescending(g => g.Count())
                .Select(g => g.Key)
                .FirstOrDefault(AuthorshipOriginSource.HandWrittenInWpf);
            notes[fullSha] = ProvenanceChainService.SerializeBlock(block);
        }

        WriteNotes(notes, "Store provenance blocks from the earlier ledger file");
        File.Move(path, path + ".migrated", overwrite: true);
    }

    /// <summary>Full SHAs of the given commits that exist in the repository, keyed by the name given.</summary>
    private Dictionary<string, string> CommitsThatExist(IReadOnlyList<string> shas)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (shas.Count == 0)
        {
            return result;
        }
        return WithRepo(r =>
        {
            foreach (string sha in shas)
            {
                try
                {
                    if (r.Lookup<Git2.Commit>(sha) is { } commit) result[sha] = commit.Sha;
                }
                catch (Git2.LibGit2SharpException)
                {
                    // Ambiguous or invalid: not a commit here.
                }
            }
            return result;
        });
    }

    /// <summary>Adds many notes in one commit to the notes ref (one <c>git fast-import</c> process).</summary>
    private void WriteNotes(IReadOnlyDictionary<string, string> notesByCommit, string message)
    {
        if (notesByCommit.Count == 0)
        {
            return;
        }

        var identity = Identity();
        _ = message;
        WithRepo(r =>
        {
            foreach (var (commit, note) in notesByCommit)
            {
                r.Notes.Add(new Git2.ObjectId(commit), note, identity, identity, ProvenanceNotesNamespace);
            }
        });
    }

    /// <summary>
    /// Retrieves the commit graph across all local and GitHub branches, annotating each commit with its lane
    /// index and cryptographic provenance status. Pass an already loaded ledger and verification result to
    /// avoid re-reading and re-verifying the chain.
    /// </summary>
    public List<GitCommitNode> GetCommitGraph(ProvenanceLedger? ledger = null, ChainVerificationResult? verification = null)
    {
        EnsureInitialized();
        // Not every ref: the provenance notes ref has commits of its own. Newest first, children before parents.
        var commits = WithRepo(r =>
        {
            // The branch and tag names at each commit, as git log --decorate shows them.
            var names = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            void Name(string? sha, string name)
            {
                if (sha == null) return;
                if (!names.TryGetValue(sha, out var list)) names[sha] = list = [];
                list.Add(name);
            }
            if (r.Info.IsHeadDetached) Name(r.Head.Tip?.Sha, "HEAD");
            foreach (var b in r.Branches.Where(b => !b.IsRemote)) Name(b.Tip?.Sha, b.FriendlyName);
            foreach (var b in r.Branches.Where(b => b.IsRemote)) Name(b.Tip?.Sha, b.FriendlyName);
            foreach (var t in r.Tags) Name((t.PeeledTarget as Git2.Commit)?.Sha, $"tag: {t.FriendlyName}");
            return History(r, Git2.CommitSortStrategies.Topological | Git2.CommitSortStrategies.Time)
                .Select(c => (c.Sha, Parents: c.Parents.Select(p => p.Sha).ToList(), Author: c.Author.Name, When: c.Author.When,
                    Refs: names.GetValueOrDefault(c.Sha) ?? [], Subject: c.MessageShort))
                .ToList();
        });
        if (commits.Count == 0)
        {
            return [];
        }

        ledger ??= LoadProvenanceLedger();
        verification ??= ProvenanceChainService.VerifyChainIntegrity(ledger);
        var verifiedByCommit = ledger.Blocks
            .GroupBy(b => b.CommitSha, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Last(), StringComparer.OrdinalIgnoreCase);
        var statusByBlock = verification.BlockStatuses
            .GroupBy(s => s.BlockIndex)
            .ToDictionary(g => g.Key, g => g.First());

        var nodes = new List<GitCommitNode>();
        foreach (var c in commits)
        {
            string sha = c.Sha;
            var parents = c.Parents;
            string author = c.Author;
            DateTimeOffset timestamp = c.When;
            var refs = c.Refs.Where(r => r != $"{RemoteName}/HEAD").ToList();
            string msg = c.Subject.Trim();

            bool isReviewLane = refs.Any(r => r.StartsWith("review/", StringComparison.OrdinalIgnoreCase)
                    || r.StartsWith($"{RemoteName}/review/", StringComparison.OrdinalIgnoreCase))
                || msg.Contains("Supervisor", StringComparison.OrdinalIgnoreCase);

            bool isCryptoVerified = false;
            string blockHashShort = string.Empty;
            if (verifiedByCommit.TryGetValue(sha, out var block))
            {
                isCryptoVerified = !statusByBlock.TryGetValue(block.BlockIndex, out var status) || status.IsValid;
                blockHashShort = block.BlockHash.Length >= 10 ? block.BlockHash[..10] : block.BlockHash;
            }

            nodes.Add(new GitCommitNode
            {
                Sha = sha,
                ParentShas = parents,
                Author = author,
                Timestamp = timestamp,
                BranchNames = refs,
                Message = msg,
                LaneIndex = isReviewLane ? 1 : 0,
                IsCryptographicallyVerified = isCryptoVerified,
                BlockHashShort = blockHashShort
            });
        }

        return nodes;
    }

    // ---- GitHub (remote) -------------------------------------------------------------------------------

    /// <summary>The URL of the "origin" remote, or null when the workspace is not connected to one.</summary>
    public string? GetRemoteUrl()
    {
        EnsureInitialized();
        return WithRepo(r => r.Network.Remotes[RemoteName]?.Url is { Length: > 0 } url ? url : null);
    }

    public void SetRemoteUrl(string url)
    {
        EnsureInitialized();
        WithRepo(r =>
        {
            if (r.Network.Remotes[RemoteName] == null) r.Network.Remotes.Add(RemoteName, url);
            else r.Network.Remotes.Update(RemoteName, remote => remote.Url = url);
        });
    }

    /// <summary>
    /// Clones a repository (for example the paper's private GitHub repository) into
    /// <paramref name="targetDirectory"/>, including its provenance blocks.
    /// </summary>
    public static GitRepositoryService CloneFromRemote(string url, string targetDirectory)
    {
        string target = Path.GetFullPath(targetDirectory);
        string parent = Path.GetDirectoryName(target) ?? target;
        Directory.CreateDirectory(parent);
        var (code, _, stderr) = RunGitRaw(parent, ["clone", "-q", url, target], null);
        if (code != 0)
        {
            throw new InvalidOperationException($"Cloning failed: {stderr.Trim()}");
        }

        var repo = new GitRepositoryService(target);
        repo.EnsureInitialized();
        repo.FetchFromRemote();
        repo.MergeRemoteProvenance(repo.ReadRefs());
        return repo;
    }

    private void FetchFromRemote()
    {
        // Tags come with --tags rather than a refspec: --prune would otherwise delete tags made here that
        // GitHub does not have yet. --force lets a tag moved on another computer move here too.
        RunGit(["fetch", "-q", "--prune", "--tags", "--force", RemoteName,
            "+refs/heads/*:refs/remotes/origin/*",
            "+refs/notes/*:refs/notes/remotes/origin/*"]);
        _ledgerCache = null;
    }

    /// <summary>Every branch, GitHub branch and notes ref with its commit, from one process.</summary>
    private Dictionary<string, string> ReadRefs()
    {
        return WithRepo(r => r.Refs
            .Where(x => x is not Git2.SymbolicReference && (x.CanonicalName.StartsWith("refs/heads/", StringComparison.Ordinal)
                || x.CanonicalName.StartsWith($"refs/remotes/{RemoteName}/", StringComparison.Ordinal)
                || x.CanonicalName.StartsWith("refs/notes/", StringComparison.Ordinal)
                || x.CanonicalName.StartsWith("refs/tags/", StringComparison.Ordinal)))
            .ToDictionary(x => x.CanonicalName, x => x.TargetIdentifier, StringComparer.Ordinal));
    }

    /// <summary>Combines the provenance blocks from GitHub with the local ones (they are on different commits).</summary>
    private void MergeRemoteProvenance(Dictionary<string, string> refs)
    {
        if (!refs.TryGetValue(RemoteNotesRef, out var remote))
        {
            return;
        }
        if (!refs.TryGetValue(ProvenanceNotesRef, out var local))
        {
            UpdateRef(ProvenanceNotesRef, remote);
        }
        else if (!string.Equals(local, remote, StringComparison.OrdinalIgnoreCase))
        {
            RunGit(["notes", $"--ref={ProvenanceNotesRef}", "merge", "-q", "-s", "ours", RemoteNotesRef]);
        }
        _ledgerCache = null;
    }

    private bool IsAncestor(string ancestor, string descendant)
        => string.Equals(ancestor, descendant, StringComparison.OrdinalIgnoreCase)
            || string.Equals(GetMergeBase(ancestor, descendant), ancestor, StringComparison.OrdinalIgnoreCase);

    private void UpdateRef(string name, string sha) => WithRepo(r =>
    {
        if (r.Refs[name] == null) r.Refs.Add(name, sha);
        else r.Refs.UpdateTarget(name, sha);
    });

    /// <summary>
    /// Synchronizes with GitHub: fetches every branch and the provenance blocks, takes branches that only
    /// changed there, sends branches that only changed here, and merges branches changed on both sides with
    /// the three-way manuscript merge (conflicting edits become pending changes). Commit local changes first.
    /// </summary>
    public SyncReport SyncWithRemote(string? authorName = null)
    {
        EnsureInitialized();
        authorName ??= AuthorName;
        if (GetRemoteUrl() == null)
        {
            throw new InvalidOperationException("This workspace is not connected to a GitHub repository yet.");
        }

        var report = new SyncReport();
        FetchFromRemote();
        var refs = ReadRefs();
        string startBranch = GetCurrentBranch();
        MergeRemoteProvenance(refs);

        const string headsPrefix = "refs/heads/";
        string remotePrefix = $"refs/remotes/{RemoteName}/";
        var localBranches = refs.Where(r => r.Key.StartsWith(headsPrefix, StringComparison.Ordinal))
            .ToDictionary(r => r.Key[headsPrefix.Length..], r => r.Value, StringComparer.Ordinal);
        var remoteBranches = refs.Where(r => r.Key.StartsWith(remotePrefix, StringComparison.Ordinal) && r.Key != $"{remotePrefix}HEAD")
            .ToDictionary(r => r.Key[remotePrefix.Length..], r => r.Value, StringComparer.Ordinal);

        var toPush = new List<string>();
        string checkedOut = startBranch;
        try
        {
            foreach (var (branch, localSha) in localBranches)
            {
                if (!remoteBranches.TryGetValue(branch, out var remoteSha))
                {
                    toPush.Add(branch);
                    report.Lines.Add($"'{branch}' is new here: sending it to GitHub.");
                    continue;
                }
                if (string.Equals(localSha, remoteSha, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (IsAncestor(localSha, remoteSha))
                {
                    if (branch == checkedOut)
                    {
                        RunGit(["merge", "-q", "--ff-only", $"{remotePrefix}{branch}"]);
                    }
                    else
                    {
                        UpdateRef($"{headsPrefix}{branch}", remoteSha);
                    }
                    report.BranchesUpdated++;
                    report.Lines.Add($"'{branch}' updated with changes from GitHub.");
                }
                else if (IsAncestor(remoteSha, localSha))
                {
                    toPush.Add(branch);
                    report.Lines.Add($"'{branch}' has new commits here: sending them to GitHub.");
                }
                else
                {
                    if (branch != checkedOut)
                    {
                        Checkout(branch);
                        checkedOut = branch;
                    }
                    var merge = MergeDivergedBranch(branch, localSha, remoteSha, authorName);
                    report.BranchesMerged++;
                    report.Conflicts += merge.Conflicts;
                    toPush.Add(branch);
                    report.Lines.Add($"'{branch}' changed both here and on GitHub: {merge.Summary}");
                }
            }

            foreach (var (branch, remoteSha) in remoteBranches)
            {
                if (!localBranches.ContainsKey(branch))
                {
                    UpdateRef($"{headsPrefix}{branch}", remoteSha);
                    report.BranchesUpdated++;
                    report.Lines.Add($"'{branch}' added from GitHub.");
                }
            }
        }
        finally
        {
            if (checkedOut != startBranch && startBranch != "HEAD")
            {
                try
                {
                    Checkout(startBranch);
                }
                catch (Exception ex) when (ex is Git2.LibGit2SharpException or InvalidOperationException)
                {
                    // Left on the merged branch; the app reloads whatever is checked out.
                }
            }
        }

        var pushArgs = new List<string> { "push", "-q", RemoteName };
        pushArgs.AddRange(toPush.Select(b => $"{headsPrefix}{b}:{headsPrefix}{b}"));
        // Blocks exist locally if they did before, came from GitHub, or were signed by a merge above.
        bool hasNotes = refs.ContainsKey(ProvenanceNotesRef) || refs.ContainsKey(RemoteNotesRef) || report.BranchesMerged > 0;
        if (hasNotes)
        {
            pushArgs.Add($"{ProvenanceNotesRef}:{ProvenanceNotesRef}");
        }
        bool hasTags = refs.Keys.Any(r => r.StartsWith("refs/tags/", StringComparison.Ordinal));
        if (hasTags)
        {
            pushArgs.Add("refs/tags/*:refs/tags/*");
        }
        if (toPush.Count > 0 || hasNotes || hasTags)
        {
            var (code, _, stderr) = RunGitRaw(WorkspacePath, pushArgs, null);
            if (code != 0)
            {
                throw new InvalidOperationException(
                    $"Sending to GitHub failed: {stderr.Trim()} (If another computer synced at the same moment, sync again.)");
            }
            // Pushing to a named remote also moves its remote-tracking branches, so no fetch is needed after.
            report.BranchesPushed = toPush.Count;
        }

        _ledgerCache = null;
        return report;
    }

    private ManuscriptMergeResult MergeDivergedBranch(string branch, string localSha, string remoteSha, string authorName)
    {
        string? baseSha = GetMergeBase(localSha, remoteSha);
        var baseDoc = baseSha != null ? LoadDocumentAtCommit(baseSha) : new ManuscriptDocument();
        var merge = ManuscriptMerger.Merge(baseDoc, LoadDocumentAtCommit(localSha), LoadDocumentAtCommit(remoteSha), $"{branch} (from GitHub)");
        merge.Notes.AddRange(MergeProjectFiles(baseSha, localSha, remoteSha));

        string message = $"Merge changes to '{branch}' from GitHub";
        if (merge.Conflicts > 0)
        {
            message += $" ({merge.Conflicts} conflicting edits left as pending changes)";
        }
        var parents = new List<string> { localSha, remoteSha };
        string sha = CommitTree(branch, merge.Document, parents, message);
        SignCommit(merge.Document, sha, parents, message, authorName, AuthorshipOriginSource.HandWrittenInWpf);
        return merge;
    }

    // ---- The paper's other files -------------------------------------------------------------------------

    /// <summary>
    /// Files besides the manuscript that belong to the paper and are committed and synced with it: the
    /// bibliography, figure images, table and results files, and the paper's settings and review responses.
    /// </summary>
    private IEnumerable<string> ProjectPaths()
    {
        string bibliography = Scholarly.PaperSettings.Load(WorkspacePath).BibliographyFile.Replace('\\', '/');
        return
        [
            bibliography,
            Scholarly.PaperSettings.FiguresFolder,
            Scholarly.PaperSettings.TablesFolder,
            Scholarly.ResultsLibrary.ResultsFolder,
            Scholarly.PaperSettings.FilePath,
            Scholarly.ResultsLibrary.RegistryPath,
            Reviews.ReviewStore.FilePath,
            Reviews.SubmittedLineMap.Folder,
            Scholarly.FrontMatter.FilePath,
            Scholarly.PaperNotes.FilePath
        ];
    }

    /// <summary>The project paths that exist here (Git refuses a path that matches nothing).</summary>
    private IEnumerable<string> ExistingProjectPaths()
        => ProjectPaths().Where(p =>
        {
            string full = Path.Combine(WorkspacePath, p.Replace('/', Path.DirectorySeparatorChar));
            return File.Exists(full) || Directory.Exists(full);
        });

    private Dictionary<string, string> ListProjectBlobs(string commitSha)
    {
        return ListBlobs(commitSha, ProjectPaths());
    }

    /// <summary>
    /// Three-way merge of the paper's other files into the working tree (the local branch is checked out):
    /// a file changed on one side only takes that side; the bibliography and review responses are merged entry
    /// by entry; for other files changed on both computers this computer's version is kept. Returns notes.
    /// </summary>
    private List<string> MergeProjectFiles(string? baseSha, string localSha, string remoteSha)
    {
        var notes = new List<string>();
        var baseBlobs = baseSha != null ? ListProjectBlobs(baseSha) : new Dictionary<string, string>();
        var local = ListProjectBlobs(localSha);
        var remote = ListProjectBlobs(remoteSha);
        var paths = baseBlobs.Keys.Union(local.Keys).Union(remote.Keys).ToList();

        var takeRemote = new List<string>();
        var both = new List<string>();
        foreach (string path in paths)
        {
            baseBlobs.TryGetValue(path, out var b);
            local.TryGetValue(path, out var l);
            remote.TryGetValue(path, out var r);
            if (l == r || r == b)
            {
                continue;
            }
            if (l == b)
            {
                takeRemote.Add(path);
            }
            else
            {
                both.Add(path);
            }
        }
        if (takeRemote.Count == 0 && both.Count == 0)
        {
            return notes;
        }

        var wanted = takeRemote.Concat(both).SelectMany(p => new[] { Get(baseBlobs, p), Get(local, p), Get(remote, p) }).OfType<string>();
        var blobs = ReadBlobBytes(wanted);
        string Text(Dictionary<string, string> tree, string path)
            => tree.TryGetValue(path, out var sha) && blobs.TryGetValue(sha, out var bytes) ? Encoding.UTF8.GetString(bytes) : string.Empty;

        foreach (string path in takeRemote)
        {
            string full = Path.Combine(WorkspacePath, path.Replace('/', Path.DirectorySeparatorChar));
            if (remote.TryGetValue(path, out var sha) && blobs.TryGetValue(sha, out var bytes))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                File.WriteAllBytes(full, bytes);
            }
            else if (File.Exists(full))
            {
                File.Delete(full);
            }
        }
        if (takeRemote.Count > 0)
        {
            notes.Add($"Took {takeRemote.Count} updated file{(takeRemote.Count == 1 ? "" : "s")} from GitHub ({string.Join(", ", takeRemote.Take(4))}{(takeRemote.Count > 4 ? ", …" : "")}).");
        }

        string bibliography = Scholarly.PaperSettings.Load(WorkspacePath).BibliographyFile.Replace('\\', '/');
        foreach (string path in both)
        {
            string full = Path.Combine(WorkspacePath, path.Replace('/', Path.DirectorySeparatorChar));
            if (string.Equals(path, bibliography, StringComparison.OrdinalIgnoreCase))
            {
                File.WriteAllText(full, Bibliography.BibFileMerge.Merge(Text(baseBlobs, path), Text(local, path), Text(remote, path)), Utf8NoBom);
                notes.Add($"Combined the references added on both computers in {path}.");
            }
            else if (path == Reviews.ReviewStore.FilePath)
            {
                var merged = Reviews.ReviewStore.Merge(
                    Reviews.ReviewStore.Parse(Text(baseBlobs, path)), Reviews.ReviewStore.Parse(Text(local, path)), Reviews.ReviewStore.Parse(Text(remote, path)));
                File.WriteAllText(full, merged.Serialize(), Utf8NoBom);
                notes.Add("Combined the reviewer responses written on both computers.");
            }
            else
            {
                notes.Add($"{path} was changed on both computers; this computer's version was kept (GitHub's is in commit {remoteSha[..7]}).");
            }
        }
        return notes;

        static string? Get(Dictionary<string, string> tree, string path) => tree.TryGetValue(path, out var sha) ? sha : null;
    }

    // ---- Submission tags -----------------------------------------------------------------------------

    /// <summary>Marks a commit as a submitted version (a Git tag, synced to GitHub), e.g. "submitted-v1".</summary>
    public void CreateTag(string name, string commitSha)
    {
        EnsureInitialized();
        WithRepo(r => r.Tags.Add(name, commitSha, allowOverwrite: true));
    }

    /// <summary>Tags and the commits they point to.</summary>
    public Dictionary<string, string> GetTags()
    {
        EnsureInitialized();
        // Annotated tags point at a tag object; the commit it tags is what matters.
        return WithRepo(r => r.Tags.ToDictionary(t => t.FriendlyName, t => (t.PeeledTarget ?? t.Target).Sha, StringComparer.Ordinal));
    }

    /// <summary>Reads one file as it was at a commit (null if it did not exist).</summary>
    public string? ReadFileAtCommit(string commitSha, string path)
    {
        var blobs = ListBlobs(commitSha, [path]);
        return blobs.TryGetValue(path, out var sha) && ReadBlobBytes([sha]).TryGetValue(sha, out var bytes) ? Encoding.UTF8.GetString(bytes) : null;
    }

    // ---- Git inside the app (libgit2) -------------------------------------------------------------------

    /// <summary>The notes namespace of <see cref="ProvenanceNotesRef"/> (refs/notes/&lt;namespace&gt;).</summary>
    private const string ProvenanceNotesNamespace = "revisions-provenance";

    private readonly object _repoLock = new();

    /// <summary>
    /// Runs an operation on the repository, opened for it (opening is cheap in-process, and a fresh handle always
    /// sees what git commands run for GitHub changed). One at a time: a handle is not thread-safe.
    /// </summary>
    private T WithRepo<T>(Func<Git2.Repository, T> operation)
    {
        lock (_repoLock)
        {
            using var repo = new Git2.Repository(WorkspacePath);
            return operation(repo);
        }
    }

    private void WithRepo(Action<Git2.Repository> operation) => WithRepo<object?>(r => { operation(r); return null; });

    /// <summary>
    /// A reviewer's commit needs an email address (libgit2 refuses an empty one); this one is on the reserved
    /// .invalid domain, so it can never reach anyone. History shows the reviewer's name.
    /// </summary>
    private static string ReviewerEmail(string name)
    {
        var slug = new string(name.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray()).Trim('-');
        return $"{(slug.Length > 0 ? slug : "reviewer")}@reviewer.invalid";
    }

    /// <summary>The local user as the author and committer of a new commit.</summary>
    private Git2.Signature Identity() => new(_authorName ?? Environment.UserName, _authorEmail ?? $"{Environment.UserName}@{Environment.MachineName}", DateTimeOffset.Now);

    /// <summary>Commits reachable from every branch, GitHub branch and tag (not the notes refs).</summary>
    private static IEnumerable<Git2.Commit> History(Git2.Repository r, Git2.CommitSortStrategies sort)
    {
        var tips = r.Refs
            .Where(x => x.CanonicalName.StartsWith("refs/heads/", StringComparison.Ordinal)
                || x.CanonicalName.StartsWith("refs/remotes/", StringComparison.Ordinal)
                || x.CanonicalName.StartsWith("refs/tags/", StringComparison.Ordinal))
            .Select(x => x.ResolveToDirectReference()?.Target?.Peel<Git2.Commit>())
            .OfType<Git2.Commit>()
            .DistinctBy(c => c.Sha)
            .ToList();
        return tips.Count == 0 ? [] : r.Commits.QueryBy(new Git2.CommitFilter { IncludeReachableFrom = tips, SortBy = sort });
    }

    // ---- Running Git (for GitHub: fetch, push, clone) ----------------------------------------------------

    private (int ExitCode, string Stdout) RunGit(IReadOnlyList<string> arguments, bool throwOnError = true, string? stdin = null)
    {
        var (code, output, stderr) = RunGitRaw(WorkspacePath, arguments, stdin == null ? null : Utf8NoBom.GetBytes(stdin));
        string stdout = Encoding.UTF8.GetString(output);
        if (code != 0 && throwOnError)
        {
            throw new InvalidOperationException($"git {string.Join(' ', arguments)} failed (exit {code}): {stderr} {stdout}");
        }
        return (code, stdout);
    }

    private static (int ExitCode, byte[] Stdout, string Stderr) RunGitRaw(string workingDirectory, IReadOnlyList<string> arguments, byte[]? stdin)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = workingDirectory,
            RedirectStandardInput = stdin != null,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardErrorEncoding = Encoding.UTF8
        };
        // Never wait for a terminal prompt nobody can see; sign-in to GitHub goes through Git Credential Manager.
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        // ArgumentList quotes each argument correctly, so messages and branch names can contain any characters.
        foreach (string arg in arguments)
        {
            psi.ArgumentList.Add(arg);
        }

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start git process.");

        // Drain both pipes concurrently with writing the input; doing them one after the other can deadlock.
        var stderrTask = proc.StandardError.ReadToEndAsync();
        var stdoutBuffer = new MemoryStream();
        var stdoutTask = proc.StandardOutput.BaseStream.CopyToAsync(stdoutBuffer);
        if (stdin != null)
        {
            try
            {
                proc.StandardInput.BaseStream.Write(stdin);
                proc.StandardInput.Close();
            }
            catch (IOException)
            {
                // Git exited early; its error output says why.
            }
        }
        stdoutTask.GetAwaiter().GetResult();
        string stderr = stderrTask.GetAwaiter().GetResult();
        proc.WaitForExit();

        return (proc.ExitCode, stdoutBuffer.ToArray(), stderr);
    }
}
