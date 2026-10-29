using System.Text.RegularExpressions;
using RevisionsEditor.Core.Models;

namespace RevisionsEditor.Core.Scholarly;

/// <summary>
/// Leaves the title page out of a copy imported from Word or Google Docs (see <see cref="TitlePageText.Recognize"/>),
/// and collects the title-page lines someone edited there, which belong in the form.
/// </summary>
public sealed class TitlePageFilter(FrontMatter front, string keywordsHeading, ManuscriptDocument? manuscript)
{
    public List<string> EditedLines { get; } = [];
    public int DroppedCount { get; private set; }

    /// <summary>For a workspace's form and journal, or null when the form has nothing to recognise.</summary>
    public static TitlePageFilter? For(ScholarlyProject project, ManuscriptDocument? manuscript)
        => project.FrontMatter.Authors.Count > 0 || project.FrontMatter.Keywords.Count > 0 || project.FrontMatter.DraftNoteLines.Count > 0
            ? new TitlePageFilter(project.FrontMatter, project.Journal.KeywordsHeading, manuscript)
            : null;

    /// <summary>Indexes of the paragraphs to leave out.</summary>
    public HashSet<int> Apply(IReadOnlyList<(BlockStyleRole Role, string Text)> paragraphs)
    {
        var (drop, edited) = TitlePageText.Recognize(paragraphs, front, keywordsHeading, manuscript);
        EditedLines.AddRange(edited);
        DroppedCount += drop.Count;
        return drop;
    }

    /// <summary>A note for the log, or null when no title page was found.</summary>
    public string? Summary => DroppedCount == 0
        ? null
        : EditedLines.Count == 0
            ? $"Left out the title page ({DroppedCount} paragraphs): it comes from the title page form."
            : $"Left out the title page ({DroppedCount} paragraphs). These lines were edited there; make the change in the Paper panel's title page form: "
              + string.Join(" | ", EditedLines);
}

/// <summary>
/// The title page as the exports write it (authors, affiliations, correspondence and keywords, with
/// <c>^superscript^</c> and <c>**bold**</c> markers), and the means to recognise it in a copy coming back from
/// Word or Google Docs. The title page is generated from the form, not part of the manuscript, so its
/// paragraphs must not be imported as manuscript text.
/// </summary>
public static partial class TitlePageText
{
    /// <summary>The lines under the title: authors, then the numbered affiliations and the footnotes.</summary>
    public static List<string> HeaderLines(FrontMatter front)
    {
        var lines = new List<string>();
        if (front.Authors.Count == 0)
        {
            return lines;
        }
        lines.Add(string.Join(", ", front.Authors.Select(author =>
        {
            var marks = front.PrintedAffiliationNumbers(author).Select(n => n.ToString()).ToList();
            if (author.Corresponding) marks.Add("*");
            if (author.EqualContribution) marks.Add("†");
            return marks.Count > 0 ? $"{author.Name}^{string.Join(",", marks)}^" : author.Name;
        })));
        foreach (var (number, affiliation) in front.AffiliationsInOrder())
        {
            lines.Add($"^{number}^ {affiliation.Text}");
        }
        foreach (var author in front.CorrespondingAuthors)
        {
            string email = author.Email.Length > 0 ? $", {author.Email}" : string.Empty;
            string orcid = author.Orcid.Length > 0 ? $"; ORCID {author.Orcid}" : string.Empty;
            lines.Add($"^*^ Corresponding author: {author.Name}{email}{orcid}");
        }
        if (front.Authors.Any(a => a.EqualContribution))
        {
            lines.Add("^†^ These authors contributed equally.");
        }
        if (front.Notes.Trim().Length > 0)
        {
            lines.Add(front.Notes.Trim());
        }
        return lines;
    }

    public const string DraftNotesHeading = "Draft notes";

    /// <summary>The draft-notes box of a draft export: its heading, then the notes. Empty without notes.</summary>
    public static List<string> DraftNoteLines(FrontMatter front)
        => front.DraftNoteLines.Count == 0 ? [] : [$"**{DraftNotesHeading}**", .. front.DraftNoteLines];

    /// <summary>The keywords line written after the abstract, or null without keywords.</summary>
    public static string? KeywordsLine(FrontMatter front, string heading)
        => front.Keywords.Count > 0 ? $"**{heading}:** {string.Join("; ", front.Keywords)}" : null;

    /// <summary>
    /// Text compared without changes, formatting, marks, punctuation or case: "lukas olson 1 nicholas c coops 2 1".
    /// Affiliation numbers are split from names, as Google Docs reads "Olson¹" as "Olson1".
    /// </summary>
    internal static string Normalize(string text)
    {
        string plain = Serialization.CriticMarkupSerializer.ResolveCriticMarkupText(text, MarkupDisplayMode.NoMarkupFinal);
        plain = LetterDigitRegex().Replace(plain.Normalize(System.Text.NormalizationForm.FormKC), " "); // ¹ → 1
        return NonWordRegex().Replace(plain.ToLowerInvariant(), " ").Trim();
    }

    [GeneratedRegex(@"[^\p{L}\p{N}]+")]
    private static partial Regex NonWordRegex();

    [GeneratedRegex(@"(?<=\p{L})(?=\p{N})|(?<=\p{N})(?=\p{L})")]
    private static partial Regex LetterDigitRegex();

    private static HashSet<string> Words(string normalized) => [.. normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries)];

    /// <summary>How much of two lines' words they share (0 to 1).</summary>
    private static double Similarity(HashSet<string> a, HashSet<string> b)
        => a.Count == 0 || b.Count == 0 ? 0 : (double)a.Intersect(b).Count() / a.Union(b).Count();

    /// <summary>
    /// Removes the title page from an imported copy: paragraphs right after the title that match the title page
    /// as written from <paramref name="front"/> (also when someone edited them a little), and the keywords line
    /// written after the abstract. A paragraph that is in <paramref name="manuscript"/> is always kept, so a
    /// manuscript that writes its own authors or keywords is imported as it is.
    /// </summary>
    /// <returns>The paragraphs to drop (indexes into <paramref name="paragraphs"/>), and those among them that
    /// differ from the form: edits to the title page, to be made in the form instead.</returns>
    public static (HashSet<int> Drop, List<string> Edited) Recognize(
        IReadOnlyList<(BlockStyleRole Role, string Text)> paragraphs,
        FrontMatter front,
        string keywordsHeading,
        ManuscriptDocument? manuscript)
    {
        var drop = new HashSet<int>();
        var edited = new List<string>();
        // A draft may carry the draft notes under the title page.
        var header = HeaderLines(front).Concat(DraftNoteLines(front)).Select(Normalize).Where(l => l.Length > 0).ToList();
        string? keywords = KeywordsLine(front, keywordsHeading) is { } k ? Normalize(k) : null;
        if (header.Count == 0 && keywords == null)
        {
            return (drop, edited);
        }
        var known = new HashSet<string>(manuscript?.Blocks.Select(b => Normalize(b.Text)) ?? [], StringComparer.Ordinal);
        var headerWords = header.Select(Words).ToList();

        // Matches the line exactly (true), roughly (false) or not at all (null).
        bool? Match(string normalized, IReadOnlyList<string> lines, IReadOnlyList<HashSet<string>> words)
        {
            if (normalized.Length == 0 || known.Contains(normalized))
            {
                return null;
            }
            if (lines.Contains(normalized))
            {
                return true;
            }
            var w = Words(normalized);
            return words.Any(lw => Similarity(w, lw) >= 0.6) ? false : null;
        }

        // The title, or the first paragraph when the title is not styled as one (a bold paragraph, say).
        int title = 0;
        for (int i = 0; i < paragraphs.Count; i++)
        {
            if (paragraphs[i].Role == BlockStyleRole.Title)
            {
                title = i;
                break;
            }
        }
        if (header.Count > 0)
        {
            // The header lines are between the title and the first heading (or the abstract), perhaps among notes.
            for (int i = title + 1; i < paragraphs.Count; i++)
            {
                if (paragraphs[i].Role is BlockStyleRole.Heading1 or BlockStyleRole.Heading2 or BlockStyleRole.Heading3 or BlockStyleRole.Title or BlockStyleRole.Abstract)
                {
                    break;
                }
                var match = Match(Normalize(paragraphs[i].Text), header, headerWords);
                if (match == null)
                {
                    continue;
                }
                drop.Add(i);
                if (match == false)
                {
                    edited.Add(paragraphs[i].Text);
                }
            }
        }
        if (keywords != null)
        {
            string label = Normalize(keywordsHeading);
            for (int i = 0; i < paragraphs.Count; i++)
            {
                string normalized = Normalize(paragraphs[i].Text);
                if (!drop.Contains(i) && normalized.StartsWith(label + " ", StringComparison.Ordinal)
                    && Match(normalized, [keywords], [Words(keywords)]) is { } match)
                {
                    drop.Add(i);
                    if (!match)
                    {
                        edited.Add(paragraphs[i].Text);
                    }
                    break;
                }
            }
        }
        return (drop, edited);
    }
}
