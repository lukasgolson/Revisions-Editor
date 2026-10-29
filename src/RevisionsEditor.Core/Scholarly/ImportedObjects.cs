using System.Security.Cryptography;
using System.Text.RegularExpressions;
using RevisionsEditor.Core.Models;
using RevisionsEditor.Core.Serialization;

namespace RevisionsEditor.Core.Scholarly;

/// <summary>
/// Turns the images and tables of a document imported from Google Docs or Word into figure and table blocks:
/// each image is saved under <c>figures/</c> and each table as a CSV file under <c>tables/</c>, captioned from
/// the "Figure 2." paragraph under the image or the "Table 1." paragraph above the table (numbering is the
/// app's, so the number itself is dropped).
/// <para>
/// Pulling the same document again gives the same blocks: an image already in the manuscript (the same bytes)
/// keeps its file, label and size; a table keeps its file and label when its caption or its cells are
/// unchanged, and its file is updated with the cells as they now are. New images are named by their content.
/// </para>
/// </summary>
public sealed partial class ImportedObjects
{
    private readonly string? _workspacePath;
    private readonly List<(FigureBlock Figure, string? Hash)> _existingFigures = [];
    private readonly List<(TableBlock Table, string? Csv)> _existingTables = [];
    private readonly HashSet<string> _labels = new(StringComparer.Ordinal);
    private readonly HashSet<string> _reused = new(StringComparer.Ordinal);
    private readonly Dictionary<string, byte[]> _files = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="current">The manuscript as it is (its figures and tables are reused), or null.</param>
    /// <param name="workspacePath">Where the manuscript's figure and table files are.</param>
    public ImportedObjects(ManuscriptDocument? current, string? workspacePath)
    {
        _workspacePath = workspacePath;
        foreach (var block in current?.Blocks ?? [])
        {
            string text = CriticMarkupSerializer.ResolveCriticMarkupText(block.Text, MarkupDisplayMode.NoMarkupFinal);
            if (block.Role == BlockStyleRole.Figure && ScholarlyBlocks.ParseFigure(text) is { } figure)
            {
                _existingFigures.Add((figure, Read(figure.ImagePath) is { } bytes ? Hash(bytes) : null));
                if (figure.Label != null) _labels.Add(figure.Label);
            }
            else if (block.Role == BlockStyleRole.Table && ScholarlyBlocks.ParseTable(text) is { } table)
            {
                _existingTables.Add((table, table.CsvPath != null && Read(table.CsvPath) is { } csv ? Normalize(System.Text.Encoding.UTF8.GetString(csv)) : null));
                if (table.Label != null) _labels.Add(table.Label);
            }
        }
    }

    /// <summary>Files to write into the workspace: relative path → content.</summary>
    public IReadOnlyDictionary<string, byte[]> Files => _files;

    public int FigureCount { get; private set; }
    public int TableCount { get; private set; }

    [GeneratedRegex(@"^\s*(?:\*{1,3})?\s*(?:Figure|Fig\.?)\s*(?:S?\d+[a-z]?|[IVXLC]+)\s*[.:–—-]?\s*(?:\*{1,3})?\s*(?<caption>.*)$", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex FigureCaptionRegex();

    [GeneratedRegex(@"^\s*(?:\*{1,3})?\s*Table\s*(?:S?\d+[a-z]?|[IVXLC]+)\s*[.:–—-]?\s*(?:\*{1,3})?\s*(?<caption>.*)$", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex TableCaptionRegex();

    [GeneratedRegex(@"^\*\*([^*]+)\*\*$")]
    private static partial Regex BoldCellRegex();

    /// <summary>A "Figure 2. Study sites…" paragraph: the caption without its number.</summary>
    public static bool TryFigureCaption(string text, out string caption) => TryCaption(FigureCaptionRegex(), text, out caption);

    /// <summary>A "Table 1: Accuracy by site" paragraph: the caption without its number.</summary>
    public static bool TryTableCaption(string text, out string caption) => TryCaption(TableCaptionRegex(), text, out caption);

    private static bool TryCaption(Regex regex, string text, out string caption)
    {
        var match = regex.Match(text);
        caption = match.Success ? match.Groups["caption"].Value.Trim() : string.Empty;
        // An emphasis marker left open by a bold "Figure 1." label.
        if (caption.StartsWith("**", StringComparison.Ordinal) && caption.Count(c => c == '*') % 2 == 1)
        {
            caption = caption.TrimStart('*').Trim();
        }
        return match.Success;
    }

    /// <summary>The figure block's text for an image, saving the image unless the manuscript already has it.</summary>
    public string Figure(byte[] image, string extension, string caption)
    {
        FigureCount++;
        string hash = Hash(image);
        bool Free(FigureBlock f) => f.Label == null || !_reused.Contains(f.Label);
        var existing = _existingFigures.FirstOrDefault(f => f.Hash == hash && Free(f.Figure));
        // Google Docs saves images again (other bytes, same pixels): the figure with this caption and the same
        // pixel size keeps its own file, which may be the SVG or PDF the copy was exported from.
        string captionKey = Key(caption);
        if (existing.Figure == null && captionKey.Length > 0)
        {
            var size = Docx.ImageInfo.TryRead(image);
            existing = _existingFigures.FirstOrDefault(f => Free(f.Figure) && Key(f.Figure.Caption) == captionKey && SamePixels(f.Figure.ImagePath, size));
        }
        if (existing.Figure != null)
        {
            if (existing.Figure.Label != null) _reused.Add(existing.Figure.Label);
            return ScholarlyBlocks.FormatFigure(caption, existing.Figure.ImagePath, existing.Figure.Label, existing.Figure.Attributes);
        }
        string ext = NormalizeExtension(extension);
        string path = $"{PaperSettings.FiguresFolder}/imported-{hash[..12]}{ext}";
        _files[path] = image;
        string label = NewLabel("fig", caption);
        return ScholarlyBlocks.FormatFigure(caption, path, label);
    }

    /// <summary>The table block's text for a table's cells, saved as CSV (updating the manuscript's file for that table).</summary>
    public string Table(IReadOnlyList<IReadOnlyList<string>> rows, string caption)
    {
        TableCount++;
        // A header row set in bold is the table's styling, not emphasis in the cells.
        if (rows.Count > 0 && rows[0].Where(c => c.Length > 0).DefaultIfEmpty("x").All(c => BoldCellRegex().IsMatch(c)))
        {
            rows = [rows[0].Select(c => BoldCellRegex().Match(c) is { Success: true } m ? m.Groups[1].Value.Trim() : c).ToList(), .. rows.Skip(1)];
        }
        string csv = CsvTable.Format(rows);
        string normalized = Normalize(csv);
        string captionKey = Key(caption);
        var existing = _existingTables.FirstOrDefault(t => Available(t.Table) && t.Csv == normalized);
        if (existing.Table == null && captionKey.Length > 0)
        {
            existing = _existingTables.FirstOrDefault(t => Available(t.Table) && Key(t.Table.Caption) == captionKey);
        }
        string label, path;
        IReadOnlyDictionary<string, string>? attributes = null;
        if (existing.Table is { CsvPath: not null } match)
        {
            label = match.Label ?? NewLabel("tbl", caption);
            path = match.CsvPath;
            attributes = match.Attributes.Where(a => !a.Key.Equals("file", StringComparison.OrdinalIgnoreCase)).ToDictionary(a => a.Key, a => a.Value);
            _reused.Add(label);
        }
        else
        {
            label = NewLabel("tbl", caption);
            path = $"{PaperSettings.TablesFolder}/{label["tbl:".Length..]}.csv";
            for (int n = 2; _files.ContainsKey(path) || _existingTables.Any(t => string.Equals(t.Table.CsvPath, path, StringComparison.OrdinalIgnoreCase)); n++)
            {
                path = $"{PaperSettings.TablesFolder}/{label["tbl:".Length..]}-{n}.csv";
            }
        }
        if (existing.Csv != normalized)
        {
            _files[path] = System.Text.Encoding.UTF8.GetBytes(csv);
        }
        return ScholarlyBlocks.FormatTable(caption, label, path, attributes);
    }

    /// <summary>
    /// The manuscript's figure has the imported image's pixel size, or cannot be compared (a vector figure, which
    /// Google Docs and Word turn into a picture of it).
    /// </summary>
    private bool SamePixels(string imagePath, Docx.ImageInfo? imported)
    {
        if (imported == null || FigureImages.KindOf(imagePath) is FigureImageKind.Svg or FigureImageKind.Pdf)
        {
            return true;
        }
        var own = Read(imagePath) is { } bytes ? Docx.ImageInfo.TryRead(bytes) : null;
        return own == null || own.Width == imported.Width && own.Height == imported.Height;
    }

    private bool Available(TableBlock table) => table.CsvPath != null && (table.Label == null || !_reused.Contains(table.Label));

    /// <summary>Writes the images and CSV files into the workspace (unchanged files are left alone).</summary>
    public void WriteTo(string workspacePath)
    {
        foreach (var (relative, bytes) in _files)
        {
            string target = Path.Combine(workspacePath, relative.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(target) && File.ReadAllBytes(target).AsSpan().SequenceEqual(bytes))
            {
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllBytes(target, bytes);
        }
    }

    private string NewLabel(string kind, string caption)
    {
        string label = ScholarlyBlocks.SuggestLabel(kind, caption.Length > 0 ? caption : (kind == "fig" ? "figure" : "table"), _labels);
        _labels.Add(label);
        return label;
    }

    private byte[]? Read(string relative)
    {
        if (_workspacePath == null || string.IsNullOrWhiteSpace(relative))
        {
            return null;
        }
        string path = Path.IsPathRooted(relative) ? relative : Path.Combine(_workspacePath, relative.Replace('/', Path.DirectorySeparatorChar));
        try
        {
            return File.Exists(path) ? File.ReadAllBytes(path) : null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    /// <summary>CSV compared by its cells, ignoring line endings and how a minus sign is written (exports print "−").</summary>
    private static string Normalize(string csv)
        => CsvTable.Format(CsvTable.Parse(csv).Select(r => (IReadOnlyList<string>)r.Select(c => c.Trim().Replace('−', '-')).ToList()));

    /// <summary>A caption compared by its words, without citations (exported as "(White et al., 2016)", typed as [@white2016]).</summary>
    private static string Key(string caption)
    {
        string text = CriticMarkupSerializer.ResolveCriticMarkupText(caption, MarkupDisplayMode.NoMarkupFinal);
        text = Regex.Replace(text, @"\[[^\]]*\]|\([^)]*\)", " ");
        return Regex.Replace(text.ToLowerInvariant(), @"[^\p{L}\p{N}]+", " ").Trim();
    }

    private static string NormalizeExtension(string extension)
    {
        string ext = extension.Trim().ToLowerInvariant();
        if (!ext.StartsWith('.'))
        {
            ext = "." + ext;
        }
        return ext switch
        {
            ".jpe" or ".jfif" => ".jpg",
            ".tif" => ".tiff",
            _ => ext
        };
    }

    /// <summary>A file extension for an image's media type (from Google Docs), or for its first bytes.</summary>
    public static string ExtensionFor(string? mimeType, byte[] bytes)
    {
        string? fromType = mimeType?.ToLowerInvariant().Split(';')[0].Trim() switch
        {
            "image/png" => ".png",
            "image/jpeg" or "image/jpg" => ".jpg",
            "image/gif" => ".gif",
            "image/bmp" => ".bmp",
            "image/tiff" => ".tiff",
            "image/webp" => ".webp",
            "image/svg+xml" => ".svg",
            "application/pdf" => ".pdf",
            _ => null
        };
        if (fromType != null)
        {
            return fromType;
        }
        return Docx.ImageInfo.TryRead(bytes) is { } info ? "." + (info.Extension == "jpeg" ? "jpg" : info.Extension) : ".png";
    }
}
