using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using RevisionsEditor.Core.Checks;

namespace RevisionsEditor.App.Dialogs;

/// <summary>Word count of the manuscript as published, by part and by section, with the journal's limits.</summary>
public sealed class WordCountDialog : FormWindow
{
    public WordCountDialog(ManuscriptStatistics stats, string journal, int? selectionWords, int editorLines) : base("Word Count", 620)
    {
        OkText = "_Close";
        AddIntro("Counted as the paper will be published: citations, cross-references and linked numbers as they render, pending changes accepted, and the reference list generated from the citations.");

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        void Row(string label, string value, bool strong = false, bool indent = false, bool warn = false)
        {
            int r = grid.RowDefinitions.Count;
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var name = new TextBlock { Text = label, Margin = new Thickness(indent ? 16 : 0, 3, 12, 3), FontWeight = strong ? FontWeights.SemiBold : FontWeights.Normal, TextWrapping = TextWrapping.Wrap };
            var number = new TextBlock { Text = value, Margin = new Thickness(0, 3, 0, 3), HorizontalAlignment = HorizontalAlignment.Right, FontWeight = strong ? FontWeights.SemiBold : FontWeights.Normal };
            if (warn)
            {
                number.SetResourceReference(TextBlock.ForegroundProperty, "SystemFillColorCriticalBrush");
            }
            Grid.SetRow(name, r);
            Grid.SetRow(number, r);
            Grid.SetColumn(number, 1);
            grid.Children.Add(name);
            grid.Children.Add(number);
        }
        static string N(int n) => n.ToString("N0");

        Row("Words", N(stats.Total.Words), strong: true);
        Row("Main text (Introduction to Conclusions, without captions)", N(stats.MainText.Words), indent: true);
        Row("Abstract", stats.AbstractLimit is { } a ? $"{N(stats.Abstract.Words)} of {N(a)}" : N(stats.Abstract.Words), indent: true, warn: stats.AbstractLimit is { } al && stats.Abstract.Words > al);
        Row("Figure and table captions", N(stats.Captions.Words), indent: true);
        Row("Back matter (acknowledgements, statements, appendices)", N(stats.BackMatter.Words), indent: true);
        Row("References", N(stats.References.Words), indent: true);
        if (stats.JournalLimit is { } limit)
        {
            Row($"{journal} limit ({limit.Scope})", $"{N(limit.Counted)} of {N(limit.Limit)}", strong: true, warn: limit.Exceeded);
        }
        if (selectionWords is { } selected)
        {
            Row("Selected text", N(selected));
        }
        Row("Characters (no spaces)", N(stats.Total.Characters));
        Row("Characters (with spaces)", N(stats.Total.CharactersWithSpaces));
        Row("Paragraphs", N(stats.Total.Paragraphs));
        Row("Lines on the page (editor layout)", N(editorLines));
        Row("Sentences", stats.Sentences == 0 ? "0" : $"{N(stats.Sentences)} (average {stats.AverageSentenceWords:0.0} words)");
        Row("Figures, tables, equations", $"{stats.Figures}, {stats.Tables}, {stats.Equations}");
        Row("Citations (references cited)", $"{N(stats.Citations)} ({N(stats.CitedReferences)})");
        AddElement(grid);

        if (stats.Sections.Count > 0)
        {
            var sections = new ListBox { MaxHeight = 200, Margin = new Thickness(0, 4, 0, 0) };
            ScrollViewer.SetHorizontalScrollBarVisibility(sections, ScrollBarVisibility.Disabled);
            foreach (var section in stats.Sections)
            {
                var row = new DockPanel { Margin = new Thickness((section.Level - 1) * 16, 0, 0, 0), LastChildFill = true };
                var words = new TextBlock { Text = N(section.Words), Margin = new Thickness(12, 0, 0, 0) };
                DockPanel.SetDock(words, Dock.Right);
                row.Children.Add(words);
                row.Children.Add(new TextBlock { Text = section.Title, TextTrimming = TextTrimming.CharacterEllipsis });
                sections.Items.Add(new ListBoxItem { Content = row, Tag = section.ParagraphId, HorizontalContentAlignment = HorizontalAlignment.Stretch });
            }
            sections.MouseDoubleClick += (_, _) => Choose((sections.SelectedItem as ListBoxItem)?.Tag as string);
            AddField("Words by _section (with subsections):", sections);
        }

        if (stats.LongSentences.Count > 0)
        {
            var list = new ListBox { MaxHeight = 160, Margin = new Thickness(0, 4, 0, 0) };
            ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Disabled);
            foreach (var sentence in stats.LongSentences.OrderByDescending(s => s.Words))
            {
                list.Items.Add(new ListBoxItem
                {
                    Content = new TextBlock { Text = $"{sentence.Words} words: {sentence.Text}", TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = sentence.Text },
                    Tag = sentence.ParagraphId
                });
            }
            list.MouseDoubleClick += (_, _) => Choose((list.SelectedItem as ListBoxItem)?.Tag as string);
            AddField($"Sentences over {ManuscriptStatistics.LongSentenceWords} words (double-click to go there):", list);
        }
    }

    /// <summary>A paragraph the author double-clicked, to show on the page.</summary>
    public string? SelectedParagraphId { get; private set; }

    private void Choose(string? paragraphId)
    {
        if (paragraphId != null)
        {
            SelectedParagraphId = paragraphId;
            DialogResult = true;
        }
    }
}

/// <summary>Chooses how the manuscript is typeset with LaTeX.</summary>
public sealed class ExportPdfDialog : FormWindow
{
    public sealed record TemplateOption(Core.Latex.LatexTemplate Template, string Label, string Description);

    public static IReadOnlyList<TemplateOption> Templates { get; } =
    [
        new(Core.Latex.LatexTemplate.JournalTwoColumn, "Journal article, two columns", "A full-width title block with authors, affiliations, abstract and keywords, then two columns of text. Times (TeX Gyre Termes) with microtype."),
        new(Core.Latex.LatexTemplate.Elsevier, "Elsevier (elsarticle, two columns)", "Elsevier's own class in its final two-column layout (5p), with the highlights, as Remote Sensing of Environment, ISPRS and JAG typeset."),
        new(Core.Latex.LatexTemplate.Ieee, "IEEE Transactions (IEEEtran)", "IEEE's two-column journal layout, with the affiliations as a first-page footnote and Index Terms."),
        new(Core.Latex.LatexTemplate.ReviewManuscript, "Review manuscript, one column", "A title page, then double-spaced text with line numbers: the copy many journals ask reviewers to read.")
    ];

    private readonly ComboBox _template;
    private readonly CheckBox _lineNumbers;
    private readonly CheckBox _keepSource;
    private readonly CheckBox _draftNotes;
    private readonly TextBlock _description;

    public ExportPdfDialog(Core.Latex.LatexTemplate suggested, string engine, bool titlePageFilled, bool hasDraftNotes = false) : base("Export PDF with LaTeX", 600)
    {
        OkText = "_Export...";
        AddIntro("The manuscript is typeset with LaTeX: citations and the reference list in the journal's style, numbered figures, tables and equations, and the title page from the form in the Paper tab.");
        _template = new ComboBox { ItemsSource = Templates, DisplayMemberPath = nameof(TemplateOption.Label), SelectedItem = Templates.First(t => t.Template == suggested) };
        AddField("_Layout:", _template);
        _description = AddHint(string.Empty);
        _template.SelectionChanged += (_, _) => _description.Text = (_template.SelectedItem as TemplateOption)?.Description ?? string.Empty;
        _description.Text = ((TemplateOption)_template.SelectedItem).Description;
        _lineNumbers = new CheckBox { Content = "_Line numbers", Margin = new Thickness(0, 12, 0, 0) };
        _keepSource = new CheckBox { Content = "Also _keep the LaTeX source and figures (in a folder next to the PDF)", Margin = new Thickness(0, 6, 0, 0) };
        _draftNotes = new CheckBox
        {
            Content = "Include the _draft notes (a draft for the authors, not for the journal)",
            Margin = new Thickness(0, 6, 0, 0),
            IsEnabled = hasDraftNotes,
            ToolTip = hasDraftNotes ? "The notes from Paper › Title page › Draft notes, in a box after the title" : "There are no draft notes (Paper › Title page)"
        };
        AddElement(_lineNumbers);
        AddElement(_draftNotes);
        AddElement(_keepSource);
        AddHint($"Engine: {engine}. Typeset with OpenType kerning and ligatures, true superscripts, optical margins (microtype protrusion), "
            + "English hyphenation, decimal-aligned table columns and vector figures (SVG and PDF).");
        if (!titlePageFilled)
        {
            AddHint("The title page has no authors yet: fill them in under Paper › Title page.");
        }
    }

    public Core.Latex.LatexTemplate LatexTemplate => ((TemplateOption)_template.SelectedItem).Template;
    public bool LineNumbers => _lineNumbers.IsChecked == true;
    public bool KeepSource => _keepSource.IsChecked == true;
    public bool DraftNotes => _draftNotes.IsChecked == true;
}

/// <summary>Goes to a line on the editor's page, or to a line of a submitted version (as the reviewers numbered it).</summary>
public sealed class GoToLineDialog : FormWindow
{
    private readonly RadioButton _editor;
    private readonly RadioButton _submitted;
    private readonly ComboBox _version;
    private readonly TextBox _line;
    private readonly TextBox _page;

    public GoToLineDialog(int editorLines, IReadOnlyList<string> submittedVersions) : base("Go to Line", 460)
    {
        OkText = "_Go To";
        _editor = new RadioButton { Content = $"Line on this page (1–{editorLines})", IsChecked = true };
        _submitted = new RadioButton { Content = "Line of the version the reviewers read", Margin = new Thickness(0, 8, 0, 0), IsEnabled = submittedVersions.Count > 0 };
        _version = new ComboBox { ItemsSource = submittedVersions, SelectedIndex = submittedVersions.Count > 0 ? 0 : -1, Margin = new Thickness(24, 6, 0, 0), IsEnabled = false };
        AddElement(_editor);
        AddElement(_submitted);
        AddElement(_version);
        _line = AddTextBox("_Line:");
        _page = AddTextBox("_Page (only if the reviewer gave one and lines restart on each page):");
        _page.IsEnabled = false;
        _submitted.Checked += (_, _) => { _version.IsEnabled = true; _page.IsEnabled = true; };
        _editor.Checked += (_, _) => { _version.IsEnabled = false; _page.IsEnabled = false; };
        AddHint(submittedVersions.Count > 0
            ? "Lines of a submitted version are those of its review copy as exported by Revisions Editor, laid out by Word."
            : "Mark a version as submitted (Paper menu) to look up the reviewers' line numbers.");
        SetValidation(() => int.TryParse(_line.Text.Trim(), out int n) && n > 0 && (_page.Text.Trim().Length == 0 || int.TryParse(_page.Text.Trim(), out _)));
        Loaded += (_, _) => _line.Focus();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                DialogResult = false;
            }
        };
    }

    public int Line => int.Parse(_line.Text.Trim());
    public int? Page => _submitted.IsChecked == true && int.TryParse(_page.Text.Trim(), out int p) ? p : null;
    public string? SubmittedVersion => _submitted.IsChecked == true ? _version.SelectedItem as string : null;
}
