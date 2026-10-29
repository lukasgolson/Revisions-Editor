using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using RevisionsEditor.Core.Equations;
using RevisionsEditor.Core.Models;
using RevisionsEditor.Core.Scholarly;
using RevisionsEditor.Core.Serialization;

namespace RevisionsEditor.App.Rendering;

/// <summary>
/// Shows a figure, table or equation on the page as it will appear in the paper: the image or the table's
/// cells with a numbered caption, or the equation with its number. The block itself is edited in a dialog
/// (double-click), so it is read-only on the page.
/// </summary>
internal static class ScholarlyBlockView
{
    private const int MaxTableRows = 40;
    private static readonly FontFamily MathFont = new("Cambria Math");

    public static BlockUIContainer Build(
        DocumentBlock original,
        DocumentBlock formatted,
        FormattingRuleProfile profile,
        ScholarlyContext? scholarly,
        string? workspacePath,
        Action<string>? onActivated)
    {
        string final = CriticMarkupSerializer.ResolveCriticMarkupText(formatted.Text, MarkupDisplayMode.NoMarkupFinal);
        var style = FlowDocumentBuilder.StyledParagraph(BlockStyleRole.Caption, profile);
        UIElement content = formatted.Role switch
        {
            BlockStyleRole.Figure => Figure(formatted, final, style.FontSize, scholarly, workspacePath),
            BlockStyleRole.Table => Table(formatted, final, style.FontSize, scholarly, workspacePath),
            _ => Equation(formatted, final, profile, scholarly)
        };

        var host = new Border
        {
            Child = content,
            Background = Brushes.Transparent,
            Cursor = Cursors.Hand,
            Padding = new Thickness(0, 6, 0, 6),
            ToolTip = "Double-click to edit"
        };
        if (onActivated != null)
        {
            string id = original.ParagraphId;
            host.MouseLeftButtonDown += (_, e) =>
            {
                if (e.ClickCount == 2)
                {
                    onActivated(id);
                    e.Handled = true;
                }
            };
        }

        return new BlockUIContainer(host)
        {
            Margin = new Thickness(0, 4, 0, 10),
            Tag = new EditorBlockTag { ParagraphId = original.ParagraphId, Role = original.Role, LockedBlock = original.Clone() }
        };
    }

    private static TextBlock Caption(string label, string caption, double fontSize, ScholarlyContext? scholarly, TextAlignment alignment)
    {
        var text = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            FontSize = fontSize,
            Foreground = FlowDocumentBuilder.InkBrush,
            TextAlignment = alignment,
            Margin = new Thickness(0, 4, 0, 4)
        };
        if (label.Length > 0)
        {
            text.Inlines.Add(new Run(label) { FontWeight = FontWeights.Bold });
        }
        FlowDocumentBuilder.AddFormattedRuns(text.Inlines, scholarly?.Render(caption) ?? caption, fontSize);
        return text;
    }

    private static TextBlock Problem(string message) => new()
    {
        Tag = LineNumberMargin.Unnumbered,
        Text = message,
        Foreground = FlowDocumentBuilder.ErrorInk,
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, 8, 0, 8),
        HorizontalAlignment = HorizontalAlignment.Center
    };

    private static string? Resolve(string? workspacePath, string? relative)
    {
        if (string.IsNullOrWhiteSpace(relative))
        {
            return null;
        }
        string path = Path.IsPathRooted(relative) || workspacePath == null
            ? relative
            : Path.Combine(workspacePath, relative.Replace('/', Path.DirectorySeparatorChar));
        return File.Exists(path) ? path : null;
    }

    private static string PlaceholderText(string what, string note)
        => $"This {what} is a placeholder{(note.Length > 0 ? $": {note}" : string.Empty)}. Replace it before submitting (double-click to edit it).";

    /// <summary>"Placeholder: synthetic data until the field season ends", in amber under a stand-in figure or above a table.</summary>
    private static Border PlaceholderBadge(string what, string note, double fontSize) => new()
    {
        Background = FlowDocumentBuilder.PlaceholderFill,
        CornerRadius = new CornerRadius(3),
        Padding = new Thickness(8, 2, 8, 2),
        Margin = new Thickness(0, 4, 0, 4),
        HorizontalAlignment = HorizontalAlignment.Center,
        ToolTip = PlaceholderText(what, note),
        Child = new TextBlock
        {
            Tag = LineNumberMargin.Unnumbered,
            Text = note.Length > 0 ? $"Placeholder {what}: {note}" : $"Placeholder {what}",
            FontSize = fontSize * 0.85,
            Foreground = FlowDocumentBuilder.PlaceholderInk,
            TextWrapping = TextWrapping.Wrap
        }
    };

    private static UIElement Figure(DocumentBlock block, string final, double fontSize, ScholarlyContext? scholarly, string? workspacePath)
    {
        var figure = ScholarlyBlocks.ParseFigure(final);
        var panel = new StackPanel();
        if (figure == null)
        {
            panel.Children.Add(Problem($"This figure could not be read: {final}"));
            return panel;
        }

        string? path = Resolve(workspacePath, figure.ImagePath);
        if (path == null)
        {
            panel.Children.Add(Problem($"Image not found: {figure.ImagePath}"));
        }
        else
        {
            try
            {
                // SVG and PDF figures (and WebP) are drawn to PNG first; the others WPF reads itself.
                var kind = FigureImages.KindOf(path);
                byte[]? drawn = kind is FigureImageKind.Svg or FigureImageKind.Pdf || path.EndsWith(".webp", StringComparison.OrdinalIgnoreCase)
                    ? FigureImages.CachedPng(path, 150)
                    : null;
                if (kind == FigureImageKind.Eps)
                {
                    throw new NotSupportedException("EPS figures cannot be shown; save the figure as PDF or SVG");
                }
                if (drawn == null && kind is FigureImageKind.Svg or FigureImageKind.Pdf)
                {
                    throw new NotSupportedException("the drawing could not be read");
                }
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad; // do not keep the file locked
                if (drawn != null)
                {
                    bitmap.StreamSource = new MemoryStream(drawn);
                }
                else
                {
                    bitmap.UriSource = new Uri(path);
                }
                bitmap.DecodePixelWidth = 1200;
                bitmap.EndInit();
                bitmap.Freeze();
                var image = new Image
                {
                    Source = bitmap,
                    Stretch = Stretch.Uniform,
                    MaxWidth = 620 * (figure.WidthFraction ?? 1.0),
                    MaxHeight = 560,
                    HorizontalAlignment = HorizontalAlignment.Center
                };
                if (DraftPlaceholders.NoteOf(figure.Attributes) is { } note)
                {
                    // A stand-in figure: stamped, with what it stands in for.
                    var stamp = new TextBlock
                    {
                        Text = "PLACEHOLDER",
                        FontSize = 30,
                        FontWeight = FontWeights.Bold,
                        Foreground = new SolidColorBrush(Color.FromArgb(0xB0, 0xD0, 0x20, 0x20)),
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center,
                        RenderTransformOrigin = new Point(0.5, 0.5),
                        RenderTransform = new RotateTransform(-18),
                        IsHitTestVisible = false
                    };
                    var grid = new Grid { HorizontalAlignment = HorizontalAlignment.Center, ToolTip = PlaceholderText("figure", note) };
                    grid.Children.Add(image);
                    grid.Children.Add(stamp);
                    panel.Children.Add(grid);
                    panel.Children.Add(PlaceholderBadge("figure", note, fontSize));
                }
                else
                {
                    panel.Children.Add(image);
                }
            }
            catch (Exception ex) when (ex is NotSupportedException or IOException or InvalidOperationException or ArgumentException)
            {
                panel.Children.Add(Problem($"The image {figure.ImagePath} could not be shown ({ex.Message})."));
            }
        }

        string label = scholarly?.CrossReferences.CaptionLabel(block) ?? string.Empty;
        panel.Children.Add(Caption(label, figure.Caption, fontSize, scholarly, TextAlignment.Center));
        if (figure.Label == null)
        {
            panel.Children.Add(new TextBlock { Tag = LineNumberMargin.Unnumbered, Text = "No label: the text cannot refer to this figure.", FontSize = fontSize * 0.85, Foreground = FlowDocumentBuilder.ErrorInk, HorizontalAlignment = HorizontalAlignment.Center });
        }
        return panel;
    }

    private static UIElement Table(DocumentBlock block, string final, double fontSize, ScholarlyContext? scholarly, string? workspacePath)
    {
        var table = ScholarlyBlocks.ParseTable(final);
        var panel = new StackPanel();
        if (table == null)
        {
            panel.Children.Add(Problem($"This table could not be read: {final}"));
            return panel;
        }

        string label = scholarly?.CrossReferences.CaptionLabel(block) ?? string.Empty;
        if (DraftPlaceholders.NoteOf(table.Attributes) is { } placeholder)
        {
            panel.Children.Add(PlaceholderBadge("table", placeholder, fontSize));
        }
        panel.Children.Add(Caption(label, table.Caption, fontSize, scholarly, TextAlignment.Left));

        string? path = Resolve(workspacePath, table.CsvPath);
        if (path == null)
        {
            panel.Children.Add(Problem(table.CsvPath == null ? "This table has no file." : $"Table file not found: {table.CsvPath}"));
            return panel;
        }

        List<string[]> rows;
        try
        {
            rows = CsvTable.Parse(File.ReadAllText(path));
        }
        catch (IOException ex)
        {
            panel.Children.Add(Problem($"{table.CsvPath} could not be read: {ex.Message}"));
            return panel;
        }
        if (rows.Count == 0)
        {
            panel.Children.Add(Problem($"{table.CsvPath} is empty."));
            return panel;
        }

        int columns = rows.Max(r => r.Length);
        var grid = new Grid { HorizontalAlignment = HorizontalAlignment.Stretch };
        for (int c = 0; c < columns; c++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = c == 0 ? new GridLength(1.4, GridUnitType.Star) : new GridLength(1, GridUnitType.Star) });
        }
        int shown = Math.Min(rows.Count, MaxTableRows);
        for (int r = 0; r < shown; r++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            for (int c = 0; c < columns; c++)
            {
                string cell = c < rows[r].Length ? rows[r][c].Trim() : string.Empty;
                var text = new TextBlock
                {
                    FontSize = fontSize,
                    Foreground = FlowDocumentBuilder.InkBrush,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(4, 2, 4, 2),
                    TextAlignment = c == 0 ? TextAlignment.Left : TextAlignment.Right,
                    Tag = LineNumberMargin.Unnumbered // as in Word, lines in tables are not numbered
                };
                FlowDocumentBuilder.AddFormattedRuns(text.Inlines, r == 0 ? $"**{cell}**" : cell, fontSize);
                var border = new Border
                {
                    Child = text,
                    BorderBrush = FlowDocumentBuilder.InkBrush,
                    BorderThickness = new Thickness(0, r == 0 ? 1.2 : 0, 0, r == 0 ? 0.6 : r == shown - 1 ? 1.2 : 0)
                };
                Grid.SetRow(border, r);
                Grid.SetColumn(border, c);
                grid.Children.Add(border);
            }
        }
        panel.Children.Add(grid);
        if (rows.Count > MaxTableRows)
        {
            panel.Children.Add(new TextBlock { Tag = LineNumberMargin.Unnumbered, Text = $"… {rows.Count - MaxTableRows} more rows in {table.CsvPath}", FontSize = fontSize * 0.85, Foreground = FlowDocumentBuilder.InkBrush, Opacity = 0.7 });
        }
        return panel;
    }

    private static UIElement Equation(DocumentBlock block, string final, FormattingRuleProfile profile, ScholarlyContext? scholarly)
    {
        var equation = ScholarlyBlocks.ParseEquation(final);
        if (equation == null)
        {
            return Problem($"This equation could not be read: {final}");
        }
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        double size = profile.BodyFontSizePt * 96.0 / 72.0 * 1.1;
        var math = new TextBlock
        {
            Text = LatexToUnicode.Convert(equation.Latex),
            FontFamily = MathFont,
            FontSize = size,
            Foreground = FlowDocumentBuilder.InkBrush,
            TextWrapping = TextWrapping.Wrap,
            HorizontalAlignment = HorizontalAlignment.Center,
            ToolTip = equation.Latex
        };
        grid.Children.Add(math);
        if (scholarly?.CrossReferences.NumberOf(block.ParagraphId) is { } number)
        {
            var label = new TextBlock { Text = $"({number})", FontSize = size / 1.1, Foreground = FlowDocumentBuilder.InkBrush, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(label, 1);
            grid.Children.Add(label);
        }
        return grid;
    }
}
