using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace RevisionsEditor.App.Rendering;

/// <summary>
/// Spell checking for the manuscript page with Windows' own spell checker (the one WPF uses, with the same user
/// dictionary), done the way a word processor does it: paragraphs are checked on a background thread when they
/// change, and the wavy underlines are drawn on a layer over the page for the paragraphs on screen.
/// <para>
/// WPF's built-in checking (SpellCheck.IsEnabled) re-checks text on the UI thread whenever the app is idle; on a
/// paper with figures each pass took up to a third of a second, which is what made typing stutter. The page
/// itself is never changed here, so undo, the caret and the Markdown source are unaffected.
/// </para>
/// Right-clicking a misspelled word offers suggestions, Ignore All and Add to Dictionary.
/// </summary>
internal sealed class EditorSpellChecker : IDisposable
{
    private readonly RichTextBox _editor;
    private readonly SpellWorker _worker;
    private readonly SquiggleLayer _layer;
    private readonly DispatcherTimer _timer;
    private readonly Dictionary<Paragraph, (string Text, List<(int Start, int Length)> Errors)> _results = [];
    private readonly HashSet<Paragraph> _dirty = [];
    private FlowDocument? _document;
    private int _generation;

    private EditorSpellChecker(RichTextBox editor, SpellWorker worker)
    {
        _editor = editor;
        _worker = worker;
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(250) };
        _timer.Tick += (_, _) => { _timer.Stop(); CheckDirty(); };
        _layer = new SquiggleLayer(editor, this);
        AdornerLayer.GetAdornerLayer(editor)?.Add(_layer);

        editor.TextChanged += (_, e) =>
        {
            foreach (var change in e.Changes)
            {
                if (ParagraphAt(change.Offset) is { } p) _dirty.Add(p);
                if (ParagraphAt(change.Offset + change.AddedLength) is { } q) _dirty.Add(q);
            }
            Schedule();
            _layer.InvalidateVisual();
        };
        editor.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler((_, _) => _layer.InvalidateVisual()));
        editor.SizeChanged += (_, _) => _layer.InvalidateVisual();
        editor.ContextMenuOpening += OnContextMenuOpening;
        Schedule();
    }

    /// <summary>
    /// Starts checking <paramref name="editor"/>'s page with Windows' spell checker for the editor's language, or
    /// returns null when it is not available (the caller then keeps WPF's own checking).
    /// </summary>
    public static EditorSpellChecker? TryAttach(RichTextBox editor)
    {
        string language = editor.Language?.IetfLanguageTag is { Length: > 0 } tag ? tag : "en-US";
        var worker = SpellWorker.TryStart(language);
        return worker == null ? null : new EditorSpellChecker(editor, worker);
    }

    public void Dispose()
    {
        _timer.Stop();
        AdornerLayer.GetAdornerLayer(_editor)?.Remove(_layer);
        _worker.Dispose();
    }

    /// <summary>Paragraphs checked and waiting to be checked (for tests).</summary>
    internal int PendingCount => _dirty.Count + _worker.Pending;

    internal IReadOnlyList<string> MisspelledWords => _results.SelectMany(r => r.Value.Errors.Select(e => r.Value.Text.Substring(e.Start, e.Length))).ToList();

    private void Schedule()
    {
        _timer.Stop();
        _timer.Start();
    }

    private Paragraph? ParagraphAt(int offset)
        => _editor.Document.ContentStart.GetPositionAtOffset(offset)?.Paragraph;

    private void CheckDirty()
    {
        var document = _editor.Document;
        if (!ReferenceEquals(document, _document))
        {
            // A new page: check every paragraph.
            _document = document;
            _results.Clear();
            _dirty.Clear();
            _generation++;
            foreach (var p in AllParagraphs(document)) _dirty.Add(p);
        }
        // Results of paragraphs that are gone (replaced by a redraw) are dropped.
        foreach (var gone in _results.Keys.Where(p => !IsOnPage(p)).ToList()) _results.Remove(gone);

        int generation = _generation;
        foreach (var paragraph in _dirty.ToList())
        {
            _dirty.Remove(paragraph);
            if (!IsOnPage(paragraph)) continue;
            string text = ParagraphText(paragraph).Text;
            if (text.Trim().Length == 0)
            {
                _results.Remove(paragraph);
                continue;
            }
            _worker.Check(text, errors => _editor.Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
            {
                // Kept only if the paragraph still reads the same (it may have been typed in meanwhile).
                if (generation != _generation || !IsOnPage(paragraph) || ParagraphText(paragraph).Text != text) return;
                if (errors.Count == 0) _results.Remove(paragraph);
                else _results[paragraph] = (text, errors);
                _layer.InvalidateVisual();
            }));
        }
    }

    private bool IsOnPage(Paragraph p)
    {
        for (DependencyObject? d = p; d != null; d = (d as TextElement)?.Parent ?? LogicalTreeHelper.GetParent(d))
        {
            if (ReferenceEquals(d, _editor.Document)) return true;
        }
        return false;
    }

    private static IEnumerable<Paragraph> AllParagraphs(FlowDocument document)
    {
        IEnumerable<Paragraph> In(BlockCollection blocks)
        {
            foreach (var block in blocks)
            {
                switch (block)
                {
                    case Paragraph p: yield return p; break;
                    case Section s: foreach (var x in In(s.Blocks)) yield return x; break;
                    case List l: foreach (var item in l.ListItems) foreach (var x in In(item.Blocks)) yield return x; break;
                }
            }
        }
        return In(document.Blocks);
    }

    /// <summary>A paragraph's text (its runs, in order) and where each run starts in it.</summary>
    private static (string Text, List<(Run Run, int Start)> Runs) ParagraphText(Paragraph paragraph)
    {
        var sb = new System.Text.StringBuilder();
        var runs = new List<(Run, int)>();
        void Walk(InlineCollection inlines)
        {
            foreach (var inline in inlines)
            {
                switch (inline)
                {
                    case Run r:
                        runs.Add((r, sb.Length));
                        sb.Append(r.Text);
                        break;
                    case Span s:
                        Walk(s.Inlines);
                        break;
                    case LineBreak:
                        sb.Append('\n');
                        break;
                    default:
                        sb.Append(' '); // a label or picture separates words
                        break;
                }
            }
        }
        Walk(paragraph.Inlines);
        return (sb.ToString(), runs);
    }

    /// <summary>The page position of a character of a paragraph's text.</summary>
    private static TextPointer? PositionOf(List<(Run Run, int Start)> runs, int offset)
    {
        for (int i = runs.Count - 1; i >= 0; i--)
        {
            var (run, start) = runs[i];
            if (offset >= start && offset <= start + run.Text.Length)
            {
                return run.ContentStart.GetPositionAtOffset(offset - start);
            }
        }
        return null;
    }

    /// <summary>The misspelling under a page position: its paragraph, text range and word.</summary>
    private (TextRange Range, string Word)? MisspellingAt(TextPointer position)
    {
        var paragraph = position.Paragraph;
        if (paragraph == null || !_results.TryGetValue(paragraph, out var result)) return null;
        var (text, runs) = ParagraphText(paragraph);
        if (text != result.Text) return null;
        foreach (var (start, length) in result.Errors)
        {
            var from = PositionOf(runs, start);
            var to = PositionOf(runs, start + length);
            if (from != null && to != null && position.CompareTo(from) >= 0 && position.CompareTo(to) <= 0)
            {
                return (new TextRange(from, to), text.Substring(start, length));
            }
        }
        return null;
    }

    private void OnContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        _editor.ContextMenu = null; // the editor's own menu (cut, copy, paste)
        var position = _editor.GetPositionFromPoint(Mouse.GetPosition(_editor), snapToText: true);
        if (position == null || MisspellingAt(position) is not { } miss) return;

        var menu = new ContextMenu();
        var suggestions = _worker.Suggest(miss.Word).Take(6).ToList();
        foreach (string suggestion in suggestions)
        {
            var item = new MenuItem { Header = suggestion, FontWeight = FontWeights.SemiBold };
            item.Click += (_, _) =>
            {
                miss.Range.Text = suggestion;
                _editor.CaretPosition = miss.Range.End;
            };
            menu.Items.Add(item);
        }
        if (suggestions.Count == 0) menu.Items.Add(new MenuItem { Header = "(no suggestions)", IsEnabled = false });
        menu.Items.Add(new Separator());
        var ignore = new MenuItem { Header = "_Ignore All" };
        ignore.Click += (_, _) => { _worker.Ignore(miss.Word); RecheckAll(); };
        var add = new MenuItem { Header = "_Add to Dictionary" };
        add.Click += (_, _) => { _worker.Add(miss.Word); RecheckAll(); };
        menu.Items.Add(ignore);
        menu.Items.Add(add);
        menu.Items.Add(new Separator());
        menu.Items.Add(new MenuItem { Command = ApplicationCommands.Cut });
        menu.Items.Add(new MenuItem { Command = ApplicationCommands.Copy });
        menu.Items.Add(new MenuItem { Command = ApplicationCommands.Paste });
        menu.Closed += (_, _) => _editor.ContextMenu = null;
        _editor.ContextMenu = menu;
    }

    private void RecheckAll()
    {
        _document = null; // next pass checks every paragraph
        Schedule();
    }

    // ---- The underlines --------------------------------------------------------------------------------

    private sealed class SquiggleLayer(RichTextBox editor, EditorSpellChecker owner) : Adorner(editor)
    {
        private static readonly Pen Pen = CreatePen();

        private static Pen CreatePen()
        {
            var pen = new Pen(new SolidColorBrush(Color.FromRgb(0xE0, 0x1B, 0x1B)), 1.0);
            pen.Freeze();
            return pen;
        }

        protected override void OnRender(DrawingContext dc)
        {
            double height = editor.ActualHeight, width = editor.ActualWidth;
            dc.PushClip(new RectangleGeometry(new Rect(0, 0, width, height)));
            foreach (var (paragraph, result) in owner._results)
            {
                var start = paragraph.ContentStart.GetCharacterRect(LogicalDirection.Forward);
                var end = paragraph.ContentEnd.GetCharacterRect(LogicalDirection.Backward);
                if (start.IsEmpty || end.IsEmpty || end.Bottom < 0 || start.Top > height) continue; // off screen
                var (text, runs) = ParagraphText(paragraph);
                if (text != result.Text) continue; // typed in since: redrawn once checked again
                foreach (var (s, length) in result.Errors)
                {
                    var from = PositionOf(runs, s);
                    var to = PositionOf(runs, s + length);
                    if (from == null || to == null) continue;
                    var a = from.GetCharacterRect(LogicalDirection.Forward);
                    var b = to.GetCharacterRect(LogicalDirection.Backward);
                    if (a.IsEmpty || b.IsEmpty || a.Bottom < 0 || a.Top > height) continue;
                    // A word broken over two lines is underlined to the end of the first.
                    double right = Math.Abs(a.Top - b.Top) < 1 ? b.Left : width;
                    Wave(dc, a.Left, right, a.Bottom - 1);
                }
            }
            dc.Pop();
        }

        private static void Wave(DrawingContext dc, double left, double right, double y)
        {
            if (right - left < 2) return;
            var geometry = new StreamGeometry();
            using (var g = geometry.Open())
            {
                g.BeginFigure(new Point(left, y), false, false);
                bool up = true;
                for (double x = left + 2; x <= right; x += 2, up = !up)
                {
                    g.LineTo(new Point(x, up ? y - 1.5 : y), true, true);
                }
            }
            geometry.Freeze();
            dc.DrawGeometry(null, Pen, geometry);
        }
    }

    // ---- Windows' spell checker, on its own thread -------------------------------------------------------

    /// <summary>Owns the spell checker on one background thread; calls from the UI are queued to it.</summary>
    private sealed class SpellWorker : IDisposable
    {
        private readonly BlockingCollection<Action> _queue = [];
        private readonly Thread _thread;
        private ISpellChecker? _checker;
        private int _pending;

        public int Pending => Volatile.Read(ref _pending);

        private SpellWorker(string language, TaskCompletionSource<bool> ready)
        {
            _thread = new Thread(() =>
            {
                try
                {
                    var factory = (ISpellCheckerFactory)new SpellCheckerFactoryClass();
                    string tag = factory.IsSupported(language) != 0 ? language : "en-US";
                    _checker = factory.IsSupported(tag) != 0 ? factory.CreateSpellChecker(tag) : null;
                    ready.SetResult(_checker != null);
                }
                catch (Exception ex) when (ex is COMException or InvalidCastException or UnauthorizedAccessException)
                {
                    ready.SetResult(false);
                    return;
                }
                foreach (var work in _queue.GetConsumingEnumerable())
                {
                    try
                    {
                        work();
                    }
                    catch (COMException)
                    {
                        // A check that fails leaves its paragraph without underlines.
                    }
                }
            })
            { IsBackground = true, Name = "Spell checking" };
            _thread.SetApartmentState(ApartmentState.MTA);
            _thread.Start();
        }

        public static SpellWorker? TryStart(string language)
        {
            var ready = new TaskCompletionSource<bool>();
            var worker = new SpellWorker(language, ready);
            if (ready.Task.Wait(TimeSpan.FromSeconds(5)) && ready.Task.Result)
            {
                return worker;
            }
            worker.Dispose();
            return null;
        }

        public void Check(string text, Action<List<(int Start, int Length)>> done)
        {
            Interlocked.Increment(ref _pending);
            _queue.Add(() =>
            {
                var errors = new List<(int, int)>();
                try
                {
                    var found = _checker!.ComprehensiveCheck(text);
                    while (found.Next(out var error) == 0 && error != null)
                    {
                        // Repeated words ("the the") are flagged too, as Word does.
                        if (error.CorrectiveAction != CorrectiveAction.None)
                        {
                            errors.Add(((int)error.StartIndex, (int)error.Length));
                        }
                        Marshal.ReleaseComObject(error);
                    }
                    Marshal.ReleaseComObject(found);
                }
                finally
                {
                    Interlocked.Decrement(ref _pending);
                    done(errors);
                }
            });
        }

        public List<string> Suggest(string word) => Run(() =>
        {
            var list = new List<string>();
            var suggestions = _checker!.Suggest(word);
            var buffer = new string[1];
            while (suggestions.Next(1, buffer, IntPtr.Zero) == 0) list.Add(buffer[0]);
            Marshal.ReleaseComObject(suggestions);
            return list;
        }) ?? [];

        public void Ignore(string word) => Run(() => { _checker!.Ignore(word); return true; });
        public void Add(string word) => Run(() => { _checker!.Add(word); return true; });

        private T? Run<T>(Func<T> work)
        {
            var result = new TaskCompletionSource<T?>();
            _queue.Add(() =>
            {
                try { result.SetResult(work()); }
                catch (COMException) { result.SetResult(default); }
            });
            return result.Task.Wait(TimeSpan.FromSeconds(2)) ? result.Task.Result : default;
        }

        public void Dispose()
        {
            _queue.CompleteAdding();
        }
    }

    // ---- Windows Spell Checking API (spellcheck.h) --------------------------------------------------------

    private enum CorrectiveAction { None = 0, GetSuggestions = 1, Replace = 2, Delete = 3 }

    [ComImport, Guid("7AB36653-1796-484B-BDFA-E74F1DB7C1DC")]
    private class SpellCheckerFactoryClass;

    [ComImport, Guid("8E018A9D-2415-4677-BF08-794EA61F94BB"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ISpellCheckerFactory
    {
        IEnumString SupportedLanguages { get; }
        int IsSupported([MarshalAs(UnmanagedType.LPWStr)] string languageTag);
        ISpellChecker CreateSpellChecker([MarshalAs(UnmanagedType.LPWStr)] string languageTag);
    }

    [ComImport, Guid("B6FD0B71-E2BC-4653-8D05-F197E412770B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ISpellChecker
    {
        string LanguageTag { [return: MarshalAs(UnmanagedType.LPWStr)] get; }
        IEnumSpellingError Check([MarshalAs(UnmanagedType.LPWStr)] string text);
        IEnumString Suggest([MarshalAs(UnmanagedType.LPWStr)] string word);
        void Add([MarshalAs(UnmanagedType.LPWStr)] string word);
        void Ignore([MarshalAs(UnmanagedType.LPWStr)] string word);
        void AutoCorrect([MarshalAs(UnmanagedType.LPWStr)] string from, [MarshalAs(UnmanagedType.LPWStr)] string to);
        byte GetOptionValue([MarshalAs(UnmanagedType.LPWStr)] string optionId);
        IEnumString OptionIds { get; }
        string Id { [return: MarshalAs(UnmanagedType.LPWStr)] get; }
        string LocalizedName { [return: MarshalAs(UnmanagedType.LPWStr)] get; }
        uint add_SpellCheckerChanged(IntPtr handler);
        void remove_SpellCheckerChanged(uint cookie);
        IntPtr GetOptionDescription([MarshalAs(UnmanagedType.LPWStr)] string optionId);
        IEnumSpellingError ComprehensiveCheck([MarshalAs(UnmanagedType.LPWStr)] string text);
    }

    [ComImport, Guid("803E3BD4-2828-4410-8290-418D1D73C762"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IEnumSpellingError
    {
        [PreserveSig]
        int Next(out ISpellingError? value);
    }

    [ComImport, Guid("B7C82D61-FBE8-4B47-9B27-6C0D2E0DE0A3"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ISpellingError
    {
        uint StartIndex { get; }
        uint Length { get; }
        CorrectiveAction CorrectiveAction { get; }
        string Replacement { [return: MarshalAs(UnmanagedType.LPWStr)] get; }
    }
}
