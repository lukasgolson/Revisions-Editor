using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace RevisionsEditor.Core.Scholarly;

/// <summary>An idea about the paper, jotted down to come back to: perhaps about one paragraph, perhaps tagged #methods.</summary>
public sealed class PaperNote
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];
    public string Text { get; set; } = string.Empty;
    public DateTimeOffset Created { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset? Updated { get; set; }
    public bool Done { get; set; }
    public bool Pinned { get; set; }
    /// <summary>The paragraph the note is about, if any.</summary>
    public string? ParagraphId { get; set; }
    /// <summary>The paragraph's first words when the note was written (shown if the paragraph is gone).</summary>
    public string? ParagraphPreview { get; set; }

    /// <summary>The #tags in the text, without the #, in lower case.</summary>
    [JsonIgnore]
    public IReadOnlyList<string> Tags => PaperNotes.TagsOf(Text);
}

/// <summary>
/// The paper's notes ("shower thoughts"): ideas, to-dos and questions about the manuscript, kept beside it in
/// <c>.revisions/notes.json</c>, so they are committed and synced with the paper and follow it to every computer.
/// </summary>
public static partial class PaperNotes
{
    public const string FilePath = ".revisions/notes.json";
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    [GeneratedRegex(@"(?<![\w#])#(?<tag>[\p{L}\p{N}][\p{L}\p{N}_-]*)")]
    private static partial Regex TagRegex();

    public static IReadOnlyList<string> TagsOf(string text)
        => TagRegex().Matches(text).Select(m => m.Groups["tag"].Value.ToLowerInvariant()).Distinct().ToList();

    public static List<PaperNote> Load(string workspacePath)
    {
        string path = Path.Combine(workspacePath, FilePath.Replace('/', Path.DirectorySeparatorChar));
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<List<PaperNote>>(File.ReadAllText(path)) ?? [] : [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public static void Save(string workspacePath, IEnumerable<PaperNote> notes)
    {
        string path = Path.Combine(workspacePath, FilePath.Replace('/', Path.DirectorySeparatorChar));
        var list = notes.ToList();
        if (list.Count == 0 && !File.Exists(path))
        {
            return;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(list, JsonOptions) + "\n", new UTF8Encoding(false));
    }

    /// <summary>
    /// The notes matching a search (all words, case ignored; "#tag" matches the tag), pinned first, then open
    /// before done, newest first.
    /// </summary>
    public static List<PaperNote> Filter(IEnumerable<PaperNote> notes, string? search, bool includeDone)
    {
        var words = (search ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return notes
            .Where(n => includeDone || !n.Done)
            .Where(n => words.All(w => w.StartsWith('#') && w.Length > 1
                ? n.Tags.Contains(w[1..].ToLowerInvariant())
                : n.Text.Contains(w, StringComparison.OrdinalIgnoreCase) || (n.ParagraphPreview?.Contains(w, StringComparison.OrdinalIgnoreCase) ?? false)))
            .OrderByDescending(n => n.Pinned)
            .ThenBy(n => n.Done)
            .ThenByDescending(n => n.Updated ?? n.Created)
            .ToList();
    }
}
