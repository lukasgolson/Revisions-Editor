using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using RevisionsEditor.App.Rendering;
using RevisionsEditor.Core.Diagrams;
using RevisionsEditor.Core.Scholarly;

namespace RevisionsEditor.App.Dialogs;

/// <summary>
/// Inserts or edits a Mermaid figure (a flowchart, Gantt chart, sequence diagram or any other Mermaid diagram):
/// the Mermaid text on the left, drawn by Mermaid itself on the right as it is typed. Saved as figures/&lt;label&gt;.mmd
/// with the PDF Mermaid drew. A flowchart can instead be converted into an editable diagram (laid out
/// automatically, with LaTeX math in the PDF).
/// </summary>
public sealed class MermaidDialog : Window
{
    private readonly TextBox _caption = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBox _label = new();
    private readonly TextBox _source = new() { AcceptsReturn = true, AcceptsTab = true, TextWrapping = TextWrapping.NoWrap, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, FontSize = 13 };
    private readonly TextBlock _errors = new() { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.IndianRed, Margin = new Thickness(0, 6, 0, 0) };
    private readonly Button _ok = new() { Content = "_Insert", IsDefault = true, MinWidth = 96 };
    private readonly Button _convert = new() { Content = "Convert to _Editable Diagram", Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(0, 0, 8, 0) };
    private readonly MermaidView _view = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(400) };
    private readonly IReadOnlySet<string> _labels;
    private readonly bool _isNew;
    private bool _drawnOk;
    private bool _labelEdited;
    private string _template;

    public MermaidDialog(FigureBlock? existing, string? source, IReadOnlySet<string> existingLabels)
    {
        _labels = existingLabels;
        _isNew = existing == null;
        Title = existing == null ? "Insert Mermaid Diagram" : "Edit Mermaid Diagram";
        Width = 1240;
        Height = 780;
        MinWidth = 800;
        MinHeight = 500;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "ApplicationBackgroundBrush");
        SetResourceReference(ForegroundProperty, "TextFillColorPrimaryBrush");
        _source.FontFamily = TryFindResource("MonoFont") as FontFamily ?? new FontFamily("Cascadia Mono, Consolas");
        _ok.Content = existing == null ? "_Insert" : "_Save";
        _caption.Text = existing?.Caption ?? string.Empty;
        _label.Text = existing?.Label ?? string.Empty;
        _source.Text = source ?? MermaidFiles.Templates[0].Source;
        _template = _source.Text;

        var root = new Grid { Margin = new Thickness(16, 12, 16, 16) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Content = root;

        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(220) });
        if (_isNew) header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(220) });
        header.Children.Add(Field("C_aption:", _caption, 0, 0));
        header.Children.Add(Field("_Label:", _label, 1, 12));
        if (_isNew)
        {
            var templates = new ComboBox { ItemsSource = MermaidFiles.Templates.Select(t => t.Name).ToList(), SelectedIndex = 0 };
            templates.SelectionChanged += (_, _) =>
            {
                var t = MermaidFiles.Templates[Math.Max(0, templates.SelectedIndex)];
                if (_source.Text != _template && MessageBox.Show(this, "Replace the diagram with this starting point?", "Start From", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
                _source.Text = _template = t.Source;
            };
            header.Children.Add(Field("_Start from:", templates, 2, 12));
        }
        root.Children.Add(header);
        _caption.TextChanged += (_, _) =>
        {
            if (!_labelEdited && _isNew) _label.Text = ScholarlyBlocks.SuggestLabel("fig", _caption.Text, _labels);
            Revalidate();
        };
        _label.TextChanged += (_, _) => _labelEdited |= _label.IsKeyboardFocusWithin;

        var main = new Grid { Margin = new Thickness(0, 12, 0, 0) };
        main.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star), MinWidth = 260 });
        main.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(6) });
        main.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3, GridUnitType.Star), MinWidth = 320 });
        Grid.SetRow(main, 1);
        root.Children.Add(main);
        var left = new DockPanel();
        var heading = new Label { Content = "_Mermaid:", Target = _source, Padding = new Thickness(0, 0, 0, 4) };
        DockPanel.SetDock(heading, Dock.Top);
        left.Children.Add(heading);
        var hint = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            Margin = new Thickness(0, 6, 0, 0),
            Text = "Any Mermaid diagram: flowchart, gantt, sequenceDiagram, timeline, classDiagram, stateDiagram… (mermaid.js.org). It is drawn by Mermaid 12.3 and saved as a PDF figure."
        };
        hint.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        DockPanel.SetDock(hint, Dock.Bottom);
        left.Children.Add(hint);
        DockPanel.SetDock(_errors, Dock.Bottom);
        left.Children.Add(_errors);
        left.Children.Add(_source);
        main.Children.Add(left);
        var splitter = new GridSplitter { Width = 6, HorizontalAlignment = HorizontalAlignment.Stretch, Background = Brushes.Transparent };
        Grid.SetColumn(splitter, 1);
        main.Children.Add(splitter);
        var frame = new Border { BorderThickness = new Thickness(1), Child = _view.Control, Background = Brushes.White };
        frame.SetResourceReference(Border.BorderBrushProperty, "ControlStrokeColorDefaultBrush");
        Grid.SetColumn(frame, 2);
        main.Children.Add(frame);

        var footer = new DockPanel { Margin = new Thickness(0, 12, 0, 0) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        _convert.ToolTip = "Turn this flowchart into a diagram you can edit on the canvas, laid out automatically, with LaTeX math in the PDF";
        _convert.Click += (_, _) => Convert();
        buttons.Children.Add(_convert);
        _ok.Click += async (_, _) => await AcceptAsync();
        buttons.Children.Add(_ok);
        buttons.Children.Add(new Button { Content = "Cancel", IsCancel = true, MinWidth = 96, Margin = new Thickness(8, 0, 0, 0) });
        DockPanel.SetDock(buttons, Dock.Right);
        footer.Children.Add(buttons);
        Grid.SetRow(footer, 2);
        root.Children.Add(footer);

        _timer.Tick += async (_, _) => { _timer.Stop(); await DrawAsync(); };
        _source.TextChanged += (_, _) =>
        {
            _convert.IsEnabled = MermaidFlowchart.LooksLikeFlowchart(_source.Text);
            _timer.Stop();
            _timer.Start();
        };
        Loaded += async (_, _) =>
        {
            try
            {
                await _view.InitializeAsync();
                await DrawAsync();
            }
            catch (Exception ex) when (ex is TimeoutException or InvalidOperationException or System.Runtime.InteropServices.COMException or Microsoft.Web.WebView2.Core.WebView2RuntimeNotFoundException)
            {
                _errors.Text = $"Mermaid could not be started: {ex.Message}";
            }
            (_isNew ? _caption : (UIElement)_source).Focus();
        };
        Closed += (_, _) => _view.Dispose();
        _convert.IsEnabled = MermaidFlowchart.LooksLikeFlowchart(_source.Text);
        Revalidate();
    }

    private static StackPanel Field(string label, FrameworkElement control, int column, double gap)
    {
        var panel = new StackPanel { Margin = new Thickness(gap, 0, 0, 0) };
        panel.Children.Add(new Label { Content = label, Target = control, Padding = new Thickness(0, 0, 0, 4) });
        panel.Children.Add(control);
        Grid.SetColumn(panel, column);
        return panel;
    }

    public string Source => _source.Text.Replace("\r\n", "\n");
    public string Caption => _caption.Text.Trim().Replace("\r\n", " ").Replace('\n', ' ');
    public string? Label => _label.Text.Trim().Length == 0 ? null : _label.Text.Trim().StartsWith("fig:", StringComparison.Ordinal) ? _label.Text.Trim() : "fig:" + _label.Text.Trim();
    /// <summary>The PDF drawn when the dialog was accepted (a temporary file).</summary>
    public string? PdfPath { get; private set; }
    /// <summary>Set when the flowchart is to become an editable diagram: its diagram text.</summary>
    public string? ConvertedDiagram { get; private set; }

    private void Revalidate() => _ok.IsEnabled = _drawnOk && Caption.Length > 0;

    internal async Task<MermaidResult> DrawAsync()
    {
        var result = await _view.RenderAsync(Source);
        _drawnOk = result.Ok;
        _errors.Text = result.Ok ? string.Empty : $"Mermaid: {result.Error}";
        Revalidate();
        return result;
    }

    private async Task AcceptAsync()
    {
        if ((await DrawAsync()).Ok is false || Caption.Length == 0) return;
        string pdf = Path.Combine(Path.GetTempPath(), $"revisions-mermaid-{Guid.NewGuid():N}.pdf");
        if (!await _view.SavePdfAsync(pdf))
        {
            _errors.Text = "The drawing could not be saved as PDF.";
            return;
        }
        PdfPath = pdf;
        DialogResult = true;
    }

    private void Convert()
    {
        var (diagram, notes) = MermaidFlowchart.Convert(Source);
        if (notes.Count > 0 && MessageBox.Show(this, "Converting leaves out:\n\n• " + string.Join("\n• ", notes.Take(8)) + "\n\nConvert anyway?", "Convert to Editable Diagram",
                MessageBoxButton.OKCancel, MessageBoxImage.Information) != MessageBoxResult.OK)
        {
            return;
        }
        ConvertedDiagram = diagram;
        DialogResult = true;
    }

    // For tests: draw and save without clicking.
    internal bool DrawnOk => _drawnOk;
    internal string ErrorText => _errors.Text;
    internal Task<bool> SavePdfForTestAsync(string path) => _view.SavePdfAsync(path);
    internal void SetSource(string text) => _source.Text = text;
}
