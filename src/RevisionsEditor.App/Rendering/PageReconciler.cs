using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using RevisionsEditor.Core.Diagrams;
using RevisionsEditor.Core.Scholarly;

namespace RevisionsEditor.App.Rendering;

/// <summary>
/// Updates a page that is on screen to a newly built one by replacing only the blocks that look different.
/// <para>
/// Assigning a whole new document makes WPF lay out every paragraph and figure again (hundreds of milliseconds
/// for a paper with figures) and loses the caret, selection and scroll position. Most edits change one or two
/// blocks; the rest are kept as they are, so the page redraws in a few milliseconds and nothing moves.
/// </para>
/// Two blocks are the same when they draw the same thing: their text and formatting run by run (so a renumbered
/// figure, a re-rendered citation or a new pending change counts as different), and for figures and tables the
/// date of the file they show.
/// </summary>
internal static class PageReconciler
{
    /// <summary>
    /// Makes <paramref name="live"/> show <paramref name="built"/>. Returns false (changing nothing) when the
    /// pages differ as a whole (font, margins), so the caller assigns the new document instead.
    /// </summary>
    public static bool TryUpdate(FlowDocument live, FlowDocument built, out int replaced)
    {
        replaced = 0;
        if (!SamePage(live, built))
        {
            return false;
        }

        var oldBlocks = live.Blocks.ToList();
        var newBlocks = built.Blocks.ToList();
        var oldKeys = oldBlocks.Select(Signature).ToArray();
        var newKeys = newBlocks.Select(Signature).ToArray();
        int[] matchOf = MatchInOrder(oldKeys, newKeys); // new index → old index, or -1

        var kept = new HashSet<Block>(matchOf.Where(i => i >= 0).Select(i => oldBlocks[i]));
        foreach (var block in oldBlocks.Where(b => !kept.Contains(b)))
        {
            live.Blocks.Remove(block);
        }

        // Walk the new order, inserting new blocks before the next kept one.
        Block? cursor = live.Blocks.FirstBlock;
        for (int n = 0; n < newBlocks.Count; n++)
        {
            if (matchOf[n] >= 0)
            {
                cursor = oldBlocks[matchOf[n]].NextBlock;
                continue;
            }
            var block = newBlocks[n];
            built.Blocks.Remove(block); // a block belongs to one document at a time
            if (cursor != null) live.Blocks.InsertBefore(cursor, block);
            else live.Blocks.Add(block);
            replaced++;
        }
        return true;
    }

    private static bool SamePage(FlowDocument a, FlowDocument b)
        => a.FontFamily.Source == b.FontFamily.Source
            && a.FontSize.Equals(b.FontSize)
            && a.PagePadding == b.PagePadding
            && a.ColumnWidth.Equals(b.ColumnWidth)
            && a.LineHeight.Equals(b.LineHeight)
            && Equals(a.Background, b.Background)
            && Equals(a.Foreground, b.Foreground);

    /// <summary>Pairs equal blocks in order (longest common subsequence), new index → old index.</summary>
    private static int[] MatchInOrder(string[] oldKeys, string[] newKeys)
    {
        int n = oldKeys.Length, m = newKeys.Length;
        var match = Enumerable.Repeat(-1, m).ToArray();
        // Trim the common start and end first: an edit usually changes the middle of the page only.
        int start = 0;
        while (start < n && start < m && oldKeys[start] == newKeys[start]) { match[start] = start; start++; }
        int endOld = n, endNew = m;
        while (endOld > start && endNew > start && oldKeys[endOld - 1] == newKeys[endNew - 1]) { match[--endNew] = --endOld; }

        int a = endOld - start, b = endNew - start;
        if (a == 0 || b == 0) return match;
        var dp = new int[a + 1, b + 1];
        for (int i = a - 1; i >= 0; i--)
            for (int j = b - 1; j >= 0; j--)
                dp[i, j] = oldKeys[start + i] == newKeys[start + j] ? dp[i + 1, j + 1] + 1 : Math.Max(dp[i + 1, j], dp[i, j + 1]);
        for (int i = 0, j = 0; i < a && j < b;)
        {
            if (oldKeys[start + i] == newKeys[start + j]) { match[start + j] = start + i; i++; j++; }
            else if (dp[i + 1, j] >= dp[i, j + 1]) i++;
            else j++;
        }
        return match;
    }

    // ---- What a block draws ------------------------------------------------------------------------

    public static string Signature(Block block)
    {
        var sb = new StringBuilder();
        sb.Append(block.GetType().Name).Append('|');
        if (block.Tag is EditorBlockTag tag)
        {
            sb.Append(tag.ParagraphId).Append('|').Append(tag.Role).Append('|').Append(tag.LockedBlock?.Text).Append('|');
        }
        Properties(sb, block);
        sb.Append(block.Margin).Append(block.Padding).Append(block.BorderThickness).Append(Brush(block.BorderBrush)).Append('|');
        switch (block)
        {
            case Paragraph p:
                sb.Append(p.TextIndent).Append('|');
                foreach (var inline in p.Inlines) Inline(sb, inline);
                break;
            case BlockUIContainer c:
                Element(sb, c.Child);
                if (c.Tag is EditorBlockTag { LockedBlock: { } original })
                {
                    sb.Append('|').Append(FileStamps(original.Text));
                }
                break;
            case Section s:
                foreach (var child in s.Blocks) sb.Append('[').Append(Signature(child)).Append(']');
                break;
            case List l:
                sb.Append(l.MarkerStyle).Append('|');
                foreach (var item in l.ListItems)
                    foreach (var child in item.Blocks) sb.Append('[').Append(Signature(child)).Append(']');
                break;
            default:
                // A kind of block this does not know how to compare is always replaced.
                sb.Append(Guid.NewGuid());
                break;
        }
        return sb.ToString();
    }

    private static void Properties(StringBuilder sb, TextElement e)
        => sb.Append(e.FontFamily?.Source).Append(',').Append(e.FontSize).Append(',').Append(e.FontWeight).Append(',')
            .Append(e.FontStyle).Append(',').Append(Brush(e.Foreground)).Append(',').Append(Brush(e.Background)).Append(';');

    private static void Inline(StringBuilder sb, Inline inline)
    {
        sb.Append('<').Append(inline.GetType().Name);
        Properties(sb, inline);
        sb.Append(inline.BaselineAlignment).Append(',').Append(inline.TextDecorations?.Count ?? 0).Append(',')
            .Append(inline.TextDecorations?.FirstOrDefault()?.Location).Append(',').Append(inline.Tag).Append(',').Append(inline.ToolTip).Append(';');
        switch (inline)
        {
            case Run r:
                sb.Append(r.Text);
                break;
            case Span span:
                if (span is Hyperlink h) sb.Append(h.NavigateUri);
                foreach (var child in span.Inlines) Inline(sb, child);
                break;
            case InlineUIContainer ui:
                Element(sb, ui.Child);
                break;
            case LineBreak:
                sb.Append("\\n");
                break;
        }
        sb.Append('>');
    }

    /// <summary>The text and layout of a figure, table or equation (its controls, walked).</summary>
    private static void Element(StringBuilder sb, DependencyObject? element)
    {
        if (element == null) return;
        sb.Append('{').Append(element.GetType().Name);
        switch (element)
        {
            case TextBlock t:
                sb.Append(':').Append(t.FontSize).Append(',').Append(t.FontWeight).Append(',').Append(t.FontStyle).Append(',')
                    .Append(t.FontFamily?.Source).Append(',').Append(Brush(t.Foreground)).Append(',').Append(t.TextAlignment).Append(':');
                if (t.Inlines.Count > 0) foreach (var inline in t.Inlines) Inline(sb, inline);
                else sb.Append(t.Text);
                break;
            case Image i:
                sb.Append(':').Append(i.Width).Append('x').Append(i.Height).Append(',').Append(i.MaxWidth).Append(',').Append(i.Stretch)
                    .Append(',').Append((i.Source as BitmapSource)?.PixelWidth).Append('x').Append((i.Source as BitmapSource)?.PixelHeight);
                break;
            case FrameworkElement f:
                sb.Append(':').Append(f.Width).Append(',').Append(f.MaxWidth).Append(',').Append(f.Margin).Append(',').Append(f.HorizontalAlignment)
                    .Append(',').Append(f.ToolTip);
                if (f is Border border) sb.Append(',').Append(Brush(border.Background)).Append(',').Append(border.BorderThickness).Append(',').Append(Brush(border.BorderBrush));
                if (f is Grid grid) sb.Append(',').Append(grid.RowDefinitions.Count).Append('x').Append(grid.ColumnDefinitions.Count);
                break;
        }
        foreach (object child in LogicalTreeHelper.GetChildren(element))
        {
            if (child is DependencyObject d) Element(sb, d);
            else if (child is string s) sb.Append('"').Append(s).Append('"');
        }
        if (element is UIElement u) sb.Append(u.Visibility);
        sb.Append('}');
    }

    private static string Brush(Brush? brush) => brush switch
    {
        null => "-",
        SolidColorBrush s => s.Color.ToString() + s.Opacity,
        _ => brush.GetType().Name + brush.GetHashCode()
    };

    /// <summary>The dates of the files a figure or table shows (a redrawn diagram or an edited CSV looks different).</summary>
    private static string FileStamps(string text)
    {
        if (ScholarlyBlocks.ParseFigure(text) is { } figure)
        {
            return Stamp(figure.ImagePath) + Stamp(DiagramFiles.SourceOf(figure));
        }
        if (ScholarlyBlocks.ParseTable(text) is { } table)
        {
            return Stamp(table.CsvPath);
        }
        return string.Empty;

        static string Stamp(string? relative) => relative == null || WorkspacePath == null ? string.Empty
            : File.GetLastWriteTimeUtc(Path.Combine(WorkspacePath, relative.Replace('/', Path.DirectorySeparatorChar))).Ticks.ToString();
    }

    /// <summary>The workspace figure and table paths are relative to (set by the window).</summary>
    public static string? WorkspacePath { get; set; }
}
