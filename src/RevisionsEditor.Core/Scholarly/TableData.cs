using System.Text;

namespace RevisionsEditor.Core.Scholarly;

/// <summary>
/// The cells of a table being edited: the first row holds the column headings. Rows are kept rectangular, and
/// the table reads and writes the CSV files table blocks are drawn from. Pasting takes cells copied from Excel,
/// Google Sheets or LibreOffice (tab-separated).
/// </summary>
public sealed class TableData
{
    public List<List<string>> Rows { get; } = [];

    public int ColumnCount => Rows.Count == 0 ? 0 : Rows.Max(r => r.Count);

    /// <summary>An empty table: a heading row and <paramref name="rows"/> - 1 rows of <paramref name="columns"/> cells.</summary>
    public static TableData Empty(int rows = 3, int columns = 3)
    {
        var table = new TableData();
        for (int r = 0; r < Math.Max(1, rows); r++)
        {
            table.Rows.Add(Enumerable.Range(0, Math.Max(1, columns)).Select(c => r == 0 ? $"Column {c + 1}" : string.Empty).ToList());
        }
        return table;
    }

    public static TableData FromRows(IEnumerable<IEnumerable<string>> rows)
    {
        var table = new TableData();
        table.Rows.AddRange(rows.Select(r => r.ToList()));
        if (table.Rows.Count == 0)
        {
            table.Rows.Add([string.Empty]);
        }
        table.Normalize();
        return table;
    }

    public static TableData FromCsv(string csv) => FromRows(CsvTable.Parse(csv));

    /// <summary>Pads short rows so every row has the same number of cells.</summary>
    public void Normalize()
    {
        int columns = Math.Max(1, ColumnCount);
        foreach (var row in Rows)
        {
            while (row.Count < columns)
            {
                row.Add(string.Empty);
            }
        }
    }

    public void InsertRow(int index)
    {
        Rows.Insert(Math.Clamp(index, 0, Rows.Count), Enumerable.Repeat(string.Empty, Math.Max(1, ColumnCount)).ToList());
    }

    public void RemoveRow(int index)
    {
        if (Rows.Count > 1 && index >= 0 && index < Rows.Count)
        {
            Rows.RemoveAt(index);
        }
    }

    public void MoveRow(int from, int to)
    {
        if (from < 0 || from >= Rows.Count || to < 0 || to >= Rows.Count || from == to)
        {
            return;
        }
        var row = Rows[from];
        Rows.RemoveAt(from);
        Rows.Insert(to, row);
    }

    public void InsertColumn(int index)
    {
        int at = Math.Clamp(index, 0, ColumnCount);
        foreach (var row in Rows)
        {
            row.Insert(Math.Min(at, row.Count), string.Empty);
        }
        if (at < Rows[0].Count)
        {
            Rows[0][at] = $"Column {at + 1}";
        }
    }

    public void RemoveColumn(int index)
    {
        if (ColumnCount <= 1 || index < 0 || index >= ColumnCount)
        {
            return;
        }
        foreach (var row in Rows.Where(r => index < r.Count))
        {
            row.RemoveAt(index);
        }
    }

    public void MoveColumn(int from, int to)
    {
        if (from < 0 || from >= ColumnCount || to < 0 || to >= ColumnCount || from == to)
        {
            return;
        }
        foreach (var row in Rows)
        {
            string cell = row[from];
            row.RemoveAt(from);
            row.Insert(to, cell);
        }
    }

    /// <summary>
    /// Pastes cells copied from a spreadsheet (tab-separated rows) with their top-left cell at (row, column),
    /// adding rows and columns as needed. Returns the size pasted.
    /// </summary>
    public (int Rows, int Columns) Paste(int row, int column, string clipboard)
    {
        var cells = ParseClipboard(clipboard);
        if (cells.Count == 0)
        {
            return (0, 0);
        }
        row = Math.Max(0, row);
        column = Math.Max(0, column);
        while (Rows.Count < row + cells.Count)
        {
            InsertRow(Rows.Count);
        }
        int width = cells.Max(r => r.Count);
        while (ColumnCount < column + width)
        {
            foreach (var r in Rows)
            {
                r.Add(string.Empty);
            }
        }
        for (int r = 0; r < cells.Count; r++)
        {
            for (int c = 0; c < cells[r].Count; c++)
            {
                Rows[row + r][column + c] = cells[r][c];
            }
        }
        return (cells.Count, width);
    }

    /// <summary>
    /// Cells copied from a spreadsheet: rows on lines, cells separated by tabs, a cell with a line break or tab in
    /// quotes (as Excel writes it). A single line without tabs is one cell.
    /// </summary>
    public static List<List<string>> ParseClipboard(string text)
    {
        var rows = new List<List<string>>();
        if (string.IsNullOrEmpty(text))
        {
            return rows;
        }
        text = text.Replace("\r\n", "\n").Replace('\r', '\n');
        if (text.EndsWith('\n'))
        {
            text = text[..^1];
        }
        var row = new List<string>();
        var cell = new StringBuilder();
        bool quoted = false;
        for (int i = 0; i < text.Length; i++)
        {
            char ch = text[i];
            if (quoted)
            {
                if (ch == '"' && i + 1 < text.Length && text[i + 1] == '"')
                {
                    cell.Append('"');
                    i++;
                }
                else if (ch == '"')
                {
                    quoted = false;
                }
                else
                {
                    cell.Append(ch);
                }
            }
            else if (ch == '"' && cell.Length == 0)
            {
                quoted = true;
            }
            else if (ch == '\t')
            {
                row.Add(cell.ToString());
                cell.Clear();
            }
            else if (ch == '\n')
            {
                row.Add(cell.ToString());
                cell.Clear();
                rows.Add(row);
                row = [];
            }
            else
            {
                cell.Append(ch);
            }
        }
        row.Add(cell.ToString());
        rows.Add(row);
        // A cell's own line breaks become spaces: table cells are one paragraph.
        return rows.Select(r => r.Select(c => c.Replace('\n', ' ').Trim()).ToList()).ToList();
    }

    /// <summary>The cells as tab-separated text, for the clipboard.</summary>
    public static string ToClipboard(IEnumerable<IEnumerable<string>> rows)
        => string.Join("\r\n", rows.Select(r => string.Join('\t', r.Select(c => c.IndexOfAny(['\t', '\n', '"']) >= 0 ? "\"" + c.Replace("\"", "\"\"") + "\"" : c))));

    /// <summary>The table as CSV, without empty rows and columns at the end.</summary>
    public string ToCsv()
    {
        var rows = Rows.Select(r => r.Select(c => c.Trim()).ToList()).ToList();
        while (rows.Count > 1 && rows[^1].All(c => c.Length == 0))
        {
            rows.RemoveAt(rows.Count - 1);
        }
        int columns = rows.Max(r => r.Count);
        // A column left as inserted: no cells, and no heading or the default "Column 4".
        bool Unused(int c) => rows.Skip(1).All(r => r.Count <= c || r[c].Length == 0)
            && (rows[0].Count <= c || rows[0][c].Length == 0 || rows[0][c] == $"Column {c + 1}");
        while (columns > 1 && Unused(columns - 1))
        {
            columns--;
        }
        return CsvTable.Format(rows.Select(r => (IReadOnlyList<string>)r.Take(columns).ToList()));
    }
}
