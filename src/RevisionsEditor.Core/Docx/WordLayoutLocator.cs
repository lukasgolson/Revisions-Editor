using System.Runtime.InteropServices;

namespace RevisionsEditor.Core.Docx;

/// <summary>
/// Where a paragraph falls in the laid-out document: page and line on that page, and the line counted from
/// the start of the document (as continuous line numbering shows it).
/// </summary>
public sealed record PageLocation(int Page, int FirstLine, int EndPage, int LastLine, int AbsoluteFirstLine, int AbsoluteLastLine, bool Continuous = true)
{
    public override string ToString()
    {
        int first = Continuous ? AbsoluteFirstLine : FirstLine;
        int last = Continuous ? AbsoluteLastLine : LastLine;
        string lines = first == last ? $"line {first}" : $"lines {first}–{last}";
        return Page == EndPage ? $"page {Page}, {lines}" : $"pages {Page}–{EndPage}, {lines}";
    }
}

/// <summary>
/// Asks Microsoft Word (when installed) to lay out a document and report the page and line number of
/// bookmarked paragraphs, so the response letter can say "page 4, lines 12–18" exactly as the reviewers will
/// see it. Word runs hidden in its own process and is closed afterwards.
/// </summary>
public static class WordLayoutLocator
{
    private const int WdActiveEndPageNumber = 3;
    private const int WdFirstCharacterLineNumber = 10;

    public static bool IsWordInstalled => Type.GetTypeFromProgID("Word.Application") != null;

    /// <summary>
    /// Page and line numbers of each bookmark; <paramref name="continuousLineNumbers"/> chooses how they are
    /// written by <see cref="PageLocation.ToString"/>. Returns null if Word is not available or fails.
    /// </summary>
    public static Dictionary<string, PageLocation>? Locate(string docxPath, IReadOnlyCollection<string> bookmarkNames, bool continuousLineNumbers, TimeSpan timeout)
    {
        if (!IsWordInstalled)
        {
            return null;
        }
        Dictionary<string, PageLocation>? result = null;
        var thread = new Thread(() => result = LocateCore(docxPath, bookmarkNames, continuousLineNumbers)) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return thread.Join(timeout) ? result : null;
    }

    private static Dictionary<string, PageLocation>? LocateCore(string docxPath, IReadOnlyCollection<string> bookmarkNames, bool continuous)
    {
        dynamic? word = null;
        dynamic? doc = null;
        try
        {
            word = Activator.CreateInstance(Type.GetTypeFromProgID("Word.Application")!)!;
            word.Visible = false;
            word.DisplayAlerts = 0;
            doc = word.Documents.Open(Path.GetFullPath(docxPath), false, true, false);
            doc.Repaginate();

            // Lines on each page (the line number of the last character of each page), to count from the start.
            int pages = (int)doc.ComputeStatistics(2); // wdStatisticPages
            var linesBeforePage = new int[pages + 2];
            for (int page = 1; page <= pages; page++)
            {
                word.Selection.GoTo(1, 1, page); // wdGoToPage, wdGoToAbsolute
                dynamic end = word.Selection.Bookmarks.Item("\\Page").Range;
                int at = Math.Max((int)end.End - 1, 0);
                int last = (int)doc.Range(at, at).Information(WdFirstCharacterLineNumber);
                linesBeforePage[page + 1] = linesBeforePage[page] + Math.Max(last, 0);
            }

            var result = new Dictionary<string, PageLocation>(StringComparer.Ordinal);
            foreach (string name in bookmarkNames)
            {
                if (!(bool)doc.Bookmarks.Exists(name))
                {
                    continue;
                }
                dynamic range = doc.Bookmarks.Item(name).Range;
                int start = (int)range.Start;
                int stop = Math.Max(start, (int)range.End - 1);
                dynamic first = doc.Range(start, start);
                dynamic last = doc.Range(stop, stop);
                int page = (int)first.Information(WdActiveEndPageNumber);
                int line = (int)first.Information(WdFirstCharacterLineNumber);
                int endPage = (int)last.Information(WdActiveEndPageNumber);
                int endLine = (int)last.Information(WdFirstCharacterLineNumber);
                int absoluteFirst = line + (page < linesBeforePage.Length ? linesBeforePage[page] : 0);
                int absoluteLast = endLine + (endPage < linesBeforePage.Length ? linesBeforePage[endPage] : 0);
                result[name] = new PageLocation(page, line, endPage, endLine, absoluteFirst, absoluteLast, continuous);
            }
            return result;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException or InvalidOperationException)
        {
            return null;
        }
        finally
        {
            try
            {
                doc?.Close(false);
            }
            catch (COMException)
            {
            }
            try
            {
                word?.Quit(false);
            }
            catch (COMException)
            {
            }
            if (doc != null)
            {
                Marshal.FinalReleaseComObject(doc);
            }
            if (word != null)
            {
                Marshal.FinalReleaseComObject(word);
            }
        }
    }
}
