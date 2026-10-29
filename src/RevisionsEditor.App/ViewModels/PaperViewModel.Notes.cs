using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using RevisionsEditor.Core.Checks;
using RevisionsEditor.Core.Scholarly;

namespace RevisionsEditor.App.ViewModels;

/// <summary>A note in the Notes panel.</summary>
public sealed class NoteRow : INotifyPropertyChanged
{
    private readonly Action<NoteRow> _changed;

    public NoteRow(PaperNote note, Action<NoteRow> changed)
    {
        Note = note;
        _changed = changed;
    }

    public PaperNote Note { get; }
    public string Text => Note.Text;

    public bool Done
    {
        get => Note.Done;
        set
        {
            if (Note.Done != value)
            {
                Note.Done = value;
                Note.Updated = DateTimeOffset.Now;
                OnPropertyChanged();
                _changed(this);
            }
        }
    }

    public bool Pinned
    {
        get => Note.Pinned;
        set
        {
            if (Note.Pinned != value)
            {
                Note.Pinned = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(PinGlyph));
                _changed(this);
            }
        }
    }

    /// <summary>A filled pin for a pinned note, an outline otherwise (Segoe Fluent Icons).</summary>
    public string PinGlyph => Pinned ? "" : "";

    public bool HasParagraph => Note.ParagraphId != null;

    /// <summary>When, and what it is about: "3 Nov 14:05 · about “Tree heights from…” · #methods".</summary>
    public string Details
    {
        get
        {
            var parts = new List<string> { FormatWhen(Note.Created) };
            if (Note.ParagraphPreview is { Length: > 0 } preview)
            {
                parts.Add($"about “{preview}”");
            }
            if (Note.Tags.Count > 0)
            {
                parts.Add(string.Join(" ", Note.Tags.Select(t => "#" + t)));
            }
            return string.Join(" · ", parts);
        }
    }

    private static string FormatWhen(DateTimeOffset when)
    {
        var local = when.ToLocalTime();
        var today = DateTime.Today;
        return local.Date == today ? $"Today {local:HH:mm}"
            : local.Date == today.AddDays(-1) ? $"Yesterday {local:HH:mm}"
            : local.Year == today.Year ? local.ToString("d MMM HH:mm", CultureInfo.CurrentCulture)
            : local.ToString("d MMM yyyy", CultureInfo.CurrentCulture);
    }

    internal void Refresh()
    {
        OnPropertyChanged(nameof(Text));
        OnPropertyChanged(nameof(Details));
        OnPropertyChanged(nameof(HasParagraph));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>The paper's notes ("shower thoughts") and the placeholders still in the manuscript.</summary>
public sealed partial class PaperViewModel
{
    private List<PaperNote> _notes = [];
    private string _noteSearch = string.Empty;
    private bool _showDoneNotes;

    public RangeObservableCollection<NoteRow> Notes { get; } = [];

    /// <summary>The placeholders in the manuscript (text, figures and tables), from the last check.</summary>
    public RangeObservableCollection<IssueItem> Placeholders { get; } = [];

    public string NoteSearch
    {
        get => _noteSearch;
        set
        {
            if (_noteSearch != value)
            {
                _noteSearch = value;
                OnPropertyChanged();
                ApplyNoteFilter();
            }
        }
    }

    public bool ShowDoneNotes
    {
        get => _showDoneNotes;
        set
        {
            if (_showDoneNotes != value)
            {
                _showDoneNotes = value;
                OnPropertyChanged();
                ApplyNoteFilter();
            }
        }
    }

    public string NotesSummary
    {
        get
        {
            int open = _notes.Count(n => !n.Done), done = _notes.Count - open;
            if (_notes.Count == 0)
            {
                return "No notes yet. Type an idea above and press Enter.";
            }
            string summary = $"{open} open note{(open == 1 ? string.Empty : "s")}" + (done > 0 ? $", {done} done" : string.Empty);
            return Notes.Count < (_showDoneNotes ? _notes.Count : open) ? $"{summary} ({Notes.Count} shown)" : summary;
        }
    }

    public string PlaceholderSummary => Placeholders.Count == 0
        ? "No placeholders. Mark stand-in text with Insert › Placeholder (Ctrl+Alt+P)."
        : $"{Placeholders.Count} placeholder{(Placeholders.Count == 1 ? string.Empty : "s")} to fill in before submitting";

    private void LoadNotes()
    {
        _notes = _main.HasWorkspace ? PaperNotes.Load(_main.WorkspacePath) : [];
        ApplyNoteFilter();
    }

    private void ApplyNoteFilter()
    {
        Notes.ReplaceAll(PaperNotes.Filter(_notes, _noteSearch, _showDoneNotes).Select(n => new NoteRow(n, OnNoteChanged)));
        OnPropertyChanged(nameof(NotesSummary));
    }

    private void OnNoteChanged(NoteRow row)
    {
        SaveNotes();
        ApplyNoteFilter();
    }

    private void SaveNotes()
    {
        if (!_main.HasWorkspace)
        {
            return;
        }
        PaperNotes.Save(_main.WorkspacePath, _notes);
        _main.MarkProjectFilesChanged();
    }

    /// <summary>Adds a note, about a paragraph when <paramref name="paragraphId"/> is given.</summary>
    public PaperNote AddNote(string text, string? paragraphId = null)
    {
        var note = new PaperNote { Text = text.Trim(), ParagraphId = paragraphId, ParagraphPreview = paragraphId != null ? ParagraphPreview(paragraphId) : null };
        _notes.Add(note);
        SaveNotes();
        ApplyNoteFilter();
        return note;
    }

    public void EditNote(PaperNote note, string text, string? paragraphId)
    {
        note.Text = text.Trim();
        if (paragraphId != note.ParagraphId)
        {
            note.ParagraphId = paragraphId;
            note.ParagraphPreview = paragraphId != null ? ParagraphPreview(paragraphId) : null;
        }
        note.Updated = DateTimeOffset.Now;
        SaveNotes();
        ApplyNoteFilter();
    }

    public void DeleteNote(PaperNote note)
    {
        _notes.Remove(note);
        SaveNotes();
        ApplyNoteFilter();
    }

    /// <summary>The first words of a paragraph, for a note about it.</summary>
    public string? ParagraphPreview(string paragraphId)
    {
        var block = _main.FindBlock(paragraphId);
        if (block == null)
        {
            return null;
        }
        string text = Core.Serialization.CriticMarkupSerializer.ResolveCriticMarkupText(block.Text, Core.Models.MarkupDisplayMode.NoMarkupFinal).Replace("*", string.Empty).Trim();
        return text.Length <= 48 ? text : text[..48].TrimEnd() + "…";
    }

    /// <summary>Lists the placeholders found by the last check (called with the checks' results).</summary>
    private void ApplyPlaceholders(IReadOnlyDictionary<string, string> locations)
    {
        var blocks = _main.CurrentDocument.Blocks;
        Placeholders.ReplaceAll(_allIssues
            .Where(i => i.Category == ManuscriptChecks.Placeholders)
            .OrderBy(i => i.ParagraphId == null ? -1 : blocks.FindIndex(b => b.ParagraphId == i.ParagraphId))
            .ThenBy(i => i.Start)
            .Select(i => new IssueItem(i, i.ParagraphId != null && locations.TryGetValue(i.ParagraphId, out var where) ? where : "Whole paper")));
        OnPropertyChanged(nameof(PlaceholderSummary));
    }
}
