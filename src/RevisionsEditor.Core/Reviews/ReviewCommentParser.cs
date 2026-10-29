using System.Text;
using System.Text.RegularExpressions;

namespace RevisionsEditor.Core.Reviews;

/// <summary>
/// Splits review comments pasted from a decision letter or the submission system into numbered points:
/// one section per reviewer ("Reviewer #1", "Referee 2", "Editor"), and within it the numbered or bulleted
/// points, or paragraphs when the reviewer did not number them.
/// </summary>
public static class ReviewCommentParser
{
    private static readonly Regex ReviewerHeadingRegex = new(
        @"^\s*(?:comments?\s+(?:from|of|by)\s+(?:the\s+)?)?(?<who>reviewer|referee)\s*#?\s*(?<n>\d+|[A-Z]\b)\b[^\n]{0,60}$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex EditorHeadingRegex = new(
        @"^\s*(?:comments?\s+(?:from|of|by)\s+(?:the\s+)?)?(?<who>(?:associate |handling |guest |academic )?editor(?:-in-chief)?)(?:'s)?(?:\s+comments?)?\s*[:.]?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex NumberedItemRegex = new(
        @"^\s*(?:(?:comment|point|remark|issue|item|query|q)\s*)?(?:#\s*)?(?<n>\d{1,3}(?:\.\d{1,3})?)\s*(?:[.):\]]|\s-)\s*",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex ParenthesizedItemRegex = new(@"^\s*\((?<n>\d{1,3}|[a-z])\)\s+", RegexOptions.Compiled);
    private static readonly Regex BulletRegex = new(@"^\s*[•●▪–*-]\s+", RegexOptions.Compiled);
    private static readonly Regex LineReferenceRegex = new(@"^\s*(?:(?:page|p\.)\s*\d+\s*,?\s*)?(?:lines?|ll?\.|l)\s*\d+", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex BoilerplateRegex = new(
        @"^\s*(comments?\s+(to|for)\s+the\s+authors?|reviewer'?s?\s+comments?|recommendation\s*:.*|please\s+see\s+(the\s+)?attach\w*.*)\s*:?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static List<ReviewComment> Parse(string pasted)
    {
        var comments = new List<ReviewComment>();
        if (string.IsNullOrWhiteSpace(pasted))
        {
            return comments;
        }

        var sections = SplitByReviewer(pasted.Replace("\r\n", "\n").Replace('\r', '\n'));
        int reviewerNumber = 0;
        foreach (var (reviewer, isEditor, explicitNumber, body) in sections)
        {
            if (!isEditor)
            {
                reviewerNumber = explicitNumber ?? reviewerNumber + 1;
            }
            string prefix = isEditor ? "E" : $"R{reviewerNumber}";
            string name = isEditor ? reviewer : $"Reviewer {reviewerNumber}";
            int n = 0;
            foreach (string item in SplitItems(body))
            {
                n++;
                comments.Add(new ReviewComment { Id = $"{prefix}.{n}", Reviewer = name, Text = item });
            }
        }
        return comments;
    }

    private static List<(string Reviewer, bool IsEditor, int? Number, string Body)> SplitByReviewer(string text)
    {
        var sections = new List<(string, bool, int?, string)>();
        string current = "Reviewer 1";
        bool editor = false;
        int? number = null;
        var body = new StringBuilder();
        bool any = false;

        void Flush()
        {
            if (body.ToString().Trim().Length > 0)
            {
                sections.Add((current, editor, number, body.ToString()));
            }
            body.Clear();
        }

        foreach (string line in text.Split('\n'))
        {
            var reviewer = ReviewerHeadingRegex.Match(line);
            var editorMatch = EditorHeadingRegex.Match(line);
            if (reviewer.Success && line.Trim().Length < 70)
            {
                Flush();
                string n = reviewer.Groups["n"].Value;
                number = int.TryParse(n, out int parsed) ? parsed : char.ToUpperInvariant(n[0]) - 'A' + 1;
                current = $"Reviewer {number}";
                editor = false;
                any = true;
                // "Reviewer 2: The paper is interesting…" — text on the heading line belongs to the body.
                int colon = line.IndexOf(':');
                if (colon > 0 && colon < line.Length - 1 && line[(colon + 1)..].Trim().Length > 20)
                {
                    body.AppendLine(line[(colon + 1)..].Trim());
                }
                continue;
            }
            if (editorMatch.Success)
            {
                Flush();
                current = Capitalize(editorMatch.Groups["who"].Value.Trim());
                editor = true;
                number = null;
                any = true;
                continue;
            }
            body.AppendLine(line);
        }
        Flush();
        if (!any && sections.Count == 1)
        {
            sections[0] = ("Reviewer 1", false, 1, sections[0].Item4);
        }
        return sections;
    }

    private static string Capitalize(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..].ToLowerInvariant();

    private static List<string> SplitItems(string body)
    {
        var lines = body.Split('\n').Where(l => !BoilerplateRegex.IsMatch(l)).ToList();
        int numbered = lines.Count(l => NumberedItemRegex.IsMatch(l) || ParenthesizedItemRegex.IsMatch(l));
        int bullets = lines.Count(l => BulletRegex.IsMatch(l));
        int lineRefs = lines.Count(l => LineReferenceRegex.IsMatch(l));

        Func<string, bool>? isStart = null;
        if (numbered >= 2)
        {
            isStart = l => NumberedItemRegex.IsMatch(l) || ParenthesizedItemRegex.IsMatch(l);
        }
        else if (bullets >= 2)
        {
            isStart = l => BulletRegex.IsMatch(l);
        }
        else if (lineRefs >= 2)
        {
            isStart = l => LineReferenceRegex.IsMatch(l);
        }

        var items = new List<string>();
        var current = new StringBuilder();
        void Flush()
        {
            string item = Regex.Replace(current.ToString().Trim(), @"[ \t]*\n[ \t]*\n\s*", "\n\n");
            if (item.Length > 0)
            {
                items.Add(item);
            }
            current.Clear();
        }

        if (isStart == null)
        {
            // Unnumbered: one point per paragraph.
            foreach (string paragraph in Regex.Split(string.Join("\n", lines), @"\n\s*\n"))
            {
                current.Append(Regex.Replace(paragraph.Trim(), @"\s*\n\s*", " "));
                Flush();
            }
            return MergeShortLeadIn(items);
        }

        foreach (string line in lines)
        {
            if (isStart(line))
            {
                Flush();
                string stripped = NumberedItemRegex.IsMatch(line) && numbered >= 2 ? NumberedItemRegex.Replace(line, string.Empty, 1)
                    : ParenthesizedItemRegex.IsMatch(line) && numbered >= 2 ? ParenthesizedItemRegex.Replace(line, string.Empty, 1)
                    : BulletRegex.IsMatch(line) && bullets >= 2 && numbered < 2 ? BulletRegex.Replace(line, string.Empty, 1)
                    : line.Trim();
                current.Append(stripped.Trim());
            }
            else if (line.Trim().Length == 0)
            {
                if (current.Length > 0)
                {
                    current.Append("\n\n");
                }
            }
            else
            {
                // Lines after a point continue it; general remarks before the first point become a point of their own.
                if (current.Length > 0 && !current.ToString().EndsWith("\n\n", StringComparison.Ordinal))
                {
                    current.Append(' ');
                }
                current.Append(line.Trim());
            }
        }
        Flush();
        return items;
    }

    /// <summary>A one-line heading such as "Minor comments:" is joined to the point after it.</summary>
    private static List<string> MergeShortLeadIn(List<string> items)
    {
        var result = new List<string>();
        for (int i = 0; i < items.Count; i++)
        {
            if (items[i].Length < 40 && items[i].EndsWith(':') && i + 1 < items.Count)
            {
                items[i + 1] = items[i] + " " + items[i + 1];
                continue;
            }
            result.Add(items[i]);
        }
        return result;
    }
}
