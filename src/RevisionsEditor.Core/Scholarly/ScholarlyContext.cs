using System.Text;
using System.Text.RegularExpressions;
using RevisionsEditor.Core.Bibliography;
using RevisionsEditor.Core.Journals;
using RevisionsEditor.Core.Models;
using RevisionsEditor.Core.Serialization;

namespace RevisionsEditor.Core.Scholarly;

/// <summary>
/// Everything about a paper that lives beside the manuscript: settings, target journal and its citation style,
/// the bibliography and the analysis results. Loaded from the workspace (on a background thread).
/// </summary>
public sealed class ScholarlyProject
{
    public string WorkspacePath { get; init; } = string.Empty;
    public PaperSettings Settings { get; init; } = new();
    public JournalProfile Journal { get; init; } = JournalProfiles.Default;
    public CitationStyle Style { get; init; } = CitationStyles.All[0];
    public IReadOnlyDictionary<string, BibEntry> Library { get; init; } = new Dictionary<string, BibEntry>();
    public BibParseResult? Bibliography { get; init; }
    /// <summary>Why the bibliography could not be read, if it exists but failed.</summary>
    public string? BibliographyError { get; init; }
    public bool BibliographyExists { get; init; }
    public ResultsLibrary Results { get; init; } = ResultsLibrary.FromContents([]);
    /// <summary>Authors, affiliations, keywords and running title, from the title-page form.</summary>
    public FrontMatter FrontMatter { get; init; } = new();

    public string BibliographyPath => Path.Combine(WorkspacePath, Settings.BibliographyFile.Replace('/', Path.DirectorySeparatorChar));

    public static ScholarlyProject Empty { get; } = new();

    public static ScholarlyProject Load(string workspacePath)
    {
        var settings = PaperSettings.Load(workspacePath);
        var journal = JournalProfiles.Find(settings.JournalId) ?? JournalProfiles.Default;
        var style = CitationStyles.Find(settings.CitationStyleOverride ?? journal.CitationStyleId) ?? CitationStyles.All[0];

        string bibPath = Path.Combine(workspacePath, settings.BibliographyFile.Replace('/', Path.DirectorySeparatorChar));
        BibParseResult? parsed = null;
        string? error = null;
        bool exists = File.Exists(bibPath);
        var library = new Dictionary<string, BibEntry>(StringComparer.Ordinal);
        if (exists)
        {
            try
            {
                parsed = BibTeXParser.Parse(File.ReadAllText(bibPath));
                foreach (var entry in parsed.Entries)
                {
                    library.TryAdd(entry.Key, entry);
                }
            }
            catch (IOException ex)
            {
                error = ex.Message;
            }
        }

        return new ScholarlyProject
        {
            WorkspacePath = workspacePath,
            Settings = settings,
            Journal = journal,
            Style = style,
            Library = library,
            Bibliography = parsed,
            BibliographyError = error,
            BibliographyExists = exists,
            Results = ResultsLibrary.Load(workspacePath),
            FrontMatter = FrontMatter.Load(workspacePath)
        };
    }
}

public enum InlinePieceKind
{
    Text,
    Citation,
    CrossReference,
    Number,
    Math,
    /// <summary>A draft placeholder <c>[[text|note]]</c>: Display is its text, Detail its note.</summary>
    Placeholder
}

/// <summary>
/// A piece of a paragraph: plain text (still with Markdown emphasis), or a citation, cross-reference, linked
/// number or inline equation together with how it is displayed.
/// </summary>
public sealed record InlinePiece(InlinePieceKind Kind, string Source, string Display, string? Detail = null, bool IsError = false);

/// <summary>
/// The scholarly view of a manuscript at one moment: citations processed in document order (so numbers and
/// 2016a/2016b suffixes are consistent), figures/tables/equations numbered, and linked numbers resolved.
/// Turns a paragraph into pieces for the editor and the Word export.
/// </summary>
public sealed class ScholarlyContext
{
    private static readonly Regex InlineMathRegex = new(@"(?<![\\$])\$(?=[^\s$])(?<tex>[^$\n]*?[^\s\\$])\$(?![\d$])", RegexOptions.Compiled);

    private readonly Dictionary<string, string> _citationTextBySource = new(StringComparer.Ordinal);
    private readonly List<string> _processedTexts;

    public ScholarlyProject Project { get; }
    public CrossReferenceIndex CrossReferences { get; }
    public CitationResult Citations { get; private set; }

    private ScholarlyContext(ScholarlyProject project, CrossReferenceIndex crossReferences, CitationResult citations, List<string> texts)
    {
        Project = project;
        CrossReferences = crossReferences;
        Citations = citations;
        _processedTexts = texts;
    }

    /// <summary>
    /// Builds the context for blocks whose formatting rules have been applied (section numbers computed).
    /// Citations are numbered by their order in the final text (pending changes accepted).
    /// </summary>
    public static ScholarlyContext Build(IReadOnlyList<DocumentBlock> blocks, ScholarlyProject project)
    {
        var crossRefs = CrossReferenceIndex.Build(blocks, project.Journal.CrossReferenceNames);

        var texts = blocks
            .Where(b => b.Role is not BlockStyleRole.ReferenceItem and not BlockStyleRole.Equation)
            .Select(b => CriticMarkupSerializer.ResolveCriticMarkupText(b.Text, MarkupDisplayMode.NoMarkupFinal))
            .ToList();
        // Text that pending changes would remove still needs its citations rendered (after all others).
        texts.AddRange(blocks
            .Where(b => b.Text.Contains("{--", StringComparison.Ordinal) || b.Text.Contains("{~~", StringComparison.Ordinal))
            .Select(b => CriticMarkupSerializer.ResolveCriticMarkupText(b.Text, MarkupDisplayMode.Original)));

        var processor = new CitationProcessor(project.Style, project.Library);
        var result = processor.Process(texts);
        var context = new ScholarlyContext(project, crossRefs, result, texts);
        context.IndexCitations(texts, result);
        return context;
    }

    private void IndexCitations(List<string> texts, CitationResult result)
    {
        for (int i = 0; i < texts.Count && i < result.PerText.Count; i++)
        {
            foreach (var rendered in result.PerText[i])
            {
                string source = texts[i].Substring(rendered.Citation.Start, rendered.Citation.Length);
                _citationTextBySource.TryAdd(source, rendered.Text);
            }
        }
    }

    public bool HasCitations => Citations.CitedKeys.Count > 0 || Citations.MissingKeys.Count > 0;

    /// <summary>Splits text into pieces, rendering citations, cross-references, linked numbers and inline equations.</summary>
    public List<InlinePiece> Tokenize(string text)
    {
        var spans = new List<(int Start, int Length, InlinePiece Piece)>();

        // Draft placeholders first: what they hold is a stand-in, not citations or references.
        foreach (var placeholder in DraftPlaceholders.Find(text))
        {
            spans.Add((placeholder.Start, placeholder.Length, new InlinePiece(InlinePieceKind.Placeholder, text.Substring(placeholder.Start, placeholder.Length),
                placeholder.Display, placeholder.Note)));
        }

        foreach (var citation in CitationSyntax.Find(text))
        {
            if (Overlaps(spans, citation.Start, citation.Length))
            {
                continue;
            }
            string source = text.Substring(citation.Start, citation.Length);
            spans.Add((citation.Start, citation.Length, new InlinePiece(InlinePieceKind.Citation, source, RenderCitation(source),
                CitationDetail(citation), citation.Items.Any(i => !Project.Library.ContainsKey(i.Key)))));
        }

        foreach (var use in CrossReferenceIndex.FindUses(text))
        {
            if (Overlaps(spans, use.Start, use.Length))
            {
                continue;
            }
            bool missing = use.Items.Any(i => CrossReferences.Find(i.Kind, i.Label) == null);
            string display = CrossReferences.Render(use, CrossReferenceIndex.IsSentenceStart(text, use.Start));
            spans.Add((use.Start, use.Length, new InlinePiece(InlinePieceKind.CrossReference, text.Substring(use.Start, use.Length), display,
                string.Join(", ", use.Items.Select(i => $"{i.Kind}:{i.Label}")), missing)));
        }

        foreach (var placeholder in ResultsLibrary.FindPlaceholders(text))
        {
            if (Overlaps(spans, placeholder.Start, placeholder.Length))
            {
                continue;
            }
            string? value = Project.Results.Resolve(placeholder, out string error);
            string file = Project.Results.SourceInfo(placeholder.Source)?.File ?? placeholder.Source;
            spans.Add((placeholder.Start, placeholder.Length, new InlinePiece(InlinePieceKind.Number, text.Substring(placeholder.Start, placeholder.Length),
                value ?? "??", value != null ? $"{placeholder.Key} from {file}" : error, value == null)));
        }

        foreach (Match m in InlineMathRegex.Matches(text))
        {
            if (Overlaps(spans, m.Index, m.Length))
            {
                continue;
            }
            string tex = m.Groups["tex"].Value;
            spans.Add((m.Index, m.Length, new InlinePiece(InlinePieceKind.Math, m.Value, Equations.LatexToUnicode.Convert(tex), tex)));
        }

        spans.Sort((a, b) => a.Start.CompareTo(b.Start));
        var pieces = new List<InlinePiece>();
        int pos = 0;
        foreach (var (start, length, piece) in spans)
        {
            if (start < pos)
            {
                continue;
            }
            if (start > pos)
            {
                pieces.Add(TextPiece(text[pos..start]));
            }
            pieces.Add(piece);
            pos = start + length;
        }
        if (pos < text.Length)
        {
            pieces.Add(TextPiece(text[pos..]));
        }
        return pieces;
    }

    private static InlinePiece TextPiece(string text)
    {
        string display = text.Replace("\\@", "@", StringComparison.Ordinal);
        return new InlinePiece(InlinePieceKind.Text, text, display);
    }

    private static bool Overlaps(List<(int Start, int Length, InlinePiece Piece)> spans, int start, int length)
        => spans.Any(s => start < s.Start + s.Length && s.Start < start + length);

    /// <summary>The text as it reads in the finished paper (emphasis markers kept, everything else rendered).</summary>
    public string Render(string text)
    {
        var sb = new StringBuilder();
        foreach (var piece in Tokenize(text))
        {
            sb.Append(piece.Display);
        }
        return sb.ToString();
    }

    private string RenderCitation(string source)
    {
        if (_citationTextBySource.TryGetValue(source, out var text))
        {
            return text;
        }
        // Not seen when the context was built (typed since): process it after the rest so numbers stay put.
        _processedTexts.Add(source);
        Citations = new CitationProcessor(Project.Style, Project.Library).Process(_processedTexts);
        IndexCitations(_processedTexts, Citations);
        return _citationTextBySource.TryGetValue(source, out text) ? text : source;
    }

    private string CitationDetail(Citation citation)
    {
        var lines = new List<string>();
        foreach (var item in citation.Items)
        {
            var reference = Citations.References.FirstOrDefault(r => string.Equals(r.Key, item.Key, StringComparison.Ordinal));
            lines.Add(reference != null
                ? Diffing.DiffEngineService.ToGoogleDocsText(reference.Text)
                : Project.Library.ContainsKey(item.Key) ? item.Key : $"{item.Key}: not in {Project.Settings.BibliographyFile}");
        }
        return string.Join("\n", lines);
    }
}
