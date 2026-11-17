using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using RevisionsEditor.App.Dialogs;
using RevisionsEditor.App.Rendering;
using RevisionsEditor.Core.Checks;
using RevisionsEditor.Core.Reviews;

namespace RevisionsEditor.App;

/// <summary>Line numbers, Go to Line and Word Count.</summary>
public partial class MainWindow
{
    private LineNumberMargin? _lineNumbers;
    internal LineNumberMargin? LineNumbers => _lineNumbers;
    private EditorSpellChecker? _spellChecker;
    internal EditorSpellChecker? SpellChecker => _spellChecker;

    private void InitializeTools()
    {
        _lineNumbers = new LineNumberMargin(ManuscriptEditor) { Enabled = _settings.ShowLineNumbers };
        ((Grid)ManuscriptEditor.Parent).Children.Add(_lineNumbers);
        _lineNumbers.Updated += UpdateEditorPosition;
        LineNumbersMenuItem.IsChecked = _settings.ShowLineNumbers;

        // Spelling is checked in the background (WPF's own checking ran on the UI thread and stalled typing);
        // WPF's is kept only where Windows' spell checker is not available.
        ManuscriptEditor.Loaded += (_, _) =>
        {
            if (_spellChecker != null) return;
            _spellChecker = EditorSpellChecker.TryAttach(ManuscriptEditor);
            SpellCheck.SetIsEnabled(ManuscriptEditor, _spellChecker == null);
        };

        Func<object?, bool> workspace = _ => ViewModel.HasWorkspace;
        Bind(AppCommands.ToggleLineNumbers, _ => ToggleLineNumbers(), allowWhileBusy: true);
        Bind(AppCommands.GoToLine, _ => GoToLine(), workspace);
        Bind(AppCommands.WordCount, _ => ShowWordCount(), workspace);
        Bind(AppCommands.GoToReviewerLine, p => _ = GoToSubmittedLineAsync((LineReference)p!), p => p is LineReference && Paper.SelectedRound != null);
    }

    private void ToggleLineNumbers()
    {
        _settings.ShowLineNumbers = !_settings.ShowLineNumbers;
        _settings.Save();
        LineNumbersMenuItem.IsChecked = _settings.ShowLineNumbers;
        if (_lineNumbers != null)
        {
            _lineNumbers.Enabled = _settings.ShowLineNumbers;
        }
    }

    /// <summary>The cursor's line and the selection's word count, in the status bar.</summary>
    private void UpdateEditorPosition()
    {
        if (_lineNumbers == null || !ViewModel.HasWorkspace || _preview || !IsPanelShown(DocumentTabIndex))
        {
            ViewModel.EditorPositionText = string.Empty;
            return;
        }
        string position = _lineNumbers.CaretLine > 0 ? $"Line {_lineNumbers.CaretLine:N0} of {_lineNumbers.LineCount:N0}" : string.Empty;
        var selection = ManuscriptEditor.Selection;
        if (!selection.IsEmpty)
        {
            int selected = ManuscriptStatistics.CountWords(selection.Text);
            int total = Paper.Statistics is { } stats ? stats.Total.Words - stats.References.Words : 0;
            position += total > 0 ? $"  |  {selected:N0} of {total:N0} words selected" : $"  |  {selected:N0} words selected";
        }
        ViewModel.EditorPositionText = position;
    }

    private async void GoToLine()
    {
        FlushEditorEdits();
        Dictionary<string, string> tags = [];
        if (ViewModel.HasCommits && !await RunAsync(async () => tags = await ViewModel.GetSubmissionTagsAsync(), "Go to Line"))
        {
            return;
        }
        if (_preview)
        {
            TogglePreview();
        }
        SelectWorkspaceTab(DocumentTabIndex);
        var versions = tags.Keys.OrderByDescending(k => k, StringComparer.Ordinal).ToList();
        var dialog = new GoToLineDialog(Math.Max(1, _lineNumbers?.LineCount ?? 1), versions) { Owner = this };
        if (dialog.ShowDialog() != true)
        {
            return;
        }
        if (dialog.SubmittedVersion is { } version)
        {
            await GoToSubmittedLineAsync(new LineReference(0, 0, dialog.Page, dialog.Line, dialog.Line), version);
        }
        else if (_lineNumbers?.GoToLine(dialog.Line) != true)
        {
            ShowEditorNotice($"There is no line {dialog.Line}; the page has {_lineNumbers?.LineCount ?? 0} lines.");
        }
    }

    /// <summary>
    /// Shows the paragraph a reviewer means by a line number of the submitted version (from its line map,
    /// recorded with Word when the version was marked as submitted).
    /// </summary>
    private async Task GoToSubmittedLineAsync(LineReference reference, string? version = null)
    {
        var round = Paper.SelectedRound;
        version ??= round?.SubmittedTag ?? round?.SubmittedCommit;
        if (version == null)
        {
            ShowEditorNotice("This review round does not say which version the reviewers read.");
            return;
        }

        SubmittedLineMap? map = null;
        if (!await RunAsync(async () => map = await ViewModel.GetLineMapAsync(version), "Go to Line"))
        {
            return;
        }
        if (map == null)
        {
            MessageBox.Show(this, "Finding the reviewers' line numbers needs Microsoft Word, to lay out the submitted version as the reviewers saw it.",
                "Go to Line", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var target = ViewModel.FindSubmittedLine(map, reference.FirstLine, reference.Page);
        if (target == null)
        {
            ShowEditorNotice($"The submitted version has no {reference.Label}.");
            return;
        }

        var (paragraphId, fraction, exact) = target.Value;
        GoToParagraph(paragraphId);
        // Put the cursor about as far into the paragraph as the line was.
        if (fraction > 0 && CaretParagraph is { } paragraph)
        {
            int length = new TextRange(paragraph.ContentStart, paragraph.ContentEnd).Text.Length;
            var pointer = OffsetToPointer(paragraph, (int)(length * fraction));
            if (pointer != null)
            {
                ManuscriptEditor.CaretPosition = pointer;
            }
        }
        _lastEditorParagraphId = paragraphId;
        ShowEditorNotice(exact
            ? $"{reference.Label} of {version} is in this paragraph."
            : $"The text at {reference.Label} of {version} has been removed since; this is the paragraph that followed it.");
    }

    private void ShowWordCount()
    {
        FlushEditorEdits();
        var stats = ViewModel.ComputeStatistics();
        int? selected = !_preview && !ManuscriptEditor.Selection.IsEmpty ? ManuscriptStatistics.CountWords(ManuscriptEditor.Selection.Text) : null;
        var dialog = new WordCountDialog(stats, Paper.Project.Journal.Name, selected, _lineNumbers?.LineCount ?? 0) { Owner = this };
        if (dialog.ShowDialog() == true && dialog.SelectedParagraphId is { } id)
        {
            GoToParagraph(id);
        }
    }
}
