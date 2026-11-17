using System.Runtime.Versioning;
using System.Text;
using RevisionsEditor.Core.Scholarly;
using SkiaSharp;
using Svg.Skia;

namespace RevisionsEditor.Core.Diagrams;

/// <summary>
/// A diagram figure's files: its source (figures/&lt;name&gt;.diagram, the file that is edited and versioned) and the
/// SVG drawn from it (figures/&lt;name&gt;.svg, which the editor, the preview and Word show). The LaTeX output draws
/// the diagram again from the source, as TikZ.
/// </summary>
[SupportedOSPlatform("windows")]
public static class DiagramFiles
{
    public const string Extension = ".diagram";

    private static readonly UTF8Encoding Utf8NoBom = new(false);

    /// <summary>Whether a figure is drawn from a diagram: its source= attribute names a .diagram file.</summary>
    public static string? SourceOf(FigureBlock figure)
        => figure.Attributes.TryGetValue("source", out var source) && source.EndsWith(Extension, StringComparison.OrdinalIgnoreCase) ? source : null;

    /// <summary>
    /// Saves the source and the SVG drawn from it. <paramref name="existingSource"/> is the diagram's file when it
    /// has one; a new diagram gets figures/&lt;label&gt;.diagram (numbered if taken). Returns both paths, relative to
    /// the workspace, and whether either file changed.
    /// </summary>
    public static (string SourcePath, string SvgPath, bool Changed) Save(string workspace, string? existingSource, string? label, string source)
    {
        string relative;
        if (!string.IsNullOrWhiteSpace(existingSource))
        {
            relative = existingSource.Replace('\\', '/');
        }
        else
        {
            string stem = Slug(label is { Length: > 4 } l && l.StartsWith("fig:", StringComparison.Ordinal) ? l[4..] : label ?? "diagram");
            relative = $"{PaperSettings.FiguresFolder}/{stem}{Extension}";
            for (int n = 2; File.Exists(Full(workspace, relative)) || File.Exists(Full(workspace, Path.ChangeExtension(relative, ".svg"))); n++)
            {
                relative = $"{PaperSettings.FiguresFolder}/{stem}-{n}{Extension}";
            }
        }
        string svgRelative = Path.ChangeExtension(relative, ".svg").Replace('\\', '/');
        var diagram = DiagramSource.Parse(source, out _);
        bool changed = WriteIfDifferent(Full(workspace, relative), source.Replace("\r\n", "\n"));
        changed |= WriteIfDifferent(Full(workspace, svgRelative), DiagramRenderer.ToSvg(diagram));
        return (relative, svgRelative, changed);
    }

    /// <summary>
    /// Draws the SVG again from the source when the source is newer (edited outside the app, or pulled from
    /// another computer). Returns true when it was redrawn.
    /// </summary>
    public static bool RefreshSvg(string workspace, string sourceRelative)
    {
        string source = Full(workspace, sourceRelative);
        string svg = Path.ChangeExtension(source, ".svg");
        if (!File.Exists(source) || (File.Exists(svg) && File.GetLastWriteTimeUtc(svg) >= File.GetLastWriteTimeUtc(source)))
        {
            return false;
        }
        var diagram = DiagramSource.Parse(File.ReadAllText(source), out var errors);
        if (errors.Count > 0 && !diagram.Nodes.Any())
        {
            return false;
        }
        WriteIfDifferent(svg, DiagramRenderer.ToSvg(diagram));
        File.SetLastWriteTimeUtc(svg, DateTime.UtcNow); // newer than the source, even when the drawing is unchanged
        return true;
    }

    /// <summary>An SVG drawn as PNG at <paramref name="scale"/> pixels per diagram unit, for an on-screen preview.</summary>
    public static byte[]? RenderPreview(string svgText, double scale)
    {
        using var svg = new SKSvg();
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(svgText));
        if (svg.Load(stream) is not { } picture || picture.CullRect.Width <= 0)
        {
            return null;
        }
        float s = (float)Math.Clamp(scale, 0.1, 8);
        using var output = new MemoryStream();
        return svg.Save(output, SKColors.White, SKEncodedImageFormat.Png, 100, s, s) ? output.ToArray() : null;
    }

    private static string Full(string workspace, string relative)
        => Path.IsPathRooted(relative) ? relative : Path.Combine(workspace, relative.Replace('/', Path.DirectorySeparatorChar));

    private static bool WriteIfDifferent(string path, string content)
    {
        if (File.Exists(path) && File.ReadAllText(path) == content)
        {
            return false;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content, Utf8NoBom);
        return true;
    }

    private static string Slug(string text)
    {
        var sb = new StringBuilder();
        foreach (char c in text.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(c)) sb.Append(c);
            else if (sb.Length > 0 && sb[^1] != '-') sb.Append('-');
        }
        string slug = sb.ToString().Trim('-');
        return slug.Length == 0 ? "diagram" : slug.Length > 40 ? slug[..40].TrimEnd('-') : slug;
    }
}
