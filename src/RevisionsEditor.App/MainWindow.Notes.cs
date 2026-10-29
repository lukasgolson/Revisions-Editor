using System.Windows;
using System.Windows.Documents;
using System.Windows.Input;
using RevisionsEditor.App.Dialogs;
using RevisionsEditor.App.Rendering;
using RevisionsEditor.App.ViewModels;
using RevisionsEditor.Core.Scholarly;

namespace RevisionsEditor.App;

/// <summary>Notes about the paper ("shower thoughts") and draft placeholders.</summary>
public partial class MainWindow
{
    private void InitializeNotes()
    {
        Func<object?, bool> workspace = _ => ViewModel.HasWorkspace;
        Bind(AppCommands.InsertPlaceholder, _ => EditPlaceholder(), workspace);
        Bind(AppCommands.NewNote, _ => NewNote(), workspace);
        Bind(AppCommands.EditNote, p => EditNote((NoteRow)p!), p => p is NoteRow);
        Bind(AppCommands.DeleteNote, p => Paper.DeleteNote(((NoteRow)p!).Note), p => p is NoteRow);
        Bind(AppCommands.GoToNoteParagraph, p => GoToNoteParagraph((NoteRow)p!), p => p is NoteRow { HasParagraph: true });
        // A double-click on a placeholder in the text edits it.
        ManuscriptEditor.PreviewMouseDoubleClick += (_, e) =>
        {
            if (PlaceholderRunAt(ManuscriptEditor.GetPositionFromPoint(e.GetPosition(ManuscriptEditor), true)) is { } run)
            {
                e.Handled = true;
                Dispatcher.BeginInvoke(() => EditPlaceholder(run));
            }
        };
    }

    /// <summary>The paragraph the cursor is in on the Document page, for a note about it (null elsewhere).</summary>
    private string? ParagraphAtCursorForNote()
        => IsPanelShown(DocumentTabIndex) && !_preview && ViewModel.CurrentDocument.Blocks.Count > 0 ? CaretBlockId() : null;

    private void NewNote()
    {
        string? paragraphId = ParagraphAtCursorForNote();
        var dialog = new NoteDialog(null, paragraphId != null ? Paper.ParagraphPreview(paragraphId) : null, aboutParagraph: paragraphId != null) { Owner = this };
        if (dialog.ShowDialog() == true)
        {
            Run(() =>
            {
                Paper.AddNote(dialog.Text, dialog.AboutParagraph ? paragraphId : null);
                ShowEditorNotice("Note added to the Notes panel.");
            }, "New Note");
        }
    }

    private void EditNote(NoteRow row)
    {
        var note = row.Note;
        string? here = ParagraphAtCursorForNote();
        // The note's own paragraph if it has one; otherwise it can be tied to the one at the cursor.
        string? paragraphId = note.ParagraphId ?? here;
        var dialog = new NoteDialog(note.Text, paragraphId != null ? Paper.ParagraphPreview(paragraphId) ?? note.ParagraphPreview : null, aboutParagraph: note.ParagraphId != null) { Owner = this };
        if (dialog.ShowDialog() == true)
        {
            Run(() => Paper.EditNote(note, dialog.Text, dialog.AboutParagraph ? paragraphId : null), "Edit Note");
        }
    }

    private void GoToNoteParagraph(NoteRow row)
    {
        if (row.Note.ParagraphId is { } id && ViewModel.FindBlock(id) != null)
        {
            GoToParagraph(id);
        }
        else
        {
            ShowEditorNotice("The paragraph this note was about is no longer in the manuscript.");
        }
    }

    private void OnQuickNoteKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            AddQuickNote();
        }
    }

    private void OnAddQuickNote(object sender, RoutedEventArgs e) => AddQuickNote();

    private void AddQuickNote()
    {
        string text = QuickNoteBox.Text.Trim();
        if (text.Length == 0 || !ViewModel.HasWorkspace)
        {
            return;
        }
        string? paragraphId = QuickNoteAboutParagraph.IsChecked == true ? LastParagraphForNote() : null;
        Run(() =>
        {
            Paper.AddNote(text, paragraphId);
            QuickNoteBox.Clear();
        }, "New Note");
    }

    /// <summary>The paragraph the cursor was last in on the Document page (the focus is in the Notes panel now).</summary>
    private string? LastParagraphForNote()
    {
        if (_preview || ViewModel.CurrentDocument.Blocks.Count == 0)
        {
            return null;
        }
        return CaretParagraph?.Tag is EditorBlockTag { ParagraphId: { } id } && ViewModel.FindBlock(id) != null ? id : CaretBlockId();
    }

    private void OnNoteDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is FrameworkElement { DataContext: NoteRow row } source && source is not System.Windows.Controls.Primitives.ToggleButton)
        {
            EditNote(row);
        }
    }

    private void OnPlaceholderDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is FrameworkElement { DataContext: IssueItem { Issue.ParagraphId: { } id } item })
        {
            GoToParagraph(id, item);
        }
    }

    /// <summary>The run of a placeholder in the editor at a position, if there is one there.</summary>
    private static Run? PlaceholderRunAt(TextPointer? position)
        => position?.Parent is Run { Tag: var tag } run && ReferenceEquals(tag, FlowDocumentBuilder.PlaceholderTag) ? run : null;

    /// <summary>
    /// Inserts a placeholder for the selected text (or at the cursor), or edits the placeholder at the cursor or
    /// <paramref name="existing"/>.
    /// </summary>
    private void EditPlaceholder(Run? existing = null)
    {
        if (_preview)
        {
            TogglePreview();
        }
        if (!IsPanelShown(DocumentTabIndex))
        {
            SelectWorkspaceTab(DocumentTabIndex);
        }
        existing ??= PlaceholderRunAt(ManuscriptEditor.Selection.Start);
        var found = existing != null ? DraftPlaceholders.Find(existing.Text).FirstOrDefault() : null;
        string selected = existing == null ? ManuscriptEditor.Selection.Text.Trim() : string.Empty;
        if (selected.Contains('\n'))
        {
            ShowEditorNotice("Select text within one paragraph to make it a placeholder.");
            return;
        }
        var dialog = new PlaceholderDialog(found?.Text ?? selected, found?.Note ?? string.Empty, existing != null) { Owner = this };
        if (dialog.ShowDialog() != true)
        {
            return;
        }
        string syntax = DraftPlaceholders.Format(dialog.PlaceholderText, dialog.Note);
        Run(() =>
        {
            if (existing != null)
            {
                var range = new TextRange(existing.ContentStart, existing.ContentEnd);
                ManuscriptEditor.Selection.Select(range.Start, range.End);
            }
            ManuscriptEditor.Selection.Text = syntax;
            ManuscriptEditor.Selection.Select(ManuscriptEditor.Selection.End, ManuscriptEditor.Selection.End);
            FlushEditorEdits();
            RenderManuscriptView(preserveCaret: true); // drawn as a placeholder
            Paper.ScheduleChecks(immediately: true);
            ShowEditorNotice(existing != null ? "Placeholder updated." : "Placeholder added: it is listed in the Notes panel until you fill it in.");
        }, "Placeholder");
    }
}
