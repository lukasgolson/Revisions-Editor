using System.Windows;
using System.Windows.Controls;

namespace RevisionsEditor.App.Dialogs;

/// <summary>Writes or edits a note about the paper, perhaps about the paragraph at the cursor.</summary>
public sealed class NoteDialog : FormWindow
{
    private readonly TextBox _text;
    private readonly CheckBox? _about;

    /// <param name="paragraphPreview">The first words of the paragraph the note can be about, or null.</param>
    /// <param name="aboutParagraph">Whether the note is about that paragraph to begin with.</param>
    public NoteDialog(string? text, string? paragraphPreview, bool aboutParagraph) : base(text == null ? "New Note" : "Edit Note", 520)
    {
        OkText = text == null ? "_Add" : "_Save";
        AddIntro("Ideas, questions and to-dos for the paper. Notes are kept with the paper (committed and synced) and listed in the Notes panel; add #tags to find them again.");
        _text = AddTextBox("_Note:", text ?? string.Empty, multiline: true, lines: 4);
        if (paragraphPreview != null)
        {
            _about = new CheckBox { Content = $"_About the paragraph “{paragraphPreview}”", IsChecked = aboutParagraph, Margin = new Thickness(0, 10, 0, 0) };
            AddElement(_about);
        }
        AddHint("Ctrl+Enter adds the note.");
        SetValidation(() => _text.Text.Trim().Length > 0);
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Enter && System.Windows.Input.Keyboard.Modifiers == System.Windows.Input.ModifierKeys.Control && _text.Text.Trim().Length > 0)
            {
                e.Handled = true;
                DialogResult = true;
            }
        };
        Loaded += (_, _) =>
        {
            _text.Focus();
            _text.CaretIndex = _text.Text.Length;
        };
    }

    public string Text => _text.Text.Trim();
    public bool AboutParagraph => _about?.IsChecked == true;
}

/// <summary>Marks text as a placeholder (a stand-in still to be written or checked), with what it stands for.</summary>
public sealed class PlaceholderDialog : FormWindow
{
    private readonly TextBox _text;
    private readonly TextBox _note;

    public PlaceholderDialog(string text, string note, bool existing) : base(existing ? "Edit Placeholder" : "Insert Placeholder", 520)
    {
        OkText = existing ? "_Save" : "_Insert";
        AddIntro("A placeholder stands in for text still to come: a number, a sentence, a section. It is highlighted on the page and in drafts, listed in the Notes panel, and the checks remind you of it before submission.");
        _text = AddTextBox("_Placeholder text (what the page shows):", text);
        _note = AddTextBox("_What it stands for (optional note):", note, multiline: true, lines: 2);
        AddHint("In the text it is written [[placeholder text|note]]; type over it when the real text is ready, or remove the brackets.");
        SetValidation(() => _text.Text.Trim().Length > 0 || _note.Text.Trim().Length > 0);
        Loaded += (_, _) =>
        {
            var box = _text.Text.Length > 0 ? _note : _text;
            box.Focus();
            box.SelectAll();
        };
    }

    public string PlaceholderText => _text.Text.Trim();
    public string Note => _note.Text.Trim().Replace("\r\n", " ").Replace('\n', ' ');
}
