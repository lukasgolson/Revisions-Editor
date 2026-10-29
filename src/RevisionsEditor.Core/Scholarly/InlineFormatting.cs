using System.Text.RegularExpressions;

namespace RevisionsEditor.Core.Scholarly;

/// <summary>A run of text with its character formatting.</summary>
public readonly record struct FormattedSpan(string Text, bool Bold, bool Italic, bool Superscript, bool Subscript);

/// <summary>
/// Character formatting written in the text: <c>*italic*</c>, <c>**bold**</c>, <c>***both***</c>,
/// <c>^superscript^</c> and <c>~subscript~</c> (no spaces inside, as in Pandoc).
/// </summary>
public static class InlineFormatting
{
    private static readonly Regex EmphasisRegex = new(@"(\*{1,3})([^*\n]+?)\1", RegexOptions.Compiled);
    private static readonly Regex ScriptRegex = new(
        @"\^(?<sup>[^\s^]+)\^|(?<![~{>\\])~(?<sub>[^\s~{}>]+)~(?![~}>])",
        RegexOptions.Compiled);

    public static List<FormattedSpan> Parse(string text)
    {
        var spans = new List<FormattedSpan>();
        int last = 0;
        foreach (Match m in EmphasisRegex.Matches(text))
        {
            if (m.Index > last)
            {
                AddScripts(spans, text[last..m.Index], false, false);
            }
            int level = m.Groups[1].Length;
            AddScripts(spans, m.Groups[2].Value, level >= 2, level != 2);
            last = m.Index + m.Length;
        }
        if (last < text.Length)
        {
            AddScripts(spans, text[last..], false, false);
        }
        return spans;
    }

    private static void AddScripts(List<FormattedSpan> spans, string text, bool bold, bool italic)
    {
        int last = 0;
        foreach (Match m in ScriptRegex.Matches(text))
        {
            if (m.Index > last)
            {
                spans.Add(new FormattedSpan(text[last..m.Index], bold, italic, false, false));
            }
            bool sup = m.Groups["sup"].Success;
            spans.Add(new FormattedSpan(sup ? m.Groups["sup"].Value : m.Groups["sub"].Value, bold, italic, sup, !sup));
            last = m.Index + m.Length;
        }
        if (last < text.Length)
        {
            spans.Add(new FormattedSpan(text[last..], bold, italic, false, false));
        }
    }

    /// <summary>The text without formatting markers.</summary>
    public static string StripMarkers(string text) => string.Concat(Parse(text).Select(s => s.Text));
}
