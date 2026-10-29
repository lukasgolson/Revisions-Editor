using System.Text;
using System.Text.Json;

namespace RevisionsEditor.Core.Scholarly;

/// <summary>
/// Per-paper choices shared by every computer (committed as <c>.revisions/paper.json</c>): the target journal,
/// its citation style, and where the bibliography lives.
/// </summary>
public sealed class PaperSettings
{
    public const string FilePath = ".revisions/paper.json";
    public const string DefaultBibliographyFile = "references.bib";
    public const string FiguresFolder = "figures";
    public const string TablesFolder = "tables";

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>Id of the target journal (see <see cref="Journals.JournalProfiles"/>).</summary>
    public string JournalId { get; set; } = "rse";
    /// <summary>Citation style to use instead of the journal's, or null.</summary>
    public string? CitationStyleOverride { get; set; }
    /// <summary>Bibliography file, relative to the workspace.</summary>
    public string BibliographyFile { get; set; } = DefaultBibliographyFile;
    /// <summary>Abbreviations this paper may use without defining them (style checks).</summary>
    public List<string> KnownAbbreviations { get; set; } = [];
    /// <summary>Style-check rules the author switched off, by name.</summary>
    public List<string> DisabledStyleRules { get; set; } = [];
    /// <summary>Issues the author dismissed, by fingerprint, so they stay dismissed on every computer.</summary>
    public List<string> DismissedIssues { get; set; } = [];

    public static PaperSettings Load(string workspacePath)
    {
        string path = Path.Combine(workspacePath, FilePath.Replace('/', Path.DirectorySeparatorChar));
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<PaperSettings>(File.ReadAllText(path)) ?? new() : new();
        }
        catch (JsonException)
        {
            return new PaperSettings();
        }
    }

    public void Save(string workspacePath)
    {
        string path = Path.Combine(workspacePath, FilePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions) + "\n", new UTF8Encoding(false));
    }

    public PaperSettings Clone() => JsonSerializer.Deserialize<PaperSettings>(JsonSerializer.Serialize(this))!;
}
