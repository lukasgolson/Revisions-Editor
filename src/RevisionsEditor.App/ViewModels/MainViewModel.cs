using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using RevisionsEditor.Core.Bridge;
using RevisionsEditor.Core.Diffing;
using RevisionsEditor.Core.Docx;
using RevisionsEditor.Core.Formatting;
using RevisionsEditor.Core.Git;
using RevisionsEditor.Core.Models;
using RevisionsEditor.Core.Provenance;
using RevisionsEditor.Core.Serialization;

namespace RevisionsEditor.App.ViewModels;

public sealed class RevisionHunkItemViewModel : INotifyPropertyChanged
{
    public RevisionHunk Model { get; }

    public RevisionHunkItemViewModel(RevisionHunk model)
    {
        Model = model;
        _editableProposedText = model.HunkType == RevisionHunkType.Comment ? model.CommentText : model.ProposedText;
    }

    public string HunkId => Model.HunkId;
    public string ParagraphId => Model.ParagraphId;
    public RevisionHunkType HunkType => Model.HunkType;
    public string Author => Model.Author;
    public string TimestampText => Model.TimestampUtc.ToLocalTime().ToString("g");
    public string OriginalText => Model.OriginalText;
    public string ProposedText => Model.ProposedText;
    public string CommentText => Model.CommentText;
    public string ContextSnippet => Model.ContextSnippet;

    public RevisionHunkStatus Status
    {
        get => Model.Status;
        set
        {
            if (Model.Status != value)
            {
                Model.Status = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(StatusBadgeText));
            }
        }
    }

    private string _editableProposedText;
    public string EditableProposedText
    {
        get => _editableProposedText;
        set
        {
            if (_editableProposedText != value)
            {
                _editableProposedText = value;
                if (Model.HunkType == RevisionHunkType.Comment)
                {
                    Model.CommentText = value;
                }
                else
                {
                    Model.ProposedText = value;
                }
                OnPropertyChanged();
                OnPropertyChanged(nameof(ProposedText));
                OnPropertyChanged(nameof(CommentText));
            }
        }
    }

    public string TypeBadgeText => Model.HunkType switch
    {
        RevisionHunkType.Insertion => "Insertion",
        RevisionHunkType.Deletion => "Deletion",
        RevisionHunkType.Replacement => "Replacement",
        RevisionHunkType.UntrackedEdit => "Untracked",
        RevisionHunkType.Comment => "Comment",
        _ => "Edit"
    };

    public string StatusBadgeText => Model.Status switch
    {
        RevisionHunkStatus.Accepted => "Accepted",
        RevisionHunkStatus.Rejected => "Rejected",
        _ => "Pending"
    };

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? prop = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(prop));
}

public sealed record MarkupModeOption(MarkupDisplayMode Mode, string Label);

public sealed record ParagraphStyleOption(BlockStyleRole Role, string Label);

public sealed partial class MainViewModel : INotifyPropertyChanged, IDisposable
{
    private const int MaxActivityLogEntries = 500;
    private const int BridgePort = 17420;

    // Parsing the whole manuscript on every keystroke is the main source of typing lag on long documents,
    // so edits in the Markdown source are applied once the user pauses.
    private static readonly TimeSpan MarkdownSyncDelay = TimeSpan.FromMilliseconds(350);

    private GitRepositoryService? _repoService;
    private readonly LocalBridgeServer _bridgeServer;
    private readonly DispatcherTimer _markdownSyncTimer;
    private ManuscriptDocument _currentDocument = new();
    private FormattingRuleProfile _activeProfile = FormattingRuleProfile.GetBuiltInProfiles()[0];
    private ProvenanceLedger _provenanceLedger = new();
    private ChainVerificationResult _chainVerification = new();
    private DocumentDiffResult _currentDiffResult = new();
    private string _mainBranch = "main";
    private int _diffRequestId;
    private bool _reloadRequested;

    /// <summary>
    /// Everything a reload needs, gathered on a background thread so Git (hundreds of milliseconds per
    /// process on Windows) and signature verification never block the UI thread.
    /// </summary>
    private sealed record WorkspaceSnapshot(
        FormattingRuleProfile Profile,
        List<string> Branches,
        string CurrentBranch,
        ManuscriptDocument Document,
        string Markdown,
        ProvenanceLedger Ledger,
        ChainVerificationResult Verification,
        List<GitCommitNode> Graph,
        GitCommitNode? CompareBase,
        GitCommitNode? CompareTarget,
        DocumentDiffResult Diff,
        IReadOnlyList<GoogleDocPatch> Patches,
        PaperViewModel.Snapshot? Paper = null);

    public event Action? DocumentRenderRequested;
    public event Action? DiffRenderRequested;

    public RangeObservableCollection<GitCommitNode> CommitGraph { get; } = [];
    public RangeObservableCollection<string> Branches { get; } = [];
    public RangeObservableCollection<RevisionHunkItemViewModel> FilteredHunks { get; } = [];
    public RangeObservableCollection<ParagraphDiffEntry> DiffEntries { get; } = [];
    public RangeObservableCollection<SentenceProvenanceRecord> Sentences { get; } = [];
    public RangeObservableCollection<TextChainBlock> ChainBlocks { get; } = [];
    public RangeObservableCollection<BlockVerificationStatus> BlockStatuses { get; } = [];
    public ObservableCollection<FormattingRuleProfile> BuiltInProfiles { get; } = [];
    public RangeObservableCollection<GoogleDocPatch> GoogleDocPatches { get; } = [];
    public ObservableCollection<string> ActivityLogs { get; } = [];

    public IReadOnlyList<MarkupModeOption> MarkupModes { get; } =
    [
        new(MarkupDisplayMode.AllMarkup, "All Markup"),
        new(MarkupDisplayMode.SimpleMarkup, "Simple Markup"),
        new(MarkupDisplayMode.NoMarkupFinal, "No Markup"),
        new(MarkupDisplayMode.Original, "Original")
    ];

    public IReadOnlyList<string> HunkFilters { get; } =
        ["All", "Pending", "Insertions", "Deletions", "Replacements", "Comments", "Untracked"];

    public IReadOnlyList<ParagraphStyleOption> ParagraphStyles { get; } =
    [
        new(BlockStyleRole.BodyParagraph, "Normal"),
        new(BlockStyleRole.Title, "Title"),
        new(BlockStyleRole.Heading1, "Heading 1"),
        new(BlockStyleRole.Heading2, "Heading 2"),
        new(BlockStyleRole.Heading3, "Heading 3"),
        new(BlockStyleRole.Abstract, "Abstract"),
        new(BlockStyleRole.BlockQuote, "Quote"),
        new(BlockStyleRole.Caption, "Caption"),
        new(BlockStyleRole.ReferenceItem, "Reference")
    ];

    /// <summary>
    /// Applies paragraphs edited on the Document page. The page itself is not re-rendered (that would move
    /// the cursor); the Markdown source, change list and status bar are updated.
    /// </summary>
    public void ApplyEditedBlocks(IReadOnlyList<DocumentBlock> blocks, IReadOnlySet<string> lockedParagraphIds)
    {
        if (!HasWorkspace)
        {
            return;
        }

        // Paragraphs edited on the page had no pending changes; their accepted or rejected changes are now
        // part of the text, so drop those hunks (as committing would). Pending hunks of read-only paragraphs stay.
        var present = blocks.Select(b => b.ParagraphId).ToHashSet(StringComparer.Ordinal);
        _currentDocument.Hunks.RemoveAll(h => !present.Contains(h.ParagraphId) || !lockedParagraphIds.Contains(h.ParagraphId));
        _currentDocument.Blocks = blocks.ToList();

        var title = blocks.FirstOrDefault(b => b.Role == BlockStyleRole.Title);
        if (title != null)
        {
            DocumentTitle = DiffEngineService.ToGoogleDocsText(title.Text);
        }

        FormattingRuleEngine.ApplyFormattingRules(_currentDocument, _activeProfile);
        HasUncommittedChanges = true;
        _markdownSyncTimer.Stop();
        _markdownSyncPending = false;
        UpdateRawMarkdownFromDocument();
        RebuildFilteredHunks();
        UpdateStatusBar();
        Paper.ScheduleChecks();
    }

    /// <summary>
    /// Set by the window: applies edits still waiting on the Document page. Called before any action that
    /// reads the manuscript. A reload from disk deliberately does not call it (it replaces the manuscript).
    /// </summary>
    public Action? FlushEditorEdits { get; set; }

    /// <summary>Ids of every paragraph and hunk, so new paragraphs never reuse one.</summary>
    public IEnumerable<string> UsedParagraphIds =>
        _currentDocument.Blocks.Select(b => b.ParagraphId).Concat(_currentDocument.Hunks.Select(h => h.ParagraphId));

    /// <summary>Journal, checks, references, figures and data, and reviewer responses.</summary>
    public PaperViewModel Paper { get; }

    public MainViewModel()
    {
        Paper = new PaperViewModel(this);
        foreach (var p in FormattingRuleProfile.GetBuiltInProfiles())
        {
            BuiltInProfiles.Add(p);
        }

        _markdownSyncTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = MarkdownSyncDelay };
        _markdownSyncTimer.Tick += (_, _) => FlushPendingMarkdownSync();

        _bridgeServer = new LocalBridgeServer(null, BridgePort);
        _bridgeServer.BridgeActivityLogged += msg =>
        {
            Application.Current?.Dispatcher.BeginInvoke(() => AddLog(msg));
        };
        _bridgeServer.SnapshotPulledFromGoogleDocs += () =>
        {
            Application.Current?.Dispatcher.BeginInvoke(() => _ = RefreshAfterExternalChangeAsync());
        };
        // A pull merges with the edits made here; those not yet committed are handed over first, otherwise the
        // reload after the pull would discard them.
        _bridgeServer.UncommittedEditsProvider = () => Application.Current?.Dispatcher.Invoke(() =>
        {
            if (_repoService == null)
            {
                return null;
            }
            FlushPendingMarkdownSync();
            return HasUncommittedChanges ? CriticMarkupSerializer.BakeResolvedHunks(_currentDocument, acceptRemainingPending: false) : null;
        });
        _bridgeServer.PatchAcknowledgedFromGoogleDocs += () =>
        {
            Application.Current?.Dispatcher.BeginInvoke(() => GoogleDocPatches.ReplaceAll(_bridgeServer.GetPendingPatches()));
        };

        bool started = _bridgeServer.TryStart();
        BridgeStatusText = started
            ? $"Google Docs bridge: listening on port {BridgePort}"
            : $"Google Docs bridge: offline (port {BridgePort} in use)";

        UpdateStatusBar();
    }

    public bool HasWorkspace => _repoService != null;
    public string WorkspacePath => _repoService?.WorkspacePath ?? string.Empty;
    public string WorkspaceName => HasWorkspace ? Path.GetFileName(WorkspacePath.TrimEnd(Path.DirectorySeparatorChar)) : string.Empty;
    public string WindowTitle => HasWorkspace ? $"{WorkspaceName} - Revisions Editor" : "Revisions Editor";
    public string AuthorName => _repoService?.AuthorName ?? Environment.UserName;

    public bool HasCommits => CommitGraph.Count > 0;

    /// <summary>True when a review branch (anything other than the trunk) is checked out and can be merged.</summary>
    public bool IsOnReviewBranch => HasWorkspace && HasCommits
        && !string.Equals(CurrentBranch, _mainBranch, StringComparison.Ordinal)
        && !string.Equals(CurrentBranch, "HEAD", StringComparison.Ordinal);

    private bool _isBusy;
    /// <summary>True while a repository operation runs in the background; commands and editors are disabled.</summary>
    public bool IsBusy
    {
        get => _isBusy;
        private set { _isBusy = value; OnPropertyChanged(); }
    }

    private string _busyText = string.Empty;
    public string BusyText
    {
        get => _busyText;
        private set { _busyText = value; OnPropertyChanged(); }
    }

    public ManuscriptDocument CurrentDocument => _currentDocument;
    public FormattingRuleProfile ActiveProfile
    {
        get => _activeProfile;
        set
        {
            if (value != null && _activeProfile != value)
            {
                _activeProfile = value.Clone();
                OnPropertyChanged();
                OnPropertyChanged(nameof(ActiveProfileName));
                if (HasWorkspace)
                {
                    ApplyFormattingRulesNow();
                }
            }
        }
    }

    public string ActiveProfileName
    {
        get => _activeProfile.ProfileName;
        set
        {
            var found = BuiltInProfiles.FirstOrDefault(p => string.Equals(p.ProfileName, value, StringComparison.Ordinal));
            if (found != null && !string.Equals(_activeProfile.ProfileName, value, StringComparison.Ordinal))
            {
                ActiveProfile = found;
            }
        }
    }

    public DocumentDiffResult CurrentDiffResult => _currentDiffResult;

    private string _currentBranch = string.Empty;
    public string CurrentBranch
    {
        get => _currentBranch;
        set
        {
            _currentBranch = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsOnReviewBranch));
        }
    }

    private string _documentTitle = string.Empty;
    public string DocumentTitle
    {
        get => _documentTitle;
        set
        {
            if (_documentTitle != value)
            {
                _documentTitle = value;
                _currentDocument.Title = value;
                OnPropertyChanged();
            }
        }
    }

    private MarkupDisplayMode _selectedMarkupMode = MarkupDisplayMode.AllMarkup;
    public MarkupDisplayMode SelectedMarkupMode
    {
        get => _selectedMarkupMode;
        set
        {
            if (_selectedMarkupMode != value)
            {
                FlushPendingMarkdownSync(); // keep page edits before the page is redrawn
                _selectedMarkupMode = value;
                OnPropertyChanged();
                DocumentRenderRequested?.Invoke();
            }
        }
    }

    private string _rawMarkdownText = string.Empty;
    private bool _suppressMarkdownSync;
    private bool _markdownSyncPending;
    public string RawMarkdownText
    {
        get => _rawMarkdownText;
        set
        {
            if (_rawMarkdownText != value)
            {
                _rawMarkdownText = value;
                OnPropertyChanged();
                if (!_suppressMarkdownSync && HasWorkspace)
                {
                    _markdownSyncPending = true;
                    _markdownSyncTimer.Stop();
                    _markdownSyncTimer.Start();
                }
            }
        }
    }

    private string _selectedHunkFilter = "All";
    public string SelectedHunkFilter
    {
        get => _selectedHunkFilter;
        set
        {
            if (_selectedHunkFilter != value)
            {
                _selectedHunkFilter = value;
                OnPropertyChanged();
                RebuildFilteredHunks();
            }
        }
    }

    private RevisionHunkItemViewModel? _selectedHunk;
    public RevisionHunkItemViewModel? SelectedHunk
    {
        get => _selectedHunk;
        set { _selectedHunk = value; OnPropertyChanged(); }
    }

    private GitCommitNode? _selectedCommit;
    public GitCommitNode? SelectedCommit
    {
        get => _selectedCommit;
        set { _selectedCommit = value; OnPropertyChanged(); }
    }

    private GoogleDocPatch? _selectedPatch;
    public GoogleDocPatch? SelectedPatch
    {
        get => _selectedPatch;
        set { _selectedPatch = value; OnPropertyChanged(); }
    }

    private GitCommitNode? _compareBaseCommit;
    public GitCommitNode? CompareBaseCommit
    {
        get => _compareBaseCommit;
        set
        {
            if (ReferenceEquals(_compareBaseCommit, value))
            {
                return;
            }
            _compareBaseCommit = value;
            OnPropertyChanged();
            RecomputeVersionDiffInBackground();
        }
    }

    private GitCommitNode? _compareTargetCommit;
    public GitCommitNode? CompareTargetCommit
    {
        get => _compareTargetCommit;
        set
        {
            if (ReferenceEquals(_compareTargetCommit, value))
            {
                return;
            }
            _compareTargetCommit = value;
            OnPropertyChanged();
            RecomputeVersionDiffInBackground();
        }
    }

    private SentenceProvenanceRecord? _selectedSentence;
    public SentenceProvenanceRecord? SelectedSentence
    {
        get => _selectedSentence;
        set { _selectedSentence = value; OnPropertyChanged(); }
    }

    private string _chainVerificationBanner = "No workspace open";
    public string ChainVerificationBanner
    {
        get => _chainVerificationBanner;
        set { _chainVerificationBanner = value; OnPropertyChanged(); }
    }

    private bool _isChainValid = true;
    public bool IsChainValid
    {
        get => _isChainValid;
        set { _isChainValid = value; OnPropertyChanged(); }
    }

    private string _authorKeyFingerprint = string.Empty;
    public string AuthorKeyFingerprint
    {
        get => _authorKeyFingerprint;
        set { _authorKeyFingerprint = value; OnPropertyChanged(); }
    }

    private string _bridgeStatusText = string.Empty;
    public string BridgeStatusText
    {
        get => _bridgeStatusText;
        set { _bridgeStatusText = value; OnPropertyChanged(); }
    }

    private string _statusBarSummary = string.Empty;
    public string StatusBarSummary
    {
        get => _statusBarSummary;
        set { _statusBarSummary = value; OnPropertyChanged(); }
    }

    public int PendingHunkCount => _currentDocument.Hunks.Count(h => h.Status == RevisionHunkStatus.Pending);
    public int AcceptedHunkCount => _currentDocument.Hunks.Count(h => h.Status == RevisionHunkStatus.Accepted);
    public int RejectedHunkCount => _currentDocument.Hunks.Count(h => h.Status == RevisionHunkStatus.Rejected);

    public string DiffSummary => _currentDiffResult.Entries.Count == 0
        ? string.Empty
        : $"+{_currentDiffResult.TotalAddedWords} / -{_currentDiffResult.TotalRemovedWords} words in {_currentDiffResult.ChangedParagraphCount} changed paragraphs";


    /// <summary>
    /// Runs a repository operation with the busy state set. Reload requests that arrive meanwhile (for
    /// example a Google Docs pull) are deferred until it finishes.
    /// </summary>
    private async Task RunBusyAsync(string busyText, Func<Task> operation)
    {
        if (IsBusy)
        {
            throw new InvalidOperationException("Another operation is still in progress.");
        }

        IsBusy = true;
        BusyText = busyText;
        try
        {
            await operation();
        }
        finally
        {
            IsBusy = false;
            BusyText = string.Empty;
            if (_reloadRequested)
            {
                _reloadRequested = false;
                _ = RefreshAfterExternalChangeAsync();
            }
        }
    }

    /// <summary>
    /// Opens (or initializes) the workspace at <paramref name="workspacePath"/>.
    /// </summary>
    public Task OpenWorkspaceAsync(string workspacePath)
        => RunBusyAsync("Opening workspace...", () => OpenCoreAsync(new GitRepositoryService(workspacePath)));

    private async Task OpenCoreAsync(GitRepositoryService repo)
    {
        Paper.FlushFrontMatter(); // the previous workspace's title page
        string? remote = await Task.Run(() =>
        {
            repo.EnsureInitialized();
            return repo.GetRemoteUrl();
        });

        _markdownSyncTimer.Stop();
        _markdownSyncPending = false;
        _repoService = repo;
        _bridgeServer.UpdateRepository(repo);
        RemoteUrl = remote;
        RaiseWorkspaceChanged();

        AddLog($"Opened workspace '{repo.WorkspacePath}'.");
        await ReloadCoreAsync(repo);
    }

    public void CloseWorkspace()
    {
        _markdownSyncTimer.Stop();
        _markdownSyncPending = false;
        _repoService = null;
        _bridgeServer.UpdateRepository(null);
        RemoteUrl = null;
        RaiseWorkspaceChanged();
        ClearWorkspaceState();
    }

    private void RaiseWorkspaceChanged()
    {
        OnPropertyChanged(nameof(HasWorkspace));
        OnPropertyChanged(nameof(WorkspacePath));
        OnPropertyChanged(nameof(WorkspaceName));
        OnPropertyChanged(nameof(WindowTitle));
        OnPropertyChanged(nameof(AuthorName));
    }

    public Task ReloadWorkspaceStateAsync()
    {
        var repo = _repoService;
        if (repo == null)
        {
            ClearWorkspaceState();
            return Task.CompletedTask;
        }
        return RunBusyAsync("Refreshing...", () => ReloadCoreAsync(repo));
    }

    private async Task RefreshAfterExternalChangeAsync()
    {
        if (IsBusy)
        {
            _reloadRequested = true;
            return;
        }

        try
        {
            await ReloadWorkspaceStateAsync();
        }
        catch (Exception ex)
        {
            AddLog($"Refresh failed: {ex.Message}");
        }
    }

    private async Task ReloadCoreAsync(GitRepositoryService repo)
    {
        _markdownSyncTimer.Stop();
        _markdownSyncPending = false;

        var snapshot = await Task.Run(() => LoadSnapshot(repo, _bridgeServer));
        if (!ReferenceEquals(repo, _repoService))
        {
            return; // The workspace was closed or switched while loading.
        }
        ApplySnapshot(snapshot);
    }

    private static WorkspaceSnapshot LoadSnapshot(GitRepositoryService repo, LocalBridgeServer bridge)
    {
        WorkspaceSnapshot snapshot;
        lock (repo.SyncRoot)
        {
            var profile = repo.LoadFormattingProfile();
            var (branches, currentBranch) = repo.GetBranchesAndCurrent();

            var document = repo.LoadCurrentWorkingDocument();
            FormattingRuleEngine.ApplyFormattingRules(document, profile);
            string markdown = CriticMarkupSerializer.ToMarkdown(document, includeAnchorComments: false);

            // Load and verify the ledger once and share the result with the commit graph.
            var ledger = repo.LoadProvenanceLedger(document.DocumentId);
            var verification = ProvenanceChainService.VerifyChainIntegrity(ledger);
            var graph = repo.GetCommitGraph(ledger, verification);

            var target = graph.FirstOrDefault();
            var compareBase = graph.Count >= 2 ? graph[1] : target;
            var diff = ComputeDiff(repo, compareBase, target);

            snapshot = new WorkspaceSnapshot(profile, branches, currentBranch, document, markdown, ledger, verification,
                graph, compareBase, target, diff, []);
        }

        bridge.RefreshPendingPatches();
        return snapshot with { Patches = bridge.GetPendingPatches(), Paper = PaperViewModel.LoadSnapshot(repo.WorkspacePath) };
    }

    private void ApplySnapshot(WorkspaceSnapshot s)
    {
        _activeProfile = s.Profile;
        OnPropertyChanged(nameof(ActiveProfile));
        OnPropertyChanged(nameof(ActiveProfileName));

        _mainBranch = GitRepositoryService.ResolveMainBranchName(s.Branches);
        Branches.ReplaceAll(s.Branches);
        CurrentBranch = s.CurrentBranch;

        _currentDocument = s.Document;
        HasUncommittedChanges = false;
        DocumentTitle = s.Document.Title;
        _suppressMarkdownSync = true;
        RawMarkdownText = s.Markdown;
        _suppressMarkdownSync = false;
        RebuildFilteredHunks();

        _provenanceLedger = s.Ledger;
        ApplyVerificationResult(s.Verification);
        AuthorKeyFingerprint = s.Ledger.AuthorKeyFingerprint;

        string? selectedSha = SelectedCommit?.Sha;
        CommitGraph.ReplaceAll(s.Graph);
        SelectedCommit = s.Graph.FirstOrDefault(c => c.Sha == selectedSha);
        OnPropertyChanged(nameof(HasCommits));
        OnPropertyChanged(nameof(IsOnReviewBranch));

        // Replacing the graph clears the compare combo boxes; restore the pair computed with the snapshot
        // and drop any comparison still running for the old graph.
        _diffRequestId++;
        _compareBaseCommit = s.CompareBase;
        _compareTargetCommit = s.CompareTarget;
        OnPropertyChanged(nameof(CompareBaseCommit));
        OnPropertyChanged(nameof(CompareTargetCommit));
        ApplyDiff(s.Diff);

        Sentences.ReplaceAll(s.Ledger.Sentences);
        SelectedSentence = Sentences.FirstOrDefault();
        ChainBlocks.ReplaceAll(s.Ledger.Blocks);

        string? selectedPatchId = SelectedPatch?.PatchId;
        GoogleDocPatches.ReplaceAll(s.Patches);
        SelectedPatch = s.Patches.FirstOrDefault(p => p.PatchId == selectedPatchId) ?? s.Patches.FirstOrDefault();

        // The page needs figure numbers and citations, so the paper is loaded before it is drawn.
        if (s.Paper != null)
        {
            Paper.Apply(s.Paper);
        }
        DocumentRenderRequested?.Invoke();
        UpdateStatusBar();
    }

    private void ClearWorkspaceState()
    {
        Paper.Clear();
        _currentDocument = new ManuscriptDocument();
        HasUncommittedChanges = false;
        _provenanceLedger = new ProvenanceLedger();
        _chainVerification = new ChainVerificationResult { IsValid = true };
        _diffRequestId++;
        _compareBaseCommit = null;
        _compareTargetCommit = null;

        Branches.ReplaceAll([]);
        CommitGraph.ReplaceAll([]);
        Sentences.ReplaceAll([]);
        ChainBlocks.ReplaceAll([]);
        BlockStatuses.ReplaceAll([]);
        GoogleDocPatches.ReplaceAll([]);
        CurrentBranch = string.Empty;
        DocumentTitle = string.Empty;
        SelectedCommit = null;
        SelectedSentence = null;
        SelectedPatch = null;
        AuthorKeyFingerprint = string.Empty;
        IsChainValid = true;
        ChainVerificationBanner = "No workspace open";

        UpdateRawMarkdownFromDocument();
        RebuildFilteredHunks();
        OnPropertyChanged(nameof(HasCommits));
        OnPropertyChanged(nameof(IsOnReviewBranch));
        OnPropertyChanged(nameof(CompareBaseCommit));
        OnPropertyChanged(nameof(CompareTargetCommit));
        ApplyDiff(new DocumentDiffResult());
        DocumentRenderRequested?.Invoke();
        UpdateStatusBar();
    }

    private void UpdateRawMarkdownFromDocument()
    {
        _suppressMarkdownSync = true;
        RawMarkdownText = CriticMarkupSerializer.ToMarkdown(_currentDocument, includeAnchorComments: false);
        _suppressMarkdownSync = false;
    }

    /// <summary>
    /// Applies any Markdown source edits that are still waiting for the typing pause. Call before any action
    /// that reads the current document so it always sees the latest text.
    /// </summary>
    public void FlushPendingMarkdownSync()
    {
        // Edits on the Document page are applied first (the window applies them through this hook).
        FlushEditorEdits?.Invoke();

        _markdownSyncTimer.Stop();
        if (!_markdownSyncPending)
        {
            return;
        }

        _markdownSyncPending = false;
        SyncFromMarkdownEditor(_rawMarkdownText);
    }

    private void SyncFromMarkdownEditor(string newMarkdown)
    {
        var parsed = CriticMarkupSerializer.ParseMarkdown(newMarkdown, AuthorName);
        parsed.DocumentId = _currentDocument.DocumentId;
        parsed.GoogleDocId = _currentDocument.GoogleDocId;
        parsed.Title = string.IsNullOrWhiteSpace(parsed.Title) ? _currentDocument.Title : parsed.Title;

        // Preserve ParagraphIds where block index matches
        for (int i = 0; i < Math.Min(_currentDocument.Blocks.Count, parsed.Blocks.Count); i++)
        {
            parsed.Blocks[i].ParagraphId = _currentDocument.Blocks[i].ParagraphId;
        }

        FormattingRuleEngine.ApplyFormattingRules(parsed, _activeProfile);
        _currentDocument = parsed;
        HasUncommittedChanges = true;
        RebuildFilteredHunks();
        Paper.RebuildContext();
        DocumentRenderRequested?.Invoke();
        UpdateStatusBar();
        Paper.ScheduleChecks();
    }

    public void RebuildFilteredHunks()
    {
        string? selectedId = SelectedHunk?.HunkId;
        var items = new List<RevisionHunkItemViewModel>();
        foreach (var h in _currentDocument.Hunks)
        {
            bool match = SelectedHunkFilter switch
            {
                "Pending" => h.Status == RevisionHunkStatus.Pending,
                "Insertions" => h.HunkType == RevisionHunkType.Insertion,
                "Deletions" => h.HunkType == RevisionHunkType.Deletion,
                "Replacements" => h.HunkType == RevisionHunkType.Replacement,
                "Comments" => h.HunkType == RevisionHunkType.Comment,
                "Untracked" => h.HunkType == RevisionHunkType.UntrackedEdit,
                _ => true
            };

            if (match)
            {
                items.Add(new RevisionHunkItemViewModel(h));
            }
        }

        FilteredHunks.ReplaceAll(items);
        SelectedHunk = items.FirstOrDefault(h => h.HunkId == selectedId) ?? items.FirstOrDefault();

        OnPropertyChanged(nameof(PendingHunkCount));
        OnPropertyChanged(nameof(AcceptedHunkCount));
        OnPropertyChanged(nameof(RejectedHunkCount));
    }

    public void SelectParagraphHunks(string paragraphId)
    {
        var first = FilteredHunks.FirstOrDefault(h => h.ParagraphId == paragraphId);
        if (first != null)
        {
            SelectedHunk = first;
        }

        var sent = Sentences.FirstOrDefault(s => s.ParagraphId == paragraphId);
        if (sent != null)
        {
            SelectedSentence = sent;
        }
    }

    public void SetHunkStatus(RevisionHunkItemViewModel item, RevisionHunkStatus status)
    {
        item.Status = status;
        HasUncommittedChanges = true;
        UpdateRawMarkdownFromDocument();
        OnPropertyChanged(nameof(PendingHunkCount));
        OnPropertyChanged(nameof(AcceptedHunkCount));
        OnPropertyChanged(nameof(RejectedHunkCount));
        DocumentRenderRequested?.Invoke();
        UpdateStatusBar();
    }

    public void AcceptAllPendingHunks()
    {
        foreach (var h in _currentDocument.Hunks.Where(h => h.Status == RevisionHunkStatus.Pending))
        {
            h.Status = RevisionHunkStatus.Accepted;
            HasUncommittedChanges = true;
        }
        RebuildFilteredHunks();
        UpdateRawMarkdownFromDocument();
        DocumentRenderRequested?.Invoke();
        AddLog("Accepted all pending changes and comments.");
        UpdateStatusBar();
    }

    public void RejectAllPendingHunks()
    {
        foreach (var h in _currentDocument.Hunks.Where(h => h.Status == RevisionHunkStatus.Pending))
        {
            h.Status = RevisionHunkStatus.Rejected;
            HasUncommittedChanges = true;
        }
        RebuildFilteredHunks();
        UpdateRawMarkdownFromDocument();
        DocumentRenderRequested?.Invoke();
        AddLog("Rejected all pending changes.");
        UpdateStatusBar();
    }

    public string SuggestCommitMessage() => HasCommits
        ? $"Revise manuscript on '{CurrentBranch}'"
        : "Initial manuscript draft";

    public Task CommitCurrentChangesAsync(string commitMessage, AuthorshipOriginSource source = AuthorshipOriginSource.HandWrittenInWpf)
    {
        var repo = RequireWorkspace();
        FlushPendingMarkdownSync();

        var baked = CriticMarkupSerializer.BakeResolvedHunks(_currentDocument, acceptRemainingPending: false);
        string author = AuthorName;

        return RunBusyAsync("Committing...", async () =>
        {
            var (sha, block) = await Task.Run(() =>
            {
                lock (repo.SyncRoot)
                {
                    return repo.CommitSnapshot(baked, commitMessage, author, source);
                }
            });

            AddLog($"Committed '{commitMessage}' as {sha[..7]} (signed block #{block.BlockIndex}).");
            await ReloadCoreAsync(repo);
        });
    }

    public Task MergeReviewBranchToMainAsync()
    {
        var repo = RequireWorkspace();
        FlushPendingMarkdownSync();

        string reviewBranch = CurrentBranch;
        string mainBranch = _mainBranch;
        var resolvedDoc = CriticMarkupSerializer.BakeResolvedHunks(_currentDocument, acceptRemainingPending: true);
        string author = AuthorName;

        return RunBusyAsync("Merging...", async () =>
        {
            var outcome = await Task.Run(() =>
            {
                lock (repo.SyncRoot)
                {
                    return repo.MergeBranchIntoMain(
                        reviewBranch,
                        resolvedDoc,
                        $"Merge reviewed changes from '{reviewBranch}' into {mainBranch}",
                        author);
                }
            });

            string detail = outcome.Merge != null
                ? $" '{mainBranch}' had changed since the review started; {outcome.Merge.Summary}"
                : string.Empty;
            AddLog($"Merged '{reviewBranch}' into '{mainBranch}' as {outcome.CommitSha[..7]} (signed block #{outcome.SignedBlock.BlockIndex}).{detail}");
            LastMergeResult = outcome.Merge;
            await ReloadCoreAsync(repo);
        });
    }

    /// <summary>The three-way merge done by the last merge into main, if main had changed meanwhile.</summary>
    public Core.Merging.ManuscriptMergeResult? LastMergeResult { get; private set; }

    // ---- GitHub ----------------------------------------------------------------------------------------

    private string? _remoteUrl;
    /// <summary>The GitHub repository this workspace syncs with, or null.</summary>
    public string? RemoteUrl
    {
        get => _remoteUrl;
        private set
        {
            _remoteUrl = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasRemote));
            OnPropertyChanged(nameof(RemoteDisplayText));
        }
    }

    public bool HasRemote => !string.IsNullOrEmpty(_remoteUrl);

    public string RemoteDisplayText => HasRemote ? $"GitHub: {ShortRemoteName(_remoteUrl!)}" : "Not connected to GitHub";

    private static string ShortRemoteName(string url)
    {
        string trimmed = url.Trim().TrimEnd('/');
        if (trimmed.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[..^4];
        }
        int host = trimmed.IndexOf("github.com", StringComparison.OrdinalIgnoreCase);
        return host >= 0 ? trimmed[(host + "github.com".Length)..].TrimStart('/', ':') : trimmed;
    }

    private bool _hasUncommittedChanges;
    /// <summary>True when the manuscript was edited since it was last loaded or committed.</summary>
    public bool HasUncommittedChanges
    {
        get => _hasUncommittedChanges;
        private set
        {
            if (_hasUncommittedChanges != value)
            {
                _hasUncommittedChanges = value;
                OnPropertyChanged();
            }
        }
    }

    public Task ConnectToGitHubAsync(string url)
    {
        var repo = RequireWorkspace();
        return RunBusyAsync("Connecting to GitHub...", async () =>
        {
            await Task.Run(() =>
            {
                lock (repo.SyncRoot)
                {
                    repo.SetRemoteUrl(url.Trim());
                }
            });
            RemoteUrl = url.Trim();
            AddLog($"Connected the workspace to '{RemoteUrl}'.");
        });
    }

    /// <summary>
    /// Sends this computer's commits to GitHub and takes those made elsewhere; branches changed on both
    /// sides are merged paragraph by paragraph. Commit local edits first.
    /// </summary>
    public async Task<SyncReport> SyncWithGitHubAsync()
    {
        var repo = RequireWorkspace();
        FlushPendingMarkdownSync();
        if (HasUncommittedChanges)
        {
            throw new InvalidOperationException("Commit your changes before syncing.");
        }

        string author = AuthorName;
        SyncReport report = new();
        await RunBusyAsync("Syncing with GitHub...", async () =>
        {
            report = await Task.Run(() =>
            {
                lock (repo.SyncRoot)
                {
                    return repo.SyncWithRemote(author);
                }
            });

            foreach (string line in report.Lines)
            {
                AddLog(line);
            }
            AddLog($"Synced with GitHub: {report.Summary}");
            await ReloadCoreAsync(repo);
        });
        return report;
    }

    /// <summary>Downloads a workspace from GitHub into <paramref name="folder"/> and opens it.</summary>
    public Task CloneFromGitHubAsync(string url, string folder) => RunBusyAsync("Downloading from GitHub...", async () =>
    {
        var repo = await Task.Run(() => GitRepositoryService.CloneFromRemote(url.Trim(), folder));
        AddLog($"Downloaded '{url.Trim()}' into '{repo.WorkspacePath}'.");
        await OpenCoreAsync(repo);
    });

    // ---- Importing reviewed Word documents ---------------------------------------------------------------

    /// <summary>A committed version a reviewed document may have started from.</summary>
    public sealed record ImportBaseOption(GitCommitNode Commit, double Score)
    {
        public string Label => $"{Commit.ShortSha}  {Commit.Timestamp.ToLocalTime():g}  {Commit.Message}  ({Score:P0} match)";
    }

    /// <summary>
    /// Finds which committed version a reviewed Word document was made from, by comparing the text it had
    /// before its tracked changes with every signed snapshot. Best match first.
    /// </summary>
    public async Task<List<ImportBaseOption>> FindImportBaseOptionsAsync(string docxFilePath)
    {
        var repo = RequireWorkspace();
        var profile = _activeProfile.Clone();
        var blocks = _provenanceLedger.Blocks.ToList();
        var commits = CommitGraph.ToDictionary(c => c.Sha, StringComparer.OrdinalIgnoreCase);

        List<ImportBaseOption> options = [];
        await RunBusyAsync("Reading Word document...", async () =>
        {
            options = await Task.Run(() =>
            {
                Core.Scholarly.TitlePageFilter? titlePage;
                lock (repo.SyncRoot)
                {
                    titlePage = TitlePageFilterFor(repo, null);
                }
                var reviewed = DocxIngestionService.IngestDocx(docxFilePath, null, profile, titlePage);
                var original = Core.Merging.ImportBaseDetector.OriginalVersionOf(reviewed);
                return Core.Merging.ImportBaseDetector.Rank(original, blocks)
                    .Where(c => commits.ContainsKey(c.CommitSha))
                    .Select(c => new ImportBaseOption(commits[c.CommitSha], c.Score))
                    .ToList();
            });
        });
        return options;
    }

    /// <summary>The newest commit of the main branch.</summary>
    public GitCommitNode? MainBranchTip => CommitGraph.FirstOrDefault(c => c.BranchNames.Contains(_mainBranch, StringComparer.Ordinal));

    /// <summary>
    /// Imports a Word document. In an empty workspace it becomes the initial manuscript on the main branch;
    /// otherwise it is treated as a reviewer's copy and committed to a new review branch that starts from
    /// <paramref name="baseCommitSha"/>, the version the reviewer was sent (by default the latest on main).
    /// Merging that branch later keeps everything done on main since then.
    /// </summary>
    public Task ImportDocxAsync(string docxFilePath, string? baseCommitSha = null)
    {
        var repo = RequireWorkspace();
        FlushPendingMarkdownSync();

        string fileName = Path.GetFileName(docxFilePath);
        bool isInitialImport = !HasCommits;
        string mainBranch = _mainBranch;
        string author = AuthorName;
        var profile = _activeProfile.Clone();

        return RunBusyAsync("Importing Word document...", async () =>
        {
            string? titlePageNote = null;
            string logMessage = await Task.Run(() =>
            {
                lock (repo.SyncRoot)
                {
                    if (isInitialImport)
                    {
                        var initialObjects = new Core.Scholarly.ImportedObjects(null, repo.WorkspacePath);
                        var initial = DocxIngestionService.IngestDocx(docxFilePath, null, profile, TitlePageFilterFor(repo, null), initialObjects);
                        initialObjects.WriteTo(repo.WorkspacePath);
                        var (initialSha, initialBlock) = repo.CommitSnapshot(
                            initial,
                            $"Import manuscript from '{fileName}'",
                            author,
                            AuthorshipOriginSource.InitialSeed);
                        return $"Imported '{fileName}' as the initial manuscript ({initialSha[..7]}, signed block #{initialBlock.BlockIndex}).";
                    }

                    // Untracked edits are found by comparing with the version the reviewer started from.
                    string baseRef = baseCommitSha ?? mainBranch;
                    var baselineDoc = repo.LoadDocumentAtCommit(baseRef);
                    var titlePage = TitlePageFilterFor(repo, baselineDoc);
                    // Pictures and tables that are not the export's own become figure and table blocks.
                    var objects = new Core.Scholarly.ImportedObjects(baselineDoc, repo.WorkspacePath);
                    var ingested = DocxIngestionService.IngestDocx(docxFilePath, baselineDoc, profile, titlePage, objects);
                    objects.WriteTo(repo.WorkspacePath);
                    titlePageNote = titlePage?.Summary;

                    string reviewer = ingested.Hunks
                        .GroupBy(h => h.Author)
                        .OrderByDescending(g => g.Count())
                        .Select(g => g.Key)
                        .FirstOrDefault(a => !string.IsNullOrWhiteSpace(a)) ?? "Reviewer";
                    string branchName = $"review/{ToBranchSlug(reviewer)}-{DateTime.Now:yyyyMMdd-HHmm}";

                    repo.CreateAndCheckoutBranch(branchName, baseRef);
                    var (sha, block) = repo.CommitSnapshot(
                        ingested,
                        $"Import reviewed document '{fileName}' ({ingested.Hunks.Count} changes)",
                        reviewer,
                        AuthorshipOriginSource.SupervisorTrackedEdit);
                    string fromNote = baseCommitSha != null ? $", based on {baseCommitSha[..7]}" : string.Empty;
                    return $"Imported '{fileName}' onto branch '{branchName}' ({ingested.Hunks.Count} changes{fromNote}, {sha[..7]}, signed block #{block.BlockIndex}).";
                }
            });

            AddLog(logMessage);
            if (titlePageNote != null)
            {
                AddLog(titlePageNote);
            }
            await ReloadCoreAsync(repo);
        });
    }

    /// <summary>
    /// Recognises the title page from the form in a reviewed copy that lost the export's markers. In an empty
    /// workspace the current manuscript is empty, so any title page matching the form is left out.
    /// </summary>
    private static Core.Scholarly.TitlePageFilter? TitlePageFilterFor(GitRepositoryService repo, ManuscriptDocument? manuscript)
        => Core.Scholarly.TitlePageFilter.For(
            Core.Scholarly.ScholarlyProject.Load(repo.WorkspacePath),
            manuscript ?? (repo.HasCommits() ? repo.LoadCurrentWorkingDocument() : null));

    private static string ToBranchSlug(string name)
    {
        var sb = new StringBuilder();
        foreach (char c in name.ToLowerInvariant())
        {
            if (char.IsAsciiLetterOrDigit(c))
            {
                sb.Append(c);
            }
            else if (sb.Length > 0 && sb[^1] != '-')
            {
                sb.Append('-');
            }
        }
        string slug = sb.ToString().Trim('-');
        return slug.Length == 0 ? "reviewer" : slug;
    }

    public void ExportDocxFile(string outputFilePath, bool includeTrackChanges)
    {
        FlushPendingMarkdownSync();

        // Apply accepted and rejected changes first so only still-pending changes are exported as revisions.
        var resolved = CriticMarkupSerializer.BakeResolvedHunks(_currentDocument, acceptRemainingPending: false);
        // A working copy (for co-authors), so it carries the draft notes; the journal exports leave them out.
        var options = ExportOptions(includeTrackChanges);
        options.IncludeDraftNotes = true;
        DocxExportService.Export(resolved, outputFilePath, options);
        AddLog($"Exported Word document ('{_activeProfile.ProfileName}') to '{outputFilePath}'.");
    }

    public Task CheckoutCommitOrBranchAsync(string refOrSha)
    {
        var repo = RequireWorkspace();
        return RunBusyAsync("Checking out...", async () =>
        {
            await Task.Run(() =>
            {
                lock (repo.SyncRoot)
                {
                    repo.CheckoutBranch(refOrSha);
                }
            });
            AddLog($"Checked out '{refOrSha}'.");
            await ReloadCoreAsync(repo);
        });
    }

    public void ApplyFormattingRulesNow()
    {
        var repo = RequireWorkspace();
        FlushPendingMarkdownSync();

        // Formatting never touches the canonical text, so the provenance chain does not need re-verifying here.
        repo.SaveFormattingProfile(_activeProfile);
        FormattingRuleEngine.ApplyFormattingRules(_currentDocument, _activeProfile);
        UpdateRawMarkdownFromDocument();
        DocumentRenderRequested?.Invoke();
        UpdateStatusBar();
        AddLog($"Applied formatting rules '{_activeProfile.ProfileName}'.");
    }

    public Task VerifyBlockchainIntegrityNowAsync()
    {
        var repo = RequireWorkspace();
        string documentId = _currentDocument.DocumentId;
        return RunBusyAsync("Verifying provenance chain...", async () =>
        {
            var (ledger, result) = await Task.Run(() =>
            {
                lock (repo.SyncRoot)
                {
                    var loaded = repo.LoadProvenanceLedger(documentId);
                    return (loaded, ProvenanceChainService.VerifyChainIntegrity(loaded));
                }
            });

            _provenanceLedger = ledger;
            ApplyVerificationResult(result);
            ChainBlocks.ReplaceAll(ledger.Blocks);
            AddLog(result.SummaryMessage);
        });
    }

    private void ApplyVerificationResult(ChainVerificationResult result)
    {
        _chainVerification = result;
        IsChainValid = result.IsValid;
        ChainVerificationBanner = result.SummaryMessage;
        BlockStatuses.ReplaceAll(result.BlockStatuses);
        UpdateStatusBar();
    }

    /// <summary>
    /// Non-destructively simulates an attacker modifying a sentence in an earlier commit block in memory
    /// and runs VerifyChainIntegrity() to demonstrate immediate Merkle Root & ECDSA tamper detection.
    /// </summary>
    public ChainVerificationResult RunNonDestructiveTamperSimulation()
    {
        var copyJson = System.Text.Json.JsonSerializer.Serialize(_provenanceLedger);
        var clonedLedger = System.Text.Json.JsonSerializer.Deserialize<ProvenanceLedger>(copyJson)!;

        if (clonedLedger.Blocks.Count > 0)
        {
            // Modify Block #0's historical snapshot without re-signing it with the author's private key
            clonedLedger.Blocks[0].CanonicalManuscriptSnapshot += " [modified]";
        }

        var tamperResult = ProvenanceChainService.VerifyChainIntegrity(clonedLedger);
        AddLog($"Tamper detection test on an in-memory copy: {tamperResult.SummaryMessage}");
        return tamperResult;
    }

    public string ExportAuditCertificateToFile(string outputHtmlPath)
    {
        FlushPendingMarkdownSync();
        string html = ProvenanceChainService.ExportAuditCertificateHtml(_provenanceLedger, _currentDocument);
        File.WriteAllText(outputHtmlPath, html);
        AddLog($"Exported provenance certificate to '{outputHtmlPath}'.");
        return outputHtmlPath;
    }

    /// <summary>
    /// Sets both comparison commits and recomputes the diff once.
    /// </summary>
    public void SetComparePair(GitCommitNode? baseCommit, GitCommitNode? targetCommit)
    {
        _compareBaseCommit = baseCommit;
        _compareTargetCommit = targetCommit;
        OnPropertyChanged(nameof(CompareBaseCommit));
        OnPropertyChanged(nameof(CompareTargetCommit));
        RecomputeVersionDiffInBackground();
    }

    private static DocumentDiffResult ComputeDiff(GitRepositoryService repo, GitCommitNode? baseCommit, GitCommitNode? targetCommit)
    {
        if (baseCommit == null || targetCommit == null)
        {
            return new DocumentDiffResult();
        }

        // Documents at a commit are cached by the repository service, so switching comparisons is cheap.
        return DiffEngineService.ComputeDocumentDiff(
            repo.LoadDocumentAtCommit(baseCommit.Sha),
            repo.LoadDocumentAtCommit(targetCommit.Sha),
            $"{baseCommit.ShortSha} ({baseCommit.Message})",
            $"{targetCommit.ShortSha} ({targetCommit.Message})");
    }

    private async void RecomputeVersionDiffInBackground()
    {
        int requestId = ++_diffRequestId;
        var repo = _repoService;
        var baseCommit = _compareBaseCommit;
        var targetCommit = _compareTargetCommit;

        if (repo == null || baseCommit == null || targetCommit == null)
        {
            ApplyDiff(new DocumentDiffResult());
            return;
        }

        try
        {
            var diff = await Task.Run(() => ComputeDiff(repo, baseCommit, targetCommit));
            if (requestId == _diffRequestId)
            {
                ApplyDiff(diff);
            }
        }
        catch (Exception ex)
        {
            AddLog($"Compare failed: {ex.Message}");
        }
    }

    private void ApplyDiff(DocumentDiffResult diff)
    {
        _currentDiffResult = diff;
        DiffEntries.ReplaceAll(diff.Entries);
        OnPropertyChanged(nameof(CurrentDiffResult));
        OnPropertyChanged(nameof(DiffSummary));
        DiffRenderRequested?.Invoke();
    }

    public void MarkGoogleDocPatchApplied(GoogleDocPatch patch)
    {
        _bridgeServer.AcknowledgePatches([patch.PatchId]);
        GoogleDocPatches.ReplaceAll(_bridgeServer.GetPendingPatches());
        SelectedPatch = patch;
    }

    public void AddLog(string message)
    {
        ActivityLogs.Insert(0, $"[{DateTime.Now:HH:mm:ss}] {message}");
        while (ActivityLogs.Count > MaxActivityLogEntries)
        {
            ActivityLogs.RemoveAt(ActivityLogs.Count - 1);
        }
    }

    private GitRepositoryService RequireWorkspace()
        => _repoService ?? throw new InvalidOperationException("Open a workspace first.");

    internal void UpdateStatusBar()
    {
        if (!HasWorkspace)
        {
            StatusBarSummary = "Ready";
            return;
        }

        // Words as published (citations and numbers rendered), once the paper's context is known.
        int words = Paper.Statistics is { } stats
            ? stats.Total.Words - stats.References.Words
            : _currentDocument.Blocks.Sum(b => Core.Checks.ManuscriptStatistics.CountWords(CriticMarkupSerializer.ResolveCriticMarkupText(b.Text, MarkupDisplayMode.NoMarkupFinal)));
        StatusBarSummary =
            $"{CurrentBranch}  |  {words:N0} words  |  {_currentDocument.Blocks.Count} paragraphs  |  {PendingHunkCount} pending changes";
    }

    private string _editorPositionText = string.Empty;
    /// <summary>The cursor's line on the Document page and the words selected, set by the window.</summary>
    public string EditorPositionText
    {
        get => _editorPositionText;
        set
        {
            if (_editorPositionText != value)
            {
                _editorPositionText = value;
                OnPropertyChanged();
            }
        }
    }

    public void Dispose()
    {
        _markdownSyncTimer.Stop();
        _bridgeServer.Dispose();
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? prop = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(prop));
}
