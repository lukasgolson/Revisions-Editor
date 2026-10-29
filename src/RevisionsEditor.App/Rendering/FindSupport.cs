using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using RevisionsEditor.Core.Checks;

namespace RevisionsEditor.App.Rendering;

/// <summary>
/// A match of the search. On the page: a text range inside an editable paragraph, or inside a read-only text
/// (the title, the title page, a paragraph with pending changes, a caption or table cell), whose
/// <see cref="Owner"/> is that text. In the Source view: a character range.
/// </summary>
internal sealed record FindMatch(TextPointer? Start, TextPointer? End, TextBlock? Owner, int Offset = 0, int Length = 0);

/// <summary>Where the find bar searches.</summary>
internal interface IFindTarget
{
    List<FindMatch> Search(string query, bool matchCase, bool wholeWord);
    /// <summary>The first match at or after the cursor, so a search starts where the writer is.</summary>
    int IndexNearCaret(IReadOnlyList<FindMatch> matches);
    /// <summary>Scrolls to the match and selects it (when it is editable text).</summary>
    void Reveal(FindMatch match);
    void Highlight(IReadOnlyList<FindMatch> matches, int current);
    /// <summary>Gives the focus back to the text.</summary>
    void Focus();
    /// <summary>Why nothing here can be replaced (the preview), or null.</summary>
    string? ReplaceUnavailableReason { get; }
    /// <summary>False for a match in read-only text (the title, a paragraph with pending changes, a caption).</summary>
    bool CanReplace(FindMatch match);
    void Replace(FindMatch match, string replacement);
    /// <summary>Replaces every match that can be (as one undo step); returns how many were replaced.</summary>
    int ReplaceAll(IReadOnlyList<FindMatch> matches, string replacement);
}

/// <summary>
/// Searches a FlowDocument shown in the editor (RichTextBox) or the preview (FlowDocumentScrollViewer): its
/// paragraphs, and the texts inside its read-only blocks.
/// </summary>
internal sealed class FlowDocumentFindTarget(Func<Control> host, FindHighlightLayer layer) : IFindTarget
{
    /// <summary>A text and where each of its runs starts in it.</summary>
    private sealed record Segment(string Text, List<(int Offset, TextPointer Start)> Runs, TextBlock? Owner);

    private FlowDocument? Document => host() switch
    {
        RichTextBox editor => editor.Document,
        FlowDocumentScrollViewer viewer => viewer.Document,
        _ => null
    };

    public List<FindMatch> Search(string query, bool matchCase, bool wholeWord)
    {
        var matches = new List<FindMatch>();
        if (Document is not { } document)
        {
            return matches;
        }
        foreach (var segment in Segments(document.Blocks))
        {
            foreach (var (start, length) in TextFinder.FindAll(segment.Text, query, matchCase, wholeWord))
            {
                if (PointerAt(segment, start, atEnd: false) is { } from && PointerAt(segment, start + length, atEnd: true) is { } to)
                {
                    matches.Add(new FindMatch(from, to, segment.Owner));
                }
            }
        }
        return matches;
    }

    private static TextPointer? PointerAt(Segment segment, int offset, bool atEnd)
    {
        // The run holding the character at `offset` (at the end of a match: the character before it).
        int look = atEnd ? offset - 1 : offset;
        for (int i = segment.Runs.Count - 1; i >= 0; i--)
        {
            if (segment.Runs[i].Offset <= look)
            {
                return segment.Runs[i].Start.GetPositionAtOffset(offset - segment.Runs[i].Offset);
            }
        }
        return null;
    }

    private static IEnumerable<Segment> Segments(IEnumerable<Block> blocks)
    {
        foreach (var block in blocks)
        {
            switch (block)
            {
                case Paragraph paragraph:
                    yield return Read(paragraph.ContentStart, paragraph.ContentEnd, null);
                    break;
                case List list:
                    foreach (var segment in list.ListItems.SelectMany(item => Segments(item.Blocks)))
                    {
                        yield return segment;
                    }
                    break;
                case Section section:
                    foreach (var segment in Segments(section.Blocks))
                    {
                        yield return segment;
                    }
                    break;
                case Table table:
                    foreach (var segment in Segments(table.RowGroups.SelectMany(g => g.Rows).SelectMany(r => r.Cells).SelectMany(c => c.Blocks)))
                    {
                        yield return segment;
                    }
                    break;
                case BlockUIContainer { Child: { } child }:
                    foreach (var text in TextBlocks(child))
                    {
                        yield return Read(text.ContentStart, text.ContentEnd, text);
                    }
                    break;
            }
        }
    }

    /// <summary>The texts inside a read-only block, in reading order (not section numbers or other inline labels).</summary>
    private static IEnumerable<TextBlock> TextBlocks(DependencyObject root)
    {
        if (root is TextBlock text)
        {
            yield return text;
            yield break;
        }
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            foreach (var found in TextBlocks(child))
            {
                yield return found;
            }
        }
    }

    private static Segment Read(TextPointer start, TextPointer end, TextBlock? owner)
    {
        var text = new System.Text.StringBuilder();
        var runs = new List<(int, TextPointer)>();
        for (var p = start; p != null && p.CompareTo(end) < 0; p = p.GetNextContextPosition(LogicalDirection.Forward))
        {
            switch (p.GetPointerContext(LogicalDirection.Forward))
            {
                case TextPointerContext.Text:
                    runs.Add((text.Length, p));
                    text.Append(p.GetTextInRun(LogicalDirection.Forward));
                    break;
                case TextPointerContext.ElementStart when p.GetAdjacentElement(LogicalDirection.Forward) is LineBreak:
                    text.Append(' ');
                    break;
                case TextPointerContext.EmbeddedElement:
                    // A section number or other label: not part of the text, and words around it stay apart.
                    text.Append(' ');
                    break;
            }
        }
        return new Segment(text.ToString(), runs, owner);
    }

    public int IndexNearCaret(IReadOnlyList<FindMatch> matches)
    {
        if (host() is not RichTextBox editor || matches.Count == 0)
        {
            return 0;
        }
        var caret = editor.Selection.Start;
        for (int i = 0; i < matches.Count; i++)
        {
            if (matches[i].Owner == null && matches[i].Start!.IsInSameDocument(caret) && matches[i].Start!.CompareTo(caret) >= 0)
            {
                return i;
            }
        }
        return 0;
    }

    public void Reveal(FindMatch match)
    {
        var control = host();
        if (match.Owner != null)
        {
            // Read-only text: scroll it into view around the match.
            var rect = match.Start!.GetCharacterRect(LogicalDirection.Forward);
            match.Owner.BringIntoView(rect.IsEmpty ? new Rect(0, 0, 1, 1) : new Rect(rect.X, rect.Y - 40, Math.Max(1, rect.Width), rect.Height + 80));
            if (control is RichTextBox editor)
            {
                editor.Selection.Select(editor.Selection.Start, editor.Selection.Start);
            }
        }
        else
        {
            if (control is RichTextBox editor)
            {
                editor.Selection.Select(match.Start!, match.End!);
            }
            ScrollTo(control, match.Start!);
        }
        layer.Refresh();
    }

    /// <summary>Scrolls so the position is in the upper third of the view.</summary>
    private static void ScrollTo(Control control, TextPointer position)
    {
        var rect = position.GetCharacterRect(LogicalDirection.Forward);
        if (rect.IsEmpty)
        {
            position.Paragraph?.BringIntoView();
            return;
        }
        double height = control.ActualHeight;
        if (rect.Top >= height * 0.15 && rect.Bottom <= height * 0.85)
        {
            return;
        }
        if (FindScrollViewer(control) is { } scroller)
        {
            scroller.ScrollToVerticalOffset(scroller.VerticalOffset + rect.Top - height / 3);
        }
    }

    internal static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is ScrollViewer viewer)
            {
                return viewer;
            }
            if (FindScrollViewer(child) is { } found)
            {
                return found;
            }
        }
        return null;
    }

    public void Highlight(IReadOnlyList<FindMatch> matches, int current) => layer.Show(host(), matches, current);

    public void Focus() => host().Focus();

    public string? ReplaceUnavailableReason => host() switch
    {
        RichTextBox { IsReadOnly: true } => "The manuscript cannot be edited right now.",
        RichTextBox => null,
        _ => "The preview cannot be edited: switch Preview off (F8) to replace."
    };

    public bool CanReplace(FindMatch match) => match.Owner == null && host() is RichTextBox { IsReadOnly: false };

    public void Replace(FindMatch match, string replacement)
    {
        if (CanReplace(match) && host() is RichTextBox editor)
        {
            editor.BeginChange();
            try
            {
                var range = new TextRange(match.Start!, match.End!) { Text = replacement };
                editor.Selection.Select(range.End, range.End);
            }
            finally
            {
                editor.EndChange();
            }
        }
    }

    public int ReplaceAll(IReadOnlyList<FindMatch> matches, string replacement)
    {
        if (host() is not RichTextBox editor)
        {
            return 0;
        }
        int replaced = 0;
        editor.BeginChange();
        try
        {
            // From the last match back, so the positions of the earlier ones stay where they were.
            foreach (var match in matches.Reverse().Where(CanReplace))
            {
                _ = new TextRange(match.Start!, match.End!) { Text = replacement };
                replaced++;
            }
        }
        finally
        {
            editor.EndChange();
        }
        return replaced;
    }
}

/// <summary>Searches the Markdown source (a TextBox): the current match is selected.</summary>
internal sealed class TextBoxFindTarget(TextBox box) : IFindTarget
{
    public List<FindMatch> Search(string query, bool matchCase, bool wholeWord)
        => TextFinder.FindAll(box.Text, query, matchCase, wholeWord).Select(m => new FindMatch(null, null, null, m.Start, m.Length)).ToList();

    public int IndexNearCaret(IReadOnlyList<FindMatch> matches)
    {
        int index = matches.ToList().FindIndex(m => m.Offset >= box.SelectionStart);
        return index < 0 ? 0 : index;
    }

    public void Reveal(FindMatch match)
    {
        box.Select(match.Offset, match.Length);
        int line = box.GetLineIndexFromCharacterIndex(match.Offset);
        if (line >= 0)
        {
            box.ScrollToLine(Math.Max(0, line - 3));
        }
    }

    public void Highlight(IReadOnlyList<FindMatch> matches, int current)
    {
    }

    public void Focus() => box.Focus();

    public string? ReplaceUnavailableReason => box.IsReadOnly ? "The source cannot be edited right now." : null;

    public bool CanReplace(FindMatch match) => !box.IsReadOnly && match.Offset + match.Length <= box.Text.Length;

    public void Replace(FindMatch match, string replacement)
    {
        if (CanReplace(match))
        {
            box.Select(match.Offset, match.Length);
            box.SelectedText = replacement;
            box.Select(match.Offset + replacement.Length, 0);
        }
    }

    public int ReplaceAll(IReadOnlyList<FindMatch> matches, string replacement)
    {
        int replaced = 0;
        box.BeginChange();
        try
        {
            foreach (var match in matches.Reverse().Where(CanReplace))
            {
                box.Select(match.Offset, match.Length);
                box.SelectedText = replacement;
                replaced++;
            }
        }
        finally
        {
            box.EndChange();
        }
        return replaced;
    }
}

/// <summary>
/// Draws the matches over the page (all in yellow, the current one in orange), without changing the document,
/// so searching never marks the manuscript as edited. Only matches in view are measured.
/// </summary>
internal sealed class FindHighlightLayer : FrameworkElement
{
    private static readonly Brush MatchFill = Frozen(Color.FromArgb(0x70, 0xFF, 0xD8, 0x00));
    private static readonly Brush CurrentFill = Frozen(Color.FromArgb(0x90, 0xFF, 0x8C, 0x00));
    private static readonly Pen CurrentPen = new Pen(Frozen(Color.FromRgb(0xC8, 0x5A, 0x00)), 1).GetAsFrozen() as Pen ?? new Pen(Brushes.DarkOrange, 1);
    private const int MaxDrawn = 2000;

    private Control? _host;
    private IReadOnlyList<FindMatch> _matches = [];
    private int _current = -1;
    private readonly DispatcherTimer _timer;
    private readonly HashSet<Control> _hooked = [];

    public FindHighlightLayer()
    {
        IsHitTestVisible = false;
        _timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(30) };
        _timer.Tick += (_, _) =>
        {
            _timer.Stop();
            InvalidateVisual();
        };
    }

    private static Brush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    public void Show(Control host, IReadOnlyList<FindMatch> matches, int current)
    {
        _host = host;
        _matches = matches;
        _current = current;
        if (_hooked.Add(host))
        {
            host.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler((_, _) => Refresh()));
            host.SizeChanged += (_, _) => Refresh();
        }
        Refresh();
    }

    public void Clear()
    {
        _matches = [];
        _current = -1;
        InvalidateVisual();
    }

    public void Refresh()
    {
        _timer.Stop();
        _timer.Start();
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (_host is not { IsVisible: true } host || _matches.Count == 0)
        {
            return;
        }
        var bounds = new Rect(0, 0, ActualWidth, ActualHeight);
        int drawn = 0;
        for (int i = 0; i < _matches.Count && drawn < MaxDrawn; i++)
        {
            var match = _matches[i];
            if (match.Start is not { } start || match.End is not { } end)
            {
                continue;
            }
            try
            {
                Visual scope = match.Owner ?? (Visual)host;
                if (match.Owner is { IsVisible: false })
                {
                    continue;
                }
                var toLayer = scope.TransformToVisual(this);
                var first = toLayer.TransformBounds(start.GetCharacterRect(LogicalDirection.Forward));
                if (first.IsEmpty || first.Bottom < -200 || first.Top > ActualHeight + 200)
                {
                    continue; // out of view: not measured
                }
                foreach (var rect in RangeRects(start, end))
                {
                    var r = toLayer.TransformBounds(rect);
                    r.Intersect(bounds);
                    if (!r.IsEmpty && r.Width > 0)
                    {
                        dc.DrawRectangle(i == _current ? CurrentFill : MatchFill, i == _current ? CurrentPen : null, r);
                    }
                }
                drawn++;
            }
            catch (InvalidOperationException)
            {
                // The text left the page (re-rendered); the next search draws it again.
            }
        }
    }

    /// <summary>One rectangle per line of the range, from each character's leading and trailing edges.</summary>
    private static IEnumerable<Rect> RangeRects(TextPointer start, TextPointer end)
    {
        Rect? line = null;
        for (var p = start; p != null && p.CompareTo(end) < 0;)
        {
            var next = p.GetNextInsertionPosition(LogicalDirection.Forward);
            if (next == null || next.CompareTo(end) > 0)
            {
                break;
            }
            var lead = p.GetCharacterRect(LogicalDirection.Forward);
            var trail = next.GetCharacterRect(LogicalDirection.Backward);
            if (!lead.IsEmpty && !trail.IsEmpty)
            {
                var ch = new Rect(new Point(Math.Min(lead.Left, trail.Left), lead.Top), new Point(Math.Max(lead.Left, trail.Left), lead.Bottom));
                if (line is { } current && Math.Abs(current.Top - ch.Top) < 1)
                {
                    current.Union(ch);
                    line = current;
                }
                else
                {
                    if (line is { } done)
                    {
                        yield return done;
                    }
                    line = ch;
                }
            }
            p = next;
        }
        if (line is { } last)
        {
            yield return last;
        }
    }
}

