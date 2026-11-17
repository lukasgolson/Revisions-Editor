using RevisionsEditor.Core.Diagrams;

namespace RevisionsEditor.Core.Latex;

/// <summary>
/// How a table is set: across both columns or in one (<see cref="Wide"/>), at its natural width or with its text
/// columns wrapping (<see cref="Wraps"/>), and for each wrapping column its share of the wrapping space (a
/// tabularx \hsize factor; null for a column that keeps its natural width).
/// </summary>
public sealed record TableSize(bool Wide, bool Wraps, double?[] WrapShare, double NaturalWidth, double AvailableWidth);

/// <summary>
/// Sizes a table from its content, as a typesetter would: every cell is measured in the table font (Times, whose
/// widths TeX Gyre Termes shares), so a table fits a column when it can, spans both columns when it needs to,
/// and wraps its text columns only when even the page is too narrow. Numbers never wrap.
/// </summary>
public static class TableSizing
{
    private const double ColumnSeparation = 6;   // \tabcolsep on each side of a cell, in points
    private const double MinWrapWidth = 54;       // a wrapping column narrower than this reads badly (about 9 em)
    private const double WrapCandidate = 64;      // text columns at least this wide may wrap

    /// <summary>Column width, text width and the \small table font size of each layout, in points.</summary>
    public static (double Column, double Text, double FontSize) Page(LatexTemplate template) => template switch
    {
        LatexTemplate.ReviewManuscript => (453.5, 453.5, 10.95), // A4, 25 mm margins; \small of 12 pt
        LatexTemplate.Elsevier => (252, 522, 9),                 // elsarticle 5p
        LatexTemplate.Ieee => (252, 516, 9),                     // IEEEtran journal
        _ => (241, 499, 9)                                       // A4, 17 mm margins, 6 mm between columns
    };

    public static TableSize Plan(IReadOnlyList<string[]> rows, IReadOnlyList<bool> numeric, LatexTemplate template, bool twoColumn, string? span = null)
    {
        int columns = Math.Max(1, rows.Count == 0 ? 1 : rows.Max(r => r.Length));
        var (columnWidth, textWidth, fontSize) = Page(template);
        var natural = new double[columns];
        var longestWord = new double[columns];
        for (int r = 0; r < rows.Count; r++)
        {
            for (int c = 0; c < rows[r].Length; c++)
            {
                string cell = rows[r][c].Trim();
                if (cell.Length == 0) continue;
                bool bold = r == 0;
                natural[c] = Math.Max(natural[c], Measure(cell, fontSize, bold));
                foreach (string word in cell.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                {
                    longestWord[c] = Math.Max(longestWord[c], Measure(word, fontSize, bold));
                }
            }
        }
        double separation = 2 * ColumnSeparation * columns;
        double naturalWidth = natural.Sum() + separation;
        bool IsNumber(int c) => c < numeric.Count && numeric[c];
        bool MayWrap(int c) => !IsNumber(c) && natural[c] >= WrapCandidate && natural[c] > longestWord[c] + 1;
        // The narrowest the table can be with its text columns wrapped.
        double minimumWidth = Enumerable.Range(0, columns)
            .Sum(c => MayWrap(c) ? Math.Max(longestWord[c], Math.Min(natural[c], MinWrapWidth)) : natural[c]) + separation;

        bool wide = span?.ToLowerInvariant() switch
        {
            "full" or "page" or "2" => twoColumn,
            "column" or "1" => false,
            // Across both columns when a column is too narrow: when the table fits the page as it is, or when
            // wrapping it into one column would leave its text columns cramped (or not fit at all).
            _ => twoColumn && naturalWidth > columnWidth && (naturalWidth <= textWidth || minimumWidth > columnWidth || naturalWidth > 1.8 * columnWidth)
        };
        double available = wide || !twoColumn ? textWidth : columnWidth;
        bool wraps = naturalWidth > available && Enumerable.Range(0, columns).Any(MayWrap);

        var share = new double?[columns];
        if (wraps)
        {
            // Wrapping columns share the space the others leave, in proportion to their content (tabularx \hsize
            // factors average to 1).
            var wrapping = Enumerable.Range(0, columns).Where(MayWrap).ToList();
            double total = wrapping.Sum(c => natural[c]);
            foreach (int c in wrapping)
            {
                share[c] = Math.Round(wrapping.Count * natural[c] / total, 3);
            }
        }
        return new TableSize(wide, wraps, share, naturalWidth, available);
    }

    private static double Measure(string text, double size, bool bold)
        => DiagramText.Layout(text, "Times New Roman", size, bold).Width;
}
