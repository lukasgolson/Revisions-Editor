using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;

namespace RevisionsEditor.App.Rendering;

/// <summary>
/// Line numbers in the left margin of the manuscript page, counted continuously over the text as it is laid
/// out on screen, as Word's line numbering does. Lines in tables are not numbered. The numbers follow the
/// editor's own layout, so they differ from those of an exported Word document.
/// </summary>
internal sealed class LineNumberMargin : FrameworkElement
{
    /// <summary>Marks a text element inside a figure or table view whose lines are not numbered (table cells).</summary>
    public const string Unnumbered = "unnumbered";

    private static readonly Brush NumberBrush = CreateBrush(Color.FromRgb(0x8A, 0x8A, 0x8A));
    private static readonly Typeface NumberFont = new("Segoe UI");

    private readonly RichTextBox _editor;
    private readonly DispatcherTimer _timer;
    private readonly List<(int Number, double Top, double Height)> _visible = [];
    private readonly List<(int Number, TextPointer? Pointer, FrameworkElement? Element)> _lineStarts = [];
    private bool _enabled;

    public LineNumberMargin(RichTextBox editor)
    {
        _editor = editor;
        IsHitTestVisible = false;
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(120) };
        _timer.Tick += (_, _) =>
        {
            _timer.Stop();
            Recompute();
        };
        editor.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler((_, _) => Schedule()));
        editor.TextChanged += (_, _) => Schedule();
        editor.SelectionChanged += (_, _) => Schedule();
        editor.SizeChanged += (_, _) => Schedule();
    }

    /// <summary>The line the cursor is on (0 when unknown).</summary>
    public int CaretLine { get; private set; }

    /// <summary>The number of lines of text.</summary>
    public int LineCount { get; private set; }

    public event Action? Updated;

    public bool Enabled
    {
        get => _enabled;
        set
        {
            _enabled = value;
            Schedule();
        }
    }

    public void Schedule()
    {
        _timer.Stop();
        _timer.Start();
    }

    /// <summary>Puts the cursor at the start of a line and scrolls it into view. False if there is no such line.</summary>
    public bool GoToLine(int number)
    {
        Recompute();
        var line = _lineStarts.FirstOrDefault(l => l.Number == number);
        if (line.Number == 0)
        {
            return false;
        }
        _editor.Focus();
        if (line.Pointer != null)
        {
            _editor.CaretPosition = line.Pointer;
            var rect = line.Pointer.GetCharacterRect(LogicalDirection.Forward);
            line.Pointer.Paragraph?.BringIntoView();
            if (!rect.IsEmpty)
            {
                _editor.ScrollToVerticalOffset(_editor.VerticalOffset + rect.Top - _editor.ActualHeight / 3);
            }
        }
        else
        {
            line.Element?.BringIntoView();
        }
        return true;
    }

    private void Recompute()
    {
        _visible.Clear();
        _lineStarts.Clear();
        int number = 0;
        int caretLine = 0;
        var caret = _editor.CaretPosition;
        double height = ActualHeight;

        foreach (var block in _editor.Document.Blocks)
        {
            switch (block)
            {
                case Paragraph paragraph:
                    var pointer = paragraph.ContentStart.GetLineStartPosition(0) ?? paragraph.ContentStart;
                    while (pointer != null)
                    {
                        number++;
                        _lineStarts.Add((number, pointer, null));
                        var next = pointer.GetLineStartPosition(1, out int moved);
                        bool last = moved == 0 || next == null || next.CompareTo(paragraph.ContentEnd) >= 0;
                        if (caret.CompareTo(pointer) >= 0 && (last ? caret.CompareTo(paragraph.ContentEnd) <= 0 : caret.CompareTo(next!) < 0))
                        {
                            caretLine = number;
                        }
                        if (_enabled)
                        {
                            var rect = pointer.GetCharacterRect(LogicalDirection.Forward);
                            if (!rect.IsEmpty && rect.Bottom >= 0 && rect.Top <= height)
                            {
                                _visible.Add((number, rect.Top, rect.Height));
                            }
                        }
                        if (last)
                        {
                            break;
                        }
                        pointer = next;
                    }
                    break;

                case BlockUIContainer container when container.Child is FrameworkElement child:
                    foreach (var text in TextElementsOf(child))
                    {
                        double lineHeight = double.IsNaN(text.LineHeight) || text.LineHeight <= 0 ? text.FontSize * 1.33 : text.LineHeight;
                        int lines = Math.Max(1, (int)Math.Round(text.ActualHeight / lineHeight));
                        Point top;
                        try
                        {
                            top = text.TranslatePoint(new Point(0, 0), this);
                        }
                        catch (InvalidOperationException)
                        {
                            continue; // not laid out yet
                        }
                        for (int k = 0; k < lines; k++)
                        {
                            number++;
                            _lineStarts.Add((number, null, text));
                            double y = top.Y + k * lineHeight;
                            if (_enabled && y + lineHeight >= 0 && y <= height && text.IsVisible)
                            {
                                _visible.Add((number, y, lineHeight));
                            }
                        }
                    }
                    if (caret.CompareTo(container.ContentStart) >= 0 && caret.CompareTo(container.ContentEnd) <= 0)
                    {
                        caretLine = number;
                    }
                    break;
            }
        }

        CaretLine = caretLine;
        LineCount = number;
        InvalidateVisual();
        Updated?.Invoke();
    }

    /// <summary>Text blocks of a figure, table, equation or read-only paragraph whose lines count (not table cells).</summary>
    private static IEnumerable<TextBlock> TextElementsOf(DependencyObject root)
    {
        if (root is TextBlock text)
        {
            if (!Equals(text.Tag, Unnumbered) && text.ActualHeight > 0)
            {
                yield return text;
            }
            yield break;
        }
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            foreach (var child in TextElementsOf(VisualTreeHelper.GetChild(root, i)))
            {
                yield return child;
            }
        }
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (!_enabled || _visible.Count == 0)
        {
            return;
        }
        double textLeft = _editor.Document.PagePadding.Left;
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        foreach (var (number, top, lineHeight) in _visible)
        {
            var text = new FormattedText(number.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, NumberFont, 11, NumberBrush, dpi);
            double x = Math.Max(2, textLeft - 14 - text.Width);
            double y = top + (lineHeight - text.Height) / 2;
            dc.DrawText(text, new Point(x, y));
        }
    }

    private static SolidColorBrush CreateBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
