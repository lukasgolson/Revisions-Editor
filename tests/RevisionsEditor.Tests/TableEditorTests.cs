using System.IO;
using System.Windows.Controls;
using RevisionsEditor.App.Dialogs;
using RevisionsEditor.Core.Scholarly;
using Xunit;

namespace RevisionsEditor.Tests;

public sealed partial class RevisionsEditorTests
{
    [Fact]
    public void TableData_EditsRowsAndColumns_PastesSpreadsheetCells_AndWritesCsv()
    {
        var table = TableData.FromCsv("Site,RMSE (m)\nGrimston,0.42\nKwantlen\n");
        Assert.Equal(2, table.ColumnCount);
        Assert.Equal(["Kwantlen", ""], table.Rows[2]); // short rows padded

        table.InsertColumn(1);
        Assert.Equal(["Site", "Column 2", "RMSE (m)"], table.Rows[0]);
        table.Rows[0][1] = "Condition";
        table.MoveColumn(1, 2);
        Assert.Equal(["Site", "RMSE (m)", "Condition"], table.Rows[0]);
        table.RemoveColumn(2);
        table.InsertRow(1);
        table.MoveRow(1, 3);
        Assert.Equal(["", ""], table.Rows[3]);
        table.RemoveRow(3);

        // Cells copied from Excel: tab-separated, a quoted cell with a tab and quotes in it; the table grows.
        var (rows, columns) = table.Paste(2, 1, "0.51\tleaf-on\r\n0.47\t\"leaf \"\"off\"\"\tdry\"\r\n");
        Assert.Equal((2, 2), (rows, columns));
        Assert.Equal(["Kwantlen", "0.51", "leaf-on"], table.Rows[2]);
        Assert.Equal(["", "0.47", "leaf \"off\"\tdry"], table.Rows[3]);
        Assert.Equal(["Grimston", "0.42", ""], table.Rows[1]);
        Assert.Equal([["one cell, with a comma"]], TableData.ParseClipboard("one cell, with a comma"));

        // Trailing empty rows and columns are not saved; commas and quotes are quoted.
        table.InsertRow(table.Rows.Count);
        table.InsertColumn(table.ColumnCount);
        Assert.Equal("Site,RMSE (m),\nGrimston,0.42,\nKwantlen,0.51,leaf-on\n,0.47,\"leaf \"\"off\"\"\tdry\"\n", table.ToCsv());
        Assert.Equal("a\t\"b\"\"c\"\r\nd\te", TableData.ToClipboard([["a", "b\"c"], ["d", "e"]]));

        var empty = TableData.Empty();
        Assert.Equal(["Column 1", "Column 2", "Column 3"], empty.Rows[0]);
        Assert.Equal(3, empty.Rows.Count);
    }

    [Fact]
    public void TableEditorDialog_ShowsTheTablesCells_AndSavesTheEditsToItsFile()
        => RunOnStaThread(() =>
        {
            var (ws, doc) = ScholarlyWorkspace();
            try
            {
                var block = ScholarlyBlocks.ParseTable(doc.Blocks[5].Text)!;
                var dialog = new TableEditorDialog(block, new HashSet<string>(), ws) { Left = -20000, Top = -20000, ShowInTaskbar = false };
                dialog.Show();
                try
                {
                    var grid = Descendants(dialog).OfType<DataGrid>().Single();
                    Assert.Equal(["A", "B", "C"], grid.Columns.Select(c => (string)c.Header));
                    Assert.Equal(3, grid.Items.Count); // headings and two sites
                    Assert.Equal("tables/accuracy.csv", dialog.CsvPath);
                    Assert.Equal("Height accuracy by site [@white2016remote].", dialog.Caption);

                    // A block of cells pasted at the current cell (the first, as nothing is selected) grows the table.
                    grid.CurrentCell = new DataGridCellInfo(grid.Items[2], grid.Columns[0]);
                    dialog.Paste("Kwantlen\t0.49\t0.02\nRichmond\t0.55\t-0.01");
                    Assert.Equal(4, grid.Items.Count);
                    Assert.Equal("Site,RMSE (m),Bias (m)\nGrimston,0.42,-0.05\nKwantlen,0.49,0.02\nRichmond,0.55,-0.01\n", dialog.Csv);
                }
                finally
                {
                    dialog.Close();
                }
            }
            finally
            {
                Directory.Delete(ws, recursive: true);
            }
        });
}
