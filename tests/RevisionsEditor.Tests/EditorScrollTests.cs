using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Threading;
using RevisionsEditor.App;
using RevisionsEditor.Core.Git;
using RevisionsEditor.Core.Models;
using Xunit;

namespace RevisionsEditor.Tests;

public sealed partial class RevisionsEditorTests
{
    [Fact]
    public void WpfEditor_InsertingABlock_KeepsThePageScrolledWhereItWas()
        => RunOnStaThread(() =>
        {
            if (Application.Current == null)
            {
                var app = new App.App();
                app.InitializeComponent();
            }
            MainWindow.UseSavedLayout = false;
            var window = new MainWindow();
            string workspace = Path.Combine(Path.GetTempPath(), $"RevEditorTest_Scroll_{Guid.NewGuid():N}");
            try
            {
                TestWorkspaceBuilder.SeedReviewWorkflow(new GitRepositoryService(workspace));
                RunOnDispatcher(() => window.ViewModel.OpenWorkspaceAsync(workspace));
                window.Left = -20000; window.Top = -20000; window.Width = 1440; window.Height = 900; window.ShowInTaskbar = false; window.ShowActivated = false;
                window.Show();
                window.SelectWorkspaceTab(0);

                // A manuscript long enough to scroll.
                for (int i = 0; i < 80; i++)
                {
                    window.ViewModel.InsertBlock(new DocumentBlock { ParagraphId = $"p-long-{i:D2}", Text = $"Paragraph {i} with enough words to fill a line of the page and then some more words after that." }, null);
                }
                var root = (FrameworkElement)window.Content;
                void Layout()
                {
                    root.UpdateLayout();
                    // Let the editor's deferred work (restoring the scroll position) run.
                    Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.Background, () => { });
                    root.UpdateLayout();
                }
                Layout();
                var editor = (RichTextBox)window.FindName("ManuscriptEditor");
                Assert.True(editor.ExtentHeight > editor.ViewportHeight * 2,
                    $"The manuscript should be longer than the page: extent {editor.ExtentHeight}, viewport {editor.ViewportHeight}, actual {editor.ActualHeight}, visible {editor.IsVisible}, blocks {editor.Document.Blocks.Count}.");

                editor.ScrollToVerticalOffset(editor.ExtentHeight / 2);
                Layout();
                double before = editor.VerticalOffset;
                Assert.True(before > 100);

                // What Insert > Figure, Table, Equation or Diagram does: a new block, then the page is drawn again.
                var caretParagraph = editor.Document.Blocks.OfType<Paragraph>().Last();
                editor.CaretPosition = caretParagraph.ContentStart;
                window.ViewModel.InsertBlock(new DocumentBlock { ParagraphId = "p-inserted", Text = "An inserted paragraph." }, "p-long-60");
                Layout();
                Assert.True(Math.Abs(editor.VerticalOffset - before) < 2, $"The page scrolled from {before} to {editor.VerticalOffset}.");
                // The page was updated in place: the paragraph with the caret is the same object, caret still in it.
                Assert.Contains(caretParagraph, editor.Document.Blocks);
                Assert.Same(caretParagraph, editor.CaretPosition.Paragraph);

                // Whatever the edit, the page updated in place draws exactly what a fresh page would.
                void AssertPageIsFresh(string after)
                {
                    var fresh = RevisionsEditor.App.Rendering.FlowDocumentBuilder.BuildManuscriptEditorDocument(
                        window.ViewModel.CurrentDocument, window.ViewModel.ActiveProfile, window.ViewModel.SelectedMarkupMode,
                        scholarly: window.ViewModel.Paper.Context, workspacePath: window.ViewModel.WorkspacePath);
                    var shown = editor.Document.Blocks.Select(RevisionsEditor.App.Rendering.PageReconciler.Signature).ToList();
                    var expected = fresh.Blocks.Select(RevisionsEditor.App.Rendering.PageReconciler.Signature).ToList();
                    Assert.True(shown.SequenceEqual(expected), $"After {after}, the page differs from a fresh one at block {shown.Zip(expected).TakeWhile(p => p.First == p.Second).Count()}.");
                }
                AssertPageIsFresh("an insert");
                window.ViewModel.ReplaceBlockText("p-long-10", "Paragraph 10, rewritten with *emphasis*.");
                Layout();
                AssertPageIsFresh("a rewrite");
                window.ViewModel.RemoveBlock("p-long-20");
                Layout();
                AssertPageIsFresh("a removal");
                // A figure and an equation inserted near the start renumber the ones after them.
                var figureText = RevisionsEditor.Core.Scholarly.ScholarlyBlocks.FormatFigure("A new figure.", "figures/none.png", "fig:new");
                window.ViewModel.InsertBlock(new DocumentBlock { ParagraphId = "p-fig-new", Role = BlockStyleRole.Figure, Text = figureText }, "p-long-01");
                window.ViewModel.InsertBlock(new DocumentBlock { ParagraphId = "p-eq-new", Role = BlockStyleRole.Equation, Text = "$$ a = b $$ {#eq:new}" }, "p-long-02");
                Layout();
                AssertPageIsFresh("inserting a figure and an equation");
                // A pending change makes a paragraph read-only; accepting it makes it editable again.
                window.ViewModel.ReplaceBlockText("p-long-30", "Paragraph 30 {++with an insertion++}.");
                Layout();
                AssertPageIsFresh("adding pending markup");
                RunOnDispatcher(window.ViewModel.ReloadWorkspaceStateAsync);
                Layout();
                AssertPageIsFresh("a reload");

                // Line numbers (used in responses to reviewers) are counted again only where the text changed; the
                // counts kept must match counting every line afresh, also after typing makes a paragraph wrap.
                var margin = window.LineNumbers!;
                void AssertLinesAsCountedAfresh(string after)
                {
                    Layout();
                    margin.RecomputeNow(everyLine: false);
                    var (count, caretLine) = (margin.LineCount, margin.CaretLine);
                    margin.RecomputeNow(everyLine: true);
                    Assert.True(count == margin.LineCount && caretLine == margin.CaretLine,
                        $"After {after}: kept {count} lines (caret {caretLine}), counted afresh {margin.LineCount} (caret {margin.CaretLine}).");
                }
                AssertLinesAsCountedAfresh("loading");
                var typedInto = editor.Document.Blocks.OfType<Paragraph>().Last(p => p.Tag is RevisionsEditor.App.Rendering.EditorBlockTag { LockedBlock: null });
                editor.CaretPosition = typedInto.ContentEnd;
                for (int i = 0; i < 40; i++)
                {
                    editor.CaretPosition.InsertTextInRun(" more words typed");
                }
                AssertLinesAsCountedAfresh("typing enough to wrap");
                editor.CaretPosition.InsertParagraphBreak();
                AssertLinesAsCountedAfresh("starting a new paragraph");
                editor.ScrollToVerticalOffset(0);
                AssertLinesAsCountedAfresh("scrolling");
                Assert.True(margin.LineCount > 5);

                // Spelling is checked in the background with Windows' spell checker: a misspelling typed on the page is
                // found (and correct words are not).
                var speller = window.SpellChecker;
                if (speller != null)
                {
                    Assert.False(System.Windows.Controls.SpellCheck.GetIsEnabled(editor)); // WPF's own checking is off
                    editor.CaretPosition.InsertTextInRun("This sentense has a mispeled wurd in it.");
                    var waited = System.Diagnostics.Stopwatch.StartNew();
                    while (waited.Elapsed < TimeSpan.FromSeconds(10) && !speller.MisspelledWords.Contains("mispeled"))
                    {
                        Layout();
                        Thread.Sleep(50);
                    }
                    Assert.Contains("mispeled", speller.MisspelledWords);
                    Assert.Contains("sentense", speller.MisspelledWords);
                    Assert.DoesNotContain("sentence", speller.MisspelledWords);
                    Assert.DoesNotContain("This", speller.MisspelledWords);
                }
            }
            finally
            {
                window.Hide(); // closing would ask whether to commit the changes
                window.ViewModel.Dispose();
                TestWorkspaceBuilder.DeleteDirectory(workspace);
            }
        });
}
