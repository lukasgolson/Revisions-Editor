using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using Microsoft.Win32;
using RevisionsEditor.Core.Scholarly;

namespace RevisionsEditor.App.Dialogs;

/// <summary>
/// Inserts or edits a table: its caption and label, and its cells in a grid like a small spreadsheet (the first
/// row holds the column headings). Rows and columns can be added, removed and moved, and cells pasted from Excel,
/// Google Sheets or LibreOffice. The cells are saved to the table's CSV file, so a table can also be linked to a
/// CSV file an analysis writes.
/// </summary>
public sealed class TableEditorDialog : FormWindow
{
    /// <summary>A row of the grid: its cells by column index (bound as [0], [1], …).</summary>
    public sealed class RowItem(List<string> cells) : INotifyPropertyChanged
    {
        public List<string> Cells { get; } = cells;

        public string this[int column]
        {
            get => column < Cells.Count ? Cells[column] : string.Empty;
            set
            {
                while (Cells.Count <= column)
                {
                    Cells.Add(string.Empty);
                }
                Cells[column] = value ?? string.Empty;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }

    private readonly TextBox _caption;
    private readonly TextBox _label;
    private readonly DataGrid _grid;
    private readonly TextBlock _fileHint;
    private readonly IReadOnlySet<string> _labels;
    private readonly string _workspace;
    private bool _labelEdited;
    private bool _editing;
    private TableData _table;

    public TableEditorDialog(TableBlock? existing, IReadOnlySet<string> existingLabels, string workspace) : base(existing == null ? "Insert Table" : "Edit Table", 860)
    {
        _labels = existingLabels;
        _workspace = workspace;
        CsvPath = existing?.CsvPath;
        OkText = existing == null ? "_Insert" : "_Save";
        AddIntro("Type the cells, or paste them from Excel, Google Sheets or LibreOffice. The first row (H) holds the column headings. Refer to the table in the text with its label, e.g. @tbl:accuracy; it is numbered automatically.");

        _caption = AddTextBox("C_aption:", existing?.Caption ?? string.Empty, multiline: true, lines: 2);
        _label = AddTextBox("_Label:", existing?.Label ?? string.Empty);
        _label.TextChanged += (_, _) => _labelEdited |= _label.IsKeyboardFocusWithin;
        _caption.TextChanged += (_, _) =>
        {
            if (!_labelEdited && existing?.Label == null)
            {
                _label.Text = ScholarlyBlocks.SuggestLabel("tbl", _caption.Text, _labels);
            }
        };

        _table = LoadExisting(existing) ?? TableData.Empty(4, 3);

        var toolbar = new WrapPanel { Margin = new Thickness(0, 12, 0, 6) };
        void Tool(string text, string tip, Action action, double gap = 4)
        {
            var button = new Button { Content = text, ToolTip = tip, Margin = new Thickness(0, 0, gap, 4), Padding = new Thickness(8, 3, 8, 3) };
            button.Click += (_, _) => Edit(action);
            toolbar.Children.Add(button);
        }
        void Group(string text) => toolbar.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 4), FontWeight = FontWeights.SemiBold });
        Group("Rows");
        Tool("Insert above", "Insert a row above the current row", () => _table.InsertRow(CurrentRow));
        Tool("Insert below", "Insert a row below the current row (Ctrl+Enter)", () => { _table.InsertRow(CurrentRow + 1); _moveTo = (CurrentRow + 1, CurrentColumn); });
        Tool("Delete", "Delete the current row", () => _table.RemoveRow(CurrentRow));
        Tool("↑", "Move the row up", () => { _table.MoveRow(CurrentRow, CurrentRow - 1); _moveTo = (CurrentRow - 1, CurrentColumn); });
        Tool("↓", "Move the row down", () => { _table.MoveRow(CurrentRow, CurrentRow + 1); _moveTo = (CurrentRow + 1, CurrentColumn); }, 18);
        Group("Columns");
        Tool("Insert left", "Insert a column left of the current column", () => _table.InsertColumn(CurrentColumn));
        Tool("Insert right", "Insert a column right of the current column", () => { _table.InsertColumn(CurrentColumn + 1); _moveTo = (CurrentRow, CurrentColumn + 1); });
        Tool("Delete", "Delete the current column", () => _table.RemoveColumn(CurrentColumn));
        Tool("←", "Move the column left", () => { _table.MoveColumn(CurrentColumn, CurrentColumn - 1); _moveTo = (CurrentRow, CurrentColumn - 1); });
        Tool("→", "Move the column right", () => { _table.MoveColumn(CurrentColumn, CurrentColumn + 1); _moveTo = (CurrentRow, CurrentColumn + 1); }, 18);
        var link = new Button { Content = "Use a CSV _File...", ToolTip = "Take the cells from a CSV file, and save them to it (for example a table your analysis writes)", Margin = new Thickness(0, 0, 0, 4), Padding = new Thickness(8, 3, 8, 3) };
        link.Click += (_, _) => UseCsvFile();
        toolbar.Children.Add(link);
        AddElement(toolbar);

        _grid = new DataGrid
        {
            AutoGenerateColumns = false,
            CanUserAddRows = false,
            CanUserDeleteRows = false,
            CanUserSortColumns = false,
            CanUserReorderColumns = false,
            CanUserResizeRows = false,
            SelectionUnit = DataGridSelectionUnit.CellOrRowHeader,
            SelectionMode = DataGridSelectionMode.Extended,
            HeadersVisibility = DataGridHeadersVisibility.All,
            ClipboardCopyMode = DataGridClipboardCopyMode.ExcludeHeader,
            GridLinesVisibility = DataGridGridLinesVisibility.All,
            RowHeaderWidth = 40,
            Height = 330,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            EnableRowVirtualization = true
        };
        AutomationProperties_SetName(_grid, "Table cells");
        _grid.LoadingRow += (_, e) =>
        {
            int index = e.Row.GetIndex();
            e.Row.Header = index == 0 ? "H" : index.ToString();
            e.Row.ToolTip = index == 0 ? "Column headings" : null;
            e.Row.FontWeight = index == 0 ? FontWeights.SemiBold : FontWeights.Normal;
        };
        _grid.BeginningEdit += (_, _) => _editing = true;
        _grid.CellEditEnding += (_, _) => _editing = false;
        _grid.PreviewKeyDown += OnGridKeyDown;
        _grid.ContextMenu = BuildContextMenu();
        AddElement(_grid);

        _fileHint = AddHint(string.Empty);
        _placeholder = AddPlaceholderOption("table", existing != null ? DraftPlaceholders.NoteOf(existing.Attributes) : null);
        AddHint("Cells can use *italic*, **bold**, superscripts m^2^ and linked numbers {{source.key}}. Numeric columns are aligned on the decimal point in the PDF. "
            + "Ctrl+V pastes a block of cells at the current cell; Delete clears the selected cells; Enter and Tab move between cells.");

        Rebuild();
        SetValidation(() => _caption.Text.Trim().Length > 0);
        Loaded += (_, _) =>
        {
            if (existing == null)
            {
                _caption.Focus();
            }
            else
            {
                FocusCell(1, 0);
            }
        };
    }

    private static void AutomationProperties_SetName(DependencyObject element, string name)
        => System.Windows.Automation.AutomationProperties.SetName(element, name);

    /// <summary>The table's file: the one it already uses or was linked to, or null for a new tables/&lt;label&gt;.csv.</summary>
    public string? CsvPath { get; private set; }
    public string Caption => _caption.Text.Trim().Replace("\r\n", " ").Replace('\n', ' ');
    public string? Label => _label.Text.Trim().Length == 0 ? null : _label.Text.Trim().StartsWith("tbl:", StringComparison.Ordinal) ? _label.Text.Trim() : "tbl:" + _label.Text.Trim();
    /// <summary>The cells as CSV, as they will be saved.</summary>
    public string Csv => _table.ToCsv();
    /// <summary>What the placeholder table stands in for (empty without a note), or null when it is the real table.</summary>
    public string? PlaceholderNote => _placeholder();
    private readonly Func<string?> _placeholder;
    internal TableData Table => _table;

    private string FullPath(string path) => Path.IsPathRooted(path) ? path : Path.Combine(_workspace, path.Replace('/', Path.DirectorySeparatorChar));

    private TableData? LoadExisting(TableBlock? existing)
    {
        if (existing?.CsvPath is not { } path || !File.Exists(FullPath(path)))
        {
            return null;
        }
        try
        {
            return TableData.FromCsv(File.ReadAllText(FullPath(path)));
        }
        catch (IOException)
        {
            return null;
        }
    }

    private (int Row, int Column)? _moveTo;

    private int CurrentRow => _grid.CurrentCell.Item is RowItem row ? Math.Max(0, _grid.Items.IndexOf(row)) : 0;
    private int CurrentColumn => _grid.CurrentCell.Column?.DisplayIndex ?? 0;

    /// <summary>Applies an edit to the cells (committing the cell being typed in first), then redraws the grid.</summary>
    private void Edit(Action action)
    {
        CommitEdits();
        var (row, column) = (CurrentRow, CurrentColumn);
        _moveTo = null;
        action();
        _table.Normalize();
        var target = _moveTo ?? (row, column);
        Rebuild();
        FocusCell(Math.Clamp(target.Row, 0, _table.Rows.Count - 1), Math.Clamp(target.Column, 0, _table.ColumnCount - 1));
    }

    private void CommitEdits()
    {
        _grid.CommitEdit(DataGridEditingUnit.Cell, true);
        _grid.CommitEdit(DataGridEditingUnit.Row, true);
        _editing = false;
    }

    private static string ColumnName(int index)
    {
        string name = string.Empty;
        for (int n = index + 1; n > 0; n = (n - 1) / 26)
        {
            name = (char)('A' + (n - 1) % 26) + name;
        }
        return name;
    }

    private void Rebuild()
    {
        _table.Normalize();
        _grid.Columns.Clear();
        for (int c = 0; c < _table.ColumnCount; c++)
        {
            _grid.Columns.Add(new DataGridTextColumn
            {
                Header = ColumnName(c),
                Binding = new Binding($"[{c}]") { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.LostFocus },
                Width = new DataGridLength(1, DataGridLengthUnitType.Star),
                MinWidth = 90
            });
        }
        _grid.ItemsSource = _table.Rows.Select(r => new RowItem(r)).ToList();
        string where = CsvPath ?? $"{PaperSettings.TablesFolder}/{(Label ?? "tbl:table")["tbl:".Length..]}.csv";
        _fileHint.Text = $"{_table.Rows.Count - 1} rows × {_table.ColumnCount} columns, saved to {where}"
            + (CsvPath != null && !CsvPath.StartsWith(PaperSettings.TablesFolder + "/", StringComparison.OrdinalIgnoreCase)
                ? ". If your analysis writes this file, it replaces these cells the next time it runs."
                : ".");
    }

    private void FocusCell(int row, int column)
    {
        if (_grid.Items.Count == 0 || _grid.Columns.Count == 0)
        {
            return;
        }
        row = Math.Clamp(row, 0, _grid.Items.Count - 1);
        column = Math.Clamp(column, 0, _grid.Columns.Count - 1);
        var cell = new DataGridCellInfo(_grid.Items[row], _grid.Columns[column]);
        _grid.CurrentCell = cell;
        _grid.SelectedCells.Clear();
        _grid.SelectedCells.Add(cell);
        _grid.ScrollIntoView(_grid.Items[row], _grid.Columns[column]);
        _grid.Focus();
    }

    private void OnGridKeyDown(object sender, KeyEventArgs e)
    {
        bool ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        if (ctrl && e.Key == Key.V && Clipboard.ContainsText())
        {
            string text = Clipboard.GetText();
            // A block of cells (or a cell while not typing in one) is pasted into the grid; text typed into a cell
            // is pasted by the cell itself.
            if (!_editing || text.Contains('\t') || text.TrimEnd('\r', '\n').Contains('\n'))
            {
                e.Handled = true;
                _grid.CancelEdit();
                Paste(text);
            }
        }
        else if (ctrl && e.Key == Key.Enter)
        {
            e.Handled = true;
            Edit(() => { _table.InsertRow(CurrentRow + 1); _moveTo = (CurrentRow + 1, CurrentColumn); });
        }
        else if (!_editing && e.Key is Key.Delete or Key.Back && _grid.SelectedCells.Count > 0)
        {
            e.Handled = true;
            foreach (var cell in _grid.SelectedCells.ToList())
            {
                if (cell.Item is RowItem row && cell.Column != null)
                {
                    row[cell.Column.DisplayIndex] = string.Empty;
                }
            }
        }
    }

    internal void Paste(string text)
    {
        Edit(() =>
        {
            var (rows, columns) = _table.Paste(CurrentRow, CurrentColumn, text);
            _moveTo = (CurrentRow + Math.Max(0, rows - 1), CurrentColumn + Math.Max(0, columns - 1));
        });
    }

    private ContextMenu BuildContextMenu()
    {
        var menu = new ContextMenu();
        void Item(string header, Action action)
        {
            var item = new MenuItem { Header = header };
            item.Click += (_, _) => Edit(action);
            menu.Items.Add(item);
        }
        Item("Insert Row _Above", () => _table.InsertRow(CurrentRow));
        Item("Insert Row _Below", () => _table.InsertRow(CurrentRow + 1));
        Item("_Delete Row", () => _table.RemoveRow(CurrentRow));
        menu.Items.Add(new Separator());
        Item("Insert Column _Left", () => _table.InsertColumn(CurrentColumn));
        Item("Insert Column _Right", () => _table.InsertColumn(CurrentColumn + 1));
        Item("Delete _Column", () => _table.RemoveColumn(CurrentColumn));
        menu.Items.Add(new Separator());
        var paste = new MenuItem { Header = "_Paste Cells", InputGestureText = "Ctrl+V" };
        paste.Click += (_, _) =>
        {
            if (Clipboard.ContainsText())
            {
                Paste(Clipboard.GetText());
            }
        };
        menu.Items.Add(paste);
        return menu;
    }

    private void UseCsvFile()
    {
        var dialog = new OpenFileDialog { Title = "Use a CSV file for the table", Filter = "CSV files (*.csv)|*.csv|Text files (*.txt;*.tsv)|*.txt;*.tsv|All files (*.*)|*.*" };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }
        try
        {
            _table = TableData.FromCsv(File.ReadAllText(dialog.FileName));
        }
        catch (IOException ex)
        {
            MessageBox.Show(this, $"The file could not be read: {ex.Message}", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        string full = Path.GetFullPath(dialog.FileName);
        string root = Path.GetFullPath(_workspace).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        CsvPath = full.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? Path.GetRelativePath(_workspace, full).Replace('\\', '/') : full;
        Rebuild();
        FocusCell(1, 0);
    }

    protected override bool OnAccepting()
    {
        CommitEdits();
        return true;
    }
}
