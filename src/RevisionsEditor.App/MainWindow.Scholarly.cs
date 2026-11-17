using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using Microsoft.Win32;
using RevisionsEditor.App.Dialogs;
using RevisionsEditor.App.Rendering;
using RevisionsEditor.App.ViewModels;
using RevisionsEditor.Core.Bibliography;
using RevisionsEditor.Core.Models;
using RevisionsEditor.Core.Reviews;
using RevisionsEditor.Core.Scholarly;
using RevisionsEditor.Core.Serialization;

namespace RevisionsEditor.App;

/// <summary>Citations, cross-references, figures, tables, equations, linked numbers, checks, journal exports and reviews.</summary>
public partial class MainWindow
{
    private const int PaperTabIndex = 5;
    private const int ReferencesTabIndex = 6;
    private const int FiguresTabIndex = 7;
    private const int ReviewersTabIndex = 8;

    private static readonly Regex QuotedAbbreviationRegex = new("“([^”]{2,12})”", RegexOptions.Compiled);

    public static IReadOnlyList<ReviewCommentStatus> CommentStatuses { get; } = Enum.GetValues<ReviewCommentStatus>();

    private bool _preview;

    private PaperViewModel Paper => ViewModel.Paper;

    private void RegisterScholarlyCommands()
    {
        Paper.StructureChanged += () =>
        {
            _manuscriptDirty = true;
            if (IsPanelShown(DocumentTabIndex) && !_editorDirty)
            {
                RenderManuscriptView(preserveCaret: true);
            }
        };

        Func<object?, bool> workspace = _ => ViewModel.HasWorkspace;
        Bind(AppCommands.InsertCitation, _ => InsertCitation(), workspace);
        Bind(AppCommands.InsertCrossReference, _ => InsertCrossReference(), workspace);
        Bind(AppCommands.InsertNumber, _ => InsertNumber(), workspace);
        Bind(AppCommands.InsertFigure, _ => EditFigure(null), workspace);
        Bind(AppCommands.InsertTable, _ => EditTable(null), workspace);
        Bind(AppCommands.InsertDiagram, _ => EditDiagram(null), workspace);
        Bind(AppCommands.InsertEquation, _ => EditEquation(null), workspace);
        Bind(AppCommands.EditScholarlyItem, p => EditScholarlyBlock(((NumberedItem)p!).ParagraphId), p => p is NumberedItem);
        Bind(AppCommands.RemoveScholarlyItem, p => RemoveScholarlyItem((NumberedItem)p!), p => p is NumberedItem);
        Bind(AppCommands.TogglePreview, _ => TogglePreview(), workspace);

        Bind(AppCommands.CheckManuscript, _ => { ViewModel.FlushPendingMarkdownSync(); Paper.ScheduleChecks(immediately: true); ShowChecksPane(); }, workspace);
        Bind(AppCommands.GoToIssue, p => GoToParagraph(((IssueItem)p!).Issue.ParagraphId!, (IssueItem)p), p => p is IssueItem { Issue.ParagraphId: not null });
        Bind(AppCommands.FixIssue, p => Run(() =>
        {
            if (!ViewModel.ApplyIssueFix((IssueItem)p!))
            {
                ShowEditorNotice("That text has changed since it was checked; check again.");
            }
        }, "Fix"), p => p is IssueItem { CanFix: true });
        Bind(AppCommands.DismissIssue, p => Paper.Dismiss((IssueItem)p!), p => p is IssueItem);
        Bind(AppCommands.RestoreDismissedIssues, _ => Paper.RestoreDismissed(), _ => Paper.Project.Settings.DismissedIssues.Count > 0);
        Bind(AppCommands.AllowAbbreviation, p => AllowAbbreviation((IssueItem)p!),
            p => p is IssueItem { Category: "Style" } item && item.Message.StartsWith("Define ", StringComparison.Ordinal));

        Bind(AppCommands.AddReferencesByDoi, _ => AddReferencesByDoi(), workspace);
        Bind(AppCommands.AddReferencesFromZotero, _ => AddReferencesFromZotero(), _ => ViewModel.HasWorkspace && Paper.Context is { Citations.MissingKeys.Count: > 0 });
        Bind(AppCommands.CheckReferencesOnline, _ => CheckReferencesOnline(), _ => Paper.References.Any(r => r.Doi.Length > 0));
        Bind(AppCommands.OpenBibliography, _ => OpenFile(Paper.Project.BibliographyPath), _ => File.Exists(Paper.Project.BibliographyPath));
        Bind(AppCommands.CiteSelectedReferences, _ => CiteSelectedReferences(), _ => ReferencesGrid.SelectedItems.Count > 0);

        Bind(AppCommands.AddResultsFile, _ => AddResultsFile(), workspace);
        Bind(AppCommands.RefreshResults, _ => _ = RunAsync(async () =>
        {
            var refreshed = await ViewModel.RefreshResultsAsync();
            ShowEditorNotice(refreshed.Count == 0 ? "All results are up to date." : $"Refreshed {string.Join(", ", refreshed)}; the paper's numbers now use the new values.");
        }, "Refresh Results"), _ => Paper.ResultsSources.Count > 0);
        Bind(AppCommands.InsertSelectedValue, p => InsertInline(((ResultValueItem)p!).Placeholder), p => p is ResultValueItem);

        Bind(AppCommands.ExportReviewCopy, _ => ExportForJournal(reviewCopy: true), _ => CanExportDocument());
        Bind(AppCommands.ExportSubmissionCopy, _ => ExportForJournal(reviewCopy: false), _ => CanExportDocument());
        Bind(AppCommands.ExportHighlights, _ => ExportHighlights(), _ => CanExportDocument());
        Bind(AppCommands.AddRequiredSections, _ => AddRequiredSections(), workspace);
        Bind(AppCommands.MarkSubmitted, _ => MarkSubmitted(), _ => ViewModel.HasCommits);

        Bind(AppCommands.AddReviewComments, _ => AddReviewComments(), workspace);
        Bind(AppCommands.LinkCommentToParagraph, _ => LinkCommentToParagraph(), _ => Paper.SelectedComment != null);
        Bind(AppCommands.UnlinkComment, _ => Paper.UnlinkAll(Paper.SelectedComment!), _ => Paper.SelectedComment is { Model.ParagraphIds.Count: > 0 });
        Bind(AppCommands.ExportRevision, _ => ExportRevision(), _ => Paper.SelectedRound != null && ViewModel.HasCommits);
    }

    // ---- Inserting at the cursor ----------------------------------------------------------------------

    /// <summary>
    /// Inserts text at the cursor of the Document page (or the Markdown source when that tab is open). Citations,
    /// cross-references and numbers are typed text, so they can also be edited by hand.
    /// </summary>
    private void InsertInline(string text)
    {
        // Into the Markdown source if that is where the author was last typing and it is on screen.
        if (_sourceHadFocusLast && IsPanelShown(SourceTabIndex))
        {
            int start = MarkdownSourceTextBox.SelectionStart;
            MarkdownSourceTextBox.SelectedText = text;
            MarkdownSourceTextBox.Select(start + text.Length, 0);
            MarkdownSourceTextBox.Focus();
            return;
        }
        if (_preview)
        {
            TogglePreview();
        }
        if (!IsPanelShown(DocumentTabIndex))
        {
            SelectWorkspaceTab(DocumentTabIndex);
        }

        var caret = ManuscriptEditor.CaretPosition;
        if (caret.Paragraph == null || ManuscriptEditor.IsReadOnly)
        {
            ShowEditorNotice("Put the cursor in a paragraph first.");
            return;
        }
        // Keep a space between the word before and what is inserted.
        string before = caret.GetTextInRun(LogicalDirection.Backward);
        if (before.Length > 0 && char.IsLetterOrDigit(before[^1]) && !text.StartsWith(' '))
        {
            text = " " + text;
        }
        if (!ManuscriptEditor.Selection.IsEmpty)
        {
            ManuscriptEditor.Selection.Text = string.Empty;
            caret = ManuscriptEditor.Selection.Start;
        }
        caret.InsertTextInRun(text);
        ManuscriptEditor.CaretPosition = caret.GetPositionAtOffset(text.Length, LogicalDirection.Forward) ?? caret;
        ManuscriptEditor.Focus();
    }

    /// <summary>The manuscript paragraph the cursor is in, after applying the page's edits (so new paragraphs have ids).</summary>
    private string? CaretBlockId()
    {
        if (!IsPanelShown(DocumentTabIndex) || _preview)
        {
            return ViewModel.CurrentDocument.Blocks.LastOrDefault()?.ParagraphId;
        }
        var paragraph = CaretParagraph;
        if (paragraph?.Tag is EditorBlockTag { ParagraphId: { } known } && ViewModel.FindBlock(known) != null && !_editorDirty)
        {
            return known;
        }
        // Count the non-empty blocks up to the cursor, apply the edits, then take the same block.
        int index = -1;
        foreach (var block in ManuscriptEditor.Document.Blocks)
        {
            bool empty = block is Paragraph p && new TextRange(p.ContentStart, p.ContentEnd).Text.Trim().Length == 0;
            if (!empty)
            {
                index++;
            }
            if (ReferenceEquals(block, paragraph) || block.ContentStart.CompareTo(ManuscriptEditor.CaretPosition) <= 0 && block.ContentEnd.CompareTo(ManuscriptEditor.CaretPosition) >= 0)
            {
                break;
            }
        }
        FlushEditorEdits();
        var blocks = ViewModel.CurrentDocument.Blocks;
        return index >= 0 && index < blocks.Count ? blocks[index].ParagraphId : blocks.LastOrDefault()?.ParagraphId;
    }

    private async void InsertCitation()
    {
        ViewModel.FlushPendingMarkdownSync();
        var references = Paper.Project.Bibliography?.Entries.Select(e => new ReferenceItem(e, 0)).ToList() ?? [];
        bool zotero = false;
        using (var services = new ReferenceServices())
        {
            zotero = await services.IsZoteroReadyAsync();
        }
        var dialog = new CitationPickerDialog(references, zotero) { Owner = this };
        if (dialog.ShowDialog() != true)
        {
            return;
        }
        if (dialog.UseZotero)
        {
            await PickInZoteroAsync();
            return;
        }
        InsertInline(dialog.CitationText);
    }

    /// <summary>Zotero's own picker; references not yet in the bibliography are fetched from Zotero too.</summary>
    private async Task PickInZoteroAsync()
    {
        using var services = new ReferenceServices();
        string? citation = await services.PickCitationInZoteroAsync();
        Activate();
        if (string.IsNullOrWhiteSpace(citation))
        {
            return;
        }
        var keys = CitationSyntax.Find(citation).SelectMany(c => c.Items).Select(i => i.Key).Where(k => !Paper.Project.Library.ContainsKey(k)).Distinct().ToList();
        if (keys.Count > 0)
        {
            string? bibtex = await services.ExportFromZoteroAsync(keys);
            if (!string.IsNullOrWhiteSpace(bibtex))
            {
                var added = Paper.AddBibTeX(bibtex, keepKeys: true);
                ViewModel.AddLog($"Added {added.Count} references from Zotero to {Paper.Project.Settings.BibliographyFile}.");
                await Paper.ReloadProjectAsync();
            }
        }
        InsertInline(citation);
    }

    private void CiteSelectedReferences()
    {
        var keys = ReferencesGrid.SelectedItems.Cast<ReferenceItem>().Select(r => "@" + r.Key);
        InsertInline("[" + string.Join("; ", keys) + "]");
    }

    private void OnReferenceDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is FrameworkElement { DataContext: ReferenceItem } && ReferencesGrid.SelectedItems.Count > 0)
        {
            CiteSelectedReferences();
        }
    }

    private void InsertCrossReference()
    {
        ViewModel.FlushPendingMarkdownSync();
        Paper.RebuildContext();
        var targets = Paper.Context?.CrossReferences.Targets.Values
            .OrderBy(t => t.Position)
            .Select(t => new CrossReferencePickerDialog.Target(t.Label, Describe(t)))
            .ToList() ?? [];
        var dialog = new CrossReferencePickerDialog(targets) { Owner = this };
        if (dialog.ShowDialog() == true)
        {
            InsertInline(dialog.ReferenceText);
        }

        string Describe(CrossReferenceTarget t)
        {
            var item = Paper.NumberedItems.FirstOrDefault(n => n.ParagraphId == t.ParagraphId);
            string what = t.Kind switch { "fig" => "Figure", "tbl" => "Table", "eq" => "Equation", _ => "Section" };
            string caption = item?.Caption ?? ViewModel.FindBlock(t.ParagraphId)?.Text ?? string.Empty;
            caption = ScholarlyBlocks.StripHeadingLabel(caption);
            return $"{what} {t.Number}  —  {(caption.Length > 80 ? caption[..80] + "…" : caption)}   (@{t.Label})";
        }
    }

    private void InsertNumber()
    {
        ViewModel.FlushPendingMarkdownSync();
        var values = Paper.Project.Results.AllValues().Take(5000).Select(v => new ResultValueItem(v.Key, v.Value)).ToList();
        var dialog = new NumberPickerDialog(values) { Owner = this };
        if (dialog.ShowDialog() == true)
        {
            InsertInline(dialog.PlaceholderText);
        }
    }

    // ---- Figures, tables and equations ---------------------------------------------------------------

    private HashSet<string> ExistingLabels()
    {
        Paper.RebuildContext();
        return Paper.Context?.CrossReferences.Targets.Values.Select(t => t.Label).ToHashSet(StringComparer.OrdinalIgnoreCase) ?? [];
    }

    private void EditScholarlyBlock(string paragraphId)
    {
        var block = ViewModel.FindBlock(paragraphId);
        switch (block?.Role)
        {
            case BlockStyleRole.Figure: EditFigure(block); break;
            case BlockStyleRole.Table: EditTable(block); break;
            case BlockStyleRole.Equation: EditEquation(block); break;
        }
    }

    private void OnNumberedItemDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is FrameworkElement { DataContext: NumberedItem item })
        {
            EditScholarlyBlock(item.ParagraphId);
        }
    }

    private static string FinalText(DocumentBlock block) => CriticMarkupSerializer.ResolveCriticMarkupText(block.Text, MarkupDisplayMode.NoMarkupFinal);

    private void EditFigure(DocumentBlock? existing)
    {
        var current = existing != null ? ScholarlyBlocks.ParseFigure(FinalText(existing)) : null;
        if (current != null && Core.Diagrams.DiagramFiles.SourceOf(current) != null)
        {
            EditDiagram(existing);
            return;
        }
        var labels = ExistingLabels();
        if (current?.Label != null)
        {
            labels.Remove(current.Label);
        }
        string? after = existing == null ? CaretBlockId() : null;
        var dialog = new FigureDialog(current, labels) { Owner = this };
        if (dialog.ShowDialog() != true)
        {
            return;
        }
        Run(() =>
        {
            string path = ViewModel.CopyIntoWorkspace(dialog.ImagePath.Contains(':') || Path.IsPathRooted(dialog.ImagePath)
                ? dialog.ImagePath
                : Path.Combine(ViewModel.WorkspacePath, dialog.ImagePath), PaperSettings.FiguresFolder);
            var attributes = new Dictionary<string, string>(current?.Attributes ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase);
            attributes.Remove("width");
            if (dialog.FigureWidth != null)
            {
                attributes["width"] = dialog.FigureWidth;
            }
            SetPlaceholderAttribute(attributes, dialog.PlaceholderNote);
            string text = ScholarlyBlocks.FormatFigure(dialog.Caption, path, dialog.Label, attributes);
            if (existing != null)
            {
                ViewModel.ReplaceBlockText(existing.ParagraphId, text);
            }
            else
            {
                ViewModel.InsertBlock(new DocumentBlock { ParagraphId = ViewModel.NewParagraphId(), Role = BlockStyleRole.Figure, Text = text }, after);
                ShowEditorNotice($"Figure added. Refer to it in the text as @{dialog.Label}.");
            }
        }, "Figure");
    }

    /// <summary>
    /// Draws a block diagram for a figure, or edits one: its source is figures/&lt;label&gt;.diagram (named by the
    /// figure's source= attribute) and the figure shows the SVG drawn from it.
    /// </summary>
    private void EditDiagram(DocumentBlock? existing)
    {
        var current = existing != null ? ScholarlyBlocks.ParseFigure(FinalText(existing)) : null;
        string? sourcePath = current != null ? Core.Diagrams.DiagramFiles.SourceOf(current) : null;
        string? source = null;
        if (sourcePath != null)
        {
            string full = Path.Combine(ViewModel.WorkspacePath, sourcePath.Replace('/', Path.DirectorySeparatorChar));
            source = File.Exists(full) ? File.ReadAllText(full) : null;
        }
        var labels = ExistingLabels();
        if (current?.Label != null)
        {
            labels.Remove(current.Label);
        }
        string? after = existing == null ? CaretBlockId() : null;
        var dialog = new DiagramEditorDialog(current, source, labels) { Owner = this };
        if (dialog.ShowDialog() != true)
        {
            return;
        }
        Run(() =>
        {
            var (diagramPath, svgPath) = ViewModel.SaveDiagram(sourcePath, dialog.Label, dialog.Source);
            var attributes = new Dictionary<string, string>(current?.Attributes ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase)
            {
                ["source"] = diagramPath
            };
            string text = ScholarlyBlocks.FormatFigure(dialog.Caption, svgPath, dialog.Label, attributes);
            if (existing != null)
            {
                ViewModel.ReplaceBlockText(existing.ParagraphId, text);
            }
            else
            {
                ViewModel.InsertBlock(new DocumentBlock { ParagraphId = ViewModel.NewParagraphId(), Role = BlockStyleRole.Figure, Text = text }, after);
                ShowEditorNotice($"Diagram added as a figure. Refer to it in the text as @{dialog.Label}; double-click it to edit the diagram.");
            }
        }, "Diagram");
    }

    /// <summary>Marks a figure or table as a placeholder (placeholder="note"), or as the real one.</summary>
    private static void SetPlaceholderAttribute(Dictionary<string, string> attributes, string? note)
    {
        attributes.Remove(DraftPlaceholders.FigureAttribute);
        if (note != null)
        {
            attributes[DraftPlaceholders.FigureAttribute] = note;
        }
    }

    private void EditTable(DocumentBlock? existing)
    {
        var current = existing != null ? ScholarlyBlocks.ParseTable(FinalText(existing)) : null;
        var labels = ExistingLabels();
        if (current?.Label != null)
        {
            labels.Remove(current.Label);
        }
        string? after = existing == null ? CaretBlockId() : null;
        var dialog = new TableEditorDialog(current, labels, ViewModel.WorkspacePath) { Owner = this };
        if (dialog.ShowDialog() != true)
        {
            return;
        }
        Run(() =>
        {
            // The cells go to the table's CSV file (a new tables/<label>.csv for a new table).
            string path = ViewModel.SaveTableCells(dialog.CsvPath, dialog.Label, dialog.Csv);
            var attributes = new Dictionary<string, string>(current?.Attributes ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase);
            attributes.Remove("file");
            SetPlaceholderAttribute(attributes, dialog.PlaceholderNote);
            string text = ScholarlyBlocks.FormatTable(dialog.Caption, dialog.Label, path, attributes);
            if (existing != null)
            {
                ViewModel.ReplaceBlockText(existing.ParagraphId, text);
            }
            else
            {
                ViewModel.InsertBlock(new DocumentBlock { ParagraphId = ViewModel.NewParagraphId(), Role = BlockStyleRole.Table, Text = text }, after);
                ShowEditorNotice($"Table added. Refer to it in the text as @{dialog.Label}.");
            }
        }, "Table");
    }

    private void EditEquation(DocumentBlock? existing)
    {
        var current = existing != null ? ScholarlyBlocks.ParseEquation(FinalText(existing)) : null;
        var labels = ExistingLabels();
        if (current?.Label != null)
        {
            labels.Remove(current.Label);
        }
        string? after = existing == null ? CaretBlockId() : null;
        var dialog = new EquationDialog(current, labels) { Owner = this };
        if (dialog.ShowDialog() != true)
        {
            return;
        }
        Run(() =>
        {
            string text = ScholarlyBlocks.FormatEquation(dialog.Latex, dialog.Label);
            if (existing != null)
            {
                ViewModel.ReplaceBlockText(existing.ParagraphId, text);
            }
            else
            {
                ViewModel.InsertBlock(new DocumentBlock { ParagraphId = ViewModel.NewParagraphId(), Role = BlockStyleRole.Equation, Text = text }, after);
            }
        }, "Equation");
    }

    private void RemoveScholarlyItem(NumberedItem item)
    {
        if (MessageBox.Show(this, $"Remove {item.Title} from the manuscript? Its file stays in the workspace.", "Remove", MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK)
        {
            Run(() => ViewModel.RemoveBlock(item.ParagraphId), "Remove");
        }
    }

    // ---- Preview ------------------------------------------------------------------------------------

    /// <summary>Shows the Checks pane on the right (used by screenshot mode).</summary>
    public void ShowChecksPane() => ShowPanel(ChecksPanel.ContentId);

    /// <summary>Shows the Notes pane (used by screenshot mode).</summary>
    public void ShowNotesPane() => ShowPanel(NotesPanel.ContentId);

    public void SetPreview(bool on)
    {
        if (on != _preview)
        {
            TogglePreview();
        }
    }

    private void TogglePreview()
    {
        FlushEditorEdits();
        _preview = !_preview;
        PreviewToggle.IsChecked = _preview;
        if (!IsPanelShown(DocumentTabIndex))
        {
            SelectWorkspaceTab(DocumentTabIndex);
        }
        ManuscriptEditor.Visibility = _preview ? Visibility.Collapsed : Visibility.Visible;
        PreviewViewer.Visibility = _preview ? Visibility.Visible : Visibility.Collapsed;
        RenderManuscriptView(preserveCaret: !_preview);
    }

    private void RenderPreview()
    {
        Paper.RebuildContext();
        var document = FlowDocumentBuilder.BuildManuscriptEditorDocument(
            ViewModel.CurrentDocument, ViewModel.ActiveProfile, MarkupDisplayMode.NoMarkupFinal,
            scholarly: Paper.Context, workspacePath: ViewModel.WorkspacePath, preview: true,
            onFrontMatterActivated: () => Dispatcher.BeginInvoke(OpenTitlePageForm));
        var margins = document.PagePadding;
        PreviewViewer.Document = document;
        document.PagePadding = margins;
    }

    // ---- Checks ---------------------------------------------------------------------------------

    private void OnIssueDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is FrameworkElement { DataContext: IssueItem { Issue.ParagraphId: { } id } item })
        {
            GoToParagraph(id, item);
        }
    }

    /// <summary>Shows a paragraph on the Document page and selects the text an issue is about.</summary>
    private void GoToParagraph(string paragraphId, IssueItem? issue = null)
    {
        if (_preview)
        {
            TogglePreview();
        }
        SelectWorkspaceTab(DocumentTabIndex);
        foreach (var block in ManuscriptEditor.Document.Blocks)
        {
            if (block.Tag is not EditorBlockTag tag || tag.ParagraphId != paragraphId)
            {
                continue;
            }
            block.BringIntoView();
            if (block is Paragraph paragraph)
            {
                var range = issue != null ? FindIssueText(paragraph, issue) : null;
                ManuscriptEditor.Focus();
                if (range != null)
                {
                    ManuscriptEditor.Selection.Select(range.Value.Start, range.Value.End);
                }
                else
                {
                    ManuscriptEditor.CaretPosition = paragraph.ContentStart;
                }
            }
            return;
        }
    }

    private (TextPointer Start, TextPointer End)? FindIssueText(Paragraph paragraph, IssueItem item)
    {
        var block = ViewModel.FindBlock(item.Issue.ParagraphId!);
        if (block == null || item.Issue.Start < 0 || item.Issue.Start + item.Issue.Length > block.Text.Length || item.Issue.Length == 0)
        {
            return null;
        }
        string target = Regex.Replace(block.Text.Substring(item.Issue.Start, item.Issue.Length), @"\*+", string.Empty);
        // The page shows the text without emphasis markers: count earlier occurrences to pick the right one.
        string before = Regex.Replace(block.Text[..item.Issue.Start], @"\*+", string.Empty);
        int occurrence = Regex.Matches(before, Regex.Escape(target)).Count;
        var pointer = paragraph.ContentStart;
        string text = new TextRange(paragraph.ContentStart, paragraph.ContentEnd).Text;
        int index = -1;
        for (int k = 0; k <= occurrence; k++)
        {
            index = text.IndexOf(target, index + 1, StringComparison.Ordinal);
            if (index < 0)
            {
                return null;
            }
        }
        var start = OffsetToPointer(paragraph, index);
        var end = OffsetToPointer(paragraph, index + target.Length);
        return start != null && end != null ? (start, end) : null;
    }

    private static TextPointer? OffsetToPointer(Paragraph paragraph, int offset)
    {
        var pointer = paragraph.ContentStart;
        int remaining = offset;
        while (pointer != null && pointer.CompareTo(paragraph.ContentEnd) < 0)
        {
            if (pointer.GetPointerContext(LogicalDirection.Forward) == TextPointerContext.Text)
            {
                int length = pointer.GetTextRunLength(LogicalDirection.Forward);
                if (length >= remaining)
                {
                    return pointer.GetPositionAtOffset(remaining);
                }
                remaining -= length;
                pointer = pointer.GetPositionAtOffset(length);
            }
            else
            {
                pointer = pointer.GetNextContextPosition(LogicalDirection.Forward);
            }
        }
        return remaining == 0 ? pointer : null;
    }

    private void AllowAbbreviation(IssueItem item)
    {
        var m = QuotedAbbreviationRegex.Match(item.Message);
        string abbreviation = m.Success ? m.Groups[1].Value : item.Message.Split(' ', StringSplitOptions.RemoveEmptyEntries).ElementAtOrDefault(1) ?? string.Empty;
        if (abbreviation.Length > 0)
        {
            Paper.AddKnownAbbreviation(abbreviation.Trim('“', '”', ','));
        }
    }

    private void OnJournalSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (JournalCombo.SelectedItem is Core.Journals.JournalProfile journal && journal.Id != Paper.SelectedJournal.Id)
        {
            Run(() => Paper.SelectedJournal = journal, "Target Journal");
        }
    }

    private void OnCitationStyleSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CitationStyleCombo.SelectedItem is CitationStyle style && style.Id != Paper.SelectedCitationStyle.Id)
        {
            Run(() => Paper.SelectedCitationStyle = style, "Citation Style");
        }
    }

    // ---- References -----------------------------------------------------------------------------

    private async void AddReferencesByDoi()
    {
        var dialog = new TextPromptDialog("Add References by DOI",
            "Enter one or more DOIs (one per line, or separated by spaces). Their details are looked up at doi.org and added to the bibliography with keys such as white2016remote.",
            "_DOIs:", "_Add", multiline: true) { Owner = this };
        if (dialog.ShowDialog() != true)
        {
            return;
        }
        var dois = dialog.Text.Split([' ', '\n', '\r', '\t', ',', ';'], StringSplitOptions.RemoveEmptyEntries).Select(ReferenceServices.NormalizeDoi).Where(d => d.StartsWith("10.", StringComparison.Ordinal)).Distinct().ToList();
        if (dois.Count == 0)
        {
            MessageBox.Show(this, "No DOI was recognised (DOIs start with 10.).", "Add References", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var added = new List<string>();
        var failed = new List<string>();
        await RunAsync(async () =>
        {
            using var services = new ReferenceServices();
            foreach (string doi in dois)
            {
                string? bibtex = await services.FetchBibTeXAsync(doi);
                if (bibtex == null)
                {
                    failed.Add(doi);
                    continue;
                }
                added.AddRange(Paper.AddBibTeX(bibtex, keepKeys: false));
            }
            await Paper.ReloadProjectAsync();
        }, "Add References");

        if (added.Count > 0)
        {
            ViewModel.AddLog($"Added {string.Join(", ", added)} to {Paper.Project.Settings.BibliographyFile}.");
        }
        string message = (added.Count > 0 ? $"Added: {string.Join(", ", added)}." : "Nothing was added.")
            + (failed.Count > 0 ? $"\n\nNot found: {string.Join(", ", failed)}." : string.Empty);
        if (added.Count > 0 && MessageBox.Show(this, message + "\n\nInsert a citation to them at the cursor?", "Add References", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
        {
            InsertInline("[" + string.Join("; ", added.Distinct().Select(k => "@" + k)) + "]");
        }
        else if (added.Count == 0)
        {
            MessageBox.Show(this, message, "Add References", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private async void AddReferencesFromZotero()
    {
        var missing = Paper.Context?.Citations.MissingKeys.ToList() ?? [];
        string? bibtex = null;
        bool ready = false;
        await RunAsync(async () =>
        {
            using var services = new ReferenceServices();
            ready = await services.IsZoteroReadyAsync();
            if (ready)
            {
                bibtex = await services.ExportFromZoteroAsync(missing);
            }
        }, "Zotero");
        if (!ready)
        {
            MessageBox.Show(this, "Zotero is not running, or the Better BibTeX plugin is not installed. Start Zotero (with Better BibTeX) and try again.", "Zotero", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var added = string.IsNullOrWhiteSpace(bibtex) ? [] : Paper.AddBibTeX(bibtex!, keepKeys: true);
        await Paper.ReloadProjectAsync();
        var stillMissing = Paper.Context?.Citations.MissingKeys ?? [];
        MessageBox.Show(this,
            (added.Count > 0 ? $"Added {added.Count} references from Zotero." : "Zotero had none of the missing references.")
            + (stillMissing.Count > 0 ? $"\n\nStill missing (not in Zotero under these keys): {string.Join(", ", stillMissing)}." : string.Empty),
            "Zotero", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private async void CheckReferencesOnline()
    {
        var items = Paper.References.Where(r => r.Doi.Length > 0).ToList();
        if (MessageBox.Show(this, $"Check the {items.Count} DOIs in the bibliography with doi.org and Crossref? Only the DOIs are sent.", "Check References Online", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
        {
            return;
        }
        List<DoiCheckResult> results = [];
        await RunAsync(async () =>
        {
            using var services = new ReferenceServices();
            results = await services.CheckDoisAsync(items.Select(i => (i.Key, i.Doi)));
        }, "Check References Online");

        foreach (var result in results)
        {
            var item = items.FirstOrDefault(i => i.Key == result.Key);
            if (item != null)
            {
                item.OnlineStatus = result.Notice ?? result.Problem ?? (result.Resolves ? "OK" : "Not found");
            }
        }
        var flagged = results.Where(r => r.Notice != null || !r.Resolves).ToList();
        string summary = flagged.Count == 0
            ? $"All {results.Count} DOIs resolve, and none has a retraction or expression of concern listed by Crossref."
            : string.Join("\n", flagged.Select(r => $"{r.Key}: {r.Notice ?? r.Problem ?? "does not resolve"}"));
        ViewModel.AddLog($"Checked {results.Count} DOIs online: {(flagged.Count == 0 ? "no problems" : $"{flagged.Count} flagged")}.");
        MessageBox.Show(this, summary, "Check References Online", MessageBoxButton.OK, flagged.Count == 0 ? MessageBoxImage.Information : MessageBoxImage.Warning);
    }

    private static void OpenFile(string path) => Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });

    // ---- Results ------------------------------------------------------------------------------

    private void AddResultsFile()
    {
        var dialog = new OpenFileDialog { Title = "Add Results File", Filter = "Results (*.csv;*.json)|*.csv;*.json|All files (*.*)|*.*" };
        if (dialog.ShowDialog(this) == true)
        {
            _ = RunAsync(() => ViewModel.AddResultsFileAsync(dialog.FileName), "Add Results File");
        }
    }

    private void OnResultValueDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is FrameworkElement { DataContext: ResultValueItem value })
        {
            InsertInline(value.Placeholder);
        }
    }

    // ---- Journal exports ------------------------------------------------------------------------

    private void ExportForJournal(bool reviewCopy)
    {
        var dlg = new SaveFileDialog
        {
            Title = reviewCopy ? "Export Review Copy" : "Export Submission Copy",
            Filter = "Word Document (*.docx)|*.docx",
            FileName = SuggestedFileName(reviewCopy ? " (review copy)" : " (submission)", ".docx"),
            InitialDirectory = ViewModel.WorkspacePath
        };
        if (dlg.ShowDialog(this) == true)
        {
            Run(() => ViewModel.ExportForJournal(dlg.FileName, reviewCopy, trackedChanges: false), "Export");
        }
    }

    private void ExportHighlights()
    {
        var dlg = new SaveFileDialog
        {
            Title = "Export Highlights",
            Filter = "Word Document (*.docx)|*.docx",
            FileName = SuggestedFileName(" - highlights", ".docx"),
            InitialDirectory = ViewModel.WorkspacePath
        };
        if (dlg.ShowDialog(this) == true && !ViewModel.ExportHighlights(dlg.FileName))
        {
            MessageBox.Show(this, "The manuscript has no “Highlights” section yet. Add one (a heading followed by one paragraph per highlight).", "Export Highlights", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private void AddRequiredSections()
    {
        var dialog = new RequiredSectionsDialog(Paper.Project.Journal.Name, Paper.MissingSections()) { Owner = this };
        if (dialog.ShowDialog() == true)
        {
            Run(() =>
            {
                foreach (var section in dialog.Selected)
                {
                    ViewModel.InsertRequiredSection(section);
                }
            }, "Add Required Sections");
        }
    }

    private async void MarkSubmitted()
    {
        ViewModel.FlushPendingMarkdownSync();
        if (ViewModel.HasUncommittedChanges)
        {
            var commit = new CommitDialog("Version submitted to the journal", ViewModel.CurrentBranch, ViewModel.AuthorName) { Owner = this };
            if (commit.ShowDialog() != true || !await RunAsync(() => ViewModel.CommitCurrentChangesAsync(commit.CommitMessage), "Commit"))
            {
                return;
            }
        }
        string tag = string.Empty;
        if (await RunAsync(async () => tag = await ViewModel.MarkSubmittedAsync(), "Mark as Submitted"))
        {
            MessageBox.Show(this, $"The current version is marked as {tag}. When the reviews come back, the revision is compared with it for the marked-up manuscript. The mark is sent to GitHub at the next sync.",
                "Mark as Submitted", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    // ---- Reviews -----------------------------------------------------------------------------------

    private async void AddReviewComments()
    {
        Dictionary<string, string> tags = [];
        if (ViewModel.HasCommits && !await RunAsync(async () => tags = await ViewModel.GetSubmissionTagsAsync(), "Add Reviewer Comments"))
        {
            return;
        }
        var commits = ViewModel.CommitGraph.ToDictionary(c => c.Sha, StringComparer.OrdinalIgnoreCase);
        var versions = tags.OrderByDescending(t => t.Key, StringComparer.Ordinal)
            .Select(t => new ReviewRoundDialog.SubmittedVersion(
                $"{t.Key}  ({(commits.TryGetValue(t.Value, out var c) ? $"{c.Timestamp.ToLocalTime():d}, {c.Message}" : t.Value[..7])})", t.Key, t.Value))
            .ToList();
        if (ViewModel.CommitGraph.FirstOrDefault() is { } latest)
        {
            versions.Add(new ReviewRoundDialog.SubmittedVersion($"Latest commit ({latest.ShortSha}, {latest.Message})", null, latest.Sha));
        }
        versions.AddRange(ViewModel.CommitGraph.Skip(1).Take(30).Select(c => new ReviewRoundDialog.SubmittedVersion($"{c.ShortSha}  {c.Timestamp.ToLocalTime():g}  {c.Message}", null, c.Sha)));

        var dialog = new ReviewRoundDialog($"Revision {Paper.Rounds.Count + 1}", versions) { Owner = this };
        if (dialog.ShowDialog() != true)
        {
            return;
        }
        Run(() =>
        {
            var round = Paper.AddRound(dialog.RoundName, dialog.ManuscriptNumber, dialog.Comments, dialog.Submitted?.Tag, dialog.Submitted?.Commit);
            ViewModel.AddLog($"Added {round.Comments.Count} reviewer comments for {round.Name}.");
            SelectWorkspaceTab(ReviewersTabIndex);
        }, "Add Reviewer Comments");
    }

    private void LinkCommentToParagraph()
    {
        var item = Paper.SelectedComment!;
        string? id = IsPanelShown(DocumentTabIndex) || _lastEditorParagraphId == null ? CaretBlockId() : _lastEditorParagraphId;
        if (id == null)
        {
            ShowEditorNotice("Put the cursor in the paragraph you changed on the Document tab first.");
            return;
        }
        Paper.LinkParagraph(item, id);
    }

    private async void ExportRevision()
    {
        var round = Paper.SelectedRound!;
        if (round.SubmittedTag == null && round.SubmittedCommit == null)
        {
            MessageBox.Show(this, "This round does not say which version the reviewers read. Add the comments again and choose the submitted version.", "Export Revision", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var folderDialog = new OpenFolderDialog { Title = "Choose a folder for the revision files", InitialDirectory = ViewModel.WorkspacePath };
        if (folderDialog.ShowDialog(this) != true)
        {
            return;
        }
        var style = MessageBox.Show(this,
            "Show the changes in the marked-up manuscript as Word tracked changes?\n\nYes: tracked changes (most journals).\nNo: the revised text with changes highlighted in yellow.",
            "Export Revision", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        if (style == MessageBoxResult.Cancel)
        {
            return;
        }

        RevisionPackage? package = null;
        if (await RunAsync(async () => package = await ViewModel.ExportRevisionPackageAsync(folderDialog.FolderName, round, highlightChanges: style == MessageBoxResult.No), "Export Revision"))
        {
            string where = package!.LocationsFromWord
                ? "Page and line numbers in the letter come from Word's layout of the revised manuscript."
                : "Word is not available, so changes are located by section and paragraph.";
            if (MessageBox.Show(this,
                $"Created:\n{Path.GetFileName(package.RevisedManuscript)}\n{Path.GetFileName(package.MarkedUpManuscript)}\n{Path.GetFileName(package.ResponseLetter)}\n\n{where} Submit the revised manuscript exactly as exported so the numbers match.\n\nOpen the folder?",
                "Export Revision", MessageBoxButton.YesNo, MessageBoxImage.Information) == MessageBoxResult.Yes)
            {
                Process.Start(new ProcessStartInfo(folderDialog.FolderName) { UseShellExecute = true });
            }
        }
    }

    private string? _lastEditorParagraphId;
}
