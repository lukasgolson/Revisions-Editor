using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using RevisionsEditor.Core.Diffing;
using RevisionsEditor.Core.Models;
using RevisionsEditor.Core.Serialization;

namespace RevisionsEditor.Core.Storage;

/// <summary>
/// Stores a manuscript in the repository as one Markdown file per section, so edits to different sections
/// touch different files and read naturally on GitHub:
///
///   manuscript/00-front-matter.md     title, abstract and anything before the first top-level heading
///   manuscript/01-introduction.md     a top-level heading and everything up to the next one
///   .revisions/manuscript.json        document identity and the details of pending review changes
///
/// The section files are the source of truth for the text (paragraph ids and styles are kept in an HTML
/// comment before each paragraph, pending review changes as CriticMarkup). The JSON file only adds what
/// Markdown cannot hold, such as who made a pending change and when.
/// </summary>
public static class ManuscriptSectionStore
{
    public const string SectionFolder = "manuscript";
    public const string MetadataPath = ".revisions/manuscript.json";

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly Regex SlugUnsafe = new(@"[^a-z0-9]+", RegexOptions.Compiled);

    /// <summary>What .revisions/manuscript.json holds.</summary>
    public sealed class Metadata
    {
        public int FormatVersion { get; set; } = 1;
        public string DocumentId { get; set; } = "doc-main";
        public string Title { get; set; } = string.Empty;
        public string? GoogleDocId { get; set; }
        public string? GoogleDocTabId { get; set; }
        public string? GoogleDocTabTitle { get; set; }
        public string ActiveFormattingProfile { get; set; } = string.Empty;
        /// <summary>Pending review changes; their text is in the section files, these carry author, date and type.</summary>
        public List<RevisionHunk> PendingChanges { get; set; } = [];
    }

    /// <summary>
    /// The repository files for <paramref name="document"/>: relative path (forward slashes) to content.
    /// </summary>
    public static Dictionary<string, string> Write(ManuscriptDocument document)
    {
        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        var sections = SplitIntoSections(document.Blocks);
        int width = sections.Count > 100 ? 3 : 2;
        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < sections.Count; i++)
        {
            var section = sections[i];
            string slug = i == 0 && section[0].Role is not BlockStyleRole.Heading1 ? "front-matter" : Slug(section[0].Text);
            string name = $"{i.ToString().PadLeft(width, '0')}-{slug}.md";
            for (int n = 2; !usedNames.Add(name); n++)
            {
                name = $"{i.ToString().PadLeft(width, '0')}-{slug}-{n}.md";
            }

            var sectionDoc = new ManuscriptDocument
            {
                Blocks = section,
                Hunks = document.Hunks.Where(h => section.Any(b => b.ParagraphId == h.ParagraphId)).ToList()
            };
            files[$"{SectionFolder}/{name}"] = CriticMarkupSerializer.ToMarkdown(sectionDoc, includeAnchorComments: true);
        }

        var metadata = new Metadata
        {
            DocumentId = document.DocumentId,
            Title = document.Title,
            GoogleDocId = document.GoogleDocId,
            GoogleDocTabId = document.GoogleDocTabId,
            GoogleDocTabTitle = document.GoogleDocTabTitle,
            ActiveFormattingProfile = document.ActiveFormattingProfile,
            PendingChanges = document.Hunks.Where(h => h.Status == RevisionHunkStatus.Pending).Select(h => h.Clone()).ToList()
        };
        files[MetadataPath] = JsonSerializer.Serialize(metadata, JsonOptions);
        return files;
    }

    /// <summary>
    /// Rebuilds the manuscript from repository files (as produced by <see cref="Write"/>, possibly edited
    /// elsewhere). Section files are read in file-name order.
    /// </summary>
    public static ManuscriptDocument Read(IReadOnlyDictionary<string, string> files)
    {
        Metadata metadata = new();
        if (files.TryGetValue(MetadataPath, out var json) && !string.IsNullOrWhiteSpace(json))
        {
            metadata = JsonSerializer.Deserialize<Metadata>(json, JsonOptions) ?? new Metadata();
        }

        var document = new ManuscriptDocument
        {
            DocumentId = metadata.DocumentId,
            Title = metadata.Title,
            GoogleDocId = metadata.GoogleDocId,
            GoogleDocTabId = metadata.GoogleDocTabId,
            GoogleDocTabTitle = metadata.GoogleDocTabTitle,
            ActiveFormattingProfile = string.IsNullOrEmpty(metadata.ActiveFormattingProfile)
                ? new ManuscriptDocument().ActiveFormattingProfile
                : metadata.ActiveFormattingProfile
        };

        var sectionPaths = files.Keys
            .Where(IsSectionFile)
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();

        var usedIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (string path in sectionPaths)
        {
            var parsed = CriticMarkupSerializer.ParseMarkdown(files[path]);
            foreach (var block in parsed.Blocks)
            {
                string originalId = block.ParagraphId;
                if (!usedIds.Add(block.ParagraphId))
                {
                    // Paragraphs added by hand without an id comment get positional ids, which can repeat
                    // across files; give them unique ones.
                    block.ParagraphId = UniqueId(usedIds);
                    foreach (var hunk in parsed.Hunks.Where(h => h.ParagraphId == originalId))
                    {
                        hunk.ParagraphId = block.ParagraphId;
                    }
                }
                document.Blocks.Add(block);
            }
            document.Hunks.AddRange(parsed.Hunks.Where(h => document.Blocks.Any(b => b.ParagraphId == h.ParagraphId)));
        }

        // The Markdown says what is pending; the metadata restores who proposed it, when, and its exact type.
        var known = metadata.PendingChanges
            .GroupBy(h => (h.ParagraphId, Token: CriticMarkupSerializer.FormatHunkAsCriticMarkup(h)))
            .ToDictionary(g => g.Key, g => new Queue<RevisionHunk>(g));
        for (int i = 0; i < document.Hunks.Count; i++)
        {
            var parsedHunk = document.Hunks[i];
            var key = (parsedHunk.ParagraphId, Token: CriticMarkupSerializer.FormatHunkAsCriticMarkup(parsedHunk));
            if (known.TryGetValue(key, out var queue) && queue.Count > 0)
            {
                var stored = queue.Dequeue().Clone();
                stored.ContextSnippet = parsedHunk.ContextSnippet;
                stored.Status = RevisionHunkStatus.Pending;
                document.Hunks[i] = stored;
            }
        }

        var title = document.Blocks.FirstOrDefault(b => b.Role == BlockStyleRole.Title);
        if (title != null)
        {
            document.Title = DiffEngineService.ToGoogleDocsText(title.Text);
        }
        return document;
    }

    public static bool IsSectionFile(string path)
        => path.StartsWith(SectionFolder + "/", StringComparison.Ordinal)
            && path.EndsWith(".md", StringComparison.OrdinalIgnoreCase)
            && path.IndexOf('/', SectionFolder.Length + 1) < 0;

    /// <summary>Sections start at each top-level heading; anything before the first one is front matter.</summary>
    private static List<List<DocumentBlock>> SplitIntoSections(IEnumerable<DocumentBlock> blocks)
    {
        var sections = new List<List<DocumentBlock>>();
        foreach (var block in blocks)
        {
            if (sections.Count == 0 || block.Role == BlockStyleRole.Heading1)
            {
                sections.Add([]);
            }
            sections[^1].Add(block);
        }
        return sections;
    }

    private static string Slug(string headingText)
    {
        string plain = DiffEngineService.ToGoogleDocsText(headingText).ToLowerInvariant();
        var ascii = new StringBuilder();
        foreach (char c in plain.Normalize(NormalizationForm.FormD))
        {
            if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.NonSpacingMark)
            {
                ascii.Append(c);
            }
        }
        string slug = SlugUnsafe.Replace(ascii.ToString(), "-").Trim('-');
        if (slug.Length > 40)
        {
            slug = slug[..40].TrimEnd('-');
        }
        return slug.Length == 0 ? "section" : slug;
    }

    private static string UniqueId(HashSet<string> used)
    {
        for (int n = used.Count + 1; ; n++)
        {
            string id = $"p-{n:D3}";
            if (used.Add(id))
            {
                return id;
            }
        }
    }
}
