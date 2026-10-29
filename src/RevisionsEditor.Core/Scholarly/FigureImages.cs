using System.Runtime.Versioning;
using System.Xml;
using SkiaSharp;
using Svg.Skia;

namespace RevisionsEditor.Core.Scholarly;

/// <summary>What kind of file a figure is.</summary>
public enum FigureImageKind
{
    /// <summary>Pixels: PNG, JPEG, TIFF, GIF, BMP or WebP.</summary>
    Raster,
    /// <summary>Vector drawing: SVG.</summary>
    Svg,
    /// <summary>Vector drawing: a PDF (the first page), as plotting tools save them.</summary>
    Pdf,
    /// <summary>EPS needs Ghostscript to draw, which the app does not ship.</summary>
    Eps,
    Unknown
}

/// <summary>
/// Figure files of every common kind, made ready for each output: vector SVG becomes a vector PDF for LaTeX,
/// and SVG and PDF figures are drawn as PNG for the editor, the preview and Word (which also gets the SVG
/// itself, for Word 2016 and later).
/// </summary>
[SupportedOSPlatform("windows")]
public static class FigureImages
{
    public static readonly IReadOnlyList<string> RasterExtensions = [".png", ".jpg", ".jpeg", ".tif", ".tiff", ".gif", ".bmp", ".webp"];

    /// <summary>For an Open File dialog.</summary>
    public const string FileDialogFilter =
        "Figures (*.png;*.jpg;*.jpeg;*.tif;*.tiff;*.gif;*.bmp;*.webp;*.svg;*.pdf)|*.png;*.jpg;*.jpeg;*.tif;*.tiff;*.gif;*.bmp;*.webp;*.svg;*.pdf"
        + "|Vector graphics (*.svg;*.pdf)|*.svg;*.pdf|Images (*.png;*.jpg;*.jpeg;*.tif;*.tiff)|*.png;*.jpg;*.jpeg;*.tif;*.tiff|All files (*.*)|*.*";

    private static readonly object PdfiumLock = new();

    public static FigureImageKind KindOf(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".svg" or ".svgz" => FigureImageKind.Svg,
        ".pdf" => FigureImageKind.Pdf,
        ".eps" or ".ps" => FigureImageKind.Eps,
        var e when RasterExtensions.Contains(e) => FigureImageKind.Raster,
        _ => FigureImageKind.Unknown
    };

    /// <summary>The figure's size as drawn, in points (1/72 inch), or null when it cannot be read.</summary>
    public static (double Width, double Height)? NaturalSizePoints(string path)
    {
        try
        {
            switch (KindOf(path))
            {
                case FigureImageKind.Svg:
                    using (var svg = LoadSvg(path))
                    {
                        // SVG user units are CSS pixels (1/96 inch).
                        return svg?.Picture is { } picture ? (picture.CullRect.Width * 0.75, picture.CullRect.Height * 0.75) : null;
                    }
                case FigureImageKind.Pdf:
                    lock (PdfiumLock)
                    {
                        var size = PDFtoImage.Conversion.GetPageSize(File.ReadAllBytes(path), page: 0);
                        return (size.Width, size.Height);
                    }
                default:
                    if (Docx.ImageInfo.TryRead(path) is { } info)
                    {
                        return (info.Width / Math.Max(info.DpiX, 1) * 72, info.Height / Math.Max(info.DpiY, 1) * 72);
                    }
                    using (var codec = SKCodec.Create(path))
                    {
                        // WebP and others the header reader does not know: 96 dpi.
                        return codec == null ? null : (codec.Info.Width * 0.75, codec.Info.Height * 0.75);
                    }
            }
        }
        catch (Exception ex) when (IsReadError(ex))
        {
            return null;
        }
    }

    private static bool IsReadError(Exception ex)
        => ex is IOException or UnauthorizedAccessException or XmlException or InvalidOperationException or ArgumentException
            or NotSupportedException or FormatException or DllNotFoundException or TypeInitializationException
            || ex.GetType().Name.Contains("Pdf", StringComparison.Ordinal); // PDFium reports damaged files with its own exception

    private static SKSvg? LoadSvg(string path)
    {
        var svg = new SKSvg();
        if (svg.Load(path) == null)
        {
            svg.Dispose();
            return null;
        }
        return svg;
    }

    /// <summary>
    /// Draws an SVG or PDF figure (or a WebP image) as PNG at <paramref name="dpi"/>, at most
    /// <paramref name="maxPixels"/> on the longer side. Returns null for other files or when drawing fails.
    /// </summary>
    public static byte[]? RenderPng(string path, double dpi = 300, int maxPixels = 4000)
    {
        try
        {
            switch (KindOf(path))
            {
                case FigureImageKind.Svg:
                {
                    using var svg = LoadSvg(path);
                    if (svg?.Picture is not { } picture || picture.CullRect.Width <= 0 || picture.CullRect.Height <= 0)
                    {
                        return null;
                    }
                    float scale = (float)Math.Min(dpi / 96.0, maxPixels / Math.Max(picture.CullRect.Width, picture.CullRect.Height));
                    using var ms = new MemoryStream();
                    return svg.Save(ms, SKColors.White, SKEncodedImageFormat.Png, 100, scale, scale) ? ms.ToArray() : null;
                }
                case FigureImageKind.Pdf:
                    lock (PdfiumLock)
                    {
                        var bytes = File.ReadAllBytes(path);
                        var size = PDFtoImage.Conversion.GetPageSize(bytes, page: 0);
                        double fit = Math.Min(dpi, maxPixels / (Math.Max(size.Width, size.Height) / 72.0));
                        using var ms = new MemoryStream();
                        PDFtoImage.Conversion.SavePng(ms, bytes, page: 0,
                            options: new PDFtoImage.RenderOptions(Dpi: (int)Math.Max(fit, 24), WithAnnotations: true, BackgroundColor: SKColors.White));
                        return ms.ToArray();
                    }
                case FigureImageKind.Raster when Path.GetExtension(path).Equals(".webp", StringComparison.OrdinalIgnoreCase):
                {
                    using var bitmap = SKBitmap.Decode(path);
                    using var data = bitmap?.Encode(SKEncodedImageFormat.Png, 100);
                    return data?.ToArray();
                }
                default:
                    return null;
            }
        }
        catch (Exception ex) when (IsReadError(ex))
        {
            return null;
        }
    }

    /// <summary>Writes an SVG figure as a vector PDF (for LaTeX), its page the size of the drawing. False if it cannot be read.</summary>
    public static bool SvgToPdf(string svgPath, string pdfPath)
    {
        try
        {
            using var svg = LoadSvg(svgPath);
            if (svg?.Picture is not { } picture || picture.CullRect.Width <= 0 || picture.CullRect.Height <= 0)
            {
                return false;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(pdfPath))!);
            var bounds = picture.CullRect;
            using (var stream = new SKFileWStream(pdfPath))
            using (var document = SKDocument.CreatePdf(stream, new SKDocumentPdfMetadata { Producer = "Revisions Editor", RasterDpi = 300 }))
            {
                // CSS pixels to points.
                var canvas = document.BeginPage(bounds.Width * 0.75f, bounds.Height * 0.75f);
                canvas.Scale(0.75f);
                canvas.Translate(-bounds.Left, -bounds.Top);
                canvas.DrawPicture(picture);
                document.EndPage();
                document.Close();
            }
            return File.Exists(pdfPath) && new FileInfo(pdfPath).Length > 0;
        }
        catch (Exception ex) when (IsReadError(ex))
        {
            return false;
        }
    }

    /// <summary>
    /// The PNG drawn from a vector figure, cached by file and time so the editor does not redraw it on every
    /// keystroke. Null when it cannot be drawn.
    /// </summary>
    public static byte[]? CachedPng(string path, double dpi)
    {
        var info = new FileInfo(path);
        if (!info.Exists)
        {
            return null;
        }
        var key = (info.FullName.ToLowerInvariant(), info.LastWriteTimeUtc, info.Length, dpi);
        lock (Cache)
        {
            if (Cache.TryGetValue(key, out var cached))
            {
                return cached;
            }
        }
        var png = RenderPng(path, dpi);
        lock (Cache)
        {
            if (Cache.Count > 64)
            {
                Cache.Clear();
            }
            Cache[key] = png;
        }
        return png;
    }

    private static readonly Dictionary<(string, DateTime, long, double), byte[]?> Cache = [];
}
