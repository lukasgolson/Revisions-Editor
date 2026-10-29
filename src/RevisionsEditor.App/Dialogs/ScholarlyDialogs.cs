using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Microsoft.Win32;
using RevisionsEditor.App.ViewModels;
using RevisionsEditor.Core.Equations;
using RevisionsEditor.Core.Journals;
using RevisionsEditor.Core.Scholarly;

namespace RevisionsEditor.App.Dialogs;

/// <summary>Adds or edits a figure: an image in the workspace's figures folder, its caption and label.</summary>
public sealed class FigureDialog : FormWindow
{
    private readonly TextBox _caption;
    private readonly TextBox _file;
    private readonly TextBox _label;
    private readonly TextBox _width;
    private readonly IReadOnlySet<string> _labels;
    private bool _labelEdited;

    public FigureDialog(FigureBlock? existing, IReadOnlySet<string> existingLabels) : base(existing == null ? "Insert Figure" : "Edit Figure")
    {
        _labels = existingLabels;
        OkText = existing == null ? "_Insert" : "_Save";
        AddIntro("The image is copied into the workspace's figures folder, so it is committed and synced with the paper. Refer to the figure in the text with its label, e.g. @fig:study-area; it is numbered automatically.");
        _file = AddTextBoxWithButton("_Figure (SVG or PDF vector graphic, or PNG, JPEG, TIFF image):", "_Browse...", Browse, existing?.ImagePath ?? string.Empty);
        _caption = AddTextBox("_Caption:", existing?.Caption ?? string.Empty, multiline: true, lines: 3);
        _label = AddTextBox("_Label:", existing?.Label ?? string.Empty);
        _width = AddTextBox("_Width (percent of the text width, empty for the natural size):", existing?.Attributes.GetValueOrDefault("width") ?? string.Empty);
        _placeholder = AddPlaceholderOption("figure", existing != null ? DraftPlaceholders.NoteOf(existing.Attributes) : null);
        AddHint("Vector graphics (SVG, or PDF as matplotlib, R and Inkscape save them) stay sharp at any size in the PDF; Word gets the SVG itself, "
            + "and a PDF figure as a 300 dpi image. In two-column layouts a figure wider than 60% of the text spans both columns. "
            + "Captions can use *italic*, citations [@key] and linked numbers {{source.key}}.");
        _label.TextChanged += (_, _) => _labelEdited |= _label.IsKeyboardFocusWithin;
        _caption.TextChanged += (_, _) =>
        {
            if (!_labelEdited && existing?.Label == null)
            {
                _label.Text = ScholarlyBlocks.SuggestLabel("fig", _caption.Text, _labels);
            }
        };
        SetValidation(() => _file.Text.Trim().Length > 0 && _caption.Text.Trim().Length > 0);
    }

    public string ImagePath => _file.Text.Trim();
    public string Caption => _caption.Text.Trim().Replace("\r\n", " ").Replace('\n', ' ');
    public string? Label => _label.Text.Trim().Length == 0 ? null : _label.Text.Trim().StartsWith("fig:", StringComparison.Ordinal) ? _label.Text.Trim() : "fig:" + _label.Text.Trim();
    public string? FigureWidth => _width.Text.Trim().Length == 0 ? null : _width.Text.Trim().EndsWith('%') ? _width.Text.Trim() : _width.Text.Trim() + "%";
    /// <summary>What the placeholder figure stands in for (empty without a note), or null when it is the real figure.</summary>
    public string? PlaceholderNote => _placeholder();
    private readonly Func<string?> _placeholder;

    private void Browse(TextBox box)
    {
        var dialog = new OpenFileDialog { Title = "Choose the figure (image or vector graphic)", Filter = FigureImages.FileDialogFilter };
        if (dialog.ShowDialog(this) == true)
        {
            box.Text = dialog.FileName;
        }
    }
}

/// <summary>Adds or edits a display equation written in LaTeX, with a live preview.</summary>
public sealed class EquationDialog : FormWindow
{
    private readonly TextBox _latex;
    private readonly TextBox _label;
    private readonly TextBlock _preview;

    public EquationDialog(EquationBlock? existing, IReadOnlySet<string> existingLabels) : base(existing == null ? "Insert Equation" : "Edit Equation", 620)
    {
        OkText = existing == null ? "_Insert" : "_Save";
        AddIntro("Write the equation in LaTeX. It is exported to Word as a native, editable equation and numbered automatically; refer to it with its label, e.g. @eq:rmse.");
        _latex = AddTextBox("_LaTeX:", existing?.Latex ?? @"RMSE = \sqrt{\frac{1}{n}\sum_{i=1}^{n}(\hat{y}_i - y_i)^2}", multiline: true, monospace: true, lines: 3);
        _preview = new TextBlock
        {
            FontFamily = new System.Windows.Media.FontFamily("Cambria Math"),
            FontSize = 18,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 10, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Center
        };
        AddElement(_preview);
        _label = AddTextBox("L_abel:", existing?.Label ?? SuggestFrom(_latex.Text, existingLabels));
        bool labelEdited = existing?.Label != null;
        _label.TextChanged += (_, _) => labelEdited |= _label.IsKeyboardFocusWithin;
        _latex.TextChanged += (_, _) =>
        {
            if (!labelEdited)
            {
                _label.Text = SuggestFrom(_latex.Text, existingLabels);
            }
        };
        AddHint("Supported: fractions, roots, sums and integrals with limits, sub- and superscripts, Greek letters, accents (\\hat, \\bar), \\left( \\right), matrices, cases and aligned equations, \\text{} and \\mathrm{}.");
        _latex.TextChanged += (_, _) => _preview.Text = LatexToUnicode.Convert(_latex.Text);
        _preview.Text = LatexToUnicode.Convert(_latex.Text);
        SetValidation(() => _latex.Text.Trim().Length > 0);
    }

    /// <summary>A label from the left-hand side of the equation ("RMSE = …" gives "eq:rmse").</summary>
    private static string SuggestFrom(string latex, IReadOnlySet<string> existingLabels)
    {
        string left = latex.Split('=')[0];
        string name = System.Text.RegularExpressions.Regex.Replace(left, @"\\[a-zA-Z]+|[^A-Za-z0-9]+", " ").Trim();
        return ScholarlyBlocks.SuggestLabel("eq", name.Length is > 0 and <= 30 ? name : "equation", existingLabels);
    }

    public string Latex => _latex.Text.Trim().Replace("\r\n", " ").Replace('\n', ' ');
    public string? Label => _label.Text.Trim().Length == 0 ? null : _label.Text.Trim().StartsWith("eq:", StringComparison.Ordinal) ? _label.Text.Trim() : "eq:" + _label.Text.Trim();
}

/// <summary>Picks references from the bibliography (or from Zotero) and builds the citation to insert.</summary>
public sealed class CitationPickerDialog : FormWindow
{
    private readonly TextBox _search;
    private readonly ListBox _list;
    private readonly CheckBox _narrative;
    private readonly TextBox _prefix;
    private readonly TextBox _locator;
    private readonly ICollectionView _view;

    public CitationPickerDialog(IReadOnlyList<ReferenceItem> references, bool zoteroAvailable) : base("Insert Citation", 680)
    {
        OkText = "_Insert";
        AddIntro(references.Count == 0
            ? "The bibliography is empty. Pick references in Zotero, or add them by DOI in the References tab."
            : "Choose one or more references (Ctrl+click for several). They are inserted as citation keys, such as [@white2016remote], and formatted in the journal's style when exported.");
        _search = AddTextBox("_Search (author, year, title or key):");
        _list = new ListBox { Height = 260, SelectionMode = SelectionMode.Extended, ItemsSource = references };
        ScrollViewer.SetHorizontalScrollBarVisibility(_list, ScrollBarVisibility.Disabled);
        _list.ItemTemplate = BuildItemTemplate();
        _view = CollectionViewSource.GetDefaultView(references);
        _view.Filter = o => o is ReferenceItem r && _search.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries).All(w => r.SearchText.Contains(w, StringComparison.OrdinalIgnoreCase));
        _search.TextChanged += (_, _) => _view.Refresh();
        _list.SelectionChanged += (_, _) => Revalidate();
        _list.MouseDoubleClick += (_, _) => { if (_list.SelectedItems.Count > 0) { DialogResult = true; } };
        AddField("_References:", _list);

        _narrative = new CheckBox { Content = "In the sentence (“White et al. (2016) showed…”) instead of in parentheses", Margin = new Thickness(0, 10, 0, 0) };
        AddElement(_narrative);
        _prefix = AddTextBox("_Prefix (optional, e.g. “see”):");
        _locator = AddTextBox("Pa_ge or other locator (optional, e.g. “p. 12”):");
        if (zoteroAvailable)
        {
            var zotero = new Button { Content = "Pick in _Zotero Instead...", Margin = new Thickness(0, 12, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
            zotero.Click += (_, _) => { UseZotero = true; DialogResult = true; };
            AddElement(zotero);
        }
        SetValidation(() => _list.SelectedItems.Count > 0);
        Loaded += (_, _) => _search.Focus();
    }

    /// <summary>The author chose Zotero's own picker instead.</summary>
    public bool UseZotero { get; private set; }

    public string CitationText
    {
        get
        {
            var keys = _list.SelectedItems.Cast<ReferenceItem>().Select(r => r.Key).ToList();
            string prefix = _prefix.Text.Trim();
            string locator = _locator.Text.Trim();
            if (_narrative.IsChecked == true && keys.Count == 1 && prefix.Length == 0 && locator.Length == 0)
            {
                return "@" + keys[0];
            }
            var items = keys.Select((k, i) =>
                (i == 0 && prefix.Length > 0 ? prefix + " " : string.Empty) + "@" + k + (i == keys.Count - 1 && locator.Length > 0 ? ", " + locator : string.Empty));
            return "[" + string.Join("; ", items) + "]";
        }
    }

    private static DataTemplate BuildItemTemplate()
    {
        var factory = new FrameworkElementFactory(typeof(TextBlock));
        factory.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
        var binding = new MultiBinding { StringFormat = "{0} ({1})  {2}   [{3}]" };
        binding.Bindings.Add(new Binding(nameof(ReferenceItem.Authors)));
        binding.Bindings.Add(new Binding(nameof(ReferenceItem.Year)));
        binding.Bindings.Add(new Binding(nameof(ReferenceItem.Title)));
        binding.Bindings.Add(new Binding(nameof(ReferenceItem.Key)));
        factory.SetBinding(TextBlock.TextProperty, binding);
        return new DataTemplate { VisualTree = factory };
    }
}

/// <summary>Picks a figure, table, equation or labelled section to refer to.</summary>
public sealed class CrossReferencePickerDialog : FormWindow
{
    private readonly ListBox _list;

    public sealed record Target(string Label, string Description);

    public CrossReferencePickerDialog(IReadOnlyList<Target> targets) : base("Insert Cross-Reference", 620)
    {
        OkText = "_Insert";
        AddIntro(targets.Count == 0
            ? "Nothing is labelled yet. Figures, tables and equations get a label when they are inserted; label a heading by ending it with {#sec:name}."
            : "The reference is written as its label (for example @fig:site) and becomes “Fig. 2” in the journal's style, renumbered automatically when items move.");
        _list = new ListBox { Height = 260, ItemsSource = targets, DisplayMemberPath = nameof(Target.Description) };
        _list.SelectionChanged += (_, _) => Revalidate();
        _list.MouseDoubleClick += (_, _) => { if (_list.SelectedItem != null) { DialogResult = true; } };
        AddField("_Refer to:", _list);
        SetValidation(() => _list.SelectedItem != null);
    }

    public string ReferenceText => "@" + ((Target)_list.SelectedItem).Label;
}

/// <summary>Picks a value from the analysis results to link into the text.</summary>
public sealed class NumberPickerDialog : FormWindow
{
    private readonly ListBox _list;
    private readonly TextBox _format;
    private readonly TextBlock _preview;
    private readonly TextBox _search;
    private readonly ICollectionView _view;

    public NumberPickerDialog(IReadOnlyList<ResultValueItem> values) : base("Insert Linked Number", 620)
    {
        OkText = "_Insert";
        AddIntro(values.Count == 0
            ? "There are no results yet. Add a CSV or JSON file from your analysis in the Figures and Data tab."
            : "The number is linked to the results file, so it updates everywhere when the analysis is rerun and the results are refreshed.");
        _search = AddTextBox("_Search:");
        _list = new ListBox { Height = 240, ItemsSource = values };
        var factory = new FrameworkElementFactory(typeof(TextBlock));
        var binding = new MultiBinding { StringFormat = "{0}  =  {1}" };
        binding.Bindings.Add(new Binding(nameof(ResultValueItem.Key)));
        binding.Bindings.Add(new Binding(nameof(ResultValueItem.Value)));
        factory.SetBinding(TextBlock.TextProperty, binding);
        _list.ItemTemplate = new DataTemplate { VisualTree = factory };
        _view = CollectionViewSource.GetDefaultView(values);
        _view.Filter = o => o is ResultValueItem v && (_search.Text.Trim().Length == 0 || v.Key.Contains(_search.Text.Trim(), StringComparison.OrdinalIgnoreCase));
        _search.TextChanged += (_, _) => _view.Refresh();
        AddField("_Value:", _list);
        _format = AddTextBox("_Format (optional): 0.00, F1, N0, P1 (percent), 3sf (significant figures):");
        _preview = AddHint(string.Empty);
        _list.SelectionChanged += (_, _) => UpdatePreview();
        _format.TextChanged += (_, _) => UpdatePreview();
        SetValidation(() => _list.SelectedItem != null);
    }

    private void UpdatePreview()
    {
        if (_list.SelectedItem is ResultValueItem v)
        {
            _preview.Text = $"Shown in the paper as: {ResultsLibrary.FormatValue(v.Value, _format.Text.Trim().Length == 0 ? null : _format.Text.Trim())}";
        }
        Revalidate();
    }

    public string PlaceholderText
    {
        get
        {
            var v = (ResultValueItem)_list.SelectedItem;
            return _format.Text.Trim().Length == 0 ? v.Placeholder : $"{{{{{v.Key}:{_format.Text.Trim()}}}}}";
        }
    }
}

/// <summary>Asks for one piece of text (for example DOIs).</summary>
public sealed class TextPromptDialog : FormWindow
{
    private readonly TextBox _text;

    public TextPromptDialog(string title, string intro, string label, string okText, bool multiline = false, string initial = "") : base(title)
    {
        OkText = okText;
        AddIntro(intro);
        _text = AddTextBox(label, initial, multiline, lines: multiline ? 6 : 1);
        SetValidation(() => _text.Text.Trim().Length > 0);
        Loaded += (_, _) => { _text.Focus(); _text.SelectAll(); };
    }

    public string Text => _text.Text.Trim();
}

/// <summary>Starts a review round from the comments pasted from the decision letter.</summary>
public sealed class ReviewRoundDialog : FormWindow
{
    private readonly TextBox _name;
    private readonly TextBox _number;
    private readonly ComboBox _submitted;
    private readonly TextBox _comments;
    private readonly TextBlock _found;

    public sealed record SubmittedVersion(string Label, string? Tag, string? Commit);

    public ReviewRoundDialog(string suggestedName, IReadOnlyList<SubmittedVersion> versions) : base("Add Reviewer Comments", 680)
    {
        OkText = "_Add";
        AddIntro("Paste the comments from the decision letter or the submission system. They are split into numbered points by reviewer (R1.1, R1.2, …, E.1 for the editor), ready for your responses.");
        _name = AddTextBox("_Round:", suggestedName);
        _number = AddTextBox("_Manuscript number (optional):");
        _submitted = new ComboBox { ItemsSource = versions, DisplayMemberPath = nameof(SubmittedVersion.Label), SelectedIndex = 0 };
        AddField("The reviewers _read this version:", _submitted);
        _comments = AddTextBox("_Comments:", string.Empty, multiline: true, lines: 10);
        _found = AddHint("No comments pasted yet.");
        _comments.TextChanged += (_, _) =>
        {
            var parsed = Core.Reviews.ReviewCommentParser.Parse(_comments.Text);
            _found.Text = parsed.Count == 0 ? "No comments pasted yet."
                : $"{parsed.Count} points found: " + string.Join(", ", parsed.GroupBy(c => c.Reviewer).Select(g => $"{g.Key} ({g.Count()})")) + ".";
        };
        SetValidation(() => _name.Text.Trim().Length > 0 && _comments.Text.Trim().Length > 0);
    }

    public string RoundName => _name.Text.Trim();
    public string ManuscriptNumber => _number.Text.Trim();
    public string Comments => _comments.Text;
    public SubmittedVersion? Submitted => _submitted.SelectedItem as SubmittedVersion;
}

/// <summary>Chooses which of the journal's required sections to add from their templates.</summary>
public sealed class RequiredSectionsDialog : FormWindow
{
    private readonly List<(CheckBox Box, RequiredSection Section)> _boxes = [];

    public RequiredSectionsDialog(string journal, IReadOnlyList<RequiredSection> missing) : base("Add Required Sections", 620)
    {
        OkText = "_Add";
        AddIntro(missing.Count == 0
            ? $"The manuscript already has every section {journal} asks for."
            : $"{journal} asks for these sections, which the manuscript does not have yet. They are added before the references with the journal's template wording, to complete.");
        foreach (var section in missing)
        {
            var box = new CheckBox { Content = $"{section.Heading}{(section.Required ? string.Empty : " (if applicable)")}", IsChecked = section.Required, Margin = new Thickness(0, 6, 0, 0), ToolTip = section.Notes };
            box.Checked += (_, _) => Revalidate();
            box.Unchecked += (_, _) => Revalidate();
            _boxes.Add((box, section));
            AddElement(box);
        }
        SetValidation(() => _boxes.Any(b => b.Box.IsChecked == true));
    }

    public IEnumerable<RequiredSection> Selected => _boxes.Where(b => b.Box.IsChecked == true).Select(b => b.Section);
}
