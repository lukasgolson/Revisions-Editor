using System.Text.RegularExpressions;

namespace RevisionsEditor.Core.Scholarly;

/// <summary>A stand-in in the text, with what it stands for: <c>[[1200 words|the introduction is still to write]]</c>.</summary>
public sealed record DraftPlaceholder(int Start, int Length, string Text, string Note)
{
    /// <summary>What the placeholder shows: its text, or "…" when it only has a note.</summary>
    public string Display => Text.Trim().Length > 0 ? Text.Trim() : "…";
}

/// <summary>
/// Placeholders written while drafting: text that stands in for something still to come (a number, a section, a
/// sentence to rewrite), marked <c>[[text]]</c> or <c>[[text|note]]</c> with what it is waiting for. They are
/// highlighted on the page and in drafts, listed in the Notes panel, and reported by the checks before
/// submission. A figure or table is marked with <c>placeholder="note"</c> in its attributes.
/// </summary>
public static partial class DraftPlaceholders
{
    public const string FigureAttribute = "placeholder";

    [GeneratedRegex(@"\[\[(?<text>[^\[\]|\n]*)(?:\|(?<note>[^\[\]\n]*))?\]\]")]
    private static partial Regex PlaceholderRegex();

    public static List<DraftPlaceholder> Find(string text)
        => PlaceholderRegex().Matches(text)
            .Where(m => m.Groups["text"].Value.Trim().Length > 0 || m.Groups["note"].Value.Trim().Length > 0)
            .Select(m => new DraftPlaceholder(m.Index, m.Length, m.Groups["text"].Value.Trim(), m.Groups["note"].Value.Trim()))
            .ToList();

    /// <summary>The syntax for a placeholder (bars and brackets in the text or note are left out).</summary>
    public static string Format(string text, string? note)
    {
        static string Clean(string s) => s.Replace("[", "(").Replace("]", ")").Replace("|", "/").Replace('\n', ' ').Trim();
        string body = Clean(text);
        string remark = Clean(note ?? string.Empty);
        return remark.Length > 0 ? $"[[{body}|{remark}]]" : $"[[{body}]]";
    }

    /// <summary>The note on a figure or table marked as a placeholder, or null when it is not one.</summary>
    public static string? NoteOf(IReadOnlyDictionary<string, string> attributes)
        => attributes.TryGetValue(FigureAttribute, out var note) ? (note.Trim().Length > 0 && note != "true" ? note.Trim() : string.Empty) : null;
}
