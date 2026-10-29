using System.Text;
using System.Text.RegularExpressions;

namespace RevisionsEditor.Core.Bibliography;

/// <summary>
/// Three-way merge of a BibTeX file by entry key, for when two computers changed the bibliography: entries
/// added on either side are kept, an entry changed on one side takes that change, and an entry changed on both
/// keeps this computer's version. Text outside entries (comments, @string) is taken from this computer.
/// </summary>
public static class BibFileMerge
{
    private static readonly Regex EntryStartRegex = new(@"^[ \t]*@(?<type>\w+)\s*[{(]\s*(?<key>[^,\s{}()]+)\s*,", RegexOptions.Multiline | RegexOptions.Compiled);

    public static string Merge(string baseText, string ours, string theirs)
    {
        var baseEntries = Split(baseText).Entries;
        var (ourEntries, preamble) = Split(ours);
        var theirEntries = Split(theirs).Entries;

        var result = new StringBuilder(preamble);
        var written = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (key, text) in ourEntries)
        {
            written.Add(key);
            bool removedThere = !theirEntries.ContainsKey(key) && baseEntries.TryGetValue(key, out var b) && b == text;
            if (removedThere)
            {
                continue;
            }
            string chosen = theirEntries.TryGetValue(key, out var their) && baseEntries.TryGetValue(key, out var bt) && bt == text ? their : text;
            Append(result, chosen);
        }
        foreach (var (key, text) in theirEntries)
        {
            if (!written.Contains(key) && !baseEntries.ContainsKey(key))
            {
                Append(result, text);
            }
        }
        return result.ToString();
    }

    private static void Append(StringBuilder sb, string entry)
    {
        if (sb.Length > 0 && !sb.ToString().EndsWith("\n\n", StringComparison.Ordinal))
        {
            sb.Append(sb.ToString().EndsWith('\n') ? "\n" : "\n\n");
        }
        sb.Append(entry.TrimEnd()).Append('\n');
    }

    /// <summary>Entries by key (in file order), and the text before the first entry.</summary>
    private static (Dictionary<string, string> Entries, string Preamble) Split(string text)
    {
        var entries = new Dictionary<string, string>(StringComparer.Ordinal);
        var matches = EntryStartRegex.Matches(text).Where(m => !m.Groups["type"].Value.Equals("string", StringComparison.OrdinalIgnoreCase)
            && !m.Groups["type"].Value.Equals("comment", StringComparison.OrdinalIgnoreCase)
            && !m.Groups["type"].Value.Equals("preamble", StringComparison.OrdinalIgnoreCase)).ToList();
        string preamble = matches.Count > 0 ? text[..matches[0].Index] : text;
        for (int i = 0; i < matches.Count; i++)
        {
            int end = i + 1 < matches.Count ? matches[i + 1].Index : text.Length;
            entries.TryAdd(matches[i].Groups["key"].Value, text[matches[i].Index..end].TrimEnd() + "\n");
        }
        return (entries, preamble);
    }
}
