using System.IO;
using System.IO.Compression;
using System.Xml.Linq;
using RevisionsEditor.App.ViewModels;
using RevisionsEditor.Core.Checks;
using RevisionsEditor.Core.Docx;
using RevisionsEditor.Core.Models;
using RevisionsEditor.Core.Reviews;
using Xunit;

namespace RevisionsEditor.Tests;

public sealed partial class RevisionsEditorTests
{
    private static string DocxText(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        using var stream = zip.GetEntry("word/document.xml")!.Open();
        return string.Concat(XDocument.Load(stream).Descendants(Wns + "t").Select(t => t.Value));
    }

    [Fact]
    public void DockingLayout_IsSavedAndRestored_WithEveryPanelsContentInPlace()
        => RunOnStaThread(() =>
        {
            if (System.Windows.Application.Current == null)
            {
                new RevisionsEditor.App.App().InitializeComponent();
            }
            // Closing this window must not end the application the other window tests run in.
            System.Windows.Application.Current!.ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown;
            RevisionsEditor.App.MainWindow.UseSavedLayout = false;
            // Shown off screen and without taking focus, so it cannot be mistaken for the real app.
            var window = new RevisionsEditor.App.MainWindow
            {
                WindowStartupLocation = System.Windows.WindowStartupLocation.Manual,
                Left = -20000,
                Top = -20000,
                ShowActivated = false,
                ShowInTaskbar = false
            };
            try
            {
                window.Show();
                RunOnDispatcher(() => Task.Delay(500)); // let the window render and run its startup
                Assert.False(window.ViewModel.HasWorkspace); // a test never opens the user's recent workspace
                var documentContent = window.FindPanel("document")!.Content;
                string defaultLayout = window.SerializeLayout();
                Assert.Contains("ContentId=\"reviewers\"", defaultLayout);

                // Hide the Checks panel and select Compare, save, put things back, then restore the saved layout.
                var checks = (AvalonDock.Layout.LayoutAnchorable)window.FindPanel("checks")!;
                var checksContent = checks.Content;
                checks.Hide();
                window.SelectWorkspaceTab(2);
                string saved = window.SerializeLayout();
                Assert.True(window.LoadLayout(defaultLayout));
                Assert.False(((AvalonDock.Layout.LayoutAnchorable)window.FindPanel("checks")!).IsHidden);

                Assert.True(window.LoadLayout(saved));
                var restored = (AvalonDock.Layout.LayoutAnchorable)window.FindPanel("checks")!;
                Assert.True(restored.IsHidden);
                Assert.Same(checksContent, restored.Content);
                Assert.Same(documentContent, window.FindPanel("document")!.Content);

                // The views still come to the front after the layout was replaced.
                window.SelectWorkspaceTab(8);
                Assert.True(window.FindPanel("reviewers")!.IsSelected);

                // Errors on the UI thread are recorded rather than shown, so floating panels can be checked for them.
                var errors = new List<Exception>();
                System.Windows.Threading.Dispatcher.CurrentDispatcher.UnhandledException += (_, e) => { errors.Add(e.Exception); e.Handled = true; };
                void Pump() => RunOnDispatcher(() => Task.Delay(400));

                // A view dragged out into its own window keeps its data and the app's commands and shortcuts.
                window.FindPanel("compare")!.Float();
                Pump();
                var floating = System.Windows.Application.Current.Windows.OfType<AvalonDock.Controls.LayoutFloatingWindowControl>().Single();
                Assert.Same(window.ViewModel, floating.DataContext);
                Assert.Contains(floating.CommandBindings.Cast<System.Windows.Input.CommandBinding>(), b => b.Command == RevisionsEditor.App.AppCommands.Commit);
                Assert.Same(window.ViewModel, ((System.Windows.FrameworkElement)window.FindPanel("compare")!.Content).DataContext);
                Assert.True(window.LoadLayout(defaultLayout)); // and docks back with Reset Window Layout
                Pump();

                // Floating a document, a tool panel and the same view again raises no errors.
                window.FindPanel("document")!.Float();
                Pump();
                window.FindPanel("checks")!.Float();
                Pump();
                window.FindPanel("compare")!.Float();
                Pump();
                Assert.True(window.LoadLayout(defaultLayout));
                Pump();

                // Views close like tabs and open again from the Views menu (ticked while open), back in their place.
                window.CloseView("source");
                window.CloseView("compare");
                window.CloseView("history");
                Pump();
                Assert.Equal((false, false), window.ViewState("source"));
                Assert.Equal((false, false), window.ViewState("history"));
                Assert.Equal((true, true), window.ViewState("document"));
                string withClosed = window.SerializeLayout();
                Assert.DoesNotContain("ContentId=\"source\"", withClosed);
                window.ClickViewButton("compare");
                Pump();
                Assert.Equal((true, true), window.ViewState("compare"));
                Assert.True(window.FindPanel("compare")!.IsSelected);
                Assert.NotNull(window.FindPanel("compare")!.Content);
                var pane = (AvalonDock.Layout.LayoutDocumentPane)window.FindPanel("compare")!.Parent;
                Assert.Equal(["document", "compare", "provenance"], pane.Children.Take(3).Select(c => c.ContentId));
                window.ClickViewButton("compare"); // ticked: closes it again
                Pump();
                Assert.False(window.ViewState("compare").Open);
                window.ClickViewButton("history");
                Pump();
                Assert.Equal((true, true), window.ViewState("history"));
                // A layout saved with closed views keeps them closed; the views still open from it.
                Assert.True(window.LoadLayout(withClosed));
                Pump();
                Assert.False(window.ViewState("source").Open);
                window.SelectWorkspaceTab(1); // the Source view, as Ctrl+F or a command would ask for it
                Pump();
                Assert.True(window.ViewState("source").Open);
                Assert.Same(window.FindPanel("source")!.Content, ((System.Windows.FrameworkElement)window.FindName("MarkdownSourceTextBox")).Parent);
                Assert.True(window.LoadLayout(defaultLayout));
                Pump();
                Assert.Empty(errors.Select(e => e.ToString()));
            }
            finally
            {
                window.Close();
                window.ViewModel.Dispose();
                RevisionsEditor.App.MainWindow.UseSavedLayout = true;
            }
        });

    [Fact]
    public void PaperWorkflow_BuildsTheManuscript_ChecksItForTheJournal_AndExportsTheRevision()
        => RunOnStaThread(() =>
        {
            var (ws, _) = ScholarlyWorkspace();
            string output = Path.Combine(Path.GetTempPath(), $"RevEditorTest_Revision_{Guid.NewGuid():N}");
            var vm = new MainViewModel();
            try
            {
                RunOnDispatcher(() => vm.OpenWorkspaceAsync(ws));

                string? last = null;
                string Add(BlockStyleRole role, string text)
                {
                    var block = new DocumentBlock { ParagraphId = vm.NewParagraphId(), Role = role, Text = text };
                    vm.InsertBlock(block, last);
                    last = block.ParagraphId;
                    return block.ParagraphId;
                }
                Add(BlockStyleRole.Title, "Tree height from UAV laser scanning");
                Add(BlockStyleRole.Abstract, "Heights had an RMSE of {{height.rmse:0.00}} m.");
                Add(BlockStyleRole.Heading1, "Introduction");
                string intro = Add(BlockStyleRole.BodyParagraph, "Laser scanning supports inventories [@white2016remote]. The sites are shown in @fig:site.");
                Add(BlockStyleRole.Figure, "![Study sites.](figures/site.png){#fig:site}");
                Add(BlockStyleRole.Heading1, "References");
                RunOnDispatcher(() => vm.CommitCurrentChangesAsync("Submitted draft"));

                string tag = string.Empty;
                RunOnDispatcher(async () => tag = await vm.MarkSubmittedAsync());
                Assert.Equal("submitted-v1", tag);

                // Marking it submitted recorded where each paragraph falls in its review copy (with Word).
                var map = SubmittedLineMap.Load(ws, "submitted-v1");
                Assert.Equal(WordLayoutLocator.IsWordInstalled, map != null);
                if (map != null)
                {
                    var introSpan = map.Paragraphs.Single(s => s.ParagraphId == intro);
                    Assert.True(introSpan.AbsoluteFirstLine > 1);
                    Assert.Equal(intro, vm.FindSubmittedLine(map, introSpan.AbsoluteFirstLine, null)!.Value.ParagraphId);
                    Assert.Contains(".revisions/line-maps/submitted-v1.json", RunGitIn(ws, "ls-files", "--others", "--exclude-standard") + RunGitIn(ws, "ls-files"));
                }

                // The revision: one paragraph changed in response to a reviewer.
                vm.ReplaceBlockText(intro, "Laser scanning supports inventories [@white2016remote; @naesset2002predicting]. The two study sites are shown in @fig:site.");
                RunOnDispatcher(() => vm.CommitCurrentChangesAsync("Revise the introduction (addresses R1.1)"));

                var round = vm.Paper.AddRound("Revision 1", "RSE-D-26-00001", "Reviewer 1\n\n1. Cite the area-based approach.\n2. Describe the sites.", "submitted-v1", null);
                Assert.Equal(2, round.Comments.Count);
                vm.Paper.Comments[0].Response = "We now cite Næsset (2002).";
                vm.Paper.LinkParagraph(vm.Paper.Comments[0], intro);
                Assert.Equal(ReviewCommentStatus.Addressed, round.Comments[0].Status);

                RevisionPackage? package = null;
                RunOnDispatcher(async () => package = await vm.ExportRevisionPackageAsync(output, round, highlightChanges: false));
                Assert.NotNull(package);
                Assert.True(File.Exists(package!.RevisedManuscript));

                string letter = DocxText(package.ResponseLetter);
                Assert.Contains("Comment R1.1.", letter);
                Assert.Contains("Response: We now cite Næsset (2002).", letter);
                Assert.Contains("(White et al., 2016; Næsset, 2002)", letter); // the quoted revised text is rendered
                Assert.Equal(WordLayoutLocator.IsWordInstalled, package.LocationsFromWord);
                Assert.Contains(package.LocationsFromWord ? "Revised text (page 1, line" : "Revised text (Section 1 (Introduction), paragraph 1)", letter);

                using (var zip = ZipFile.OpenRead(package.MarkedUpManuscript))
                using (var stream = zip.GetEntry("word/document.xml")!.Open())
                {
                    var xml = XDocument.Load(stream);
                    Assert.Contains(xml.Descendants(Wns + "ins"), i => string.Concat(i.Descendants(Wns + "t").Select(t => t.Value)).Contains("two study"));
                }
                Assert.Contains("Næsset, E., 2002.", DocxText(package.RevisedManuscript));

                // The journal checks ran in the background.
                vm.Paper.ScheduleChecks(immediately: true);
                RunOnDispatcher(async () =>
                {
                    for (int i = 0; i < 100 && vm.Paper.Issues.Count == 0; i++)
                    {
                        await Task.Delay(100);
                    }
                });
                Assert.Contains(vm.Paper.Issues, i => i.Category == ManuscriptChecks.Submission && i.Message.Contains("highlights"));
            }
            finally
            {
                vm.Dispose();
                TestWorkspaceBuilder.DeleteDirectory(ws);
                TestWorkspaceBuilder.DeleteDirectory(output);
            }
        });
}
