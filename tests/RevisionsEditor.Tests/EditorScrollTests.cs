using System.IO;
using System.Windows;
using System.Windows.Controls;
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
                window.ViewModel.InsertBlock(new DocumentBlock { ParagraphId = "p-inserted", Text = "An inserted paragraph." }, "p-long-60");
                Layout();
                Assert.True(Math.Abs(editor.VerticalOffset - before) < 2, $"The page scrolled from {before} to {editor.VerticalOffset}.");
            }
            finally
            {
                window.Hide(); // closing would ask whether to commit the changes
                window.ViewModel.Dispose();
                TestWorkspaceBuilder.DeleteDirectory(workspace);
            }
        });
}
