using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using RevisionsEditor.App.Rendering;
using RevisionsEditor.App.ViewModels;
using RevisionsEditor.Core.Models;

namespace RevisionsEditor.App;

public partial class MainWindow : Window
{
    private const int DocumentTabIndex = 0;
    private const int SourceTabIndex = 1;
    private const int CompareTabIndex = 2;
    private const int FormattingTabIndex = 4;

    private readonly AppSettings _settings = AppSettings.Load();
    private bool _manuscriptDirty = true;
    private bool _diffDirty = true;

    public MainViewModel ViewModel { get; }

    /// <summary>
    /// Workspace to open once the window has rendered; defaults to the most recently used one.
    /// </summary>
    public string? StartupWorkspace { get; set; }

    public MainWindow()
    {
        InitializeComponent();
        ViewModel = new MainViewModel();
        DataContext = ViewModel;

        ViewModel.DocumentRenderRequested += () =>
        {
            _manuscriptDirty = true;
            if (IsPanelShown(DocumentTabIndex))
            {
                RenderManuscriptView();
            }
        };

        ViewModel.DiffRenderRequested += () =>
        {
            _diffDirty = true;
            if (IsPanelShown(CompareTabIndex))
            {
                RenderDiffView();
            }
        };

        ViewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.IsBusy))
            {
                // Re-evaluate every command's enabled state as soon as a background operation starts or ends.
                CommandManager.InvalidateRequerySuggested();
            }
        };

        ViewModel.FlushEditorEdits = FlushEditorEdits;
        DataObject.AddPastingHandler(ManuscriptEditor, OnEditorPasting);

        RegisterCommands();
        InitializeTools();
        InitializeFind();
        InitializeNotes();
        InitializeLatex();
        InitializeDocking();
        ContentRendered += OnFirstContentRendered;
    }

    private async void OnFirstContentRendered(object? sender, EventArgs e)
    {
        ContentRendered -= OnFirstContentRendered;
        if (ViewModel.HasWorkspace || ViewModel.IsBusy)
        {
            return;
        }

        // Opening a workspace runs Git, so do it after the window is on screen rather than before.
        // Tests set UseSavedLayout to false: they must never open the user's own workspaces.
        string? path = StartupWorkspace ?? (UseSavedLayout ? _settings.RecentWorkspaces.FirstOrDefault(Directory.Exists) : null);
        if (path != null && Directory.Exists(path))
        {
            await OpenWorkspaceAsync(path);
        }
        else
        {
            FlushPendingTabRenders();
        }
    }


    /// <summary>
    /// Opens a workspace and records it in the recent list. Returns false if opening failed.
    /// </summary>
    public async Task<bool> OpenWorkspaceAsync(string path, bool addToRecent = true)
    {
        bool opened = await RunAsync(() => ViewModel.OpenWorkspaceAsync(path), "Open Workspace");
        if (opened && addToRecent)
        {
            _settings.AddRecentWorkspace(ViewModel.WorkspacePath);
            _settings.Save();
        }
        FlushPendingTabRenders();
        return opened;
    }

    private void RegisterCommands()
    {
        Bind(AppCommands.OpenWorkspace, _ => PromptOpenWorkspace());
        Bind(AppCommands.OpenRecentWorkspace, p => OpenRecentWorkspace(p as string), p => p is string);
        Bind(AppCommands.CloseWorkspace, _ => ViewModel.CloseWorkspace(), _ => ViewModel.HasWorkspace);
        Bind(AppCommands.ImportDocx, _ => PromptImportDocx(), _ => ViewModel.HasWorkspace);
        Bind(AppCommands.ExportCleanDocx, _ => PromptExportDocx(includeTrackChanges: false), _ => CanExportDocument());
        Bind(AppCommands.ExportTrackedDocx, _ => PromptExportDocx(includeTrackChanges: true), _ => CanExportDocument());
        Bind(AppCommands.ExportCertificate, _ => PromptExportCertificate(), _ => ViewModel.HasCommits);
        Bind(AppCommands.Exit, _ => Close(), allowWhileBusy: true);

        Bind(AppCommands.InsertCriticMarkup, p => InsertCriticMarkup(p as string), _ => ViewModel.HasWorkspace);
        Bind(AppCommands.SetMarkupMode, p =>
        {
            if (p is MarkupDisplayMode mode)
            {
                ViewModel.SelectedMarkupMode = mode;
                SelectWorkspaceTab(DocumentTabIndex);
            }
        }, _ => ViewModel.HasWorkspace);

        Bind(AppCommands.Commit, _ => PromptCommit(), _ => ViewModel.HasWorkspace);
        Bind(AppCommands.MergeIntoMain, _ => ConfirmAndMerge(), _ => ViewModel.IsOnReviewBranch);
        Bind(AppCommands.CheckoutBranch,
            _ => _ = RunAsync(() => ViewModel.CheckoutCommitOrBranchAsync((string)BranchSelectorCombo.SelectedItem), "Check Out Branch"),
            _ => ViewModel.HasCommits
                && BranchSelectorCombo.SelectedItem is string branch
                && !string.Equals(branch, ViewModel.CurrentBranch, StringComparison.Ordinal));
        Bind(AppCommands.CheckoutCommit,
            _ => _ = RunAsync(() => ViewModel.CheckoutCommitOrBranchAsync(ViewModel.SelectedCommit!.Sha), "Check Out Commit"),
            _ => ViewModel.SelectedCommit != null);
        Bind(AppCommands.CompareWithHead, _ => CompareSelectedWithLatest(), _ => ViewModel.SelectedCommit != null);

        Bind(AppCommands.SyncWithGitHub, _ => SyncWithGitHub(), _ => ViewModel.HasWorkspace);
        Bind(AppCommands.ConnectGitHub, _ => PromptConnectGitHub(), _ => ViewModel.HasWorkspace);
        Bind(AppCommands.CloneFromGitHub, _ => PromptCloneFromGitHub());

        Bind(AppCommands.AcceptChange, _ => SetSelectedHunkStatus(RevisionHunkStatus.Accepted), _ => ViewModel.SelectedHunk != null);
        Bind(AppCommands.RejectChange, _ => SetSelectedHunkStatus(RevisionHunkStatus.Rejected), _ => ViewModel.SelectedHunk != null);
        Bind(AppCommands.ResetChange, _ => SetSelectedHunkStatus(RevisionHunkStatus.Pending),
            _ => ViewModel.SelectedHunk is { Status: not RevisionHunkStatus.Pending });
        Bind(AppCommands.AcceptAllChanges, _ => Run(ViewModel.AcceptAllPendingHunks), _ => ViewModel.PendingHunkCount > 0);
        Bind(AppCommands.RejectAllChanges, _ => Run(ViewModel.RejectAllPendingHunks), _ => ViewModel.PendingHunkCount > 0);

        Bind(AppCommands.VerifyChain, _ => _ = RunAsync(ViewModel.VerifyBlockchainIntegrityNowAsync, "Verify Provenance Chain"), _ => ViewModel.HasWorkspace);
        Bind(AppCommands.TestTamperDetection, _ => RunTamperDetectionTest(), _ => ViewModel.ChainBlocks.Count > 0);
        Bind(AppCommands.ApplyFormatting, _ => Run(ViewModel.ApplyFormattingRulesNow, "Apply Formatting Rules"), _ => ViewModel.HasWorkspace);

        Bind(AppCommands.CopyPatchText, _ => Clipboard.SetText(ViewModel.SelectedPatch!.AfterText), _ => ViewModel.SelectedPatch != null);
        Bind(AppCommands.MarkPatchApplied, _ => ViewModel.MarkGoogleDocPatchApplied(ViewModel.SelectedPatch!),
            _ => ViewModel.SelectedPatch is { AppliedInGoogleDocs: false });

        Bind(AppCommands.About, _ => ShowAbout(), allowWhileBusy: true);
        RegisterScholarlyCommands();
    }

    /// <param name="allowWhileBusy">Commands that never touch the repository stay available during background work.</param>
    private void Bind(RoutedUICommand command, Action<object?> execute, Func<object?, bool>? canExecute = null, bool allowWhileBusy = false)
    {
        var binding = new CommandBinding(command, (_, e) => execute(e.Parameter));
        binding.CanExecute += (_, e) =>
            e.CanExecute = (allowWhileBusy || !ViewModel.IsBusy) && (canExecute?.Invoke(e.Parameter) ?? true);
        CommandBindings.Add(binding);
    }

    /// <summary>
    /// Runs a quick, UI-thread user action and reports failures in a message box instead of terminating the app.
    /// Returns true when the action completed.
    /// </summary>
    private bool Run(Action action, string title = "Revisions Editor")
    {
        try
        {
            ViewModel.FlushPendingMarkdownSync();
            action();
            return true;
        }
        catch (Exception ex)
        {
            ReportFailure(title, ex);
            return false;
        }
    }

    /// <summary>
    /// Runs a repository operation. The view model performs the Git work on a background thread, so the
    /// window stays responsive; failures (for example a Git error) are reported in a message box.
    /// </summary>
    private async Task<bool> RunAsync(Func<Task> operation, string title)
    {
        try
        {
            ViewModel.FlushPendingMarkdownSync();
            await operation();
            return true;
        }
        catch (Exception ex)
        {
            ReportFailure(title, ex);
            return false;
        }
        finally
        {
            CommandManager.InvalidateRequerySuggested();
        }
    }

    private void ReportFailure(string title, Exception ex)
    {
        ViewModel.AddLog($"{title} failed: {ex.Message}");
        MessageBox.Show(this, ex.Message, title, MessageBoxButton.OK, MessageBoxImage.Error);
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        // Keep what was being typed: a title-page cell still being edited, a box whose text is applied when it
        // loses focus, and edits waiting for the pause before they are saved.
        AuthorsGrid.CommitEdit(DataGridEditingUnit.Row, exitEditingMode: true);
        AffiliationsGrid.CommitEdit(DataGridEditingUnit.Row, exitEditingMode: true);
        (Keyboard.FocusedElement as TextBox)?.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
        ViewModel.Paper.FlushFrontMatter();

        // Edits to the manuscript are kept by committing them: offer to before they are lost.
        if (!_closeConfirmed && !App.IsRenderingScreenshots && ViewModel.HasWorkspace && !ViewModel.IsBusy)
        {
            ViewModel.FlushPendingMarkdownSync();
            if (ViewModel.HasUncommittedChanges)
            {
                var answer = MessageBox.Show(this,
                    "The manuscript has changes that are not committed yet; closing now loses them.\n\nCommit them before closing?",
                    "Revisions Editor", MessageBoxButton.YesNoCancel, MessageBoxImage.Warning);
                if (answer == MessageBoxResult.Cancel)
                {
                    e.Cancel = true;
                    return;
                }
                if (answer == MessageBoxResult.Yes)
                {
                    e.Cancel = true;
                    Dispatcher.BeginInvoke(CommitThenClose);
                    return;
                }
            }
        }
        base.OnClosing(e);
    }

    private bool _closeConfirmed;

    private async void CommitThenClose()
    {
        var dialog = new CommitDialog(ViewModel.SuggestCommitMessage(), ViewModel.CurrentBranch, ViewModel.AuthorName) { Owner = this };
        if (dialog.ShowDialog() == true && await RunAsync(() => ViewModel.CommitCurrentChangesAsync(dialog.CommitMessage), "Commit"))
        {
            _closeConfirmed = true;
            Close();
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        SaveLayout();
        ViewModel.Dispose();
        base.OnClosed(e);
    }


    private void FlushPendingTabRenders()
    {
        // Leaving an editor applies any edits still waiting for the typing pause.
        if (!IsPanelShown(DocumentTabIndex))
        {
            FlushEditorEdits();
        }
        if (!IsPanelShown(SourceTabIndex))
        {
            ViewModel.FlushPendingMarkdownSync();
        }

        // Views on screen side by side are each kept up to date.
        if (IsPanelShown(DocumentTabIndex) && _manuscriptDirty)
        {
            RenderManuscriptView();
        }
        if (IsPanelShown(CompareTabIndex) && _diffDirty)
        {
            RenderDiffView();
        }
    }

    // ---- Document editor ------------------------------------------------------------------------------

    // Reading the page back and updating the Markdown, change list and status bar on every keystroke would
    // make typing lag on long manuscripts, so page edits are applied once typing pauses.
    private static readonly TimeSpan EditorSyncDelay = TimeSpan.FromMilliseconds(400);
    private DispatcherTimer? _editorSyncTimer;
    private bool _editorDirty;
    private bool _renderingEditor;
    private bool _updatingEditorToolbar;
    private List<DocumentBlock> _blocksAtRender = [];
    private HashSet<string> _lockedIdsAtRender = [];

    private void RenderManuscriptView() => RenderManuscriptView(preserveCaret: false);

    /// <summary>
    /// Rebuilds the page from the manuscript. Unapplied page edits are discarded, so callers that start from
    /// a user action flush them first (the view model does this before every operation).
    /// </summary>
    private void RenderManuscriptView(bool preserveCaret)
    {
        _manuscriptDirty = false;
        var caret = preserveCaret ? CaptureCaret() : null;
        var caretParagraph = CaretParagraph;
        // Assigning a new document scrolls the editor to the top; inserting a figure or fixing an issue should
        // leave the page where it was.
        double scrollOffset = ManuscriptEditor.VerticalOffset;
        bool updatedInPlace = false;

        _renderingEditor = true;
        try
        {
            if (_preview)
            {
                RenderPreview();
            }
            var document = FlowDocumentBuilder.BuildManuscriptEditorDocument(
                ViewModel.CurrentDocument,
                ViewModel.ActiveProfile,
                ViewModel.SelectedMarkupMode,
                paragraphId => ViewModel.SelectParagraphHunks(paragraphId),
                ViewModel.Paper.Context,
                ViewModel.WorkspacePath,
                paragraphId => Dispatcher.BeginInvoke(() => EditScholarlyBlock(paragraphId)),
                onFrontMatterActivated: () => Dispatcher.BeginInvoke(OpenTitlePageForm));
            // RichTextBox replaces the document's page padding when it is assigned, so set the page margins
            // afterwards. (Its own Padding would be painted with the theme's fill, grey in dark mode.)
            var margins = document.PagePadding;
            // Most edits change a block or two: replace only those, so the page does not lay itself out again
            // and the caret, selection and scroll position stay where they are.
            PageReconciler.WorkspacePath = ViewModel.WorkspacePath;
            if (ManuscriptEditor.Document is { Blocks.Count: > 0 } live && live != document)
            {
                // As with a new document, the page's own undo history does not reach back past a redraw.
                ManuscriptEditor.IsUndoEnabled = false;
                ManuscriptEditor.BeginChange();
                try
                {
                    updatedInPlace = PageReconciler.TryUpdate(live, document, out _);
                }
                finally
                {
                    ManuscriptEditor.EndChange();
                    ManuscriptEditor.IsUndoEnabled = true;
                }
            }
            if (!updatedInPlace)
            {
                ManuscriptEditor.Document = document;
                document.PagePadding = margins;
            }
            var shown = ManuscriptEditor.Document;

            _blocksAtRender = ViewModel.CurrentDocument.Blocks.Select(b => b.Clone()).ToList();
            // Paragraphs with pending changes are put back if deleted by accident; figures, tables and
            // equations may be deleted on the page like any paragraph.
            _lockedIdsAtRender = shown.Blocks
                .OfType<BlockUIContainer>()
                .Select(b => b.Tag as EditorBlockTag)
                .Where(t => t?.LockedBlock != null && t.Role is not (BlockStyleRole.Figure or BlockStyleRole.Table or BlockStyleRole.Equation))
                .Select(t => t!.LockedBlock!.ParagraphId)
                .ToHashSet(StringComparer.Ordinal);
            EmptyEditorHint.Margin = margins;
        }
        finally
        {
            _renderingEditor = false;
        }

        _editorDirty = false;
        _editorSyncTimer?.Stop();
        // Updated in place, the caret stays in its paragraph unless that paragraph was replaced.
        bool caretKept = updatedInPlace && caretParagraph != null && ManuscriptEditor.Document.Blocks.Contains(caretParagraph);
        if (caret is { } position && !caretKept)
        {
            RestoreCaret(position);
        }
        if (scrollOffset > 0 && !updatedInPlace)
        {
            // After the new page is laid out (and after the caret, which would scroll to itself).
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => ManuscriptEditor.ScrollToVerticalOffset(scrollOffset));
        }
        UpdateEditorHint();
        UpdateEditorToolbar();
        ScheduleFindRefresh(); // the page is new: an open search runs again on it
    }

    private void OnEditorTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_renderingEditor)
        {
            return;
        }

        _editorDirty = true;
        if (_editorSyncTimer == null)
        {
            _editorSyncTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = EditorSyncDelay };
            _editorSyncTimer.Tick += (_, _) => FlushEditorEdits();
        }
        _editorSyncTimer.Stop();
        _editorSyncTimer.Start();
        UpdateEditorHint();
        ScheduleFindRefresh();
    }

    /// <summary>
    /// Applies page edits to the manuscript. Paragraphs with pending review changes are read-only, but a
    /// selection that spans one can still delete it; such paragraphs are put back, so review changes are
    /// never lost by accident.
    /// </summary>
    private void FlushEditorEdits()
    {
        _editorSyncTimer?.Stop();
        if (!_editorDirty || !ViewModel.HasWorkspace)
        {
            _editorDirty = false;
            return;
        }
        _editorDirty = false;

        var result = ManuscriptEditorReader.Read(ManuscriptEditor.Document, ViewModel.UsedParagraphIds);
        var missingLocked = _lockedIdsAtRender.Where(id => !result.LockedParagraphIds.Contains(id)).ToList();
        if (missingLocked.Count == 0)
        {
            ViewModel.ApplyEditedBlocks(result.Blocks, result.LockedParagraphIds);
            return;
        }

        var blocks = result.Blocks;
        foreach (string id in missingLocked)
        {
            int previousIndex = _blocksAtRender.FindIndex(b => b.ParagraphId == id) - 1;
            int insertAt = 0;
            for (int k = previousIndex; k >= 0; k--)
            {
                int found = blocks.FindIndex(b => b.ParagraphId == _blocksAtRender[k].ParagraphId);
                if (found >= 0)
                {
                    insertAt = found + 1;
                    break;
                }
            }
            blocks.Insert(insertAt, _blocksAtRender.First(b => b.ParagraphId == id).Clone());
        }

        var locked = new HashSet<string>(result.LockedParagraphIds.Concat(missingLocked), StringComparer.Ordinal);
        ViewModel.ApplyEditedBlocks(blocks, locked);
        RenderManuscriptView(preserveCaret: true);
        bool onlyTitle = missingLocked.All(id => _blocksAtRender.FirstOrDefault(b => b.ParagraphId == id)?.Role == BlockStyleRole.Title);
        ShowEditorNotice(onlyTitle
            ? "The title was kept: it is edited in the Paper panel's title page form."
            : "Paragraphs with pending changes were kept. Accept or reject their changes before removing them.");
    }

    /// <summary>Shows the Paper panel with the title page form, the title box focused.</summary>
    private void OpenTitlePageForm()
    {
        FlushEditorEdits();
        ShowPanel(PaperPanel.ContentId);
        Dispatcher.BeginInvoke(() =>
        {
            TitleBox.Focus();
            TitleBox.SelectAll();
            TitleBox.BringIntoView();
        }, System.Windows.Threading.DispatcherPriority.Input);
    }

    private DispatcherTimer? _editorNoticeTimer;

    /// <summary>Shows a short notice next to the editor's toolbar without interrupting typing.</summary>
    private void ShowEditorNotice(string text)
    {
        EditorNotice.Text = text;
        EditorNotice.ToolTip = text;
        EditorNotice.Visibility = Visibility.Visible;
        ViewModel.AddLog(text);

        _editorNoticeTimer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(8) };
        _editorNoticeTimer.Tick -= HideEditorNotice;
        _editorNoticeTimer.Tick += HideEditorNotice;
        _editorNoticeTimer.Stop();
        _editorNoticeTimer.Start();
    }

    private void HideEditorNotice(object? sender, EventArgs e)
    {
        _editorNoticeTimer?.Stop();
        EditorNotice.Visibility = Visibility.Collapsed;
    }

    private void UpdateEditorHint()
    {
        var document = ManuscriptEditor.Document;
        bool empty = document.Blocks.Count <= 1
            && document.Blocks.FirstBlock is Paragraph only
            && new TextRange(only.ContentStart, only.ContentEnd).Text.Trim().Length == 0;
        EmptyEditorHint.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
    }

    private Paragraph? CaretParagraph => ManuscriptEditor.CaretPosition.Paragraph;

    private static BlockStyleRole RoleOf(Paragraph paragraph)
        => paragraph.Tag is EditorBlockTag tag ? tag.Role : BlockStyleRole.BodyParagraph;

    private void OnEditorSelectionChanged(object sender, RoutedEventArgs e)
    {
        UpdateEditorToolbar();
        if (CaretParagraph?.Tag is EditorBlockTag { ParagraphId: { } id })
        {
            _lastEditorParagraphId = id;
            ViewModel.SelectParagraphHunks(id);
        }
    }

    /// <summary>Reflects the selection's paragraph style, bold and italic in the toolbar.</summary>
    private void UpdateEditorToolbar()
    {
        _updatingEditorToolbar = true;
        try
        {
            var paragraph = CaretParagraph;
            ParagraphStyleCombo.SelectedValue = paragraph != null ? RoleOf(paragraph) : null;

            var selection = ManuscriptEditor.Selection;
            BoldButton.IsChecked = selection.GetPropertyValue(TextElement.FontWeightProperty) is FontWeight weight
                && weight.ToOpenTypeWeight() >= FontWeights.Bold.ToOpenTypeWeight();
            ItalicButton.IsChecked = selection.GetPropertyValue(TextElement.FontStyleProperty) is FontStyle style
                && style != FontStyles.Normal;
        }
        finally
        {
            _updatingEditorToolbar = false;
        }
    }

    private void OnParagraphStyleSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingEditorToolbar || ParagraphStyleCombo.SelectedValue is not BlockStyleRole role)
        {
            return;
        }
        SetSelectedParagraphsRole(role);
        ManuscriptEditor.Focus();
    }

    /// <summary>
    /// Gives the selected paragraphs a style, then redraws the page so heading numbers follow the new structure.
    /// </summary>
    private void SetSelectedParagraphsRole(BlockStyleRole role)
    {
        var first = ManuscriptEditor.Selection.Start.Paragraph;
        var last = ManuscriptEditor.Selection.End.Paragraph;
        if (first == null || ManuscriptEditor.IsReadOnly)
        {
            return;
        }

        for (Block? block = first; block != null; block = block.NextBlock)
        {
            if (block is Paragraph paragraph)
            {
                if (paragraph.Tag is EditorBlockTag tag)
                {
                    tag.Role = role;
                }
                else
                {
                    paragraph.Tag = new EditorBlockTag { Role = role };
                }
            }
            if (ReferenceEquals(block, last))
            {
                break;
            }
        }

        _editorDirty = true;
        FlushEditorEdits();
        RenderManuscriptView(preserveCaret: true);
    }

    private void OnEditorPreviewKeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var modifiers = Keyboard.Modifiers;

        // Paragraph styles, with Word's shortcuts.
        if (modifiers == (ModifierKeys.Control | ModifierKeys.Alt) && key is Key.D1 or Key.D2 or Key.D3 or Key.NumPad1 or Key.NumPad2 or Key.NumPad3)
        {
            SetSelectedParagraphsRole(key is Key.D1 or Key.NumPad1 ? BlockStyleRole.Heading1 : key is Key.D2 or Key.NumPad2 ? BlockStyleRole.Heading2 : BlockStyleRole.Heading3);
            e.Handled = true;
            return;
        }
        if (modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && key == Key.N)
        {
            SetSelectedParagraphsRole(BlockStyleRole.BodyParagraph);
            e.Handled = true;
            return;
        }

        // The editor's own shortcuts for these would otherwise hide the app's.
        if (modifiers == ModifierKeys.Control && key == Key.E)
        {
            if (AppCommands.ExportCleanDocx.CanExecute(null, this)) AppCommands.ExportCleanDocx.Execute(null, this);
            e.Handled = true;
            return;
        }
        if (modifiers == ModifierKeys.Control && key == Key.M)
        {
            if (AppCommands.MergeIntoMain.CanExecute(null, this)) AppCommands.MergeIntoMain.Execute(null, this);
            e.Handled = true;
            return;
        }

        // Formatting the manuscript cannot store (underline, alignment, lists, indents, font size, sub- and
        // superscript) is turned off rather than shown and then silently lost.
        bool unsupported = modifiers == ModifierKeys.Control && key is Key.U or Key.L or Key.R or Key.J or Key.OemCloseBrackets or Key.OemOpenBrackets or Key.OemPlus
            || modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && key is Key.L or Key.M or Key.OemPlus or Key.OemCloseBrackets or Key.OemOpenBrackets or Key.OemPeriod or Key.OemComma;
        if (unsupported)
        {
            e.Handled = true;
            return;
        }

        // Enter at the end of a title or heading starts a Normal paragraph, as in Word.
        if (key == Key.Enter && modifiers == ModifierKeys.None && CaretParagraph is { } current
            && RoleOf(current) is BlockStyleRole.Title or BlockStyleRole.Heading1 or BlockStyleRole.Heading2 or BlockStyleRole.Heading3
            && new TextRange(ManuscriptEditor.CaretPosition, current.ContentEnd).Text.Length == 0)
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
            {
                if (CaretParagraph is { } created && !ReferenceEquals(created, current))
                {
                    created.Tag = new EditorBlockTag { Role = BlockStyleRole.BodyParagraph };
                    FlowDocumentBuilder.ApplyParagraphStyle(created, BlockStyleRole.BodyParagraph, ViewModel.ActiveProfile);
                    UpdateEditorToolbar();
                }
            });
        }
    }

    /// <summary>
    /// Pasting brings in plain text only: the manuscript has no place for fonts, colors or images.
    /// </summary>
    private void OnEditorPasting(object sender, DataObjectPastingEventArgs e)
    {
        if (!e.DataObject.GetDataPresent(DataFormats.UnicodeText))
        {
            e.CancelCommand();
            return;
        }
        string text = e.DataObject.GetData(DataFormats.UnicodeText) as string ?? string.Empty;
        var plain = new DataObject();
        plain.SetData(DataFormats.UnicodeText, text);
        e.DataObject = plain;
        e.FormatToApply = DataFormats.UnicodeText;
    }

    /// <summary>Caret position as (top-level block index, character offset within the paragraph).</summary>
    private (int Block, int Offset)? CaptureCaret()
    {
        var paragraph = CaretParagraph;
        if (paragraph == null)
        {
            return null;
        }
        int index = ManuscriptEditor.Document.Blocks.TakeWhile(b => !ReferenceEquals(b, paragraph)).Count();
        int offset = new TextRange(paragraph.ContentStart, ManuscriptEditor.CaretPosition).Text.Length;
        return (index, offset);
    }

    private void RestoreCaret((int Block, int Offset) caret)
    {
        var block = ManuscriptEditor.Document.Blocks.ElementAtOrDefault(caret.Block);
        if (block is not Paragraph paragraph)
        {
            return;
        }

        // Walk the paragraph's text runs until the offset is reached.
        var pointer = paragraph.ContentStart;
        int remaining = caret.Offset;
        while (remaining > 0 && pointer != null && pointer.CompareTo(paragraph.ContentEnd) < 0)
        {
            if (pointer.GetPointerContext(LogicalDirection.Forward) == TextPointerContext.Text)
            {
                int length = pointer.GetTextRunLength(LogicalDirection.Forward);
                if (length >= remaining)
                {
                    pointer = pointer.GetPositionAtOffset(remaining);
                    remaining = 0;
                    break;
                }
                remaining -= length;
                pointer = pointer.GetPositionAtOffset(length);
            }
            else
            {
                pointer = pointer.GetNextContextPosition(LogicalDirection.Forward);
            }
        }

        ManuscriptEditor.CaretPosition = pointer ?? paragraph.ContentEnd;
        ManuscriptEditor.Focus();
    }

    private void RenderDiffView()
    {
        _diffDirty = false;
        UnifiedDiffViewer.Document = FlowDocumentBuilder.BuildUnifiedDiffFlowDocument(ViewModel.CurrentDiffResult);
    }

    private async void PromptOpenWorkspace()
    {
        var dlg = new OpenFolderDialog
        {
            Title = "Open Workspace",
            InitialDirectory = ViewModel.HasWorkspace
                ? Path.GetDirectoryName(ViewModel.WorkspacePath)
                : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
        };

        if (dlg.ShowDialog(this) != true)
        {
            return;
        }

        string folder = dlg.FolderName;
        if (!Directory.Exists(Path.Combine(folder, ".git")))
        {
            var answer = MessageBox.Show(
                this,
                $"'{folder}' is not a workspace yet.\n\nCreate a new workspace in this folder? A Git repository will be initialized there to hold the manuscript history.",
                "Open Workspace",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);
            if (answer != MessageBoxResult.Yes)
            {
                return;
            }
        }

        await OpenWorkspaceAsync(folder);
    }

    private async void OpenRecentWorkspace(string? path)
    {
        if (path == null)
        {
            return;
        }

        if (!Directory.Exists(path))
        {
            MessageBox.Show(this, $"The folder '{path}' no longer exists. It has been removed from the recent list.",
                "Open Workspace", MessageBoxButton.OK, MessageBoxImage.Warning);
            _settings.RemoveRecentWorkspace(path);
            _settings.Save();
            return;
        }

        await OpenWorkspaceAsync(path);
    }

    private void OnRecentWorkspacesSubmenuOpened(object sender, RoutedEventArgs e)
    {
        RecentWorkspacesMenu.Items.Clear();
        if (_settings.RecentWorkspaces.Count == 0)
        {
            RecentWorkspacesMenu.Items.Add(new MenuItem { Header = "(none)", IsEnabled = false });
            return;
        }

        for (int i = 0; i < _settings.RecentWorkspaces.Count; i++)
        {
            string path = _settings.RecentWorkspaces[i];
            RecentWorkspacesMenu.Items.Add(new MenuItem
            {
                // Escape underscores in the path so they are not read as access keys.
                Header = $"_{i + 1} {path.Replace("_", "__")}",
                Command = AppCommands.OpenRecentWorkspace,
                CommandParameter = path
            });
        }
    }

    private bool CanExportDocument() => ViewModel.HasWorkspace && ViewModel.CurrentDocument.Blocks.Count > 0;

    private string SuggestedFileName(string suffix, string extension)
    {
        string baseName = string.IsNullOrWhiteSpace(ViewModel.DocumentTitle) ? ViewModel.WorkspaceName : ViewModel.DocumentTitle;
        foreach (char c in Path.GetInvalidFileNameChars())
        {
            baseName = baseName.Replace(c, ' ');
        }
        baseName = baseName.Trim();
        if (baseName.Length > 80)
        {
            baseName = baseName[..80].TrimEnd();
        }
        return $"{(baseName.Length == 0 ? "Manuscript" : baseName)}{suffix}{extension}";
    }

    private void PromptImportDocx()
    {
        var dlg = new OpenFileDialog
        {
            Title = ViewModel.HasCommits ? "Import Reviewed Word Document" : "Import Manuscript",
            Filter = "Word Documents (*.docx)|*.docx|All Files (*.*)|*.*",
            InitialDirectory = ViewModel.WorkspacePath
        };

        if (dlg.ShowDialog(this) == true)
        {
            _ = ImportDocxAsync(dlg.FileName);
        }
    }

    /// <summary>
    /// Imports a reviewed document onto a review branch that starts from the version the reviewer was sent,
    /// which is detected from the document's text and confirmed when it is not the latest version.
    /// </summary>
    private async Task ImportDocxAsync(string path)
    {
        string? baseSha = null;
        if (ViewModel.HasCommits)
        {
            List<ViewModels.MainViewModel.ImportBaseOption>? options = null;
            if (!await RunAsync(async () => options = await ViewModel.FindImportBaseOptionsAsync(path), "Import Word Document"))
            {
                return;
            }

            var latest = ViewModel.MainBranchTip;
            if (options is { Count: > 0 })
            {
                bool bestIsLatest = latest != null && options[0].Commit.Sha == latest.Sha;
                if (!bestIsLatest || options[0].Score < 0.98)
                {
                    var dialog = new ImportBaseDialog(Path.GetFileName(path), options, bestIsLatest) { Owner = this };
                    if (dialog.ShowDialog() != true)
                    {
                        return;
                    }
                    baseSha = dialog.SelectedOption!.Commit.Sha;
                }
                if (latest != null && baseSha == latest.Sha)
                {
                    baseSha = null;
                }
            }
        }

        await RunAsync(() => ViewModel.ImportDocxAsync(path, baseSha), "Import Word Document");
    }

    private async void SyncWithGitHub()
    {
        if (!ViewModel.HasRemote)
        {
            PromptConnectGitHub();
            return;
        }

        ViewModel.FlushPendingMarkdownSync();
        if (ViewModel.HasUncommittedChanges)
        {
            var dialog = new CommitDialog("Changes before syncing", ViewModel.CurrentBranch, ViewModel.AuthorName) { Owner = this };
            if (dialog.ShowDialog() != true
                || !await RunAsync(() => ViewModel.CommitCurrentChangesAsync(dialog.CommitMessage), "Commit"))
            {
                return;
            }
        }

        Core.Git.SyncReport? report = null;
        if (await RunAsync(async () => report = await ViewModel.SyncWithGitHubAsync(), "Sync with GitHub") && report is { Conflicts: > 0 })
        {
            MessageBox.Show(this,
                $"{report.Summary}\n\nThe same words were edited on two computers. Both versions are kept: the text from this computer is current and the other is proposed as a change. Review them in the Changes pane.",
                "Sync with GitHub", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private async void PromptConnectGitHub()
    {
        var dialog = new GitHubDialog(clone: false, ViewModel.RemoteUrl) { Owner = this };
        if (dialog.ShowDialog() == true && await RunAsync(() => ViewModel.ConnectToGitHubAsync(dialog.Url), "Connect to GitHub"))
        {
            SyncWithGitHub();
        }
    }

    private async void PromptCloneFromGitHub()
    {
        string? parent = ViewModel.HasWorkspace ? Path.GetDirectoryName(ViewModel.WorkspacePath) : null;
        var dialog = new GitHubDialog(clone: true, defaultParentFolder: parent) { Owner = this };
        if (dialog.ShowDialog() == true
            && await RunAsync(() => ViewModel.CloneFromGitHubAsync(dialog.Url, dialog.Folder), "Open from GitHub"))
        {
            _settings.AddRecentWorkspace(ViewModel.WorkspacePath);
            _settings.Save();
        }
    }

    private void PromptExportDocx(bool includeTrackChanges)
    {
        var dlg = new SaveFileDialog
        {
            Title = includeTrackChanges ? "Export with Tracked Changes" : "Export Word Document",
            Filter = "Word Document (*.docx)|*.docx",
            FileName = SuggestedFileName(includeTrackChanges ? " (tracked changes)" : string.Empty, ".docx"),
            InitialDirectory = ViewModel.WorkspacePath
        };

        if (dlg.ShowDialog(this) == true)
        {
            Run(() => ViewModel.ExportDocxFile(dlg.FileName, includeTrackChanges), "Export Word Document");
        }
    }

    private void PromptExportCertificate()
    {
        var dlg = new SaveFileDialog
        {
            Title = "Export Provenance Certificate",
            Filter = "HTML Document (*.html)|*.html",
            FileName = SuggestedFileName(" - provenance certificate", ".html"),
            InitialDirectory = ViewModel.WorkspacePath
        };

        if (dlg.ShowDialog(this) == true)
        {
            Run(() => ViewModel.ExportAuditCertificateToFile(dlg.FileName), "Export Provenance Certificate");
        }
    }

    private void PromptCommit()
    {
        ViewModel.FlushPendingMarkdownSync();
        // While a revision is under way, the commit can say which reviewer comments it addresses.
        var openComments = ViewModel.Paper.SelectedRound?.Comments
            .Where(c => c.Status != Core.Reviews.ReviewCommentStatus.Answered)
            .Select(c => (c.Id, c.Text))
            .ToList();
        var dialog = new CommitDialog(ViewModel.SuggestCommitMessage(), ViewModel.CurrentBranch, ViewModel.AuthorName, openComments)
        {
            Owner = this
        };

        if (dialog.ShowDialog() == true)
        {
            _ = RunAsync(() => ViewModel.CommitCurrentChangesAsync(dialog.CommitMessage), "Commit");
        }
    }

    private async void ConfirmAndMerge()
    {
        string message = ViewModel.PendingHunkCount > 0
            ? $"Merge '{ViewModel.CurrentBranch}' into the main branch?\n\n{ViewModel.PendingHunkCount} pending changes will be accepted as part of the merge."
            : $"Merge '{ViewModel.CurrentBranch}' into the main branch?";

        if (MessageBox.Show(this, message, "Merge Review Branch", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
        {
            return;
        }

        if (await RunAsync(ViewModel.MergeReviewBranchToMainAsync, "Merge Review Branch"))
        {
            SelectWorkspaceTab(FormattingTabIndex);
        }
    }

    private void CompareSelectedWithLatest()
    {
        if (ViewModel.SelectedCommit != null && ViewModel.CommitGraph.Count > 0)
        {
            ViewModel.SetComparePair(ViewModel.SelectedCommit, ViewModel.CommitGraph[0]);
            SelectWorkspaceTab(CompareTabIndex);
        }
    }

    private void OnCommitGridDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is FrameworkElement { DataContext: GitCommitNode })
        {
            CompareSelectedWithLatest();
        }
    }

    private void SetSelectedHunkStatus(RevisionHunkStatus status)
    {
        if (ViewModel.SelectedHunk is { } hunk)
        {
            Run(() => ViewModel.SetHunkStatus(hunk, status));
        }
    }

    private void RunTamperDetectionTest()
    {
        var result = ViewModel.RunNonDestructiveTamperSimulation();
        string outcome = result.IsValid
            ? "The modification was NOT detected. The provenance chain should be investigated."
            : "The modification was detected, as expected.";
        MessageBox.Show(
            this,
            $"A copy of the provenance chain was made in memory and the text recorded in block #0 was modified without re-signing it. Your files were not changed.\n\n{outcome}\n\n{result.SummaryMessage}",
            "Test Tamper Detection",
            MessageBoxButton.OK,
            result.IsValid ? MessageBoxImage.Warning : MessageBoxImage.Information);
    }

    private void ShowAbout()
    {
        var version = Assembly.GetExecutingAssembly().GetName().Version;
        MessageBox.Show(
            this,
            $"Revisions Editor {version?.ToString(3)}\n\nGit version control, Word Track Changes and signed authorship provenance for academic manuscripts.",
            "About Revisions Editor",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private void InsertCriticMarkup(string? kind)
    {
        if (!IsPanelShown(SourceTabIndex))
        {
            SelectWorkspaceTab(SourceTabIndex);
        }

        // Wrap the selection when there is one; otherwise insert a placeholder.
        string selected = MarkdownSourceTextBox.SelectedText;
        bool hasSelection = selected.Length > 0;
        string snippet = kind switch
        {
            "insertion" => $"{{++{(hasSelection ? selected : "inserted text")}++}}",
            "deletion" => $"{{--{(hasSelection ? selected : "deleted text")}--}}",
            "substitution" => $"{{~~{(hasSelection ? selected : "original")}~>replacement~~}}",
            "comment" => $"{{=={(hasSelection ? selected : "highlighted text")}==}}{{>>{ViewModel.AuthorName}: comment<<}}",
            _ => string.Empty
        };
        if (snippet.Length == 0)
        {
            return;
        }

        // Replacing SelectedText (rather than Text) keeps the TextBox undo history intact.
        int start = MarkdownSourceTextBox.SelectionStart;
        MarkdownSourceTextBox.SelectedText = snippet;
        MarkdownSourceTextBox.Select(start + snippet.Length, 0);
        MarkdownSourceTextBox.Focus();
    }
}
