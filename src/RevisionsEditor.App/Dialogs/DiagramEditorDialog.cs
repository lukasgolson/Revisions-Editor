using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using RevisionsEditor.Core.Diagrams;
using RevisionsEditor.Core.Scholarly;

namespace RevisionsEditor.App.Dialogs;

/// <summary>
/// Draws a block diagram (a pipeline or a network architecture) for a figure. The diagram is written as text on
/// the left (the form that is saved and versioned) and drawn on the right as it is typed. On the drawing, a click
/// selects a node to edit its texts and style below, dragging moves it along its row, and the toolbar adds,
/// connects and removes nodes; every change on the drawing is written back into the text.
/// </summary>
public sealed class DiagramEditorDialog : Window
{
    private readonly TextBox _caption = new() { TextWrapping = TextWrapping.Wrap, AcceptsReturn = false };
    private readonly TextBox _label = new();
    private readonly TextBox _source = new()
    {
        AcceptsReturn = true,
        AcceptsTab = true,
        TextWrapping = TextWrapping.NoWrap,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto
    };
    private readonly TextBlock _errors = new() { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.IndianRed, Margin = new Thickness(0, 6, 0, 0) };
    private readonly Image _image = new() { Stretch = Stretch.Fill, SnapsToDevicePixels = true };
    private readonly Canvas _overlay = new() { Background = Brushes.Transparent, Focusable = true, FocusVisualStyle = null };
    private readonly ScrollViewer _scroller = new() { HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly Grid _canvasHost = new() { HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(8) };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0), FontSize = 12 };
    private readonly CheckBox _fit = new() { Content = "Fit", IsChecked = true, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 4) };
    private readonly List<Button> _editButtons = [];
    private readonly Button _ok = new() { Content = "_Insert", IsDefault = true, MinWidth = 96 };

    // Properties of the selected node.
    private readonly Grid _properties = new() { Margin = new Thickness(0, 8, 0, 0) };
    private readonly ComboBox _kind = new() { ItemsSource = new[] { "Block", "Tensor", "Operator", "Text" }, MinWidth = 110 };
    private readonly TextBox _title = new();
    private readonly TextBox _subtitle = new();
    private readonly TextBox _nodeCaption = new();
    private readonly ComboBox _shape = new() { ItemsSource = new[] { "Descriptor", "Image (thin)", "Feature stack (deep)", "Flat" }, MinWidth = 140 };
    private readonly CheckBox _dashed = new() { Content = "Dashed", Margin = new Thickness(0, 0, 12, 0) };
    private readonly CheckBox _shaded = new() { Content = "Shaded", Margin = new Thickness(0, 0, 12, 0) };
    private readonly CheckBox _bold = new() { Content = "Bold title", IsThreeState = false };

    private readonly DispatcherTimer _renderTimer = new() { Interval = TimeSpan.FromMilliseconds(300) };
    private readonly DispatcherTimer _propertyTimer = new() { Interval = TimeSpan.FromMilliseconds(400) };
    private readonly Stack<string> _undo = new();
    private readonly IReadOnlySet<string> _labels;
    private readonly bool _isNew;

    private Diagram? _diagram;           // the last text that parsed without errors
    private DiagramLayoutResult? _layout; // and its layout
    private string? _selectedId;
    private bool _connecting;
    private bool _suppress;
    private bool _labelEdited;
    private double _zoom = 1;
    private Point? _dragStart;
    private Rectangle? _dragGhost;
    private string _templateSource = string.Empty;

    public DiagramEditorDialog(FigureBlock? existing, string? source, IReadOnlySet<string> existingLabels)
    {
        _labels = existingLabels;
        _isNew = existing == null;
        Title = existing == null ? "Insert Diagram" : "Edit Diagram";
        Width = 1320;
        Height = 820;
        MinWidth = 900;
        MinHeight = 560;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "ApplicationBackgroundBrush");
        SetResourceReference(ForegroundProperty, "TextFillColorPrimaryBrush");
        _source.FontFamily = MonoFont();
        _source.FontSize = 13;
        _ok.Content = existing == null ? "_Insert" : "_Save";

        _caption.Text = existing?.Caption ?? string.Empty;
        _label.Text = existing?.Label ?? string.Empty;
        _source.Text = source ?? DiagramTemplates.All[0].Source;
        _templateSource = _source.Text;

        var root = new Grid { Margin = new Thickness(16, 12, 16, 16) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Content = root;

        var header = BuildHeader(existing == null);
        root.Children.Add(header);

        var main = new Grid { Margin = new Thickness(0, 12, 0, 0) };
        main.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star), MinWidth = 260 });
        main.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(6) });
        main.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3, GridUnitType.Star), MinWidth = 360 });
        Grid.SetRow(main, 1);
        root.Children.Add(main);
        main.Children.Add(BuildSourcePane());
        var splitter = new GridSplitter { Width = 6, HorizontalAlignment = HorizontalAlignment.Stretch, Background = Brushes.Transparent };
        Grid.SetColumn(splitter, 1);
        main.Children.Add(splitter);
        var preview = BuildPreviewPane();
        Grid.SetColumn(preview, 2);
        main.Children.Add(preview);

        var footer = BuildFooter();
        Grid.SetRow(footer, 2);
        root.Children.Add(footer);

        _renderTimer.Tick += (_, _) => { _renderTimer.Stop(); Render(); };
        _propertyTimer.Tick += (_, _) => { _propertyTimer.Stop(); ApplyProperties(); };
        _source.TextChanged += (_, _) =>
        {
            if (_suppress) return;
            _renderTimer.Stop();
            _renderTimer.Start();
        };
        _scroller.SizeChanged += (_, _) => { if (_fit.IsChecked == true) ShowLayout(); };
        _fit.Checked += (_, _) => ShowLayout();
        _fit.Unchecked += (_, _) => ShowLayout();
        Loaded += (_, _) =>
        {
            Render();
            (existing == null ? _caption : (UIElement)_source).Focus();
        };
        PreviewKeyDown += OnKeyDown;
    }

    /// <summary>The diagram's text, as saved in its .diagram file.</summary>
    public string Source => _source.Text.Replace("\r\n", "\n");
    public string Caption => _caption.Text.Trim().Replace("\r\n", " ").Replace('\n', ' ');
    public string? Label => _label.Text.Trim().Length == 0 ? null : _label.Text.Trim().StartsWith("fig:", StringComparison.Ordinal) ? _label.Text.Trim() : "fig:" + _label.Text.Trim();

    private FontFamily MonoFont() => TryFindResource("MonoFont") as FontFamily ?? new FontFamily("Cascadia Mono, Consolas");

    // ---- For tests: the drawing's state and the actions behind its clicks ---------------------------

    internal DiagramLayoutResult? CurrentLayout => _layout;
    internal string? SelectedId => _selectedId;
    internal string Errors => _errors.Text;

    internal void SelectAt(double diagramX, double diagramY)
    {
        _selectedId = _layout?.NodeAt(diagramX, diagramY)?.Node.Id;
        ShowProperties();
        DrawSelection();
    }

    internal void AddAfterSelected(DiagramNodeKind kind) => Add(kind);
    internal void MoveSelected(int steps) => Move(steps);
    internal void RemoveSelected() => Remove();
    internal void UndoLast() => Undo();

    internal void SetSelectedTexts(string title, string subtitle)
    {
        _title.Text = title;
        _subtitle.Text = subtitle;
        _propertyTimer.Stop();
        ApplyProperties();
    }

    internal void TypeSource(string text)
    {
        _source.Text = text;
        _renderTimer.Stop();
        Render();
    }

    // ---- Building the window ----------------------------------------------------------------------

    private FrameworkElement BuildHeader(bool isNew)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(220) });
        if (isNew) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(280) });

        grid.Children.Add(Field("C_aption:", _caption, 0));
        var label = Field("_Label:", _label, 1);
        label.Margin = new Thickness(12, 0, 0, 0);
        grid.Children.Add(label);
        _label.TextChanged += (_, _) => { _labelEdited |= _label.IsKeyboardFocusWithin; Revalidate(); };
        _caption.TextChanged += (_, _) =>
        {
            if (!_labelEdited && _isNew)
            {
                _label.Text = ScholarlyBlocks.SuggestLabel("fig", _caption.Text, _labels);
            }
            Revalidate();
        };

        if (isNew)
        {
            var templates = new ComboBox { ItemsSource = DiagramTemplates.All, DisplayMemberPath = nameof(DiagramTemplates.Template.Name), SelectedIndex = 0 };
            templates.SelectionChanged += (_, _) =>
            {
                if (templates.SelectedItem is not DiagramTemplates.Template t) return;
                if (_source.Text != _templateSource && MessageBox.Show(this, "Replace the diagram with this starting point?", "Start From",
                        MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
                {
                    return;
                }
                _templateSource = t.Source;
                SetSource(t.Source, recordUndo: true);
                _selectedId = null;
            };
            var start = Field("_Start from:", templates, 2);
            start.Margin = new Thickness(12, 0, 0, 0);
            templates.ToolTip = "A starting point: a plain pipeline, or diagrams like a feature extractor, a residual block or a two-branch adaptor";
            grid.Children.Add(start);
        }
        return grid;
    }

    private static StackPanel Field(string label, FrameworkElement control, int column)
    {
        var panel = new StackPanel();
        panel.Children.Add(new Label { Content = label, Target = control, Padding = new Thickness(0, 0, 0, 4) });
        panel.Children.Add(control);
        Grid.SetColumn(panel, column);
        return panel;
    }

    private FrameworkElement BuildSourcePane()
    {
        var dock = new DockPanel();
        var heading = new Label { Content = "_Diagram (text):", Target = _source, Padding = new Thickness(0, 0, 0, 4) };
        DockPanel.SetDock(heading, Dock.Top);
        dock.Children.Add(heading);

        var help = new Expander { Header = "How to write a diagram", Margin = new Thickness(0, 6, 0, 0) };
        help.Content = new TextBlock
        {
            Margin = new Thickness(0, 4, 0, 0),
            FontFamily = MonoFont(),
            FontSize = 11.5,
            TextWrapping = TextWrapping.Wrap,
            Text = """
                block  id "Title" "Subtitle"       a box (title bold)
                tensor id "Name" "$C \times H \times W$"  a 3D slab: thin | deep | flat
                op     id add                       ⊕  (also mul, concat, or "symbol")
                text   id "$x$"                     text alone (bold: bold)
                  options: caption="…" below=id above=id after=id
                           row=1 span=2 dashed dotted shaded plain width=120
                group  id "Title" a b c             a frame round nodes (dotted, shaded)
                a -> b -> c                         arrows (--> dashed, ..> dotted, -- line)
                  options: label="…"  over | under  (route round the row)
                font "Times New Roman"   size 16   gap 34
                Text between $…$ is LaTeX math: \frac{H}{4}, x^2, \mathbb{R}, \to, \times.
                """
        };
        DockPanel.SetDock(help, Dock.Bottom);
        dock.Children.Add(help);
        DockPanel.SetDock(_errors, Dock.Bottom);
        dock.Children.Add(_errors);
        dock.Children.Add(_source);
        return dock;
    }

    private FrameworkElement BuildPreviewPane()
    {
        var dock = new DockPanel();
        var toolbar = new WrapPanel { Margin = new Thickness(0, 0, 0, 4) };
        void Tool(string text, string tip, Action action)
        {
            var b = new Button { Content = text, ToolTip = tip, Margin = new Thickness(0, 0, 4, 4), Padding = new Thickness(8, 3, 8, 3), Focusable = false };
            b.Click += (_, _) => action();
            toolbar.Children.Add(b);
            _editButtons.Add(b);
        }
        void Gap() => toolbar.Children.Add(new Border { Width = 12 });
        Tool("+ Block", "Add a block after the selected node (spliced into its arrow)", () => Add(DiagramNodeKind.Block));
        Tool("+ Tensor", "Add a tensor after the selected node", () => Add(DiagramNodeKind.Tensor));
        Tool("+ ⊕", "Add an operator (⊕) after the selected node", () => Add(DiagramNodeKind.Op));
        Tool("+ Text", "Add text after the selected node", () => Add(DiagramNodeKind.Text));
        Gap();
        Tool("Connect…", "Draw an arrow from the selected node to the next node you click", StartConnecting);
        Tool("◀", "Move the selected node left along its row (or drag it)", () => Move(-1));
        Tool("▶", "Move the selected node right along its row (or drag it)", () => Move(1));
        Tool("Delete", "Remove the selected node; arrows through it are joined (Delete)", Remove);
        Gap();
        Tool("Undo", "Undo the last change made on the drawing (Ctrl+Z on the drawing)", Undo);
        toolbar.Children.Add(_fit);
        DockPanel.SetDock(toolbar, Dock.Top);
        dock.Children.Add(toolbar);

        BuildProperties();
        DockPanel.SetDock(_properties, Dock.Bottom);
        dock.Children.Add(_properties);
        DockPanel.SetDock(_status, Dock.Bottom);
        dock.Children.Add(_status);
        _status.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");

        _canvasHost.Children.Add(_image);
        _canvasHost.Children.Add(_overlay);
        _overlay.MouseLeftButtonDown += OnCanvasDown;
        _overlay.MouseMove += OnCanvasMove;
        _overlay.MouseLeftButtonUp += OnCanvasUp;
        _scroller.Content = _canvasHost;
        var frame = new Border { BorderThickness = new Thickness(1), Background = Brushes.White, Child = _scroller };
        frame.SetResourceReference(Border.BorderBrushProperty, "ControlStrokeColorDefaultBrush");
        dock.Children.Add(frame);
        return dock;
    }

    private void BuildProperties()
    {
        var g = _properties;
        for (int c = 0; c < 4; c++)
        {
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = c % 2 == 0 ? GridLength.Auto : new GridLength(1, GridUnitType.Star) });
        }
        for (int r = 0; r < 4; r++) g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        void Put(UIElement e, int row, int column, int span = 1)
        {
            Grid.SetRow(e, row);
            Grid.SetColumn(e, column);
            Grid.SetColumnSpan(e, span);
            if (e is FrameworkElement f) f.Margin = new Thickness(column % 2 == 0 ? 0 : 0, 2, column == 1 ? 12 : 0, 2);
            g.Children.Add(e);
        }
        Label L(string text, UIElement target) => new() { Content = text, Target = target, Padding = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        Put(L("_Kind:", _kind), 0, 0);
        Put(_kind, 0, 1);
        Put(L("Tensor _shape:", _shape), 0, 2);
        Put(_shape, 0, 3);
        Put(L("_Title:", _title), 1, 0);
        Put(_title, 1, 1, 3);
        Put(L("S_ubtitle:", _subtitle), 2, 0);
        Put(_subtitle, 2, 1);
        Put(L("Ca_ption:", _nodeCaption), 2, 2);
        Put(_nodeCaption, 2, 3);
        var flags = new StackPanel { Orientation = Orientation.Horizontal };
        flags.Children.Add(_dashed);
        flags.Children.Add(_shaded);
        flags.Children.Add(_bold);
        Put(flags, 3, 1, 3);
        _title.ToolTip = "Text with LaTeX math between $…$; for an operator: add, mul, concat or a symbol";
        _nodeCaption.ToolTip = "Text under the node, such as a shape or a dimension (\\n starts a new line)";

        foreach (var box in new[] { _title, _subtitle, _nodeCaption })
        {
            box.TextChanged += (_, _) => PropertyEdited();
            box.LostKeyboardFocus += (_, _) => { if (_propertyTimer.IsEnabled) { _propertyTimer.Stop(); ApplyProperties(); } };
        }
        _kind.SelectionChanged += (_, _) => PropertyEdited(immediately: true);
        _shape.SelectionChanged += (_, _) => PropertyEdited(immediately: true);
        foreach (var check in new[] { _dashed, _shaded, _bold })
        {
            check.Click += (_, _) => PropertyEdited(immediately: true);
        }
        g.IsEnabled = false;
    }

    private FrameworkElement BuildFooter()
    {
        var dock = new DockPanel { Margin = new Thickness(0, 12, 0, 0) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        _ok.Click += (_, _) => Accept();
        buttons.Children.Add(_ok);
        buttons.Children.Add(new Button { Content = "Cancel", IsCancel = true, MinWidth = 96, Margin = new Thickness(8, 0, 0, 0) });
        DockPanel.SetDock(buttons, Dock.Right);
        dock.Children.Add(buttons);
        var hint = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
            Text = "Saved as figures/<label>.diagram with an SVG drawing for the editor and Word; the LaTeX export draws it as TikZ, so its math is typeset by LaTeX. Refer to it in the text with its label, e.g. @fig:network."
        };
        hint.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        dock.Children.Add(hint);
        return dock;
    }

    private void Revalidate() => _ok.IsEnabled = Caption.Length > 0 && _diagram != null && _diagram.Nodes.Any();

    private void Accept()
    {
        Render();
        var parsed = DiagramSource.Parse(Source, out var errors);
        if (errors.Count > 0)
        {
            MessageBox.Show(this, $"Line {errors[0].Line}: {errors[0].Message}", "The Diagram Has Errors", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (!parsed.Nodes.Any() || Caption.Length == 0) return;
        DialogResult = true;
    }

    // ---- Drawing ----------------------------------------------------------------------------------

    private void Render()
    {
        var parsed = DiagramSource.Parse(Source, out var errors);
        _errors.Text = string.Join("\n", errors.Take(4).Select(e => $"Line {e.Line}: {e.Message}")) + (errors.Count > 4 ? $"\n…and {errors.Count - 4} more." : string.Empty);
        _errors.Visibility = errors.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        bool valid = errors.Count == 0;
        foreach (var b in _editButtons) b.IsEnabled = valid || (string)b.Content == "Undo";
        if (!valid)
        {
            _status.Text = "Fix the text to edit on the drawing; it shows the last version without errors.";
            Revalidate();
            return;
        }
        _diagram = parsed;
        try
        {
            _layout = DiagramLayout.Layout(parsed);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            _errors.Text = $"The diagram cannot be drawn: {ex.Message}";
            _errors.Visibility = Visibility.Visible;
            return;
        }
        if (_selectedId != null && _diagram.FindNode(_selectedId) == null) _selectedId = null;
        ShowLayout();
        ShowProperties();
        Revalidate();
    }

    private void ShowLayout()
    {
        if (_layout == null) return;
        double available = Math.Max(200, _scroller.ActualWidth - 40);
        _zoom = _fit.IsChecked == true ? Math.Min(1.3, available / _layout.Width) : 1;
        double device = VisualTreeHelper.GetDpi(this).DpiScaleX;
        var png = DiagramFiles.RenderPreview(DiagramRenderer.ToSvg(_layout), _zoom * device * 1.25);
        if (png != null)
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = new MemoryStream(png);
            bitmap.EndInit();
            bitmap.Freeze();
            _image.Source = bitmap;
        }
        _image.Width = _overlay.Width = _layout.Width * _zoom;
        _image.Height = _overlay.Height = _layout.Height * _zoom;
        DrawSelection();
    }

    private void DrawSelection()
    {
        _overlay.Children.Clear();
        _dragGhost = null;
        if (_layout == null) return;
        var selected = _layout.Nodes.FirstOrDefault(n => n.Node.Id == _selectedId);
        if (selected != null)
        {
            var r = selected.Body.Inflate(4, 4);
            var rect = new Rectangle
            {
                Width = r.Width * _zoom,
                Height = r.Height * _zoom,
                Stroke = new SolidColorBrush(Color.FromRgb(0x1E, 0x78, 0xD7)),
                StrokeThickness = 2,
                StrokeDashArray = _connecting ? [3, 2] : null,
                Fill = new SolidColorBrush(Color.FromArgb(0x18, 0x1E, 0x78, 0xD7)),
                IsHitTestVisible = false
            };
            Canvas.SetLeft(rect, r.X * _zoom);
            Canvas.SetTop(rect, r.Y * _zoom);
            _overlay.Children.Add(rect);
        }
        _status.Text = _connecting
            ? $"Click the node the arrow from “{_selectedId}” goes to (Esc to stop)."
            : selected != null
                ? $"Selected “{selected.Node.Id}”. Edit it below, drag it along its row, or add a node after it."
                : "Click a node to select it. Changes on the drawing are written into the text.";
    }

    // ---- Selecting, dragging, connecting -----------------------------------------------------------

    private DiagramNodeLayout? NodeAt(Point p) => _layout?.NodeAt(p.X / _zoom, p.Y / _zoom);

    private void OnCanvasDown(object sender, MouseButtonEventArgs e)
    {
        _overlay.Focus();
        var hit = NodeAt(e.GetPosition(_overlay));
        if (_connecting)
        {
            _connecting = false;
            if (hit != null && _selectedId != null && hit.Node.Id != _selectedId)
            {
                string from = _selectedId;
                Edit(d => DiagramSource.Connect(d, from, hit.Node.Id));
                _selectedId = hit.Node.Id;
            }
            DrawSelection();
            return;
        }
        _selectedId = hit?.Node.Id;
        ShowProperties();
        DrawSelection();
        if (hit != null)
        {
            if (e.ClickCount == 2)
            {
                _title.Focus();
                _title.SelectAll();
                return;
            }
            _dragStart = e.GetPosition(_overlay);
            _overlay.CaptureMouse();
        }
    }

    private void OnCanvasMove(object sender, MouseEventArgs e)
    {
        if (_dragStart is not { } start || e.LeftButton != MouseButtonState.Pressed || _layout == null) return;
        var p = e.GetPosition(_overlay);
        double dx = p.X - start.X;
        if (Math.Abs(dx) < 6 && _dragGhost == null) return;
        var node = _layout.Nodes.FirstOrDefault(n => n.Node.Id == _selectedId);
        if (node == null) return;
        if (_dragGhost == null)
        {
            _dragGhost = new Rectangle
            {
                Width = node.Body.Width * _zoom,
                Height = node.Body.Height * _zoom,
                Stroke = new SolidColorBrush(Color.FromRgb(0x1E, 0x78, 0xD7)),
                StrokeDashArray = [4, 3],
                StrokeThickness = 1.5,
                IsHitTestVisible = false
            };
            _overlay.Children.Add(_dragGhost);
            _status.Text = "Drop the node where it should go in its row.";
        }
        Canvas.SetLeft(_dragGhost, node.Body.X * _zoom + dx);
        Canvas.SetTop(_dragGhost, node.Body.Y * _zoom);
    }

    private void OnCanvasUp(object sender, MouseButtonEventArgs e)
    {
        _overlay.ReleaseMouseCapture();
        var start = _dragStart;
        _dragStart = null;
        if (_dragGhost == null || start == null || _layout == null || _selectedId == null)
        {
            if (_dragGhost != null) DrawSelection();
            return;
        }
        var node = _layout.Nodes.First(n => n.Node.Id == _selectedId);
        double drop = node.Body.CenterX + (e.GetPosition(_overlay).X - start.Value.X) / _zoom;
        var row = _layout.Nodes.Where(n => n.Row == node.Row).OrderBy(n => n.Body.CenterX).ToList();
        int current = row.IndexOf(node);
        int target = row.Count(n => n != node && n.Body.CenterX < drop);
        string id = _selectedId;
        if (target != current && !Edit(d => DiagramSource.MoveNode(d, id, target - current)))
        {
            _status.Text = "This node is placed below= or above= another; change that in the text to move it.";
            return;
        }
        DrawSelection();
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && _connecting)
        {
            _connecting = false;
            DrawSelection();
            e.Handled = true;
        }
        else if (_overlay.IsKeyboardFocused && e.Key == Key.Delete)
        {
            Remove();
            e.Handled = true;
        }
        else if (_overlay.IsKeyboardFocused && e.Key == Key.Z && Keyboard.Modifiers == ModifierKeys.Control)
        {
            Undo();
            e.Handled = true;
        }
        else if (_overlay.IsKeyboardFocused && e.Key is Key.Left or Key.Right && Keyboard.Modifiers == ModifierKeys.Alt)
        {
            Move(e.Key == Key.Left ? -1 : 1);
            e.Handled = true;
        }
    }

    // ---- Edits on the drawing ------------------------------------------------------------------------

    /// <summary>Applies an edit to the diagram and writes it back into the text. False when the edit did nothing.</summary>
    private bool Edit(Func<Diagram, bool> edit)
    {
        var diagram = DiagramSource.Parse(Source, out var errors);
        if (errors.Count > 0) return false;
        if (!edit(diagram)) return false;
        SetSource(DiagramSource.Write(diagram), recordUndo: true);
        return true;
    }

    private void Edit(Action<Diagram> edit) => Edit(d => { edit(d); return true; });

    private void SetSource(string text, bool recordUndo)
    {
        if (recordUndo) _undo.Push(Source);
        _suppress = true;
        int caret = _source.CaretIndex;
        _source.Text = text;
        _source.CaretIndex = Math.Min(caret, text.Length);
        _suppress = false;
        _renderTimer.Stop();
        Render();
    }

    private void Undo()
    {
        if (_undo.Count == 0)
        {
            _status.Text = "Nothing to undo on the drawing (Ctrl+Z in the text undoes typing).";
            return;
        }
        SetSource(_undo.Pop(), recordUndo: false);
    }

    private void Add(DiagramNodeKind kind)
    {
        string? after = _selectedId;
        DiagramNode? added = null;
        Edit(d => { added = DiagramSource.AddNode(d, kind, after); });
        if (added != null)
        {
            _selectedId = added.Id;
            ShowProperties();
            DrawSelection();
            _title.Focus();
            _title.SelectAll();
        }
    }

    private void Remove()
    {
        if (_selectedId == null) return;
        string id = _selectedId;
        _selectedId = null;
        Edit(d => DiagramSource.RemoveNode(d, id));
    }

    private void Move(int steps)
    {
        if (_selectedId == null) return;
        string id = _selectedId;
        if (!Edit(d => DiagramSource.MoveNode(d, id, steps)))
        {
            _status.Text = "It cannot move further, or it is placed below= or above= another node.";
        }
    }

    private void StartConnecting()
    {
        if (_selectedId == null)
        {
            _status.Text = "Select the node the arrow starts from first.";
            return;
        }
        _connecting = true;
        DrawSelection();
        _overlay.Focus();
    }

    // ---- Properties of the selected node -------------------------------------------------------------

    private void ShowProperties()
    {
        var node = _selectedId != null ? _diagram?.FindNode(_selectedId) : null;
        _properties.IsEnabled = node != null;
        _suppress = true;
        _kind.SelectedIndex = node == null ? -1 : (int)node.Kind;
        _title.Text = node?.Title ?? string.Empty;
        _subtitle.Text = node?.Subtitle ?? string.Empty;
        _nodeCaption.Text = node?.Caption ?? string.Empty;
        _shape.SelectedIndex = node?.Shape switch { "thin" => 1, "deep" => 2, "flat" => 3, _ => 0 };
        _shape.IsEnabled = node?.Kind == DiagramNodeKind.Tensor;
        _dashed.IsChecked = node?.Border == DiagramLineStyle.Dashed;
        _shaded.IsChecked = node?.Shaded == true;
        _bold.IsChecked = node?.IsTitleBold == true;
        _suppress = false;
    }

    private void PropertyEdited(bool immediately = false)
    {
        if (_suppress || _selectedId == null) return;
        _propertyTimer.Stop();
        if (immediately) ApplyProperties();
        else _propertyTimer.Start();
    }

    private void ApplyProperties()
    {
        if (_selectedId == null) return;
        string id = _selectedId;
        var kind = (DiagramNodeKind)Math.Max(0, _kind.SelectedIndex);
        string title = _title.Text, subtitle = _subtitle.Text, caption = _nodeCaption.Text;
        string shape = _shape.SelectedIndex switch { 1 => "thin", 2 => "deep", 3 => "flat", _ => string.Empty };
        bool dashed = _dashed.IsChecked == true, shaded = _shaded.IsChecked == true, bold = _bold.IsChecked == true;
        Edit(d =>
        {
            var node = d.FindNode(id);
            if (node == null) return false;
            var before = DiagramSource.Write(new DiagramNodeStatement(node));
            node.Kind = kind;
            node.Title = title.Replace("\r", string.Empty).Replace("\n", " ");
            node.Subtitle = subtitle.Replace("\r", string.Empty).Replace("\n", " ");
            node.Caption = caption.Replace("\r", string.Empty).Replace("\n", " ");
            node.Shape = kind == DiagramNodeKind.Tensor ? shape : string.Empty;
            node.Border = dashed ? DiagramLineStyle.Dashed : node.Border == DiagramLineStyle.Dotted ? DiagramLineStyle.Dotted : DiagramLineStyle.Solid;
            node.Shaded = shaded;
            bool defaultBold = kind is DiagramNodeKind.Block or DiagramNodeKind.Tensor;
            node.BoldTitle = bold == defaultBold ? null : bold;
            return DiagramSource.Write(new DiagramNodeStatement(node)) != before;
        });
    }
}
