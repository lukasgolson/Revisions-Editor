using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Threading;
using RevisionsEditor.Core.Bibliography;
using RevisionsEditor.Core.Checks;
using RevisionsEditor.Core.Formatting;
using RevisionsEditor.Core.Journals;
using RevisionsEditor.Core.Models;
using RevisionsEditor.Core.Reviews;
using RevisionsEditor.Core.Scholarly;
using RevisionsEditor.Core.Serialization;

namespace RevisionsEditor.App.ViewModels;

/// <summary>One problem in the Checks pane.</summary>
public sealed class IssueItem
{
    public IssueItem(ManuscriptIssue issue, string location)
    {
        Issue = issue;
        Location = location;
    }

    public ManuscriptIssue Issue { get; }
    public string Location { get; }
    public string Category => Issue.Category;
    public string Message => Issue.Message;
    public IssueSeverity Severity => Issue.Severity;
    public string SeverityGlyph => Issue.Severity switch
    {
        IssueSeverity.Error => "",
        IssueSeverity.Warning => "",
        _ => ""
    };
    public string SeverityText => Issue.Severity.ToString();
    public bool CanFix => Issue.Fix != null && Issue.ParagraphId != null && Issue.Start >= 0;
    public string FixLabel => Issue.FixLabel ?? (Issue.Fix != null ? $"Change to “{Issue.Fix}”" : string.Empty);
}

/// <summary>A row of the title page's author table.</summary>
public sealed class AuthorRow : INotifyPropertyChanged
{
    private string _name = string.Empty, _affiliations = "1", _email = string.Empty, _orcid = string.Empty;
    private bool _corresponding, _equal;

    public string Name { get => _name; set => Set(ref _name, value); }
    public string Affiliations { get => _affiliations; set => Set(ref _affiliations, value); }
    public string Email { get => _email; set => Set(ref _email, value); }
    public string Orcid { get => _orcid; set => Set(ref _orcid, value); }
    public bool Corresponding { get => _corresponding; set => Set(ref _corresponding, value); }
    public bool EqualContribution { get => _equal; set => Set(ref _equal, value); }

    public FrontMatterAuthor ToModel() => new()
    {
        Name = Name.Trim(), Affiliations = Affiliations.Trim(), Email = Email.Trim(), Orcid = Orcid.Trim(),
        Corresponding = Corresponding, EqualContribution = EqualContribution
    };

    public static AuthorRow From(FrontMatterAuthor a) => new()
    {
        _name = a.Name, _affiliations = a.Affiliations, _email = a.Email, _orcid = a.Orcid, _corresponding = a.Corresponding, _equal = a.EqualContribution
    };

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (!EqualityComparer<T>.Default.Equals(field, value))
        {
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>A row of the title page's affiliation table.</summary>
public sealed class AffiliationRow : INotifyPropertyChanged
{
    private int _number = 1;
    private string _text = string.Empty;

    public int Number { get => _number; set { if (_number != value) { _number = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Number))); } } }
    public string Text { get => _text; set { if (_text != value) { _text = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Text))); } } }

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>A bibliography entry in the References pane.</summary>
public sealed class ReferenceItem : INotifyPropertyChanged
{
    public ReferenceItem(BibEntry entry, int citations)
    {
        Entry = entry;
        Citations = citations;
    }

    public BibEntry Entry { get; }
    public string Key => Entry.Key;
    public string Authors => Entry.Authors.Count switch
    {
        0 => string.Empty,
        1 => Entry.Authors[0].FamilyWithParticle,
        2 => $"{Entry.Authors[0].FamilyWithParticle} and {Entry.Authors[1].FamilyWithParticle}",
        _ => $"{Entry.Authors[0].FamilyWithParticle} et al."
    };
    public string Year => Entry.Year ?? string.Empty;
    public string Title => Core.Diffing.DiffEngineService.ToGoogleDocsText(Entry.Title ?? string.Empty);
    public string Container => Entry.ShortContainer ?? Entry.Container ?? string.Empty;
    public string Doi => Entry.Doi ?? string.Empty;
    public int Citations { get; }
    public string CitationsText => Citations == 0 ? "not cited" : Citations == 1 ? "cited once" : $"cited {Citations} times";

    private string _onlineStatus = string.Empty;
    /// <summary>Result of the last online check (resolves, retracted, …).</summary>
    public string OnlineStatus
    {
        get => _onlineStatus;
        set { _onlineStatus = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(OnlineStatus))); }
    }

    public string SearchText => $"{Key} {Authors} {Year} {Title} {Container} {Doi}";

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>A figure, table or equation in the Figures and Data pane.</summary>
public sealed record NumberedItem(string Kind, string Number, string Label, string Caption, string File, string ParagraphId, bool FileMissing)
{
    public string Title => $"{Kind} {Number}";
    public string Reference => Label.Length > 0 ? "@" + Label : "(no label)";
}

/// <summary>A results file in the Figures and Data pane.</summary>
public sealed record ResultsSourceItem(string Name, string File, string Origin, bool Stale, string? Error)
{
    public string Status => Error != null ? "Cannot be read" : Stale ? "Analysis output changed: refresh" : "Up to date";
}

public sealed record ResultValueItem(string Key, string Value)
{
    public string Placeholder => $"{{{{{Key}}}}}";
}

/// <summary>A reviewer comment and the author's response, editable in the Reviewers pane.</summary>
public sealed class ReviewCommentItem : INotifyPropertyChanged
{
    private readonly Action _changed;

    public ReviewCommentItem(ReviewComment model, Action changed, Func<string, string> describeParagraph)
    {
        Model = model;
        _changed = changed;
        LinkedParagraphs = string.Join("\n", model.ParagraphIds.Select(describeParagraph));
    }

    public ReviewComment Model { get; }
    public string Id => Model.Id;
    public string Reviewer => Model.Reviewer;
    public string Text => Model.Text;
    public string ShortText => Model.Text.Length > 140 ? Model.Text[..140].TrimEnd() + "…" : Model.Text;
    public string LinkedParagraphs { get; }
    /// <summary>Lines of the submitted version the comment refers to ("line 120", "L45–50").</summary>
    public IReadOnlyList<LineReference> LineReferences => Core.Reviews.LineReferences.Find(Model.Text);

    public string LinkSummary => Model.ParagraphIds.Count == 0 ? "No changes linked" : Model.ParagraphIds.Count == 1 ? "1 paragraph linked" : $"{Model.ParagraphIds.Count} paragraphs linked";

    public string Response
    {
        get => Model.Response;
        set
        {
            if (Model.Response != value)
            {
                Model.Response = value;
                if (value.Trim().Length > 0 && Model.Status == ReviewCommentStatus.Open)
                {
                    Model.Status = Model.ParagraphIds.Count > 0 ? ReviewCommentStatus.Addressed : ReviewCommentStatus.Answered;
                    OnPropertyChanged(nameof(Status));
                    OnPropertyChanged(nameof(StatusText));
                }
                OnPropertyChanged();
                _changed();
            }
        }
    }

    public ReviewCommentStatus Status
    {
        get => Model.Status;
        set
        {
            if (Model.Status != value)
            {
                Model.Status = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(StatusText));
                _changed();
            }
        }
    }

    public string StatusText => Model.Status.ToString();

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>
/// The paper beyond its text: target journal and its checks, bibliography, figures, tables and linked
/// results, and the responses to reviewers. Kept up to date as the manuscript is edited.
/// </summary>
public sealed partial class PaperViewModel : INotifyPropertyChanged
{
    private static readonly TimeSpan CheckDelay = TimeSpan.FromMilliseconds(900);

    private readonly MainViewModel _main;
    private readonly DispatcherTimer _checkTimer;
    private readonly List<FileSystemWatcher> _watchers = [];
    private DispatcherTimer? _reloadTimer;
    private int _checkRequest;
    private List<ManuscriptIssue> _allIssues = [];

    public PaperViewModel(MainViewModel main)
    {
        _main = main;
        _checkTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = CheckDelay };
        _checkTimer.Tick += (_, _) =>
        {
            _checkTimer.Stop();
            _ = RunChecksAsync();
        };
    }

    public ScholarlyProject Project { get; private set; } = ScholarlyProject.Empty;
    public ScholarlyContext? Context { get; private set; }
    /// <summary>Word counts and other statistics of the manuscript as published.</summary>
    public ManuscriptStatistics? Statistics { get; private set; }
    public ReviewStore Reviews { get; private set; } = new();

    public IReadOnlyList<JournalProfile> Journals => JournalProfiles.All;
    public IReadOnlyList<CitationStyle> CitationStyleOptions => CitationStyles.All;

    public RangeObservableCollection<IssueItem> Issues { get; } = [];
    public RangeObservableCollection<ReferenceItem> References { get; } = [];
    public RangeObservableCollection<NumberedItem> NumberedItems { get; } = [];
    public RangeObservableCollection<ResultsSourceItem> ResultsSources { get; } = [];
    public RangeObservableCollection<ResultValueItem> ResultValues { get; } = [];
    public RangeObservableCollection<ReviewRound> Rounds { get; } = [];
    public RangeObservableCollection<ReviewCommentItem> Comments { get; } = [];
    public ObservableCollection<string> IssueFilters { get; } = ["All", ManuscriptChecks.Submission, ManuscriptChecks.Placeholders, ManuscriptChecks.Citations, ManuscriptChecks.CrossReferences, ManuscriptChecks.Numbers, ManuscriptChecks.Style];

    /// <summary>Raised when figure numbers or captions may have changed, so the page is redrawn.</summary>
    public event Action? StructureChanged;

    // ---- Loading -----------------------------------------------------------------------------------

    /// <summary>Loaded on the background thread together with the rest of the workspace.</summary>
    public sealed record Snapshot(ScholarlyProject Project, ReviewStore Reviews);

    public static Snapshot LoadSnapshot(string workspacePath) => new(ScholarlyProject.Load(workspacePath), ReviewStore.Load(workspacePath));

    public void Apply(Snapshot snapshot)
    {
        Project = snapshot.Project;
        Reviews = snapshot.Reviews;
        OnPropertyChanged(nameof(Project));
        OnPropertyChanged(nameof(SelectedJournal));
        OnPropertyChanged(nameof(SelectedCitationStyle));
        OnPropertyChanged(nameof(JournalSummary));
        OnPropertyChanged(nameof(BibliographyStatus));
        RefreshRounds();
        LoadFrontMatter();
        LoadNotes();
        RebuildContext();
        WatchFiles();
        ScheduleChecks(immediately: true);
    }

    public void Clear()
    {
        FlushFrontMatter();
        StopWatching();
        Project = ScholarlyProject.Empty;
        Reviews = new ReviewStore();
        Context = null;
        Statistics = null;
        _allIssues = [];
        Issues.ReplaceAll([]);
        Placeholders.ReplaceAll([]);
        _notes = [];
        Notes.ReplaceAll([]);
        References.ReplaceAll([]);
        NumberedItems.ReplaceAll([]);
        ResultsSources.ReplaceAll([]);
        ResultValues.ReplaceAll([]);
        Rounds.ReplaceAll([]);
        Comments.ReplaceAll([]);
        _frontMatterTimer?.Stop();
        Project = ScholarlyProject.Empty;
        LoadFrontMatter();
        SelectedRound = null;
        OnPropertyChanged(nameof(IssueSummary));
        OnPropertyChanged(nameof(JournalSummary));
        OnPropertyChanged(nameof(BibliographyStatus));
    }

    /// <summary>Reloads the bibliography, results and settings from disk (after they change).</summary>
    public async Task ReloadProjectAsync()
    {
        if (!_main.HasWorkspace)
        {
            return;
        }
        FlushFrontMatter(); // keep edits still waiting to be saved
        string path = _main.WorkspacePath;
        var project = await Task.Run(() => ScholarlyProject.Load(path));
        if (_main.WorkspacePath != path)
        {
            return;
        }
        Project = project;
        OnPropertyChanged(nameof(Project));
        OnPropertyChanged(nameof(SelectedJournal));
        OnPropertyChanged(nameof(SelectedCitationStyle));
        OnPropertyChanged(nameof(JournalSummary));
        OnPropertyChanged(nameof(BibliographyStatus));
        LoadNotes(); // synced from another computer, perhaps
        RebuildContext();
        StructureChanged?.Invoke();
        ScheduleChecks(immediately: true);
    }

    // The bibliography is often rewritten by Zotero's auto-export, and results by an analysis run.
    private void WatchFiles()
    {
        StopWatching();
        if (!_main.HasWorkspace)
        {
            return;
        }
        string root = _main.WorkspacePath;
        void Watch(string directory, string filter, bool subdirectories)
        {
            if (!Directory.Exists(directory))
            {
                return;
            }
            var watcher = new FileSystemWatcher(directory, filter) { IncludeSubdirectories = subdirectories, NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size };
            FileSystemEventHandler handler = (_, _) => Application.Current?.Dispatcher.BeginInvoke(ScheduleReload);
            watcher.Changed += handler;
            watcher.Created += handler;
            watcher.Deleted += handler;
            watcher.Renamed += (_, _) => Application.Current?.Dispatcher.BeginInvoke(ScheduleReload);
            watcher.EnableRaisingEvents = true;
            _watchers.Add(watcher);
        }
        string bib = Project.BibliographyPath;
        Watch(Path.GetDirectoryName(bib) ?? root, Path.GetFileName(bib), false);
        Watch(Path.Combine(root, ResultsLibrary.ResultsFolder), "*.*", true);
        Watch(Path.Combine(root, PaperSettings.TablesFolder), "*.csv", true);
    }

    private void StopWatching()
    {
        foreach (var watcher in _watchers)
        {
            watcher.Dispose();
        }
        _watchers.Clear();
    }

    private void ScheduleReload()
    {
        _reloadTimer ??= new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(800) };
        _reloadTimer.Tick -= OnReloadTick;
        _reloadTimer.Tick += OnReloadTick;
        _reloadTimer.Stop();
        _reloadTimer.Start();
    }

    private async void OnReloadTick(object? sender, EventArgs e)
    {
        _reloadTimer?.Stop();
        _main.MarkProjectFilesChanged();
        await ReloadProjectAsync();
    }

    // ---- Context and checks -------------------------------------------------------------------------

    /// <summary>Recomputes numbering, citations and the panes that depend on them (fast; on the UI thread).</summary>
    public void RebuildContext()
    {
        if (!_main.HasWorkspace)
        {
            Context = null;
            return;
        }
        var formatted = _main.CurrentDocument.Clone();
        FormattingRuleEngine.ApplyFormattingRules(formatted, _main.ActiveProfile);
        Context = ScholarlyContext.Build(formatted.Blocks, Project);
        OnPropertyChanged(nameof(Context));
        Statistics = ManuscriptStatistics.Compute(formatted.Blocks, Context, Project.Journal);
        OnPropertyChanged(nameof(Statistics));
        _main.UpdateStatusBar();

        var citationCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var block in formatted.Blocks)
        {
            foreach (var citation in CitationSyntax.Find(block.Text))
            {
                foreach (var item in citation.Items)
                {
                    citationCounts[item.Key] = citationCounts.GetValueOrDefault(item.Key) + 1;
                }
            }
        }
        var previous = References.ToDictionary(r => r.Key, r => r.OnlineStatus, StringComparer.Ordinal);
        References.ReplaceAll((Project.Bibliography?.Entries ?? [])
            .Select(e => new ReferenceItem(e, citationCounts.GetValueOrDefault(e.Key)) { OnlineStatus = previous.GetValueOrDefault(e.Key) ?? string.Empty })
            .Where(r => MatchesFilter(r)));

        var items = new List<NumberedItem>();
        foreach (var target in Context.CrossReferences.NumberedItems)
        {
            var block = formatted.Blocks.First(b => b.ParagraphId == target.ParagraphId);
            string final = CriticMarkupSerializer.ResolveCriticMarkupText(block.Text, MarkupDisplayMode.NoMarkupFinal);
            (string kind, string caption, string file, string? label) = block.Role switch
            {
                BlockStyleRole.Figure when ScholarlyBlocks.ParseFigure(final) is { } f => ("Figure", f.Caption, f.ImagePath, f.Label),
                BlockStyleRole.Table when ScholarlyBlocks.ParseTable(final) is { } t => ("Table", t.Caption, t.CsvPath ?? string.Empty, t.Label),
                BlockStyleRole.Equation when ScholarlyBlocks.ParseEquation(final) is { } q => ("Equation", Core.Equations.LatexToUnicode.Convert(q.Latex), string.Empty, q.Label),
                _ => ("Item", final, string.Empty, null)
            };
            bool missing = file.Length > 0 && !File.Exists(Path.Combine(_main.WorkspacePath, file.Replace('/', Path.DirectorySeparatorChar)));
            items.Add(new NumberedItem(kind, target.Number, label ?? string.Empty, Core.Diffing.DiffEngineService.ToGoogleDocsText(caption), file, block.ParagraphId, missing));
        }
        NumberedItems.ReplaceAll(items);

        string? selectedSource = SelectedResultsSource?.Name;
        ResultsSources.ReplaceAll(Project.Results.SourceNames.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).Select(n =>
        {
            var info = Project.Results.SourceInfo(n)!;
            string origin = info.SourcePath != null ? $"copied from {info.SourcePath}" : "in the workspace";
            return new ResultsSourceItem(n, info.File, origin, Project.Results.IsStale(n), Project.Results.LoadError(n));
        }));
        SelectedResultsSource = ResultsSources.FirstOrDefault(s => s.Name == selectedSource) ?? ResultsSources.FirstOrDefault();
    }

    /// <summary>Re-runs the checks after the typing pause.</summary>
    public void ScheduleChecks(bool immediately = false)
    {
        _checkTimer.Stop();
        if (immediately)
        {
            _ = RunChecksAsync();
        }
        else
        {
            _checkTimer.Start();
        }
    }

    private async Task RunChecksAsync()
    {
        if (!_main.HasWorkspace)
        {
            return;
        }
        int request = ++_checkRequest;
        RebuildContext();
        var context = Context!;
        var document = _main.CurrentDocument.Clone();
        var profile = _main.ActiveProfile.Clone();
        var settings = Project.Settings;

        try
        {
            var issues = await Task.Run(() =>
            {
                FormattingRuleEngine.ApplyFormattingRules(document, profile);
                var found = ManuscriptChecks.Run(document.Blocks, context);
                var options = new StyleCheckOptions();
                foreach (string abbreviation in settings.KnownAbbreviations)
                {
                    options.KnownAbbreviations.Add(abbreviation);
                }
                options.CheckAbbreviations = !settings.DisabledStyleRules.Contains("Abbreviations");
                options.CheckTerminology = !settings.DisabledStyleRules.Contains("Terminology");
                options.CheckUnits = !settings.DisabledStyleRules.Contains("Units");
                options.CheckSpecies = !settings.DisabledStyleRules.Contains("Species");
                options.CheckSoftwareVersions = !settings.DisabledStyleRules.Contains("Software");
                options.CheckNumbers = !settings.DisabledStyleRules.Contains("Numbers");
                options.CheckGeneral = !settings.DisabledStyleRules.Contains("General");
                // Highlights and keywords stand on their own and are not prose to check for style.
                var structure = new ManuscriptStructure(document.Blocks);
                var exempt = (structure.Highlights()?.Select(h => h.Block.ParagraphId) ?? [])
                    .Concat(structure.Keywords().Block is { } keywords ? [keywords.ParagraphId] : [])
                    .ToHashSet(StringComparer.Ordinal);
                found.AddRange(StyleChecker.Check(document.Blocks.Where(b => !exempt.Contains(b.ParagraphId)).ToList(), options));
                return found;
            });
            if (request != _checkRequest)
            {
                return;
            }
            _allIssues = issues;
            ApplyIssueFilter();
        }
        catch (Exception ex)
        {
            _main.AddLog($"Checking the manuscript failed: {ex.Message}");
        }
    }

    private string _issueFilter = "All";
    public string IssueFilter
    {
        get => _issueFilter;
        set
        {
            if (_issueFilter != value)
            {
                _issueFilter = value;
                OnPropertyChanged();
                ApplyIssueFilter();
            }
        }
    }

    private bool _showInfo = true;
    /// <summary>Show suggestions (Info) as well as warnings and errors.</summary>
    public bool ShowInfo
    {
        get => _showInfo;
        set
        {
            if (_showInfo != value)
            {
                _showInfo = value;
                OnPropertyChanged();
                ApplyIssueFilter();
            }
        }
    }

    private void ApplyIssueFilter()
    {
        var dismissed = Project.Settings.DismissedIssues.ToHashSet(StringComparer.Ordinal);
        var blocks = _main.CurrentDocument.Blocks;
        var locations = RevisionDocuments.StructuralLocations(FormattedDocument());
        var visible = _allIssues
            .Where(i => !dismissed.Contains(ManuscriptChecks.Fingerprint(i)))
            .Where(i => _issueFilter == "All" || i.Category == _issueFilter)
            .Where(i => _showInfo || i.Severity != IssueSeverity.Info)
            .OrderByDescending(i => i.Severity)
            .ThenBy(i => i.ParagraphId == null ? -1 : blocks.FindIndex(b => b.ParagraphId == i.ParagraphId))
            .Select(i => new IssueItem(i, i.ParagraphId != null && locations.TryGetValue(i.ParagraphId, out var where) ? where : "Whole paper"))
            .ToList();
        Issues.ReplaceAll(visible);
        OnPropertyChanged(nameof(IssueSummary));
        ApplyPlaceholders(locations);
    }

    public string IssueSummary
    {
        get
        {
            var dismissed = Project.Settings.DismissedIssues.ToHashSet(StringComparer.Ordinal);
            var active = _allIssues.Where(i => !dismissed.Contains(ManuscriptChecks.Fingerprint(i))).ToList();
            int errors = active.Count(i => i.Severity == IssueSeverity.Error);
            int warnings = active.Count(i => i.Severity == IssueSeverity.Warning);
            int info = active.Count(i => i.Severity == IssueSeverity.Info);
            return active.Count == 0 ? "No problems found" : $"{errors} errors, {warnings} warnings, {info} suggestions";
        }
    }

    private ManuscriptDocument FormattedDocument()
    {
        var formatted = _main.CurrentDocument.Clone();
        FormattingRuleEngine.ApplyFormattingRules(formatted, _main.ActiveProfile);
        return formatted;
    }

    public void Dismiss(IssueItem item)
    {
        var settings = Project.Settings;
        string fingerprint = ManuscriptChecks.Fingerprint(item.Issue);
        if (!settings.DismissedIssues.Contains(fingerprint))
        {
            settings.DismissedIssues.Add(fingerprint);
            SaveSettings(settings);
        }
        ApplyIssueFilter();
    }

    public void RestoreDismissed()
    {
        Project.Settings.DismissedIssues.Clear();
        SaveSettings(Project.Settings);
        ApplyIssueFilter();
    }

    // ---- Journal and settings --------------------------------------------------------------------

    public JournalProfile SelectedJournal
    {
        get => Project.Journal;
        set
        {
            if (value != null && value.Id != Project.Journal.Id && _main.HasWorkspace)
            {
                var settings = Project.Settings.Clone();
                settings.JournalId = value.Id;
                settings.CitationStyleOverride = null;
                SaveSettings(settings);
                _ = ReloadProjectAsync();
            }
        }
    }

    public CitationStyle SelectedCitationStyle
    {
        get => Project.Style;
        set
        {
            if (value != null && value.Id != Project.Style.Id && _main.HasWorkspace)
            {
                var settings = Project.Settings.Clone();
                settings.CitationStyleOverride = value.Id == Project.Journal.CitationStyleId ? null : value.Id;
                SaveSettings(settings);
                _ = ReloadProjectAsync();
            }
        }
    }

    private void SaveSettings(PaperSettings settings)
    {
        settings.Save(_main.WorkspacePath);
        _main.MarkProjectFilesChanged();
        Project = new ScholarlyProject
        {
            WorkspacePath = Project.WorkspacePath,
            Settings = settings,
            Journal = Project.Journal,
            Style = Project.Style,
            Library = Project.Library,
            Bibliography = Project.Bibliography,
            BibliographyError = Project.BibliographyError,
            BibliographyExists = Project.BibliographyExists,
            Results = Project.Results,
            FrontMatter = Project.FrontMatter
        };
    }

    public void AddKnownAbbreviation(string abbreviation)
    {
        var settings = Project.Settings.Clone();
        if (!settings.KnownAbbreviations.Contains(abbreviation))
        {
            settings.KnownAbbreviations.Add(abbreviation);
            SaveSettings(settings);
            ScheduleChecks(immediately: true);
        }
    }

    public string JournalSummary
    {
        get
        {
            var j = Project.Journal;
            var lines = new List<string>();
            lines.Add($"{j.Name} ({j.Publisher}). Citation style: {Project.Style.Name}.");
            if (j.AbstractMaxWords != null) lines.Add($"Abstract: at most {j.AbstractMaxWords} words. {j.AbstractNotes}");
            else if (j.AbstractNotes.Length > 0) lines.Add($"Abstract: {j.AbstractNotes}");
            if (j.HighlightsRequired) lines.Add($"Highlights: {j.HighlightsMin}–{j.HighlightsMax} bullets{(j.HighlightMaxCharacters is { } c ? $" of at most {c} characters" : string.Empty)}.");
            if (j.KeywordsMax != null || j.KeywordsMin != null) lines.Add($"{j.KeywordsHeading}: {(j.KeywordsMin != null ? $"{j.KeywordsMin}–" : "up to ")}{j.KeywordsMax}.");
            if (j.LengthLimit != null) lines.Add($"Length: {j.LengthLimit}");
            lines.Add($"Graphical abstract: {j.GraphicalAbstract}. Line numbers for review: {(j.LineNumbersForReview ? "yes" : "not required")}. Double spacing: {(j.DoubleSpacingForReview ? "yes" : "not required")}.");
            foreach (var section in j.RequiredSections)
            {
                lines.Add($"{(section.Required ? "Required" : "If applicable")}: {section.Heading}. {section.Notes}");
            }
            lines.AddRange(j.OtherRequirements);
            lines.Add($"Source: {j.Confidence} Always check the guide for authors before submitting.");
            return string.Join("\n", lines);
        }
    }

    /// <summary>Sections the journal requires that the manuscript does not have yet.</summary>
    public List<RequiredSection> MissingSections()
    {
        var structure = new ManuscriptStructure(_main.CurrentDocument.Blocks);
        return Project.Journal.RequiredSections
            .Where(s => structure.FindSection(s.Heading, s.Aliases) == null)
            .ToList();
    }

    // ---- Title page -------------------------------------------------------------------------------

    private DispatcherTimer? _frontMatterTimer;
    private bool _loadingFrontMatter;
    private bool _frontMatterDirty;

    public ObservableCollection<AuthorRow> Authors { get; } = [];
    public ObservableCollection<AffiliationRow> Affiliations { get; } = [];

    private string _shortTitle = string.Empty, _articleType = string.Empty, _keywordsText = string.Empty, _frontNotes = string.Empty;
    public string ShortTitle { get => _shortTitle; set => SetFront(ref _shortTitle, value); }
    public string ArticleType { get => _articleType; set => SetFront(ref _articleType, value); }
    /// <summary>Keywords separated by semicolons.</summary>
    public string KeywordsText { get => _keywordsText; set => SetFront(ref _keywordsText, value); }
    public string FrontNotes { get => _frontNotes; set => SetFront(ref _frontNotes, value); }
    private string _draftNotes = string.Empty;
    /// <summary>Working notes for the authors, shown in drafts and left out of copies for the journal.</summary>
    public string DraftNotes { get => _draftNotes; set => SetFront(ref _draftNotes, value); }

    /// <summary>The title, edited in the form but kept in the manuscript (it is reviewed like the text).</summary>
    public string PaperTitle
    {
        get => CriticMarkupSerializer.ResolveCriticMarkupText(_main.CurrentDocument.Blocks.FirstOrDefault(b => b.Role == BlockStyleRole.Title)?.Text ?? string.Empty, MarkupDisplayMode.NoMarkupFinal);
        set
        {
            if (value.Trim().Length > 0 && value.Trim() != PaperTitle)
            {
                _main.SetTitle(value.Trim());
                OnPropertyChanged();
            }
        }
    }

    private void LoadFrontMatter()
    {
        _loadingFrontMatter = true;
        try
        {
            var front = Project.FrontMatter;
            foreach (var row in Authors) row.PropertyChanged -= OnFrontMatterRowChanged;
            foreach (var row in Affiliations) row.PropertyChanged -= OnFrontMatterRowChanged;
            Authors.CollectionChanged -= OnFrontMatterRowsChanged;
            Affiliations.CollectionChanged -= OnFrontMatterRowsChanged;
            Authors.Clear();
            Affiliations.Clear();
            foreach (var a in front.Authors) Authors.Add(AuthorRow.From(a));
            foreach (var a in front.Affiliations) Affiliations.Add(new AffiliationRow { Number = a.Number, Text = a.Text });
            foreach (var row in Authors) row.PropertyChanged += OnFrontMatterRowChanged;
            foreach (var row in Affiliations) row.PropertyChanged += OnFrontMatterRowChanged;
            Authors.CollectionChanged += OnFrontMatterRowsChanged;
            Affiliations.CollectionChanged += OnFrontMatterRowsChanged;
            _shortTitle = front.ShortTitle;
            _articleType = front.ArticleType;
            _keywordsText = string.Join("; ", front.Keywords);
            _frontNotes = front.Notes;
            _draftNotes = front.DraftNotes;
            OnPropertyChanged(nameof(DraftNotes));
            OnPropertyChanged(nameof(ShortTitle));
            OnPropertyChanged(nameof(ArticleType));
            OnPropertyChanged(nameof(KeywordsText));
            OnPropertyChanged(nameof(FrontNotes));
            OnPropertyChanged(nameof(PaperTitle));
        }
        finally
        {
            _loadingFrontMatter = false;
        }
    }

    private void OnFrontMatterRowsChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        foreach (INotifyPropertyChanged row in e.NewItems ?? Array.Empty<object>())
        {
            row.PropertyChanged += OnFrontMatterRowChanged;
            if (row is AffiliationRow affiliation && Affiliations.Count > 1 && affiliation.Number == 1)
            {
                affiliation.Number = Affiliations.Where(a => !ReferenceEquals(a, affiliation)).Max(a => a.Number) + 1;
            }
        }
        foreach (INotifyPropertyChanged row in e.OldItems ?? Array.Empty<object>())
        {
            row.PropertyChanged -= OnFrontMatterRowChanged;
        }
        SaveFrontMatterSoon();
    }

    private void OnFrontMatterRowChanged(object? sender, PropertyChangedEventArgs e) => SaveFrontMatterSoon();

    private void SetFront(ref string field, string value, [CallerMemberName] string? name = null)
    {
        if (field != value)
        {
            field = value;
            OnPropertyChanged(name);
            SaveFrontMatterSoon();
        }
    }

    private void SaveFrontMatterSoon()
    {
        if (_loadingFrontMatter || !_main.HasWorkspace)
        {
            return;
        }
        _frontMatterDirty = true;
        _frontMatterTimer ??= new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(600) };
        _frontMatterTimer.Tick -= OnFrontMatterTimer;
        _frontMatterTimer.Tick += OnFrontMatterTimer;
        _frontMatterTimer.Stop();
        _frontMatterTimer.Start();
    }

    private void OnFrontMatterTimer(object? sender, EventArgs e)
    {
        _frontMatterTimer?.Stop();
        SaveFrontMatter();
        // The page shows the title page and the draft notes from the form.
        RebuildContext();
        StructureChanged?.Invoke();
    }

    /// <summary>Saves the title-page form (it is committed and synced with the paper).</summary>
    public void SaveFrontMatter()
    {
        _frontMatterTimer?.Stop();
        _frontMatterDirty = false;
        if (!_main.HasWorkspace)
        {
            return;
        }
        var front = new FrontMatter
        {
            ShortTitle = _shortTitle.Trim(),
            ArticleType = _articleType.Trim(),
            Authors = Authors.Where(a => a.Name.Trim().Length > 0).Select(a => a.ToModel()).ToList(),
            Affiliations = Affiliations.Where(a => a.Text.Trim().Length > 0).Select(a => new FrontMatterAffiliation { Number = a.Number, Text = a.Text.Trim() }).ToList(),
            Keywords = _keywordsText.Split([';', '\n'], StringSplitOptions.RemoveEmptyEntries).Select(k => k.Trim()).Where(k => k.Length > 0).ToList(),
            Notes = _frontNotes.Trim(),
            DraftNotes = _draftNotes.Replace("\r\n", "\n").Trim()
        };
        front.Save(_main.WorkspacePath);
        _main.MarkProjectFilesChanged();
        Project = new ScholarlyProject
        {
            WorkspacePath = Project.WorkspacePath,
            Settings = Project.Settings,
            Journal = Project.Journal,
            Style = Project.Style,
            Library = Project.Library,
            Bibliography = Project.Bibliography,
            BibliographyError = Project.BibliographyError,
            BibliographyExists = Project.BibliographyExists,
            Results = Project.Results,
            FrontMatter = front
        };
        ScheduleChecks();
    }

    /// <summary>Saves edits still waiting for the short pause (before closing or switching workspaces).</summary>
    public void FlushFrontMatter()
    {
        if (_frontMatterDirty)
        {
            SaveFrontMatter();
        }
    }

    /// <summary>Fills the keywords from a "Keywords:" paragraph of the manuscript.</summary>
    public bool ImportKeywordsFromManuscript()
    {
        var (_, keywords) = new ManuscriptStructure(_main.CurrentDocument.Blocks).Keywords();
        if (keywords.Count == 0)
        {
            return false;
        }
        KeywordsText = string.Join("; ", keywords);
        return true;
    }

    // ---- References -----------------------------------------------------------------------------

    public string BibliographyStatus => !_main.HasWorkspace ? string.Empty
        : Project.BibliographyError != null ? $"{Project.Settings.BibliographyFile} could not be read: {Project.BibliographyError}"
        : !Project.BibliographyExists ? $"No {Project.Settings.BibliographyFile} yet. Add references by DOI or from Zotero, or set Better BibTeX to keep this file up to date (Zotero › File › Export Library… › Better BibLaTeX, Keep updated)."
        : $"{Project.Library.Count} references in {Project.Settings.BibliographyFile}";

    private string _referenceFilter = string.Empty;
    public string ReferenceFilter
    {
        get => _referenceFilter;
        set
        {
            if (_referenceFilter != value)
            {
                _referenceFilter = value;
                OnPropertyChanged();
                RebuildContext();
            }
        }
    }

    private bool MatchesFilter(ReferenceItem item)
        => _referenceFilter.Trim().Length == 0
           || _referenceFilter.Split(' ', StringSplitOptions.RemoveEmptyEntries).All(w => item.SearchText.Contains(w, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Appends BibTeX entries to the bibliography and returns their keys. Keys from Zotero are kept
    /// (<paramref name="keepKeys"/>); entries looked up by DOI get a Better BibTeX-style key such as "white2016remote".
    /// </summary>
    public List<string> AddBibTeX(string bibtex, bool keepKeys)
    {
        var parsed = BibTeXParser.Parse(bibtex);
        var existing = new HashSet<string>(Project.Library.Keys, StringComparer.OrdinalIgnoreCase);
        var keys = new List<string>();
        var sb = new System.Text.StringBuilder();
        foreach (var entry in parsed.Entries)
        {
            // Entries already in the bibliography (same DOI) are cited by their existing key.
            var same = Project.Library.Values.FirstOrDefault(e => e.Doi != null && entry.Doi != null && string.Equals(e.Doi, entry.Doi, StringComparison.OrdinalIgnoreCase));
            if (same != null)
            {
                keys.Add(same.Key);
                continue;
            }
            string key = !keepKeys || existing.Contains(entry.Key) || entry.Key.Length == 0
                ? BibTeXWriter.SuggestKey(entry, existing)
                : entry.Key;
            existing.Add(key);
            keys.Add(key);
            string raw = RawEntry(bibtex, entry);
            sb.Append('\n').Append(key == entry.Key ? raw : BibTeXWriter.ReplaceKey(raw, key)).Append('\n');
        }
        if (sb.Length > 0)
        {
            string path = Project.BibliographyPath;
            string current = File.Exists(path) ? File.ReadAllText(path) : string.Empty;
            File.WriteAllText(path, current.TrimEnd() + (current.Length > 0 ? "\n" : string.Empty) + sb.ToString().TrimStart('\n'), new System.Text.UTF8Encoding(false));
            _main.MarkProjectFilesChanged();
        }
        return keys;
    }

    private static string RawEntry(string bibtex, BibEntry entry)
    {
        // The text of one entry: from its "@type{key," to the next entry.
        int start = bibtex.IndexOf("{" + entry.Key, StringComparison.Ordinal);
        start = start < 0 ? 0 : bibtex.LastIndexOf('@', start);
        int next = bibtex.IndexOf("\n@", Math.Max(start, 0) + 1, StringComparison.Ordinal);
        return (next < 0 ? bibtex[Math.Max(start, 0)..] : bibtex[Math.Max(start, 0)..next]).Trim();
    }

    // ---- Results -------------------------------------------------------------------------------

    private ResultsSourceItem? _selectedResultsSource;
    public ResultsSourceItem? SelectedResultsSource
    {
        get => _selectedResultsSource;
        set
        {
            _selectedResultsSource = value;
            OnPropertyChanged();
            ResultValues.ReplaceAll(value == null ? [] : Project.Results.AllValues()
                .Where(v => v.Key.StartsWith(value.Name + ".", StringComparison.OrdinalIgnoreCase))
                .Take(2000)
                .Select(v => new ResultValueItem(v.Key, v.Value)));
        }
    }

    // ---- Reviews ----------------------------------------------------------------------------------

    private ReviewRound? _selectedRound;
    public ReviewRound? SelectedRound
    {
        get => _selectedRound;
        set
        {
            _selectedRound = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(RoundSummary));
            RefreshComments();
        }
    }

    private ReviewCommentItem? _selectedComment;
    public ReviewCommentItem? SelectedComment
    {
        get => _selectedComment;
        set { _selectedComment = value; OnPropertyChanged(); }
    }

    public string RoundSummary => _selectedRound == null
        ? "No review round yet. Paste the reviewers' comments to start one."
        : $"{_selectedRound.Comments.Count} comments, {_selectedRound.OpenCount} still open. Submitted version: {(_selectedRound.SubmittedTag ?? _selectedRound.SubmittedCommit?[..7] ?? "not set")}.";

    private void RefreshRounds()
    {
        string? selected = _selectedRound?.Id;
        Rounds.ReplaceAll(Reviews.Rounds);
        SelectedRound = Reviews.Rounds.FirstOrDefault(r => r.Id == selected) ?? Reviews.Rounds.LastOrDefault();
    }

    public void RefreshComments()
    {
        string? selected = _selectedComment?.Id;
        var locations = RevisionDocuments.StructuralLocations(FormattedDocument());
        string Describe(string id) => locations.TryGetValue(id, out var where) ? where : $"{id} (removed)";
        Comments.ReplaceAll(_selectedRound?.Comments.Select(c => new ReviewCommentItem(c, SaveReviews, Describe)) ?? []);
        SelectedComment = Comments.FirstOrDefault(c => c.Id == selected) ?? Comments.FirstOrDefault();
        OnPropertyChanged(nameof(RoundSummary));
    }

    public void SaveReviews()
    {
        if (!_main.HasWorkspace)
        {
            return;
        }
        Reviews.Save(_main.WorkspacePath);
        _main.MarkProjectFilesChanged();
        OnPropertyChanged(nameof(RoundSummary));
    }

    public ReviewRound AddRound(string name, string manuscriptNumber, string pastedComments, string? submittedTag, string? submittedCommit)
    {
        var round = new ReviewRound
        {
            Name = name,
            ManuscriptNumber = manuscriptNumber,
            SubmittedTag = submittedTag,
            SubmittedCommit = submittedCommit,
            Comments = ReviewCommentParser.Parse(pastedComments)
        };
        Reviews.Rounds.Add(round);
        SaveReviews();
        RefreshRounds();
        SelectedRound = round;
        return round;
    }

    public void AddCommentsToRound(string pastedComments)
    {
        if (_selectedRound == null)
        {
            return;
        }
        foreach (var comment in ReviewCommentParser.Parse(pastedComments))
        {
            // Keep ids unique within the round.
            string id = comment.Id;
            for (int n = 2; _selectedRound.Comments.Any(c => c.Id == id); n++)
            {
                id = $"{comment.Id}-{n}";
            }
            comment.Id = id;
            _selectedRound.Comments.Add(comment);
        }
        SaveReviews();
        RefreshComments();
    }

    public void LinkParagraph(ReviewCommentItem item, string paragraphId)
    {
        if (!item.Model.ParagraphIds.Contains(paragraphId))
        {
            item.Model.ParagraphIds.Add(paragraphId);
            if (item.Model.Status == ReviewCommentStatus.Open || item.Model.Status == ReviewCommentStatus.Answered)
            {
                item.Model.Status = ReviewCommentStatus.Addressed;
            }
            SaveReviews();
            RefreshComments();
        }
    }

    public void UnlinkAll(ReviewCommentItem item)
    {
        item.Model.ParagraphIds.Clear();
        SaveReviews();
        RefreshComments();
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
