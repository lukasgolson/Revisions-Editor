using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace RevisionsEditor.Core.Reviews;

/// <summary>A reviewer's reference to the submitted manuscript: "line 120", "lines 45–50", "p. 4, l. 12", "L230".</summary>
public sealed record LineReference(int Start, int Length, int? Page, int FirstLine, int LastLine)
{
    public string Label => (Page != null ? $"page {Page}, " : string.Empty) + (FirstLine == LastLine ? $"line {FirstLine}" : $"lines {FirstLine}–{LastLine}");
}

public static class LineReferences
{
    private static readonly Regex ReferenceRegex = new(
        @"(?:(?<![\p{L}])(?:p(?:age|g)?\.?|pp\.)\s*(?<page>\d{1,3})\s*[,;:]?\s*)?(?:(?<![\p{L}])(?:lines?|lns?\.?|ll?\.)\s*|(?<![\p{L}\d])L\s?)(?<first>\d{1,5})(?:\s*(?:[-–—]|to)\s*(?:l\.?\s*|L)?(?<last>\d{1,5}))?",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static List<LineReference> Find(string text)
    {
        var result = new List<LineReference>();
        foreach (Match m in ReferenceRegex.Matches(text))
        {
            int first = int.Parse(m.Groups["first"].Value);
            int last = m.Groups["last"].Success ? int.Parse(m.Groups["last"].Value) : first;
            if (last < first)
            {
                // "lines 120-5" means 120 to 125.
                string f = m.Groups["first"].Value, l = m.Groups["last"].Value;
                last = l.Length < f.Length ? int.Parse(f[..^l.Length] + l) : first;
            }
            int? page = m.Groups["page"].Success ? int.Parse(m.Groups["page"].Value) : null;
            if (first > 0 && last - first < 500)
            {
                result.Add(new LineReference(m.Index, m.Length, page, first, last));
            }
        }
        return result;
    }
}

/// <summary>Lines one paragraph occupied in the laid-out review copy.</summary>
public sealed record LineSpan(string ParagraphId, int Page, int FirstLine, int EndPage, int LastLine, int AbsoluteFirstLine, int AbsoluteLastLine);

/// <summary>
/// Where each paragraph of a submitted version fell in its review copy (as Word laid it out), so a reviewer's
/// "line 245" can be found in the manuscript. Saved in <c>.revisions/line-maps</c> and synced with the paper.
/// </summary>
public sealed class SubmittedLineMap
{
    public const string Folder = ".revisions/line-maps";
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public string Version { get; set; } = string.Empty;
    public string Commit { get; set; } = string.Empty;
    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
    public List<LineSpan> Paragraphs { get; set; } = [];

    public static string PathFor(string workspacePath, string version)
    {
        var sb = new StringBuilder();
        foreach (char c in version)
        {
            sb.Append(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '-');
        }
        return Path.Combine(workspacePath, Folder.Replace('/', Path.DirectorySeparatorChar), sb + ".json");
    }

    public static SubmittedLineMap? Load(string workspacePath, string version)
    {
        string path = PathFor(workspacePath, version);
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<SubmittedLineMap>(File.ReadAllText(path)) : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public void Save(string workspacePath)
    {
        string path = PathFor(workspacePath, Version);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions) + "\n", new UTF8Encoding(false));
    }

    /// <summary>
    /// The paragraph a line is in: by page and line on the page when the reference names a page, otherwise by
    /// the line counted from the start. A line between paragraphs gives the paragraph before it.
    /// </summary>
    public LineSpan? Find(int line, int? page = null)
    {
        if (page is { } p)
        {
            return Paragraphs.FirstOrDefault(s =>
                    (s.Page == s.EndPage && s.Page == p && line >= s.FirstLine && line <= s.LastLine)
                    || (s.Page != s.EndPage && ((p == s.Page && line >= s.FirstLine) || (p == s.EndPage && line <= s.LastLine) || (p > s.Page && p < s.EndPage))))
                ?? Paragraphs.LastOrDefault(s => s.Page < p || (s.Page == p && s.FirstLine <= line));
        }
        return Paragraphs.FirstOrDefault(s => line >= s.AbsoluteFirstLine && line <= s.AbsoluteLastLine)
            ?? Paragraphs.LastOrDefault(s => s.AbsoluteFirstLine <= line);
    }

    /// <summary>How far into the paragraph the line is (0 at its first line, 1 at its last).</summary>
    public static double Fraction(LineSpan span, int line, int? page)
    {
        int first = span.AbsoluteFirstLine, last = span.AbsoluteLastLine;
        int absolute = line;
        if (page != null)
        {
            absolute = page == span.Page ? span.AbsoluteFirstLine + (line - span.FirstLine) : span.AbsoluteLastLine - (span.LastLine - line);
        }
        return last <= first ? 0 : Math.Clamp((absolute - first) / (double)(last - first), 0, 1);
    }
}
