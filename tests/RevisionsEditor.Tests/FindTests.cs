using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using RevisionsEditor.Core.Checks;
using RevisionsEditor.Core.Formatting;
using RevisionsEditor.Core.Models;
using Xunit;

namespace RevisionsEditor.Tests;

public sealed partial class RevisionsEditorTests
{
    [Fact]
    public void TextFinder_MatchesCaseWholeWordsAndTypographicVariants()
    {
        Assert.Equal([(0, 4), (21, 4)], TextFinder.FindAll("Tree heights and the tree crowns", "tree"));
        Assert.Equal([(21, 4)], TextFinder.FindAll("Tree heights and the tree crowns", "tree", matchCase: true));
        // Whole words: "height" is not found inside "heights".
        Assert.Empty(TextFinder.FindAll("Tree heights", "height", wholeWord: true));
        Assert.Single(TextFinder.FindAll("Tree heights", "height"));
        // Curly apostrophes, en dashes and odd spacing match what is typed.
        Assert.Equal([(0, 5)], TextFinder.FindAll("don’t stop", "don't"));
        Assert.Equal([(7, 5)], TextFinder.FindAll("ranged 10–20 m", "10-20"));
        Assert.Equal([(0, 15)], TextFinder.FindAll("leaf   on sites", "leaf on sites"));
        Assert.Equal(3, TextFinder.FindAll("aaa", "a").Count);
        Assert.Empty(TextFinder.FindAll("text", "  "));
    }

    [Fact]
    public void Find_SearchesThePage_IncludingTheReadOnlyTitleAndCaptions_AndSelectsEditableMatches()
        => RunOnStaThread(() =>
        {
            var (ws, doc) = ScholarlyWorkspace();
            try
            {
                var context = ContextFor(doc, ws);
                var profile = FormattingRuleProfile.GetBuiltInProfiles()[0];
                var editor = new RichTextBox();
                var window = new Window { Content = new Grid { Children = { editor } }, Width = 900, Height = 700, Left = -20000, Top = -20000, ShowInTaskbar = false };
                window.Show();
                try
                {
                    editor.Document = RevisionsEditor.App.Rendering.FlowDocumentBuilder.BuildManuscriptEditorDocument(
                        doc, profile, MarkupDisplayMode.AllMarkup, scholarly: context, workspacePath: ws);
                    window.UpdateLayout();
                    var layer = new RevisionsEditor.App.Rendering.FindHighlightLayer();
                    var target = new RevisionsEditor.App.Rendering.FlowDocumentFindTarget(() => editor, layer);

                    // "UAV" is in the read-only title; "sites" in a body paragraph and in the figure's caption.
                    var title = target.Search("UAV", matchCase: false, wholeWord: false);
                    var match = Assert.Single(title);
                    Assert.NotNull(match.Owner);
                    Assert.Equal("UAV", new TextRange(match.Start!, match.End!).Text);

                    var sites = target.Search("sites", matchCase: false, wholeWord: true);
                    Assert.Contains(sites, m => m.Owner == null);
                    Assert.Contains(sites, m => m.Owner != null); // "Study sites in Grimston Park." caption
                    Assert.All(sites, m => Assert.Equal("sites", new TextRange(m.Start!, m.End!).Text, ignoreCase: true));

                    // An editable match is selected, ready to be typed over.
                    var body = sites.First(m => m.Owner == null);
                    target.Reveal(body);
                    Assert.Equal("sites", editor.Selection.Text, ignoreCase: true);

                    Assert.Empty(target.Search("no such words", false, false));

                    // Replace All changes the editable text, and leaves read-only text (the caption) alone.
                    Assert.Null(target.ReplaceUnavailableReason);
                    int editable = sites.Count(target.CanReplace);
                    Assert.True(editable >= 1 && editable < sites.Count);
                    Assert.Equal(editable, target.ReplaceAll(sites, "plots"));
                    var read = RevisionsEditor.App.Rendering.ManuscriptEditorReader.Read(editor.Document, []);
                    Assert.Contains(read.Blocks, b => b.Text.Contains("The plots are shown"));
                    Assert.Equal(sites.Count - editable, target.Search("sites", false, true).Count);
                    Assert.True(editor.CanUndo); // one Ctrl+Z puts them back

                    // The preview cannot be edited.
                    var preview = new RevisionsEditor.App.Rendering.FlowDocumentFindTarget(() => new FlowDocumentScrollViewer(), layer);
                    Assert.NotNull(preview.ReplaceUnavailableReason);
                }
                finally
                {
                    window.Close();
                }
            }
            finally
            {
                Directory.Delete(ws, recursive: true);
            }
        });

    [Fact]
    public void FindBar_ReplacesTheCurrentMatchAndThenAll_InTheSource()
        => RunOnStaThread(() =>
        {
            var box = new TextBox { Text = "Tree height. Mean height was 12 m. Heights vary.", AcceptsReturn = true };
            var bar = new RevisionsEditor.App.Rendering.FindBar();
            var window = new Window { Content = new DockPanel { Children = { bar, box } }, Width = 900, Height = 300, Left = -20000, Top = -20000, ShowInTaskbar = false };
            window.Show();
            try
            {
                bar.Open(new RevisionsEditor.App.Rendering.TextBoxFindTarget(box), "height", replace: true);
                Assert.True(bar.IsReplaceShown);
                Assert.Equal(3, bar.MatchCount); // "height", "height", "Height(s)"
                bar.ReplacementText = "length";
                bar.ReplaceCurrent();
                Assert.Equal("Tree length. Mean height was 12 m. Heights vary.", box.Text);
                Assert.Equal(2, bar.MatchCount);
                bar.ReplaceEverything();
                Assert.Equal("Tree length. Mean length was 12 m. lengths vary.", box.Text);
                Assert.Equal(0, bar.MatchCount);
            }
            finally
            {
                window.Close();
            }
        });
}
