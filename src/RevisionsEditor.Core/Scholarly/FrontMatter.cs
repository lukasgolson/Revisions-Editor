using System.Text;
using System.Text.Json;

namespace RevisionsEditor.Core.Scholarly;

/// <summary>An author on the title page.</summary>
public sealed class FrontMatterAuthor
{
    public string Name { get; set; } = string.Empty;
    /// <summary>Numbers of the author's affiliations, e.g. "1,2".</summary>
    public string Affiliations { get; set; } = "1";
    public string Email { get; set; } = string.Empty;
    /// <summary>ORCID iD such as 0000-0002-1825-0097.</summary>
    public string Orcid { get; set; } = string.Empty;
    public bool Corresponding { get; set; }
    /// <summary>Contributed equally with the other authors marked so.</summary>
    public bool EqualContribution { get; set; }

    [System.Text.Json.Serialization.JsonIgnore]
    public IReadOnlyList<int> AffiliationNumbers => Affiliations
        .Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries)
        .Select(a => int.TryParse(a.Trim(), out int n) ? n : 0)
        .Where(n => n > 0)
        .Distinct()
        .ToList();
}

/// <summary>A numbered affiliation on the title page.</summary>
public sealed class FrontMatterAffiliation
{
    public int Number { get; set; } = 1;
    /// <summary>Department, institution, city, country.</summary>
    public string Text { get; set; } = string.Empty;
}

/// <summary>
/// The title page as data rather than text: authors, affiliations, correspondence, keywords and running title.
/// The title and abstract stay in the manuscript (they are prose that is reviewed and revised); everything
/// else is filled in a form and laid out by each export. Committed as <c>.revisions/front-matter.json</c>.
/// </summary>
public sealed class FrontMatter
{
    public const string FilePath = ".revisions/front-matter.json";
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>Running head, for page headers.</summary>
    public string ShortTitle { get; set; } = string.Empty;
    public string ArticleType { get; set; } = "Research article";
    public List<FrontMatterAuthor> Authors { get; set; } = [];
    public List<FrontMatterAffiliation> Affiliations { get; set; } = [];
    public List<string> Keywords { get; set; } = [];
    /// <summary>Extra lines for the title page, e.g. "Present address: …".</summary>
    public string Notes { get; set; } = string.Empty;
    /// <summary>
    /// Working notes for the authors (target journal, word count, what a placeholder figure stands for): shown
    /// in the editor and in drafts, never in a copy for the journal.
    /// </summary>
    public string DraftNotes { get; set; } = string.Empty;

    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsEmpty => Authors.Count == 0 && Affiliations.Count == 0 && Keywords.Count == 0 && ShortTitle.Length == 0 && DraftNotes.Length == 0;

    /// <summary>The draft notes, one per line.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public IReadOnlyList<string> DraftNoteLines => DraftNotes.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();

    [System.Text.Json.Serialization.JsonIgnore]
    public IEnumerable<FrontMatterAuthor> CorrespondingAuthors => Authors.Where(a => a.Corresponding);

    /// <summary>Affiliations in the order authors first cite them, renumbered 1, 2, … for the page.</summary>
    public List<(int Number, FrontMatterAffiliation Affiliation)> AffiliationsInOrder()
    {
        var byNumber = Affiliations.GroupBy(a => a.Number).ToDictionary(g => g.Key, g => g.First());
        var order = Authors.SelectMany(a => a.AffiliationNumbers).Distinct().Where(byNumber.ContainsKey).ToList();
        order.AddRange(Affiliations.Select(a => a.Number).Where(n => !order.Contains(n)));
        return order.Select((n, i) => (i + 1, byNumber[n])).ToList();
    }

    /// <summary>The page number an author's affiliation is printed under (see <see cref="AffiliationsInOrder"/>).</summary>
    public IReadOnlyList<int> PrintedAffiliationNumbers(FrontMatterAuthor author)
    {
        var map = AffiliationsInOrder().ToDictionary(a => a.Affiliation.Number, a => a.Number);
        return author.AffiliationNumbers.Where(map.ContainsKey).Select(n => map[n]).ToList();
    }

    public static FrontMatter Load(string workspacePath)
    {
        string path = Path.Combine(workspacePath, FilePath.Replace('/', Path.DirectorySeparatorChar));
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<FrontMatter>(File.ReadAllText(path)) ?? new() : new();
        }
        catch (JsonException)
        {
            return new FrontMatter();
        }
    }

    public void Save(string workspacePath)
    {
        string path = Path.Combine(workspacePath, FilePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions) + "\n", new UTF8Encoding(false));
    }

    public FrontMatter Clone() => JsonSerializer.Deserialize<FrontMatter>(JsonSerializer.Serialize(this))!;
}
