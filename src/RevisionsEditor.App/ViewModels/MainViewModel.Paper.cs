using System.IO;
using System.Text.RegularExpressions;
using RevisionsEditor.Core.Docx;
using RevisionsEditor.Core.Formatting;
using RevisionsEditor.Core.Models;
using RevisionsEditor.Core.Reviews;
using RevisionsEditor.Core.Scholarly;
using RevisionsEditor.Core.Serialization;

namespace RevisionsEditor.App.ViewModels;

/// <summary>What exporting a revision produced.</summary>
public sealed record RevisionPackage(string RevisedManuscript, string MarkedUpManuscript, string ResponseLetter, bool LocationsFromWord);

public sealed partial class MainViewModel
{
    private static readonly Regex ParagraphNumberRegex = new(@"^p-(\d+)$", RegexOptions.Compiled);

    /// <summary>The bibliography, figures, results, settings or review responses changed: they go with the next commit.</summary>
    public void MarkProjectFilesChanged()
    {
        if (HasWorkspace)
        {
            HasUncommittedChanges = true;
        }
    }

    private DocxExportOptions ExportOptions(bool trackedChanges)
    {
        Paper.RebuildContext();
        return new DocxExportOptions
        {
            Profile = _activeProfile,
            IncludeTrackedChanges = trackedChanges,
            Scholarly = Paper.Context,
            WorkspacePath = WorkspacePath,
            RoundTripMarkers = true
        };
    }

    // ---- Editing blocks -------------------------------------------------------------------------------

    public DocumentBlock? FindBlock(string paragraphId) => _currentDocument.Blocks.FirstOrDefault(b => b.ParagraphId == paragraphId);

    public string NewParagraphId()
    {
        int next = UsedParagraphIds.Select(id => ParagraphNumberRegex.Match(id)).Where(m => m.Success)
            .Select(m => int.Parse(m.Groups[1].Value)).DefaultIfEmpty(0).Max() + 1;
        return $"p-{next:D3}";
    }

    /// <summary>Inserts a block after <paramref name="afterParagraphId"/> (or at the end).</summary>
    public void InsertBlock(DocumentBlock block, string? afterParagraphId)
    {
        FlushPendingMarkdownSync();
        int index = afterParagraphId == null ? -1 : _currentDocument.Blocks.FindIndex(b => b.ParagraphId == afterParagraphId);
        _currentDocument.Blocks.Insert(index < 0 ? _currentDocument.Blocks.Count : index + 1, block);
        AfterStructuralEdit();
    }

    public void ReplaceBlockText(string paragraphId, string text)
    {
        FlushPendingMarkdownSync();
        var block = FindBlock(paragraphId) ?? throw new InvalidOperationException("That paragraph no longer exists.");
        block.Text = text;
        AfterStructuralEdit();
    }

    public void RemoveBlock(string paragraphId)
    {
        FlushPendingMarkdownSync();
        _currentDocument.Blocks.RemoveAll(b => b.ParagraphId == paragraphId);
        _currentDocument.Hunks.RemoveAll(h => h.ParagraphId == paragraphId);
        AfterStructuralEdit();
    }

    private void AfterStructuralEdit()
    {
        FormattingRuleEngine.ApplyFormattingRules(_currentDocument, _activeProfile);
        HasUncommittedChanges = true;
        UpdateRawMarkdownFromDocument();
        RebuildFilteredHunks();
        Paper.RebuildContext();
        DocumentRenderRequested?.Invoke();
        UpdateStatusBar();
        Paper.ScheduleChecks();
    }

    /// <summary>Applies an issue's one-click fix, if the text it was found in is unchanged.</summary>
    public bool ApplyIssueFix(IssueItem item)
    {
        FlushPendingMarkdownSync();
        var issue = item.Issue;
        if (issue.Fix == null || issue.ParagraphId == null || FindBlock(issue.ParagraphId) is not { } block
            || issue.Start < 0 || issue.Start + issue.Length > block.Text.Length)
        {
            return false;
        }
        block.Text = string.Concat(block.Text.AsSpan(0, issue.Start), issue.Fix, block.Text.AsSpan(issue.Start + issue.Length));
        AfterStructuralEdit();
        Paper.ScheduleChecks(immediately: true);
        return true;
    }

    // ---- Files that belong to the paper ------------------------------------------------------------------

    /// <summary>
    /// Saves a diagram's source and the SVG drawn from it (to its .diagram file, or a new figures/&lt;label&gt;.diagram).
    /// Returns both paths in the workspace.
    /// </summary>
    public (string DiagramPath, string SvgPath) SaveDiagram(string? diagramPath, string? label, string source)
    {
        RequireWorkspace();
        var (path, svg, changed) = Core.Diagrams.DiagramFiles.Save(WorkspacePath, diagramPath, label, source);
        if (changed)
        {
            MarkProjectFilesChanged();
        }
        return (path, svg);
    }

    /// <summary>Saves a Mermaid figure's source and the PDF Mermaid drew (from a temporary file, which is removed).</summary>
    public (string SourcePath, string PdfPath) SaveMermaid(string? sourcePath, string? label, string source, string pdfFile)
    {
        RequireWorkspace();
        var saved = Core.Diagrams.MermaidFiles.Save(WorkspacePath, sourcePath, label, source, pdfFile);
        try { File.Delete(pdfFile); } catch (IOException) { }
        MarkProjectFilesChanged();
        return saved;
    }

    /// <summary>Copies a file into a folder of the workspace (unless it is already there) and returns its relative path.</summary>
    public string CopyIntoWorkspace(string sourcePath, string folder)
    {
        RequireWorkspace();
        string full = Path.GetFullPath(sourcePath);
        string root = Path.GetFullPath(WorkspacePath).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            return Path.GetRelativePath(WorkspacePath, full).Replace('\\', '/');
        }
        string directory = Path.Combine(WorkspacePath, folder);
        Directory.CreateDirectory(directory);
        string name = Path.GetFileName(full);
        string target = Path.Combine(directory, name);
        for (int n = 2; File.Exists(target) && !FilesEqual(target, full); n++)
        {
            target = Path.Combine(directory, $"{Path.GetFileNameWithoutExtension(name)}-{n}{Path.GetExtension(name)}");
        }
        File.Copy(full, target, overwrite: true);
        MarkProjectFilesChanged();
        return Path.GetRelativePath(WorkspacePath, target).Replace('\\', '/');
    }

    /// <summary>
    /// Saves a table's cells as CSV: to <paramref name="csvPath"/> (a file outside the workspace is copied in first),
    /// or to a new tables/&lt;label&gt;.csv. Returns the file's path in the workspace.
    /// </summary>
    public string SaveTableCells(string? csvPath, string? label, string csv)
    {
        RequireWorkspace();
        string relative;
        if (!string.IsNullOrWhiteSpace(csvPath))
        {
            string full = Path.IsPathRooted(csvPath) ? csvPath : Path.Combine(WorkspacePath, csvPath.Replace('/', Path.DirectorySeparatorChar));
            relative = File.Exists(full) ? CopyIntoWorkspace(full, PaperSettings.TablesFolder) : Path.GetRelativePath(WorkspacePath, Path.GetFullPath(full)).Replace('\\', '/');
        }
        else
        {
            string stem = (label ?? "tbl:table")["tbl:".Length..];
            relative = $"{PaperSettings.TablesFolder}/{stem}.csv";
            for (int n = 2; File.Exists(Path.Combine(WorkspacePath, relative.Replace('/', Path.DirectorySeparatorChar))); n++)
            {
                relative = $"{PaperSettings.TablesFolder}/{stem}-{n}.csv";
            }
        }
        string target = Path.Combine(WorkspacePath, relative.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(target) || File.ReadAllText(target) != csv)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllText(target, csv, new System.Text.UTF8Encoding(false));
            MarkProjectFilesChanged();
        }
        return relative;
    }

    private static bool FilesEqual(string a, string b)
        => new FileInfo(a).Length == new FileInfo(b).Length && ResultsLibrary.HashFile(a) == ResultsLibrary.HashFile(b);

    public async Task AddResultsFileAsync(string path)
    {
        RequireWorkspace();
        string workspace = WorkspacePath;
        var source = await Task.Run(() => ResultsLibrary.ImportFile(workspace, path));
        MarkProjectFilesChanged();
        AddLog($"Added results '{source.Name}' ({source.File}){(source.SourcePath != null ? $", copied from '{source.SourcePath}'" : string.Empty)}.");
        await Paper.ReloadProjectAsync();
    }

    public async Task<List<string>> RefreshResultsAsync()
    {
        RequireWorkspace();
        string workspace = WorkspacePath;
        var refreshed = await Task.Run(() => ResultsLibrary.RefreshChangedSources(workspace));
        if (refreshed.Count > 0)
        {
            MarkProjectFilesChanged();
            AddLog($"Refreshed results: {string.Join(", ", refreshed)}.");
        }
        await Paper.ReloadProjectAsync();
        return refreshed;
    }

    // ---- Journal exports -------------------------------------------------------------------------------

    /// <summary>
    /// Exports for the target journal. A review copy has line and page numbers (and double spacing when the
    /// journal asks for it) and can be imported back after co-authors review it; a submission copy is clean.
    /// </summary>
    public void ExportForJournal(string outputFilePath, bool reviewCopy, bool trackedChanges)
    {
        FlushPendingMarkdownSync();
        var journal = Paper.Project.Journal;
        var resolved = CriticMarkupSerializer.BakeResolvedHunks(_currentDocument, acceptRemainingPending: !trackedChanges);
        var options = ExportOptions(trackedChanges);
        options.LineNumbers = reviewCopy || journal.LineNumbersForReview;
        options.PageNumbers = true;
        options.DoubleSpacing = journal.DoubleSpacingForReview || reviewCopy;
        options.RoundTripMarkers = reviewCopy;
        DocxExportService.Export(resolved, outputFilePath, options);
        AddLog($"Exported the {(reviewCopy ? "review copy" : "submission copy")} for {journal.Name} to '{outputFilePath}'.");
    }

    /// <summary>The highlights as their own document (Elsevier asks for them as a separate file).</summary>
    public bool ExportHighlights(string outputFilePath)
    {
        FlushPendingMarkdownSync();
        var bullets = new Core.Checks.ManuscriptStructure(_currentDocument.Blocks).Highlights();
        if (bullets == null || bullets.Count == 0)
        {
            return false;
        }
        var doc = new ManuscriptDocument { Title = "Highlights" };
        doc.Blocks.Add(new DocumentBlock { ParagraphId = "h-000", Role = BlockStyleRole.Heading1, Text = "Highlights" });
        doc.Blocks.AddRange(bullets.Select((b, i) => new DocumentBlock { ParagraphId = $"h-{i + 1:D3}", Text = "• " + b.Text }));
        var profile = _activeProfile.Clone();
        profile.AutoNumberHeadings = false;
        profile.FirstLineIndentInches = 0;
        DocxExportService.Export(doc, outputFilePath, new DocxExportOptions { Profile = profile, RoundTripMarkers = false, IncludeBibliography = false });
        AddLog($"Exported the highlights to '{outputFilePath}'.");
        return true;
    }

    /// <summary>Inserts the journal's template for a section it requires, before the references (or at the end).</summary>
    public void InsertRequiredSection(Core.Journals.RequiredSection section)
    {
        FlushPendingMarkdownSync();
        var references = _currentDocument.Blocks.FindIndex(b => b.Role == BlockStyleRole.Heading1
            && Regex.IsMatch(ScholarlyBlocks.StripHeadingLabel(b.Text).Trim(), "^(references|bibliography|literature cited)$", RegexOptions.IgnoreCase));
        int at = references < 0 ? _currentDocument.Blocks.Count : references;
        int next = int.Parse(NewParagraphId()[2..]);
        var blocks = new List<DocumentBlock> { new() { ParagraphId = $"p-{next++:D3}", Role = BlockStyleRole.Heading1, Text = section.Heading } };
        foreach (string line in (section.Template ?? $"[{section.Notes}]").Split('\n').Where(l => l.Trim().Length > 0))
        {
            blocks.Add(new DocumentBlock { ParagraphId = $"p-{next++:D3}", Role = BlockStyleRole.BodyParagraph, Text = line.Trim() });
        }
        _currentDocument.Blocks.InsertRange(at, blocks);
        AfterStructuralEdit();
        AddLog($"Added the “{section.Heading}” section for {Paper.Project.Journal.Name}.");
    }

    // ---- Submissions and revisions -----------------------------------------------------------------

    public async Task<Dictionary<string, string>> GetSubmissionTagsAsync()
    {
        var repo = RequireWorkspace();
        return await Task.Run(() =>
        {
            lock (repo.SyncRoot)
            {
                return repo.GetTags().Where(t => t.Key.StartsWith("submitted-", StringComparison.Ordinal)).ToDictionary(t => t.Key, t => t.Value);
            }
        });
    }

    /// <summary>Tags the latest commit as a submitted version ("submitted-v1", "submitted-v2", …), synced to GitHub.</summary>
    public async Task<string> MarkSubmittedAsync()
    {
        var repo = RequireWorkspace();
        FlushPendingMarkdownSync();
        if (HasUncommittedChanges)
        {
            throw new InvalidOperationException("Commit your changes first, so the tag marks exactly what was submitted.");
        }
        string tag = string.Empty;
        await RunBusyAsync("Marking as submitted...", async () =>
        {
            tag = await Task.Run(() =>
            {
                lock (repo.SyncRoot)
                {
                    var tags = repo.GetTags();
                    int n = 1;
                    while (tags.ContainsKey($"submitted-v{n}"))
                    {
                        n++;
                    }
                    string name = $"submitted-v{n}";
                    repo.CreateTag(name, repo.GetHeadCommitSha());
                    return name;
                }
            });
            AddLog($"Marked the current version as submitted ({tag}).");
            // Record where every paragraph falls in the review copy, so reviewers' line numbers can be found later.
            BusyText = "Recording the line numbers of the submitted version...";
            if (await BuildLineMapCoreAsync(repo, tag) is { } map)
            {
                AddLog($"Recorded the line numbers of {tag} ({map.Paragraphs.LastOrDefault()?.AbsoluteLastLine ?? 0} lines).");
            }
            await ReloadCoreAsync(repo);
        });
        return tag;
    }

    /// <summary>
    /// Exports what a revision is resubmitted with: the revised manuscript (review copy with line numbers), the
    /// marked-up manuscript (every change since the submitted version as a tracked change, or highlighted), and
    /// the response letter with the location of each change in the revised manuscript, as Word lays it out.
    /// </summary>
    public async Task<RevisionPackage> ExportRevisionPackageAsync(string folder, ReviewRound round, bool highlightChanges)
    {
        var repo = RequireWorkspace();
        FlushPendingMarkdownSync();
        string submittedRef = round.SubmittedCommit ?? round.SubmittedTag
            ?? throw new InvalidOperationException("Choose which submitted version the reviewers read first.");

        var revised = CriticMarkupSerializer.BakeResolvedHunks(_currentDocument, acceptRemainingPending: true);
        var profile = _activeProfile.Clone();
        var journal = Paper.Project.Journal;
        string title = string.IsNullOrWhiteSpace(DocumentTitle) ? WorkspaceName : DocumentTitle;
        string baseName = SafeFileName(title);
        string author = AuthorName;
        var project = Paper.Project;
        string workspace = WorkspacePath;

        RevisionPackage? package = null;
        await RunBusyAsync("Preparing the revision...", async () =>
        {
            package = await Task.Run(() =>
            {
                ManuscriptDocument submitted;
                lock (repo.SyncRoot)
                {
                    string sha = round.SubmittedCommit ?? repo.GetTags().GetValueOrDefault(submittedRef) ?? submittedRef;
                    submitted = repo.LoadDocumentAtCommit(sha);
                }

                var formatted = revised.Clone();
                FormattingRuleEngine.ApplyFormattingRules(formatted, profile);
                var context = ScholarlyContext.Build(formatted.Blocks, project);
                Directory.CreateDirectory(folder);

                string revisedPath = Path.Combine(folder, $"{baseName} - revised manuscript.docx");
                var bookmarks = new Dictionary<string, string>(StringComparer.Ordinal);
                DocxExportService.Export(revised, revisedPath, new DocxExportOptions
                {
                    Profile = profile,
                    Scholarly = context,
                    WorkspacePath = workspace,
                    LineNumbers = true,
                    PageNumbers = true,
                    DoubleSpacing = journal.DoubleSpacingForReview,
                    RoundTripMarkers = false,
                    ParagraphBookmarks = bookmarks
                });

                var locations = RevisionDocuments.StructuralLocations(formatted);
                var pages = WordLayoutLocator.Locate(revisedPath, bookmarks.Values.ToList(), continuousLineNumbers: true, TimeSpan.FromMinutes(3));
                if (pages != null)
                {
                    foreach (var (paragraphId, bookmark) in bookmarks)
                    {
                        if (pages.TryGetValue(bookmark, out var where))
                        {
                            locations[paragraphId] = where.ToString();
                        }
                    }
                }

                string markedPath = Path.Combine(folder, $"{baseName} - marked-up manuscript.docx");
                var marked = RevisionDocuments.BuildMarkedUpManuscript(submitted, revised, author, DateTimeOffset.UtcNow);
                DocxExportService.Export(marked, markedPath, new DocxExportOptions
                {
                    Profile = profile,
                    Scholarly = context,
                    WorkspacePath = workspace,
                    IncludeTrackedChanges = !highlightChanges,
                    HighlightChanges = highlightChanges,
                    LineNumbers = true,
                    PageNumbers = true,
                    DoubleSpacing = journal.DoubleSpacingForReview,
                    RoundTripMarkers = false
                });

                string letterPath = Path.Combine(folder, $"{baseName} - response to reviewers.docx");
                var letter = RevisionDocuments.BuildResponseLetter(round, submitted, revised, locations, title);
                var letterProfile = profile.Clone();
                letterProfile.LineSpacingMultiplier = 1.15;
                letterProfile.FirstLineIndentInches = 0;
                letterProfile.AutoNumberHeadings = false;
                letterProfile.ParagraphSpacingAfterPt = 8;
                DocxExportService.Export(letter, letterPath, new DocxExportOptions
                {
                    Profile = letterProfile,
                    Scholarly = context,
                    WorkspacePath = workspace,
                    RoundTripMarkers = false,
                    IncludeBibliography = false,
                    PageNumbers = true
                });
                return new RevisionPackage(revisedPath, markedPath, letterPath, pages != null);
            });
            AddLog($"Exported the revision for {round.Name} to '{folder}'{(package!.LocationsFromWord ? " (page and line numbers from Word)" : " (locations by section; Word was not available for page and line numbers)")}.");
        });
        return package!;
    }

    // ---- Line numbers of submitted versions --------------------------------------------------------------

    /// <summary>
    /// The line map of a submitted version (where each paragraph fell in its review copy), built with Word
    /// when it is not recorded yet. Null when Word is not available.
    /// </summary>
    public async Task<SubmittedLineMap?> GetLineMapAsync(string version)
    {
        var repo = RequireWorkspace();
        if (SubmittedLineMap.Load(WorkspacePath, version) is { } existing)
        {
            return existing;
        }
        SubmittedLineMap? map = null;
        await RunBusyAsync("Laying out the submitted version in Word...", async () => map = await BuildLineMapCoreAsync(repo, version));
        return map;
    }

    private async Task<SubmittedLineMap?> BuildLineMapCoreAsync(Core.Git.GitRepositoryService repo, string version)
    {
        if (!WordLayoutLocator.IsWordInstalled)
        {
            return null;
        }
        var profile = _activeProfile.Clone();
        var project = Paper.Project;
        string workspace = WorkspacePath;
        var map = await Task.Run(() =>
        {
            ManuscriptDocument doc;
            string sha;
            lock (repo.SyncRoot)
            {
                sha = repo.GetTags().GetValueOrDefault(version) ?? version;
                doc = repo.LoadDocumentAtCommit(sha);
            }
            var resolved = CriticMarkupSerializer.BakeResolvedHunks(doc, acceptRemainingPending: true);
            var formatted = resolved.Clone();
            FormattingRuleEngine.ApplyFormattingRules(formatted, profile);
            var context = ScholarlyContext.Build(formatted.Blocks, project);

            // Laid out exactly as Export Review Copy writes it.
            string temp = Path.Combine(Path.GetTempPath(), $"RevisionsEditor-{Guid.NewGuid():N}.docx");
            try
            {
                var bookmarks = new Dictionary<string, string>(StringComparer.Ordinal);
                DocxExportService.Export(resolved, temp, new DocxExportOptions
                {
                    Profile = profile,
                    Scholarly = context,
                    WorkspacePath = workspace,
                    LineNumbers = true,
                    PageNumbers = true,
                    DoubleSpacing = true,
                    RoundTripMarkers = true,
                    ParagraphBookmarks = bookmarks
                });
                var pages = WordLayoutLocator.Locate(temp, bookmarks.Values.ToList(), continuousLineNumbers: true, TimeSpan.FromMinutes(3));
                if (pages == null)
                {
                    return null;
                }
                var result = new SubmittedLineMap { Version = version, Commit = sha };
                foreach (var block in resolved.Blocks)
                {
                    if (bookmarks.TryGetValue(block.ParagraphId, out var name) && pages.TryGetValue(name, out var where))
                    {
                        result.Paragraphs.Add(new LineSpan(block.ParagraphId, where.Page, where.FirstLine, where.EndPage, where.LastLine, where.AbsoluteFirstLine, where.AbsoluteLastLine));
                    }
                }
                result.Save(workspace);
                return result;
            }
            finally
            {
                File.Delete(temp);
            }
        });
        if (map != null)
        {
            MarkProjectFilesChanged();
        }
        return map;
    }

    /// <summary>
    /// The paragraph of the current manuscript at a line of a submitted version. A paragraph removed since then
    /// gives the next one that still exists.
    /// </summary>
    public (string ParagraphId, double Fraction, bool Exact)? FindSubmittedLine(SubmittedLineMap map, int line, int? page)
    {
        var span = map.Find(line, page);
        if (span == null)
        {
            return null;
        }
        int index = map.Paragraphs.IndexOf(span);
        for (int i = index; i < map.Paragraphs.Count; i++)
        {
            if (FindBlock(map.Paragraphs[i].ParagraphId) != null)
            {
                return (map.Paragraphs[i].ParagraphId, i == index ? SubmittedLineMap.Fraction(span, line, page) : 0, i == index);
            }
        }
        return null;
    }

    /// <summary>Statistics of the manuscript as it is now (pending changes accepted).</summary>
    public Core.Checks.ManuscriptStatistics ComputeStatistics()
    {
        FlushPendingMarkdownSync();
        Paper.RebuildContext();
        return Paper.Statistics!;
    }

    // ---- Title and typesetting ------------------------------------------------------------------------

    /// <summary>Sets the manuscript's title (adding a Title paragraph at the top when there is none).</summary>
    public void SetTitle(string title)
    {
        FlushPendingMarkdownSync();
        var block = _currentDocument.Blocks.FirstOrDefault(b => b.Role == BlockStyleRole.Title);
        if (block == null)
        {
            _currentDocument.Blocks.Insert(0, new DocumentBlock { ParagraphId = NewParagraphId(), Role = BlockStyleRole.Title, Text = title });
        }
        else
        {
            block.Text = title;
        }
        DocumentTitle = title;
        AfterStructuralEdit();
    }

    /// <summary>The LaTeX source of the manuscript as it is now, with the files it includes.</summary>
    public Core.Latex.LatexDocument BuildLatex(Core.Latex.LatexTemplate template, bool lineNumbers, Func<string, string, string?>? convertImage, bool draftNotes = false)
    {
        FlushPendingMarkdownSync();
        Paper.SaveFrontMatter();
        Paper.RebuildContext();
        return Core.Latex.LatexExportService.Build(_currentDocument, Paper.Context!, _activeProfile, new Core.Latex.LatexExportOptions
        {
            Template = template,
            LineNumbers = lineNumbers,
            JournalName = Paper.Project.Journal.Name,
            WorkspacePath = WorkspacePath,
            ConvertImage = convertImage,
            IncludeDraftNotes = draftNotes
        });
    }

    /// <summary>Writes the LaTeX source and its figures to a folder (for journals that take LaTeX submissions).</summary>
    public string ExportLatexSource(string folder, Core.Latex.LatexTemplate template, bool lineNumbers, Func<string, string, string?>? convertImage, bool draftNotes = false)
    {
        var latex = BuildLatex(template, lineNumbers, convertImage, draftNotes);
        string tex = Core.Latex.LatexExportService.WriteTo(latex, folder);
        AddLog($"Exported the LaTeX source to '{tex}'.");
        foreach (string warning in latex.Warnings)
        {
            AddLog(warning);
        }
        return tex;
    }

    /// <summary>
    /// Typesets the manuscript with LaTeX into <paramref name="pdfPath"/>. The source is written to
    /// <paramref name="sourceFolder"/> (kept) or a temporary folder.
    /// </summary>
    public async Task<Core.Latex.LatexCompileResult> ExportPdfAsync(string pdfPath, Core.Latex.LatexEngine engine, Core.Latex.LatexTemplate template, bool lineNumbers,
        string? sourceFolder, Func<string, string, string?>? convertImage, bool draftNotes = false)
    {
        RequireWorkspace();
        var latex = BuildLatex(template, lineNumbers, convertImage, draftNotes);
        string folder = sourceFolder ?? Path.Combine(Path.GetTempPath(), $"RevisionsEditor-latex-{Guid.NewGuid():N}");
        Core.Latex.LatexCompileResult result = new(false, null, string.Empty, []);
        await RunBusyAsync("Typesetting with LaTeX (the first time, the LaTeX packages are downloaded)...", async () =>
        {
            string tex = await Task.Run(() => Core.Latex.LatexExportService.WriteTo(latex, folder));
            result = await Core.Latex.LatexCompiler.CompileAsync(tex, engine, TimeSpan.FromMinutes(15));
            if (result.Success)
            {
                File.Copy(result.PdfPath!, pdfPath, overwrite: true);
                AddLog($"Typeset the manuscript with {engine.Name} into '{pdfPath}'.");
            }
            else
            {
                AddLog($"Typesetting failed: {string.Join(" | ", result.Errors.Take(3))}");
            }
            foreach (string warning in latex.Warnings)
            {
                AddLog(warning);
            }
        });
        if (sourceFolder == null && result.Success)
        {
            try
            {
                Directory.Delete(folder, recursive: true);
            }
            catch (IOException)
            {
            }
        }
        return result with { PdfPath = result.Success ? pdfPath : null };
    }

    private static string SafeFileName(string name)
    {
        foreach (char c in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(c, ' ');
        }
        name = name.Trim();
        return name.Length == 0 ? "Manuscript" : name.Length > 60 ? name[..60].TrimEnd() : name;
    }
}
