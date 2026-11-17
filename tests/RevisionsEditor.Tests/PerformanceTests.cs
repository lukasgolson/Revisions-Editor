using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Threading;
using RevisionsEditor.App;
using RevisionsEditor.App.Rendering;
using RevisionsEditor.Core.Models;
using Xunit;

namespace RevisionsEditor.Tests;

/// <summary>
/// Times the editor's everyday operations on a real manuscript. Runs only when PERF_WORKSPACE names a workspace
/// (it is copied first, so the original is never changed); prints a table of timings.
/// </summary>
public sealed partial class RevisionsEditorTests
{
    [Fact]
    public void Performance_EverydayOperations_OnARealManuscript()
    {
        string? source = Environment.GetEnvironmentVariable("PERF_WORKSPACE");
        if (source == null || !Directory.Exists(source))
        {
            return;
        }
        RunOnStaThread(() => MeasureEverydayOperations(source));
    }

    private static void MeasureEverydayOperations(string source)
    {
        string copy = Path.Combine(Path.GetTempPath(), $"RevEditorPerf_{Guid.NewGuid():N}");
        CopyDirectory(source, copy);
        if (Application.Current == null)
        {
            var app = new App.App();
            app.InitializeComponent();
        }
        MainWindow.UseSavedLayout = false;
        var window = new MainWindow { Left = -20000, Top = -20000, Width = 1440, Height = 900, ShowInTaskbar = false, ShowActivated = false };
        var results = new List<(string Name, double Ms)>();
        void Pump()
        {
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ContextIdle, () => frame.Continue = false);
            Dispatcher.PushFrame(frame);
        }
        double Time(string name, Action action, int repeat = 1)
        {
            action(); // warm-up
            Pump();
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < repeat; i++)
            {
                action();
                Pump();
            }
            double ms = sw.Elapsed.TotalMilliseconds / repeat;
            results.Add((name, ms));
            return ms;
        }
        try
        {
            window.Show();
            var sw = Stopwatch.StartNew();
            RunOnDispatcher(() => window.ViewModel.OpenWorkspaceAsync(copy));
            window.SelectWorkspaceTab(0);
            Pump();
            results.Add(("Open workspace and draw the page (first time)", sw.Elapsed.TotalMilliseconds));

            var editor = (RichTextBox)window.FindName("ManuscriptEditor");
            var paragraph = editor.Document.Blocks.OfType<Paragraph>().Skip(10).First(p => p.Tag is EditorBlockTag { LockedBlock: null });
            editor.Focus();
            editor.CaretPosition = paragraph.ContentEnd;

            Time("Keystroke (text changed, before the edit is applied)", () => editor.CaretPosition.InsertTextInRun("x"), repeat: 20);
            SpellCheck.SetIsEnabled(editor, false);
            Time("Keystroke with spell checking off", () => editor.CaretPosition.InsertTextInRun("x"), repeat: 20);
            SpellCheck.SetIsEnabled(editor, true);
            var images = editor.Document.Blocks.OfType<BlockUIContainer>().ToList();
            foreach (var b in images) b.Child.Visibility = Visibility.Collapsed;
            Time("Keystroke with figures hidden", () => editor.CaretPosition.InsertTextInRun("x"), repeat: 20);
            foreach (var b in images) b.Child.Visibility = Visibility.Visible;
            var handlerTimer = Stopwatch.StartNew();
            for (int i = 0; i < 20; i++) editor.CaretPosition.InsertTextInRun("x");
            results.Add(("Keystroke, handlers only (no layout or render)", handlerTimer.Elapsed.TotalMilliseconds / 20));
            Time("Caret move (selection changed)", () => editor.CaretPosition = paragraph.ContentStart.GetPositionAtOffset(3) ?? paragraph.ContentStart, repeat: 20);
            Time("Apply typed edits to the manuscript (after the typing pause)", () =>
            {
                editor.CaretPosition.InsertTextInRun("y");
                window.ViewModel.FlushPendingMarkdownSync();
            }, repeat: 10);
            Time("Redraw the page (insert a paragraph)", () =>
                window.ViewModel.InsertBlock(new DocumentBlock { ParagraphId = window.ViewModel.NewParagraphId(), Text = "Inserted." }, null), repeat: 5);
            Time("Toggle Preview on and off", () =>
            {
                window.SetPreview(true);
                Pump();
                window.SetPreview(false);
            }, repeat: 3);
            Time("Reload workspace state", () => RunOnDispatcher(window.ViewModel.ReloadWorkspaceStateAsync), repeat: 3);
            Time("Commit", () =>
            {
                editor.CaretPosition.InsertTextInRun("z");
                RunOnDispatcher(() => window.ViewModel.CommitCurrentChangesAsync("Benchmark edit"));
            }, repeat: 3);
            Time("Checks (style, numbering, references)", () => window.ViewModel.Paper.ScheduleChecks(immediately: true), repeat: 3);

            Console.WriteLine();
            Console.WriteLine($"    Timings on {Path.GetFileName(source)} ({window.ViewModel.CurrentDocument.Blocks.Count} blocks):");
            foreach (var (name, ms) in results)
            {
                Console.WriteLine($"    {ms,9:0.0} ms  {name}");
            }
        }
        finally
        {
            window.Hide();
            window.ViewModel.Dispose();
            TestWorkspaceBuilder.DeleteDirectory(copy);
        }
    }

    [Fact]
    public void Performance_PartsOfReloadCommitAndRedraw_OnARealManuscript()
    {
        string? source = Environment.GetEnvironmentVariable("PERF_WORKSPACE");
        if (source == null || !Directory.Exists(source))
        {
            return;
        }
        RunOnStaThread(() =>
        {
            string copy = Path.Combine(Path.GetTempPath(), $"RevEditorPerf_{Guid.NewGuid():N}");
            CopyDirectory(source, copy);
            var lines = new List<string>();
            T Time<T>(string name, Func<T> action)
            {
                var sw = Stopwatch.StartNew();
                var result = action();
                lines.Add($"    {sw.Elapsed.TotalMilliseconds,9:0.0} ms  {name}");
                return result;
            }
            try
            {
                var repo = new RevisionsEditor.Core.Git.GitRepositoryService(copy);
                Time("EnsureInitialized", () => { repo.EnsureInitialized(); return 0; });
                Time("One git process (git --version)", () => { var p = Process.Start(new ProcessStartInfo("git", "--version") { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true })!; p.WaitForExit(); return 0; });
                var profile = Time("LoadFormattingProfile", repo.LoadFormattingProfile);
                Time("GetBranchesAndCurrent", repo.GetBranchesAndCurrent);
                var doc = Time("LoadCurrentWorkingDocument", repo.LoadCurrentWorkingDocument);
                var ledger = Time("LoadProvenanceLedger (first: from git notes)", () => repo.LoadProvenanceLedger(doc.DocumentId));
                Time("LoadProvenanceLedger (cached copy)", () => repo.LoadProvenanceLedger(doc.DocumentId));
                var verification = Time("VerifyChainIntegrity", () => RevisionsEditor.Core.Provenance.ProvenanceChainService.VerifyChainIntegrity(ledger));
                var graph = Time("GetCommitGraph", () => repo.GetCommitGraph(ledger, verification));
                Time("Diff of the last two commits", () => RevisionsEditor.Core.Diffing.DiffEngineService.ComputeDocumentDiff(repo.LoadDocumentAtCommit(graph[1].Sha), repo.LoadDocumentAtCommit(graph[0].Sha)));
                using var bridge = new RevisionsEditor.Core.Bridge.LocalBridgeServer(repo, 1);
                Time("Google Docs patches", () => { bridge.RefreshPendingPatches(); return 0; });
                Time("Paper snapshot (references, results, reviews)", () => RevisionsEditor.App.ViewModels.PaperViewModel.LoadSnapshot(copy));
                var context = Time("Scholarly context", () => RevisionsEditor.Core.Scholarly.ScholarlyContext.Build(doc.Blocks, RevisionsEditor.Core.Scholarly.ScholarlyProject.Load(copy)));
                Time("Build the page (FlowDocument)", () => FlowDocumentBuilder.BuildManuscriptEditorDocument(doc, profile, MarkupDisplayMode.AllMarkup, scholarly: context, workspacePath: copy));
                Time("Build the page again (images cached)", () => FlowDocumentBuilder.BuildManuscriptEditorDocument(doc, profile, MarkupDisplayMode.AllMarkup, scholarly: context, workspacePath: copy));
                doc.Blocks[5].Text += " Edited.";
                Time("CommitSnapshot", () => repo.CommitSnapshot(doc, "Benchmark"));
                Console.WriteLine();
                Console.WriteLine($"    Parts, on {Path.GetFileName(source)} ({graph.Count} commits, {ledger.Blocks.Count} signed blocks):");
                lines.ForEach(Console.WriteLine);
            }
            finally
            {
                TestWorkspaceBuilder.DeleteDirectory(copy);
            }
        });
    }

    [Fact]
    public void Performance_TheRealWorkspaceOpensInProcess_ReadOnly()
    {
        // libgit2 refuses repositories owned by another Windows user; this opens the real one (nothing is written).
        string? source = Environment.GetEnvironmentVariable("PERF_WORKSPACE");
        if (source == null || !Directory.Exists(Path.Combine(source, ".git")))
        {
            return;
        }
        var sw = Stopwatch.StartNew();
        using var repo = new LibGit2Sharp.Repository(source);
        int commits = repo.Commits.Count();
        int notes = repo.Notes["revisions-provenance"].Count();
        Console.WriteLine($"    Opened {source} in process: {commits} commits on {repo.Head.FriendlyName}, {notes} signed blocks, {sw.Elapsed.TotalMilliseconds:0} ms.");
        Assert.True(commits > 0);
    }

    private static void CopyDirectory(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (string dir in Directory.EnumerateDirectories(from, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, dir)));
        }
        foreach (string file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            File.Copy(file, Path.Combine(to, Path.GetRelativePath(from, file)));
        }
    }
}
