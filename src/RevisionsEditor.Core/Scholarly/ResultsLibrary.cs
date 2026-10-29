using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace RevisionsEditor.Core.Scholarly;

/// <summary>A results file registered with the workspace, optionally copied from an analysis output elsewhere.</summary>
public sealed class ResultsSource
{
    /// <summary>Name used in placeholders: <c>{{height.rmse}}</c> reads source "height".</summary>
    public string Name { get; set; } = string.Empty;
    /// <summary>Path inside the workspace (forward slashes), e.g. "results/height.json". This copy is committed and synced.</summary>
    public string File { get; set; } = string.Empty;
    /// <summary>The analysis output it was copied from (absolute path on the computer that added it), if any.</summary>
    public string? SourcePath { get; set; }
    /// <summary>SHA-256 of the file when it was last copied in.</summary>
    public string? Sha256 { get; set; }
    public DateTimeOffset? ImportedUtc { get; set; }
}

public sealed class ResultsRegistry
{
    public List<ResultsSource> Sources { get; set; } = [];
}

/// <summary>A <c>{{source.path:format}}</c> placeholder found in text.</summary>
public sealed record ResultsPlaceholder(int Start, int Length, string Source, string Path, string? Format)
{
    public string Key => $"{Source}.{Path}";
}

/// <summary>
/// Values from analysis results (CSV or JSON files in the workspace's <c>results</c> folder) that the text
/// refers to with placeholders such as <c>{{height.rmse:0.00}}</c>, so rerunning an analysis updates every
/// number in the paper. JSON values are addressed by key path (<c>{{metrics.leafOn.rmse}}</c>); CSV values
/// by the row's key columns then the column header (<c>{{accuracy.Grimston.rmse}}</c>).
/// </summary>
public sealed class ResultsLibrary
{
    public const string ResultsFolder = "results";
    public const string RegistryPath = ".revisions/results.json";

    private static readonly Regex PlaceholderRegex = new(
        @"\{\{\s*(?<src>[A-Za-z0-9_-]+)\.(?<path>[^{}:]+?)\s*(?::(?<fmt>[^{}]+?))?\s*\}\}",
        RegexOptions.Compiled);

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly Dictionary<string, LoadedSource> _sources = new(StringComparer.OrdinalIgnoreCase);

    private sealed class LoadedSource
    {
        public required ResultsSource Info { get; init; }
        public Dictionary<string, string> Values { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<string> Columns { get; } = [];
        public List<string[]> Rows { get; } = [];
        public bool IsCsv { get; init; }
        public string? Error { get; set; }
    }

    public ResultsRegistry Registry { get; private set; } = new();
    public IReadOnlyCollection<string> SourceNames => _sources.Keys;

    /// <summary>Loads every CSV and JSON file in the results folder, plus the registry that says where they came from.</summary>
    public static ResultsLibrary Load(string workspacePath)
    {
        var library = new ResultsLibrary { Registry = LoadRegistry(workspacePath) };
        string folder = Path.Combine(workspacePath, ResultsFolder);
        var files = Directory.Exists(folder)
            ? Directory.EnumerateFiles(folder, "*.*", SearchOption.AllDirectories)
                .Where(f => f.EndsWith(".csv", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            : Enumerable.Empty<string>();

        foreach (string file in files)
        {
            string relative = Path.GetRelativePath(workspacePath, file).Replace('\\', '/');
            var info = library.Registry.Sources.FirstOrDefault(s => string.Equals(s.File, relative, StringComparison.OrdinalIgnoreCase))
                ?? new ResultsSource { Name = Path.GetFileNameWithoutExtension(file), File = relative };
            if (library._sources.ContainsKey(info.Name))
            {
                continue;
            }
            library._sources[info.Name] = LoadFile(file, info);
        }
        return library;
    }

    /// <summary>Builds a library from in-memory file contents (tests, and versions read from a commit).</summary>
    public static ResultsLibrary FromContents(IEnumerable<(string Name, string FileName, string Content)> files)
    {
        var library = new ResultsLibrary();
        foreach (var (name, fileName, content) in files)
        {
            var info = new ResultsSource { Name = name, File = $"{ResultsFolder}/{fileName}" };
            library._sources[name] = Parse(content, fileName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase), info);
        }
        return library;
    }

    public static ResultsRegistry LoadRegistry(string workspacePath)
    {
        string path = Path.Combine(workspacePath, RegistryPath.Replace('/', Path.DirectorySeparatorChar));
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<ResultsRegistry>(File.ReadAllText(path)) ?? new() : new();
        }
        catch (JsonException)
        {
            return new ResultsRegistry();
        }
    }

    public static void SaveRegistry(string workspacePath, ResultsRegistry registry)
    {
        string path = Path.Combine(workspacePath, RegistryPath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(registry, JsonOptions) + "\n", new UTF8Encoding(false));
    }

    /// <summary>
    /// Copies an analysis output into the results folder (replacing an earlier copy of the same source) and
    /// records where it came from, so it can be refreshed after the analysis is rerun.
    /// </summary>
    public static ResultsSource ImportFile(string workspacePath, string sourceFilePath, string? name = null)
    {
        var registry = LoadRegistry(workspacePath);
        string fullSource = Path.GetFullPath(sourceFilePath);
        string resultsDir = Path.Combine(workspacePath, ResultsFolder);
        bool inside = fullSource.StartsWith(Path.GetFullPath(resultsDir) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

        var existing = registry.Sources.FirstOrDefault(s => s.SourcePath != null && string.Equals(s.SourcePath, fullSource, StringComparison.OrdinalIgnoreCase));
        string sourceName = existing?.Name ?? SanitizeName(name ?? Path.GetFileNameWithoutExtension(fullSource));
        if (existing == null)
        {
            var taken = registry.Sources.Select(s => s.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (Directory.Exists(resultsDir))
            {
                foreach (string f in Directory.EnumerateFiles(resultsDir))
                {
                    taken.Add(Path.GetFileNameWithoutExtension(f));
                }
            }
            if (!inside)
            {
                string baseName = sourceName;
                for (int n = 2; taken.Contains(sourceName); n++)
                {
                    sourceName = $"{baseName}-{n}";
                }
            }
        }

        string relative = inside
            ? Path.GetRelativePath(workspacePath, fullSource).Replace('\\', '/')
            : $"{ResultsFolder}/{sourceName}{Path.GetExtension(fullSource).ToLowerInvariant()}";
        if (!inside)
        {
            Directory.CreateDirectory(resultsDir);
            File.Copy(fullSource, Path.Combine(workspacePath, relative.Replace('/', Path.DirectorySeparatorChar)), overwrite: true);
        }

        var entry = existing ?? new ResultsSource();
        entry.Name = sourceName;
        entry.File = relative;
        entry.SourcePath = inside ? null : fullSource;
        entry.Sha256 = HashFile(fullSource);
        entry.ImportedUtc = DateTimeOffset.UtcNow;
        if (existing == null)
        {
            registry.Sources.RemoveAll(s => string.Equals(s.File, relative, StringComparison.OrdinalIgnoreCase));
            registry.Sources.Add(entry);
        }
        SaveRegistry(workspacePath, registry);
        return entry;
    }

    /// <summary>Copies again every source whose original analysis output changed. Returns the names refreshed.</summary>
    public static List<string> RefreshChangedSources(string workspacePath)
    {
        var refreshed = new List<string>();
        foreach (var source in LoadRegistry(workspacePath).Sources.ToList())
        {
            if (source.SourcePath != null && File.Exists(source.SourcePath) && HashFile(source.SourcePath) != source.Sha256)
            {
                ImportFile(workspacePath, source.SourcePath, source.Name);
                refreshed.Add(source.Name);
            }
        }
        return refreshed;
    }

    /// <summary>True when the analysis output a source was copied from exists on this computer and has changed since.</summary>
    public bool IsStale(string sourceName)
        => _sources.TryGetValue(sourceName, out var s) && s.Info.SourcePath is { } path && File.Exists(path) && HashFile(path) != s.Info.Sha256;

    public ResultsSource? SourceInfo(string name) => _sources.TryGetValue(name, out var s) ? s.Info : null;

    public string? LoadError(string name) => _sources.TryGetValue(name, out var s) ? s.Error : null;

    public static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static string SanitizeName(string name)
    {
        var sb = new StringBuilder();
        foreach (char c in name)
        {
            sb.Append(char.IsAsciiLetterOrDigit(c) || c is '_' or '-' ? c : '-');
        }
        string s = sb.ToString().Trim('-');
        return s.Length == 0 ? "results" : s;
    }

    private static LoadedSource LoadFile(string path, ResultsSource info)
    {
        try
        {
            return Parse(File.ReadAllText(path), path.EndsWith(".csv", StringComparison.OrdinalIgnoreCase), info);
        }
        catch (IOException ex)
        {
            return new LoadedSource { Info = info, Error = ex.Message };
        }
    }

    private static LoadedSource Parse(string content, bool isCsv, ResultsSource info)
    {
        var source = new LoadedSource { Info = info, IsCsv = isCsv };
        try
        {
            if (isCsv)
            {
                var rows = CsvTable.Parse(content);
                if (rows.Count > 0)
                {
                    source.Columns.AddRange(rows[0].Select(c => c.Trim()));
                    source.Rows.AddRange(rows.Skip(1).Where(r => r.Any(c => c.Trim().Length > 0)));
                }
                // Flattened for listing and literal-number checks: "rowkey.column".
                foreach (var row in source.Rows)
                {
                    for (int c = 1; c < source.Columns.Count && c < row.Length; c++)
                    {
                        source.Values.TryAdd($"{row[0].Trim()}.{source.Columns[c]}", row[c].Trim());
                    }
                }
            }
            else
            {
                using var doc = JsonDocument.Parse(content, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
                Flatten(doc.RootElement, string.Empty, source.Values);
            }
        }
        catch (Exception ex) when (ex is JsonException or FormatException)
        {
            source.Error = ex.Message;
        }
        return source;
    }

    private static void Flatten(JsonElement element, string prefix, Dictionary<string, string> values)
    {
        if (values.Count > 20000)
        {
            return;
        }
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var p in element.EnumerateObject())
                {
                    Flatten(p.Value, prefix.Length == 0 ? p.Name : $"{prefix}.{p.Name}", values);
                }
                break;
            case JsonValueKind.Array:
                int i = 0;
                foreach (var item in element.EnumerateArray())
                {
                    Flatten(item, prefix.Length == 0 ? i.ToString() : $"{prefix}.{i}", values);
                    i++;
                }
                break;
            case JsonValueKind.String:
                values[prefix] = element.GetString() ?? string.Empty;
                break;
            case JsonValueKind.Number:
                values[prefix] = element.GetRawText();
                break;
            case JsonValueKind.True or JsonValueKind.False:
                values[prefix] = element.GetRawText();
                break;
        }
    }

    /// <summary>Every value of every source as "source.path" → raw value (CSV rows keyed by their first column).</summary>
    public IEnumerable<(string Key, string Value)> AllValues()
        => _sources.SelectMany(s => s.Value.Values.Select(v => ($"{s.Key}.{v.Key}", v.Value)));

    public static List<ResultsPlaceholder> FindPlaceholders(string text)
        => PlaceholderRegex.Matches(text)
            .Select(m => new ResultsPlaceholder(m.Index, m.Length, m.Groups["src"].Value, m.Groups["path"].Value.Trim(),
                m.Groups["fmt"].Success ? m.Groups["fmt"].Value.Trim() : null))
            .ToList();

    /// <summary>The raw value a placeholder points at, or an explanation of why there is none.</summary>
    public bool TryGetRawValue(string sourceName, string path, out string value, out string error)
    {
        value = string.Empty;
        if (!_sources.TryGetValue(sourceName, out var source))
        {
            error = $"No results file named '{sourceName}' in the {ResultsFolder} folder.";
            return false;
        }
        if (source.Error != null)
        {
            error = $"'{source.Info.File}' could not be read: {source.Error}";
            return false;
        }
        if (!source.IsCsv)
        {
            string key = path.Replace("[", ".").Replace("]", string.Empty);
            if (source.Values.TryGetValue(key, out var v))
            {
                value = v;
                error = string.Empty;
                return true;
            }
            error = $"'{source.Info.File}' has no value '{path}'.";
            return false;
        }
        return TryCsvValue(source, path.Split('.'), out value, out error);
    }

    private static bool TryCsvValue(LoadedSource source, string[] segments, out string value, out string error)
    {
        value = string.Empty;
        error = string.Empty;
        int column = FindColumn(source.Columns, segments[^1]);
        var keys = segments[..^1];

        if (keys.Length == 0 && column < 0 && source.Columns.Count == 2)
        {
            // Two-column key/value table: {{metrics.rmse}} reads the row whose first cell is "rmse".
            var kv = source.Rows.FirstOrDefault(r => r.Length >= 2 && Same(r[0], segments[0]));
            if (kv != null)
            {
                value = kv[1].Trim();
                return true;
            }
        }
        if (column < 0)
        {
            error = $"'{source.Info.File}' has no column '{segments[^1]}' (columns: {string.Join(", ", source.Columns)}).";
            return false;
        }

        IEnumerable<string[]> rows = source.Rows;
        if (keys.Length == 1 && keys[0].StartsWith('#') && int.TryParse(keys[0][1..], out int rowNumber))
        {
            rows = source.Rows.Skip(rowNumber - 1).Take(1);
        }
        else
        {
            for (int k = 0; k < keys.Length; k++)
            {
                int col = k;
                string key = keys[k];
                rows = rows.Where(r => r.Length > col && Same(r[col], key));
            }
        }
        var matches = rows.Take(2).ToList();
        if (matches.Count == 0)
        {
            error = $"'{source.Info.File}' has no row '{string.Join(".", keys)}'.";
            return false;
        }
        if (matches.Count > 1 && keys.Length > 0)
        {
            error = $"'{source.Info.File}' has several rows matching '{string.Join(".", keys)}'; add the next key column.";
            return false;
        }
        if (matches.Count > 1)
        {
            error = $"'{source.Info.File}' has several rows; name the row, e.g. {{{{{source.Info.Name}.{source.Rows[0][0].Trim()}.{segments[^1]}}}}}.";
            return false;
        }
        value = column < matches[0].Length ? matches[0][column].Trim() : string.Empty;
        return true;
    }

    private static int FindColumn(List<string> columns, string name)
    {
        for (int i = 0; i < columns.Count; i++)
        {
            if (Same(columns[i], name))
            {
                return i;
            }
        }
        return -1;
    }

    private static bool Same(string a, string b) => string.Equals(Normalize(a), Normalize(b), StringComparison.OrdinalIgnoreCase);

    private static string Normalize(string s) => s.Trim().Replace(' ', '_').Replace('-', '_');

    /// <summary>The placeholder's value formatted for the text, or null with an error.</summary>
    public string? Resolve(ResultsPlaceholder placeholder, out string error)
        => TryGetRawValue(placeholder.Source, placeholder.Path, out string raw, out error) ? FormatValue(raw, placeholder.Format) : null;

    /// <summary>
    /// Formats a value: .NET numeric formats ("0.00", "F2", "N0", "E2"), "P1" for a percentage of a fraction
    /// (0.873 → 87.3%), "3sf" for significant figures. Negative numbers use a true minus sign.
    /// </summary>
    public static string FormatValue(string raw, string? format)
    {
        if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double number))
        {
            return raw;
        }
        string text;
        if (string.IsNullOrWhiteSpace(format))
        {
            text = raw.Trim();
        }
        else if (format.EndsWith("sf", StringComparison.OrdinalIgnoreCase) && int.TryParse(format[..^2], out int sf) && sf > 0)
        {
            text = SignificantFigures(number, sf);
        }
        else if ((format[0] is 'P' or 'p') && (format.Length == 1 || int.TryParse(format[1..], out _)))
        {
            int decimals = format.Length == 1 ? 0 : int.Parse(format[1..], CultureInfo.InvariantCulture);
            text = (number * 100).ToString("F" + decimals, CultureInfo.InvariantCulture) + "%";
        }
        else
        {
            try
            {
                text = number.ToString(format, CultureInfo.InvariantCulture);
            }
            catch (FormatException)
            {
                text = raw.Trim();
            }
        }
        return text.StartsWith('-') ? "−" + text[1..] : text;
    }

    private static string SignificantFigures(double value, int figures)
    {
        if (value == 0)
        {
            return "0";
        }
        int magnitude = (int)Math.Floor(Math.Log10(Math.Abs(value)));
        int decimals = Math.Max(0, figures - magnitude - 1);
        double rounded = Math.Round(value, Math.Min(decimals, 15), MidpointRounding.AwayFromZero);
        if (decimals == 0)
        {
            double scale = Math.Pow(10, magnitude - figures + 1);
            rounded = Math.Round(value / scale, MidpointRounding.AwayFromZero) * scale;
        }
        return rounded.ToString("F" + decimals, CultureInfo.InvariantCulture);
    }
}

/// <summary>Minimal RFC 4180 CSV reader (quoted fields, doubled quotes, CRLF); also accepts semicolon or tab separators.</summary>
public static class CsvTable
{
    public static List<string[]> Parse(string content)
    {
        var rows = new List<string[]>();
        if (string.IsNullOrEmpty(content))
        {
            return rows;
        }
        if (content[0] == '﻿')
        {
            content = content[1..];
        }
        char separator = DetectSeparator(content);
        var row = new List<string>();
        var field = new StringBuilder();
        bool quoted = false;
        for (int i = 0; i < content.Length; i++)
        {
            char c = content[i];
            if (quoted)
            {
                if (c == '"')
                {
                    if (i + 1 < content.Length && content[i + 1] == '"')
                    {
                        field.Append('"');
                        i++;
                    }
                    else
                    {
                        quoted = false;
                    }
                }
                else
                {
                    field.Append(c);
                }
            }
            else if (c == '"' && field.Length == 0)
            {
                quoted = true;
            }
            else if (c == separator)
            {
                row.Add(field.ToString());
                field.Clear();
            }
            else if (c is '\n' or '\r')
            {
                if (c == '\r' && i + 1 < content.Length && content[i + 1] == '\n')
                {
                    i++;
                }
                row.Add(field.ToString());
                field.Clear();
                rows.Add([.. row]);
                row.Clear();
            }
            else
            {
                field.Append(c);
            }
        }
        if (field.Length > 0 || row.Count > 0)
        {
            row.Add(field.ToString());
            rows.Add([.. row]);
        }
        return rows;
    }

    /// <summary>Writes rows as CSV (RFC 4180): fields with commas, quotes or line breaks are quoted.</summary>
    public static string Format(IEnumerable<IReadOnlyList<string>> rows)
    {
        var sb = new StringBuilder();
        foreach (var row in rows)
        {
            sb.AppendJoin(',', row.Select(f => f.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? "\"" + f.Replace("\"", "\"\"") + "\"" : f));
            sb.Append('\n');
        }
        return sb.ToString();
    }

    private static char DetectSeparator(string content)
    {
        int end = content.IndexOfAny(['\r', '\n']);
        string header = end < 0 ? content : content[..end];
        int commas = header.Count(c => c == ',');
        int semicolons = header.Count(c => c == ';');
        int tabs = header.Count(c => c == '\t');
        return tabs > commas && tabs >= semicolons ? '\t' : semicolons > commas ? ';' : ',';
    }
}
