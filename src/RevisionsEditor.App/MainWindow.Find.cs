using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using RevisionsEditor.App.Rendering;

namespace RevisionsEditor.App;

/// <summary>Find (Ctrl+F, F3, Shift+F3) on the Document page, its preview and the Markdown source.</summary>
public partial class MainWindow
{
    private FindBar? _documentFind;
    private FindBar? _sourceFind;
    private FindHighlightLayer? _findLayer;
    private IFindTarget? _documentTarget;
    private IFindTarget? _sourceTarget;
    private DispatcherTimer? _findRefreshTimer;

    private void InitializeFind()
    {
        _findLayer = new FindHighlightLayer();
        ((Grid)ManuscriptEditor.Parent).Children.Add(_findLayer);
        _documentTarget = new FlowDocumentFindTarget(() => _preview ? PreviewViewer : ManuscriptEditor, _findLayer);
        _sourceTarget = new TextBoxFindTarget(MarkdownSourceTextBox);
        // The selection stays visible while typing in the find box.
        ManuscriptEditor.IsInactiveSelectionHighlightEnabled = true;
        MarkdownSourceTextBox.IsInactiveSelectionHighlightEnabled = true;

        _documentFind = new FindBar();
        _sourceFind = new FindBar();
        AddUnderCommandStrip((DockPanel)DocumentPanel.Content, _documentFind);
        AddUnderCommandStrip((DockPanel)SourcePanel.Content, _sourceFind);
        MarkdownSourceTextBox.TextChanged += (_, _) => ScheduleFindRefresh();

        Func<object?, bool> workspace = _ => ViewModel.HasWorkspace;
        Bind(ApplicationCommands.Find, _ => OpenFind(), workspace, allowWhileBusy: true);
        Bind(ApplicationCommands.Replace, _ => OpenFind(replace: true), workspace, allowWhileBusy: true);
        Bind(AppCommands.FindNext, _ => FindAgain(1), workspace, allowWhileBusy: true);
        Bind(AppCommands.FindPrevious, _ => FindAgain(-1), workspace, allowWhileBusy: true);
        // The preview handles Ctrl+F itself (its own find toolbar, which is hidden): use the app's.
        PreviewViewer.CommandBindings.Add(new CommandBinding(ApplicationCommands.Find, (_, _) => OpenFind(), (_, e) => e.CanExecute = ViewModel.HasWorkspace));
    }

    private static void AddUnderCommandStrip(DockPanel panel, FindBar bar)
    {
        DockPanel.SetDock(bar, Dock.Top);
        panel.Children.Insert(1, bar);
    }

    /// <summary>The Source view's search when the Markdown source is being worked in, else the Document page's.</summary>
    private (FindBar Bar, IFindTarget Target, string? Selected) ActiveFind()
    {
        bool source = IsPanelShown(SourcePanelIndex)
            && (MarkdownSourceTextBox.IsKeyboardFocusWithin || _sourceFind!.IsKeyboardFocusWithin || !IsPanelShown(DocumentTabIndex));
        return source
            ? (_sourceFind!, _sourceTarget!, MarkdownSourceTextBox.SelectedText)
            : (_documentFind!, _documentTarget!, _preview ? null : ManuscriptEditor.Selection.Text);
    }

    private const int SourcePanelIndex = 1;

    private void OpenFind(bool replace = false)
    {
        var (bar, target, selected) = ActiveFind();
        if (bar == _documentFind && !IsPanelShown(DocumentTabIndex))
        {
            SelectWorkspaceTab(DocumentTabIndex);
        }
        FlushEditorEdits();
        bar.Open(target, selected, replace);
    }

    /// <summary>Opens the Document page's search for <paramref name="query"/> (used by screenshot mode).</summary>
    internal void ShowFind(string? query, bool replace = false)
    {
        if (query == null)
        {
            _documentFind?.Close();
            return;
        }
        SelectWorkspaceTab(DocumentTabIndex);
        _documentFind!.Open(_documentTarget!, query, replace);
        UpdateLayout();
        _findLayer?.InvalidateVisual(); // draw the highlights now, not after the short redraw delay
    }

    private void FindAgain(int direction)
    {
        var (bar, target, selected) = ActiveFind();
        if (!bar.IsOpen)
        {
            bar.Open(target, selected);
            return;
        }
        bar.Move(direction);
    }

    /// <summary>The text changed or the page was drawn again: an open search runs again shortly.</summary>
    private void ScheduleFindRefresh()
    {
        if (_documentFind?.IsOpen != true && _sourceFind?.IsOpen != true)
        {
            return;
        }
        if (_findRefreshTimer == null)
        {
            _findRefreshTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(400) };
            _findRefreshTimer.Tick += (_, _) =>
            {
                _findRefreshTimer.Stop();
                _documentFind?.Refresh();
                _sourceFind?.Refresh();
            };
        }
        _findRefreshTimer.Stop();
        _findRefreshTimer.Start();
    }
}
