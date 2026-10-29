using System.Globalization;
using System.Text.RegularExpressions;
using RevisionsEditor.Core.Bibliography;
using RevisionsEditor.Core.Models;
using RevisionsEditor.Core.Scholarly;
using RevisionsEditor.Core.Serialization;

namespace RevisionsEditor.Core.Checks;

/// <summary>
/// Checks citations, cross-references, linked numbers and the target journal's requirements. Style is checked
/// separately by <see cref="StyleChecker"/>.
/// </summary>
public static class ManuscriptChecks
{
    public const string Citations = "Citations";
    public const string CrossReferences = "Cross-references";
    public const string Numbers = "Numbers";
    public const string Submission = "Submission";
    public const string Style = "Style";

    private static readonly Regex LiteralFigureRefRegex = new(
        @"\b(Fig(?:ure)?s?\.?|Tables?|Eqs?\.?|Equations?)\s+(\d+|\(\d+\))(?![\d.]*\s*(?:m|cm|km|%))",
        RegexOptions.Compiled);

    private static readonly Regex NumberRegex = new(@"(?<![\w.^~{])[-−]?\d+(?:\.\d+)?(?![\w.]*\d)", RegexOptions.Compiled);

    private static readonly (string Name, Regex Pattern)[] Metrics =
    [
        ("rrmse", new Regex(@"\b(rRMSE|relative RMSE|RMSE%|%RMSE)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        ("rmse", new Regex(@"\b(RMSE|root[- ]mean[- ]square(?:d)? error)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        ("mae", new Regex(@"\b(MAE|mean absolute error)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        ("bias", new Regex(@"\b(bias|mean error|ME)\b", RegexOptions.Compiled)),
        ("r2", new Regex(@"(\bR\^2\^|\bR²|\bR2\b|\br\^2\^|coefficient of determination)", RegexOptions.Compiled)),
        ("accuracy", new Regex(@"\b(overall accuracy|OA|accuracy)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        ("kappa", new Regex(@"\b(kappa|κ)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        ("f1", new Regex(@"\b(F1|F-score|F1-score)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled))
    ];

    public static List<ManuscriptIssue> Run(IReadOnlyList<DocumentBlock> blocks, ScholarlyContext context)
    {
        var issues = new List<ManuscriptIssue>();
        var structure = new ManuscriptStructure(blocks);
        CheckCitations(blocks, context, structure, issues);
        CheckCrossReferences(blocks, context, issues);
        CheckNumbers(blocks, context, structure, issues);
        CheckSubmission(blocks, context, structure, issues);
        CheckPlaceholders(blocks, issues);
        return issues;
    }

    public const string Placeholders = "Placeholders";

    /// <summary>Every draft placeholder still in the manuscript (text, figures and tables), to fill in before submission.</summary>
    public static IEnumerable<ManuscriptIssue> FindPlaceholders(IReadOnlyList<DocumentBlock> blocks)
    {
        foreach (var block in blocks)
        {
            if (block.Role is BlockStyleRole.Figure or BlockStyleRole.Table)
            {
                string final = CriticMarkupSerializer.ResolveCriticMarkupText(block.Text, MarkupDisplayMode.NoMarkupFinal);
                var attributes = block.Role == BlockStyleRole.Figure ? ScholarlyBlocks.ParseFigure(final)?.Attributes : ScholarlyBlocks.ParseTable(final)?.Attributes;
                if (attributes != null && DraftPlaceholders.NoteOf(attributes) is { } note)
                {
                    string what = block.Role == BlockStyleRole.Figure ? "This figure" : "This table";
                    yield return new ManuscriptIssue(Placeholders, IssueSeverity.Warning,
                        $"{what} is a placeholder{(note.Length > 0 ? $": {note}" : string.Empty)}. Replace it before submitting.", block.ParagraphId);
                }
            }
            foreach (var placeholder in DraftPlaceholders.Find(block.Text))
            {
                yield return new ManuscriptIssue(Placeholders, IssueSeverity.Warning,
                    $"Placeholder “{placeholder.Display}”{(placeholder.Note.Length > 0 ? $": {placeholder.Note}" : string.Empty)}. Fill it in before submitting.",
                    block.ParagraphId, placeholder.Start, placeholder.Length);
            }
        }
    }

    private static void CheckPlaceholders(IReadOnlyList<DocumentBlock> blocks, List<ManuscriptIssue> issues) => issues.AddRange(FindPlaceholders(blocks));

    /// <summary>Stable identity of an issue, used to remember that the author dismissed it.</summary>
    public static string Fingerprint(ManuscriptIssue issue) => $"{issue.Category}|{issue.ParagraphId}|{issue.Message}";

    // ---- Citations --------------------------------------------------------------------------------------

    private static void CheckCitations(IReadOnlyList<DocumentBlock> blocks, ScholarlyContext context, ManuscriptStructure structure, List<ManuscriptIssue> issues)
    {
        var project = context.Project;
        string bibName = project.Settings.BibliographyFile;
        bool anyCitations = false;

        foreach (var block in blocks)
        {
            if (block.Role is BlockStyleRole.Equation or BlockStyleRole.ReferenceItem)
            {
                continue;
            }
            foreach (var citation in CitationSyntax.Find(block.Text))
            {
                anyCitations = true;
                foreach (var item in citation.Items.Where(i => !project.Library.ContainsKey(i.Key)))
                {
                    string message = project.BibliographyExists
                        ? $"“{item.Key}” is not in {bibName}."
                        : $"“{item.Key}” cannot be found: there is no {bibName} in the workspace yet.";
                    issues.Add(new ManuscriptIssue(Citations, IssueSeverity.Error, message, block.ParagraphId, citation.Start, citation.Length));
                }
            }
        }

        if (project.BibliographyError != null)
        {
            issues.Add(new ManuscriptIssue(Citations, IssueSeverity.Error, $"{bibName} could not be read: {project.BibliographyError}"));
        }
        if (project.Bibliography != null)
        {
            foreach (string warning in project.Bibliography.Warnings.Take(20))
            {
                issues.Add(new ManuscriptIssue(Citations, IssueSeverity.Warning, $"{bibName}: {warning}"));
            }
            foreach (string key in project.Bibliography.DuplicateKeys)
            {
                issues.Add(new ManuscriptIssue(Citations, IssueSeverity.Warning, $"{bibName} defines “{key}” more than once; the first entry is used."));
            }
        }

        if (anyCitations && project.Library.Count > 0)
        {
            var cited = context.Citations.CitedKeys.ToHashSet(StringComparer.Ordinal);
            var withoutDoi = cited
                .Select(k => project.Library.TryGetValue(k, out var e) ? e : null)
                .Where(e => e is { Type: "article" } && string.IsNullOrWhiteSpace(e.Doi))
                .Select(e => e!.Key)
                .ToList();
            if (withoutDoi.Count > 0)
            {
                issues.Add(new ManuscriptIssue(Citations, IssueSeverity.Info,
                    $"{Plural(withoutDoi.Count, "cited article has", "cited articles have")} no DOI ({string.Join(", ", withoutDoi.Take(6))}{(withoutDoi.Count > 6 ? ", …" : string.Empty)}); most journals ask for DOIs."));
            }
        }

        // Journals that ask for an abstract without references.
        if (context.Project.Journal.AbstractNotes.Contains("no references", StringComparison.OrdinalIgnoreCase)
            || context.Project.Journal.AbstractNotes.Contains("without abbreviations, references", StringComparison.OrdinalIgnoreCase)
            || context.Project.Journal.AbstractNotes.Contains("no citations", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var block in structure.AbstractBlocks())
            {
                foreach (var citation in CitationSyntax.Find(block.Text))
                {
                    issues.Add(new ManuscriptIssue(Citations, IssueSeverity.Warning,
                        $"{context.Project.Journal.Name} asks for an abstract without citations.", block.ParagraphId, citation.Start, citation.Length));
                }
            }
        }

        if (anyCitations && blocks.Any(b => b.Role == BlockStyleRole.ReferenceItem))
        {
            var manual = blocks.First(b => b.Role == BlockStyleRole.ReferenceItem);
            issues.Add(new ManuscriptIssue(Citations, IssueSeverity.Warning,
                "The reference list is generated from the citations; references typed as paragraphs are exported as they are, in addition to it.",
                manual.ParagraphId));
        }
    }

    // ---- Cross-references --------------------------------------------------------------------------

    private static void CheckCrossReferences(IReadOnlyList<DocumentBlock> blocks, ScholarlyContext context, List<ManuscriptIssue> issues)
    {
        var index = context.CrossReferences;
        var firstUse = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        int position = 0;

        foreach (var block in blocks)
        {
            position++;
            foreach (var use in CrossReferenceIndex.FindUses(block.Text))
            {
                foreach (var (kind, label, _) in use.Items)
                {
                    var target = index.Find(kind, label);
                    if (target == null)
                    {
                        issues.Add(new ManuscriptIssue(CrossReferences, IssueSeverity.Error,
                            $"Nothing is labelled “{kind}:{label}”.", block.ParagraphId, use.Start, use.Length));
                    }
                    else
                    {
                        firstUse.TryAdd($"{kind}:{label}", position);
                    }
                }
            }
        }

        foreach (var (label, paragraphId) in index.DuplicateLabels)
        {
            issues.Add(new ManuscriptIssue(CrossReferences, IssueSeverity.Error, $"The label “{label}” is used for more than one item.", paragraphId));
        }

        var targetsByParagraph = index.Targets.Values.ToDictionary(t => t.ParagraphId, StringComparer.Ordinal);
        var previousByKind = new Dictionary<string, (string Number, int FirstUse)>(StringComparer.Ordinal);
        foreach (var item in index.NumberedItems)
        {
            if (item.Kind == "eq")
            {
                continue; // equations are often not referred to
            }
            string noun = item.Kind == "fig" ? "Figure" : "Table";
            if (!targetsByParagraph.TryGetValue(item.ParagraphId, out var labelled))
            {
                issues.Add(new ManuscriptIssue(CrossReferences, IssueSeverity.Info,
                    $"{noun} {item.Number} has no label, so the text cannot refer to it (add {{#{item.Kind}:name}}).", item.ParagraphId));
                continue;
            }
            if (!firstUse.TryGetValue(labelled.Label, out int first))
            {
                issues.Add(new ManuscriptIssue(CrossReferences, IssueSeverity.Warning,
                    $"{noun} {item.Number} is never referred to in the text (journals expect every {noun.ToLowerInvariant()} to be cited).", item.ParagraphId));
                continue;
            }
            if (previousByKind.TryGetValue(item.Kind, out var previous) && first < previous.FirstUse)
            {
                issues.Add(new ManuscriptIssue(CrossReferences, IssueSeverity.Warning,
                    $"{noun} {item.Number} is first referred to before {noun} {previous.Number}; number {noun.ToLowerInvariant()}s in the order they are first cited (move it earlier).",
                    item.ParagraphId));
            }
            else
            {
                previousByKind[item.Kind] = (item.Number, first);
            }
        }

        foreach (var block in blocks.Where(b => b.Role is BlockStyleRole.Figure or BlockStyleRole.Table))
        {
            string final = CriticMarkupSerializer.ResolveCriticMarkupText(block.Text, MarkupDisplayMode.NoMarkupFinal);
            string? file = block.Role == BlockStyleRole.Figure ? ScholarlyBlocks.ParseFigure(final)?.ImagePath : ScholarlyBlocks.ParseTable(final)?.CsvPath;
            string noun = block.Role == BlockStyleRole.Figure ? "image" : "table file";
            if (string.IsNullOrWhiteSpace(file))
            {
                issues.Add(new ManuscriptIssue(CrossReferences, IssueSeverity.Error, $"This {(block.Role == BlockStyleRole.Figure ? "figure" : "table")} has no {noun}.", block.ParagraphId));
                continue;
            }
            string path = Path.Combine(context.Project.WorkspacePath, file.Replace('/', Path.DirectorySeparatorChar));
            if (context.Project.WorkspacePath.Length > 0 && !File.Exists(path))
            {
                issues.Add(new ManuscriptIssue(CrossReferences, IssueSeverity.Error, $"The {noun} “{file}” is missing from the workspace.", block.ParagraphId));
            }
            else if (File.Exists(path) && new FileInfo(path).Length > 95L * 1024 * 1024)
            {
                issues.Add(new ManuscriptIssue(CrossReferences, IssueSeverity.Error, $"“{file}” is larger than GitHub's 100 MB file limit; export a smaller version for the paper.", block.ParagraphId));
            }
            else if (File.Exists(path) && new FileInfo(path).Length > 20L * 1024 * 1024)
            {
                issues.Add(new ManuscriptIssue(CrossReferences, IssueSeverity.Info, $"“{file}” is over 20 MB; a smaller copy keeps the paper quick to sync and within journal upload limits.", block.ParagraphId));
            }
        }

        if (index.NumberedItems.Any(i => i.Kind is "fig" or "tbl"))
        {
            foreach (var block in blocks.Where(b => b.Role is BlockStyleRole.BodyParagraph or BlockStyleRole.Abstract or BlockStyleRole.BlockQuote))
            {
                foreach (Match m in LiteralFigureRefRegex.Matches(block.Text))
                {
                    issues.Add(new ManuscriptIssue(CrossReferences, IssueSeverity.Info,
                        $"“{m.Value}” is typed as text; a cross-reference (e.g. @fig:name) keeps the number right when items move.",
                        block.ParagraphId, m.Index, m.Length));
                }
            }
        }
    }

    // ---- Linked numbers -------------------------------------------------------------------------------

    private static void CheckNumbers(IReadOnlyList<DocumentBlock> blocks, ScholarlyContext context, ManuscriptStructure structure, List<ManuscriptIssue> issues)
    {
        var results = context.Project.Results;
        var usedSources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var block in blocks)
        {
            foreach (var placeholder in ResultsLibrary.FindPlaceholders(block.Text))
            {
                usedSources.Add(placeholder.Source);
                if (results.Resolve(placeholder, out string error) == null)
                {
                    issues.Add(new ManuscriptIssue(Numbers, IssueSeverity.Error, error, block.ParagraphId, placeholder.Start, placeholder.Length));
                }
            }
        }

        foreach (string source in results.SourceNames)
        {
            if (results.IsStale(source))
            {
                var info = results.SourceInfo(source)!;
                issues.Add(new ManuscriptIssue(Numbers, IssueSeverity.Warning,
                    $"“{info.SourcePath}” has changed since it was copied to {info.File}; refresh the results so the paper's numbers update."));
            }
            if (results.LoadError(source) is { } loadError)
            {
                issues.Add(new ManuscriptIssue(Numbers, IssueSeverity.Error, $"{results.SourceInfo(source)?.File} could not be read: {loadError}"));
            }
        }

        // Literal numbers that equal a result value: linking them keeps them right after the analysis is rerun.
        var numericValues = results.AllValues()
            .Where(v => v.Value.Contains('.') && double.TryParse(v.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
            .ToList();
        if (numericValues.Count > 0 && numericValues.Count < 5000)
        {
            foreach (var block in blocks.Where(b => b.Role is BlockStyleRole.BodyParagraph or BlockStyleRole.Abstract or BlockStyleRole.Caption))
            {
                string text = block.Text;
                foreach (Match m in NumberRegex.Matches(text))
                {
                    if (!m.Value.Contains('.') || InsideMarkup(text, m.Index))
                    {
                        continue;
                    }
                    int decimals = m.Value.Length - m.Value.IndexOf('.') - 1;
                    var literal = double.Parse(m.Value.Replace('−', '-'), CultureInfo.InvariantCulture);
                    var match = numericValues.FirstOrDefault(v =>
                        Math.Round(double.Parse(v.Value, CultureInfo.InvariantCulture), decimals, MidpointRounding.AwayFromZero) == literal);
                    if (match.Key != null)
                    {
                        string format = "F" + decimals.ToString(CultureInfo.InvariantCulture);
                        string placeholder = $"{{{{{match.Key}:{format}}}}}";
                        issues.Add(new ManuscriptIssue(Numbers, IssueSeverity.Info,
                            $"{m.Value} matches {match.Key} in the results; link it so it updates when the analysis is rerun.",
                            block.ParagraphId, m.Index, m.Length, placeholder, $"Link to {match.Key}"));
                    }
                }
            }
        }

        // Reported metrics that agree with none of the results (abstract, results and conclusions only).
        var metricValues = results.AllValues()
            .Select(v => (v.Key, Metric: MetricOfKey(v.Key), Ok: double.TryParse(v.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double d), Value: d))
            .Where(v => v.Metric != null && v.Ok)
            .GroupBy(v => v.Metric!)
            .ToDictionary(g => g.Key, g => g.Select(v => v.Value).ToList());
        if (metricValues.Count == 0)
        {
            return;
        }
        var checkedBlocks = structure.AbstractBlocks()
            .Concat(structure.Sections
                .Where(s => Regex.IsMatch(ManuscriptStructure.NormalizeHeading(s.Title), @"^(results|conclusions?|summary|results and discussion)$"))
                .SelectMany(s => s.Content))
            .Where(b => b.Role is BlockStyleRole.BodyParagraph or BlockStyleRole.Abstract)
            .DistinctBy(b => b.ParagraphId);
        foreach (var block in checkedBlocks)
        {
            string text = block.Text;
            foreach (var (metric, pattern) in Metrics)
            {
                if (!metricValues.TryGetValue(metric, out var values))
                {
                    continue;
                }
                foreach (Match name in pattern.Matches(text))
                {
                    var number = NumberRegex.Match(text, name.Index + name.Length);
                    if (!number.Success || number.Index - (name.Index + name.Length) > 25 || InsideMarkup(text, number.Index)
                        || text[(name.Index + name.Length)..number.Index].Contains("{{", StringComparison.Ordinal))
                    {
                        continue;
                    }
                    string literalText = number.Value.Replace('−', '-');
                    if (!double.TryParse(literalText, NumberStyles.Float, CultureInfo.InvariantCulture, out double literal))
                    {
                        continue;
                    }
                    int decimals = literalText.Contains('.') ? literalText.Length - literalText.IndexOf('.') - 1 : 0;
                    bool agrees = values.Any(v =>
                        Math.Round(v, decimals, MidpointRounding.AwayFromZero) == literal
                        || Math.Round(v * 100, decimals, MidpointRounding.AwayFromZero) == literal);
                    if (!agrees)
                    {
                        string known = string.Join(", ", values.Take(5).Select(v => v.ToString("G4", CultureInfo.InvariantCulture)));
                        issues.Add(new ManuscriptIssue(Numbers, IssueSeverity.Warning,
                            $"The {name.Value} of {number.Value} agrees with none of the {name.Value} values in the results ({known}{(values.Count > 5 ? ", …" : string.Empty)}).",
                            block.ParagraphId, number.Index, number.Length));
                    }
                }
            }
        }
    }

    private static string? MetricOfKey(string key)
    {
        string last = key[(key.LastIndexOf('.') + 1)..].ToLowerInvariant().Replace("_", string.Empty).Replace("-", string.Empty);
        return last switch
        {
            "rrmse" or "rmsepct" or "rmsepercent" or "relrmse" => "rrmse",
            "rmse" => "rmse",
            "mae" => "mae",
            "bias" or "meanerror" or "me" => "bias",
            "r2" or "rsquared" or "rsq" => "r2",
            "oa" or "accuracy" or "overallaccuracy" => "accuracy",
            "kappa" => "kappa",
            "f1" or "fscore" or "f1score" => "f1",
            _ => null
        };
    }

    /// <summary>True inside a placeholder, citation, attribute group or inline equation.</summary>
    private static bool InsideMarkup(string text, int index)
    {
        int open = text.LastIndexOf("{{", index, StringComparison.Ordinal);
        if (open >= 0 && text.IndexOf("}}", open, StringComparison.Ordinal) is int close && close >= index)
        {
            return true;
        }
        int brace = text.LastIndexOf('{', index);
        if (brace >= 0 && brace + 1 < text.Length && text[brace + 1] == '#' && text.IndexOf('}', brace) >= index)
        {
            return true;
        }
        int bracket = text.LastIndexOf('[', index);
        if (bracket >= 0 && bracket + 1 < text.Length && text.IndexOf(']', bracket) >= index && text.IndexOf('@', bracket) is int at && at >= 0 && at < index)
        {
            return true;
        }
        int dollars = text[..index].Count(c => c == '$');
        return dollars % 2 == 1;
    }

    // ---- Journal requirements ----------------------------------------------------------------------

    private static void CheckSubmission(IReadOnlyList<DocumentBlock> blocks, ScholarlyContext context, ManuscriptStructure structure, List<ManuscriptIssue> issues)
    {
        var journal = context.Project.Journal;
        string name = journal.Name;

        var abstractBlocks = structure.AbstractBlocks();
        if (abstractBlocks.Count == 0)
        {
            issues.Add(new ManuscriptIssue(Submission, IssueSeverity.Warning, "No abstract found: style a paragraph as Abstract or put it under an “Abstract” heading."));
        }
        else if (journal.AbstractMaxWords is { } maxAbstract)
        {
            int words = abstractBlocks.Sum(b => ManuscriptStatistics.CountWords(context.Render(ManuscriptStructure.PlainText(b.Text))));
            if (words > maxAbstract)
            {
                issues.Add(new ManuscriptIssue(Submission, IssueSeverity.Warning,
                    $"The abstract has {words} words; {name} allows {maxAbstract}.", abstractBlocks[0].ParagraphId));
            }
        }

        var highlights = structure.Highlights(context.Render);
        if (highlights == null)
        {
            if (journal.HighlightsRequired)
            {
                string limits = journal.HighlightMaxCharacters is { } c
                    ? $" ({journal.HighlightsMin}–{journal.HighlightsMax} bullets of at most {c} characters)"
                    : string.Empty;
                issues.Add(new ManuscriptIssue(Submission, IssueSeverity.Warning, $"{name} requires highlights{limits}: add a “Highlights” section."));
            }
        }
        else
        {
            if (journal.HighlightsMin is { } min && highlights.Count < min || journal.HighlightsMax is { } max && highlights.Count > max)
            {
                issues.Add(new ManuscriptIssue(Submission, IssueSeverity.Warning,
                    $"There are {highlights.Count} highlights; {name} asks for {journal.HighlightsMin}–{journal.HighlightsMax}.",
                    highlights.FirstOrDefault().Block?.ParagraphId));
            }
            if (journal.HighlightMaxCharacters is { } maxChars)
            {
                foreach (var (block, text) in highlights.Where(h => h.Text.Length > maxChars))
                {
                    issues.Add(new ManuscriptIssue(Submission, IssueSeverity.Warning,
                        $"This highlight has {text.Length} characters; the limit is {maxChars} including spaces.", block.ParagraphId));
                }
            }
        }

        var (keywordBlock, keywords) = structure.Keywords();
        if (context.Project.FrontMatter.Keywords.Count > 0)
        {
            // The title-page form has the keywords.
            (keywordBlock, keywords) = (null, context.Project.FrontMatter.Keywords);
        }
        if (keywords.Count == 0 && (journal.KeywordsMin != null || journal.KeywordsMax != null))
        {
            issues.Add(new ManuscriptIssue(Submission, IssueSeverity.Info, $"No {journal.KeywordsHeading.ToLowerInvariant()} found: add a paragraph starting “{journal.KeywordsHeading}:”."));
        }
        else if (keywords.Count > 0 && (journal.KeywordsMin is { } kmin && keywords.Count < kmin || journal.KeywordsMax is { } kmax && keywords.Count > kmax))
        {
            string range = journal.KeywordsMin != null && journal.KeywordsMax != null ? $"{journal.KeywordsMin}–{journal.KeywordsMax}"
                : journal.KeywordsMax != null ? $"at most {journal.KeywordsMax}" : $"at least {journal.KeywordsMin}";
            issues.Add(new ManuscriptIssue(Submission, IssueSeverity.Warning,
                $"There are {keywords.Count} {journal.KeywordsHeading.ToLowerInvariant()}; {name} asks for {range}.", keywordBlock?.ParagraphId));
        }

        foreach (var section in journal.RequiredSections.Where(s => s.Required))
        {
            if (structure.FindSection(section.Heading, section.Aliases) == null && !blocks.Take(80).Any(b => StartsWithLabel(b, section)))
            {
                issues.Add(new ManuscriptIssue(Submission, IssueSeverity.Warning, $"{name} asks for a “{section.Heading}” section. {section.Notes}"));
            }
        }

        if (journal.MaxWords != null && ManuscriptStatistics.Compute(blocks, context, journal).JournalLimit is { Exceeded: true } limit)
        {
            issues.Add(new ManuscriptIssue(Submission, IssueSeverity.Warning,
                $"The {limit.Scope} has {limit.Counted:N0} words; {name} allows {limit.Limit:N0} ({journal.LengthLimit})"));
        }
    }

    /// <summary>Statements written as a bold run-in label ("**Funding:** …") count as the section.</summary>
    private static bool StartsWithLabel(DocumentBlock block, Journals.RequiredSection section)
    {
        string text = ManuscriptStructure.PlainText(block.Text);
        int colon = text.IndexOf(':');
        if (colon <= 0 || colon > 80)
        {
            return false;
        }
        string label = ManuscriptStructure.NormalizeHeading(text[..colon]);
        return new[] { section.Heading }.Concat(section.Aliases).Any(a => ManuscriptStructure.NormalizeHeading(a) == label);
    }

    private static string Plural(int n, string singular, string plural) => n == 1 ? $"1 {singular}" : $"{n} {plural}";
}
