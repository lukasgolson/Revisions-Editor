using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using RevisionsEditor.App.Dialogs;
using RevisionsEditor.Core.Diagrams;
using Xunit;

namespace RevisionsEditor.Tests;

public sealed partial class RevisionsEditorTests
{
    [Fact]
    public void DiagramEditorDialog_EditsOnTheDrawing_WriteBackIntoTheText_AndUndo()
        => RunOnStaThread(() =>
        {
            string source = DiagramTemplates.All.First(t => t.Name == "Feature extraction network").Source;
            var dialog = new DiagramEditorDialog(null, source, new HashSet<string>()) { Left = -20000, Top = -20000, ShowInTaskbar = false };
            dialog.Show();
            try
            {
                Assert.Equal(string.Empty, dialog.Errors);
                var layout = dialog.CurrentLayout!;
                Assert.NotNull(layout);

                // A click on the Linear block selects it.
                var linear = layout.Nodes.Single(n => n.Node.Id == "lin");
                dialog.SelectAt(linear.Body.CenterX, linear.Body.CenterY);
                Assert.Equal("lin", dialog.SelectedId);

                // A block added after it is spliced into its arrow and selected.
                dialog.AddAfterSelected(DiagramNodeKind.Block);
                Assert.Equal("block1", dialog.SelectedId);
                Assert.Contains("lin -> block1 -> ref2", dialog.Source);

                // Its texts edited below the drawing go into the text, math included.
                dialog.SetSelectedTexts("LayerNorm", "$C = 1024$");
                Assert.Contains("block  block1 \"LayerNorm\" \"$C = 1024$\"", dialog.Source);
                Assert.Contains(dialog.CurrentLayout!.Nodes, n => n.Node.Title == "LayerNorm");

                // Moved left, it is drawn before Linear; removed, Linear joins Refiner again.
                dialog.MoveSelected(-1);
                var grid = DiagramLayout.AssignGrid(DiagramSource.Parse(dialog.Source, out _));
                Assert.True(grid["block1"].Column < grid["lin"].Column);
                dialog.RemoveSelected();
                Assert.DoesNotContain("block1", dialog.Source);
                Assert.Contains("lin -> ref2", dialog.Source);

                // Undo brings it back; the comment at the top survives every edit.
                dialog.UndoLast();
                Assert.Contains("block1", dialog.Source);
                Assert.StartsWith("# Feature extraction network", dialog.Source);

                // A mistake in the text is reported by line, and the drawing keeps the last good version.
                dialog.TypeSource(dialog.Source + "\nblok x \"X\"\n");
                Assert.Contains("Unknown statement", dialog.Errors);
                Assert.NotNull(dialog.CurrentLayout);

                if (Environment.GetEnvironmentVariable("DIAGRAM_OUT") is { } outDir)
                {
                    dialog.TypeSource(source);
                    dialog.SelectAt(linear.Body.CenterX, linear.Body.CenterY);
                    dialog.Left = 0;
                    dialog.Top = 0;
                    dialog.UpdateLayout();
                    var content = (FrameworkElement)dialog.Content;
                    var bitmap = new RenderTargetBitmap((int)content.ActualWidth, (int)content.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                    var visual = new DrawingVisual();
                    using (var dc = visual.RenderOpen())
                    {
                        dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, content.ActualWidth, content.ActualHeight));
                        dc.DrawRectangle(new VisualBrush(content), null, new Rect(0, 0, content.ActualWidth, content.ActualHeight));
                    }
                    bitmap.Render(visual);
                    var png = new PngBitmapEncoder();
                    png.Frames.Add(BitmapFrame.Create(bitmap));
                    using var file = File.Create(Path.Combine(outDir, "dialog.png"));
                    png.Save(file);
                }
            }
            finally
            {
                dialog.Close();
            }
        });
}
