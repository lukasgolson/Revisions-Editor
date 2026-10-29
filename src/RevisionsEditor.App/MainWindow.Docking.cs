using System.IO;
using System.Windows;
using System.Windows.Input;
using AvalonDock;
using AvalonDock.Layout;
using AvalonDock.Serializer.Xml;
using AvalonDock.Themes;
using Microsoft.Win32;

namespace RevisionsEditor.App;

/// <summary>
/// The window is a docking layout: the views (Document, Source, Compare, …) are document panels and History,
/// Output, Changes and Checks are tool panels. Any of them can be dragged out into its own window, docked
/// elsewhere or shown side by side; the arrangement is saved per user and restored next time.
/// </summary>
public partial class MainWindow
{
    private static string LayoutPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Revisions Editor", "layout.xml");

    /// <summary>
    /// Set to false by tests: the user's saved layout is neither read nor overwritten, and the most recent
    /// workspace is not opened at startup.
    /// </summary>
    public static bool UseSavedLayout { get; set; } = true;

    private LayoutContent[] _panels = [];
    private LayoutAnchorable[] _toolPanels = [];
    private Dictionary<string, FrameworkElement> _panelContent = [];
    private string? _defaultLayout;
    private bool _sourceHadFocusLast;

    private void InitializeDocking()
    {
        // In the order the views were tabs, so their indexes (DocumentTabIndex, …) still apply.
        _panels = [DocumentPanel, SourcePanel, ComparePanel, ProvenancePanel, FormattingPanel, PaperPanel, ReferencesPanel, FiguresPanel, ReviewersPanel];
        _toolPanels = [HistoryPanel, OutputPanel, ChangesPanel, ChecksPanel, NotesPanel];
        _panelContent = _panels.Concat(_toolPanels).ToDictionary(p => p.ContentId, p => (FrameworkElement)p.Content, StringComparer.Ordinal);

        foreach (var content in _panelContent.Values)
        {
            // A panel moved to its own window keeps the bindings it had in the main window.
            content.DataContext = ViewModel;
            content.IsVisibleChanged += (_, _) => Dispatcher.BeginInvoke(() =>
            {
                FlushPendingTabRenders();
                UpdateEditorPosition();
            });
        }

        // Commands and their shortcuts also work in panels moved to their own windows.
        DockManager.LayoutFloatingWindowControlCreated += (_, e) =>
        {
            var window = e.LayoutFloatingWindowControl;
            window.DataContext = ViewModel;
            foreach (CommandBinding binding in CommandBindings)
            {
                window.CommandBindings.Add(binding);
            }
            window.InputBindings.AddRange(InputBindings);
        };

        HookPanels();
        ManuscriptEditor.GotKeyboardFocus += (_, _) => _sourceHadFocusLast = false;
        MarkdownSourceTextBox.GotKeyboardFocus += (_, _) => _sourceHadFocusLast = true;

        ApplyDockTheme();
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        Closed += (_, _) => SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;

        _defaultLayout = SerializeLayout();
        HookLayoutEvents();
        RestoreLayout();
        BuildViewsMenu();

        Bind(AppCommands.ResetLayout, _ => ResetLayout(), allowWhileBusy: true);
        Bind(AppCommands.ShowPanel, p => ShowPanel(p as string), p => p is string, allowWhileBusy: true);
        Bind(AppCommands.ClosePanel, _ => CloseActivePanel(), _ => ActivePanel() != null, allowWhileBusy: true);
    }

    /// <summary>The view or tool panel that has the focus (or is in front).</summary>
    private LayoutContent? ActivePanel()
        => _panels.Concat<LayoutContent>(_toolPanels).FirstOrDefault(p => IsPanelOpen(p) && p.IsActive);

    private void CloseActivePanel()
    {
        if (ActivePanel() is { } panel)
        {
            ClosePanel(panel);
        }
    }

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category == UserPreferenceCategory.General)
        {
            Dispatcher.BeginInvoke(ApplyDockTheme);
        }
    }

    /// <summary>Panel chrome in the app's light or dark theme.</summary>
    private void ApplyDockTheme()
    {
        var mode = Application.Current.ThemeMode;
        bool dark = mode == ThemeMode.Dark || (mode != ThemeMode.Light && SystemUsesDarkTheme());
        DockManager.Theme = dark ? new Vs2013DarkTheme() : new Vs2013LightTheme();
    }

    private static bool SystemUsesDarkTheme()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("AppsUseLightTheme") is int light && light == 0;
    }

    /// <summary>True when a view is in front: the selected tab of its pane, or on screen in its own window.</summary>
    private bool IsPanelShown(int index)
        => index < _panels.Length && IsPanelOpen(_panels[index]) && (_panels[index].IsSelected || _panels[index].Content is FrameworkElement { IsVisible: true });

    /// <summary>The panel is in the layout (a view not closed, a tool panel not hidden).</summary>
    private bool IsPanelOpen(LayoutContent panel)
        => panel is LayoutAnchorable anchorable
            ? !anchorable.IsHidden && anchorable.Root == DockManager.Layout
            : panel.Parent != null && panel.Root == DockManager.Layout;

    /// <summary>Where each closed view was, so it opens there again.</summary>
    private readonly Dictionary<LayoutContent, LayoutDocumentPane> _closedFrom = [];

    /// <summary>Opens a closed view (in the pane it was in, at its place among the views) or shows a hidden tool panel.</summary>
    private void OpenPanel(LayoutContent panel)
    {
        if (panel is LayoutAnchorable anchorable)
        {
            if (anchorable.IsHidden)
            {
                anchorable.Show();
            }
            return;
        }
        if (IsPanelOpen(panel) || panel is not LayoutDocument document)
        {
            return;
        }
        if (document.Parent is ILayoutContainer stale)
        {
            stale.RemoveChild(document); // still in a layout that was replaced
        }
        // Closing (or loading a layout without it) can leave the view without its content: put it back.
        if (document.Content == null && _panelContent.TryGetValue(document.ContentId, out var content))
        {
            document.Content = content;
        }
        var pane = _closedFrom.TryGetValue(panel, out var previous) && previous.Root == DockManager.Layout
            ? previous
            : DockManager.Layout.Descendents().OfType<LayoutDocumentPane>().FirstOrDefault();
        if (pane == null)
        {
            pane = new LayoutDocumentPane();
            if (DockManager.Layout.Descendents().OfType<LayoutDocumentPaneGroup>().FirstOrDefault() is { } group)
            {
                group.Children.Add(pane);
            }
            else
            {
                DockManager.Layout.RootPanel.Children.Add(pane);
            }
        }
        // At its usual place: after the views that come before it and are in this pane.
        int order = Array.IndexOf(_panels, panel);
        int at = pane.Children.Count(c => Array.IndexOf(_panels, c) is int i && i >= 0 && i < order);
        pane.Children.Insert(Math.Min(at, pane.Children.Count), document);
        _closedFrom.Remove(panel);
    }

    /// <summary>Closes a view (it can be opened again from the Views toolbar) or hides a tool panel.</summary>
    private void ClosePanel(LayoutContent panel)
    {
        if (panel == DocumentPanel || panel.ContentId == "document")
        {
            FlushEditorEdits();
        }
        if (panel is LayoutAnchorable anchorable)
        {
            anchorable.Hide();
        }
        else
        {
            RememberPane(panel);
            panel.Close();
        }
        UpdateViewsMenu();
    }

    private void RememberPane(LayoutContent panel)
    {
        if (panel.Parent is LayoutDocumentPane pane)
        {
            _closedFrom[panel] = pane;
        }
    }

    /// <summary>The views closed in the current layout (saved with it, so they stay closed next time).</summary>
    private IEnumerable<string> ClosedPanelIds() => _panels.Where(p => !IsPanelOpen(p)).Select(p => p.ContentId);

    private void HookLayoutEvents()
    {
        // Closing a view's tab (its × or the tab's menu) closes it; it opens again from the Views toolbar.
        DockManager.DocumentClosing += (_, e) =>
        {
            if (e.Document.ContentId == "document")
            {
                FlushEditorEdits();
            }
            RememberPane(e.Document);
        };
        DockManager.DocumentClosed += (_, _) => Dispatcher.BeginInvoke(UpdateViewsMenu);
        DockManager.AnchorableHidden += (_, _) => Dispatcher.BeginInvoke(UpdateViewsMenu);
        DockManager.ActiveContentChanged += (_, _) => UpdateViewsMenu();
        DockManager.LayoutChanged += (_, _) =>
        {
            DockManager.Layout.Updated += (_, _) => Dispatcher.BeginInvoke(UpdateViewsMenu, System.Windows.Threading.DispatcherPriority.Background);
            Dispatcher.BeginInvoke(UpdateViewsMenu);
        };
        DockManager.Layout.Updated += (_, _) => Dispatcher.BeginInvoke(UpdateViewsMenu, System.Windows.Threading.DispatcherPriority.Background);
    }

    // ---- Views menu ------------------------------------------------------------------------------

    private readonly Dictionary<string, System.Windows.Controls.MenuItem> _viewItems = new(StringComparer.Ordinal);

    /// <summary>
    /// The Views menu: every view and tool panel, ticked while it is open. Choosing a closed one opens it (and
    /// brings it to the front); choosing an open one closes it. Then Close Panel and Reset Window Layout.
    /// </summary>
    private void BuildViewsMenu()
    {
        ViewsMenu.Items.Clear();
        _viewItems.Clear();
        string[] accessKeys = ["_Document", "_Source", "_Compare", "_Provenance", "_Formatting", "P_aper", "Refere_nces", "Fi_gures", "Re_viewers", "_History", "_Output", "C_hanges", "Chec_ks", "No_tes"];
        int k = 0;
        foreach (var panel in _panels.Concat<LayoutContent>(_toolPanels))
        {
            if (panel == _toolPanels.FirstOrDefault())
            {
                ViewsMenu.Items.Add(new System.Windows.Controls.Separator());
            }
            string id = panel.ContentId;
            var item = new System.Windows.Controls.MenuItem { Header = k < accessKeys.Length ? accessKeys[k] : panel.Title, IsCheckable = false };
            k++;
            item.Click += (_, _) => OnViewItem(id);
            _viewItems[id] = item;
            ViewsMenu.Items.Add(item);
        }
        ViewsMenu.Items.Add(new System.Windows.Controls.Separator());
        ViewsMenu.Items.Add(new System.Windows.Controls.MenuItem { Command = AppCommands.ClosePanel, Header = "_Close Panel" });
        ViewsMenu.Items.Add(new System.Windows.Controls.MenuItem { Command = AppCommands.ResetLayout, Header = "_Reset Window Layout" });
        ViewsMenu.SubmenuOpened += (_, _) => UpdateViewsMenu();
        UpdateViewsMenu();
    }

    /// <summary>For tests: whether a view or tool panel is open, and whether the Views menu ticks it.</summary>
    internal (bool Open, bool ButtonChecked) ViewState(string id)
        => (FindPanel(id) is { } panel && IsPanelOpen(panel), _viewItems.TryGetValue(id, out var item) && item.IsChecked);

    /// <summary>For tests: what choosing the view in the Views menu does.</summary>
    internal void ClickViewButton(string id) => OnViewItem(id);

    /// <summary>Opens a closed view (in front), or closes an open one.</summary>
    private void OnViewItem(string id)
    {
        if (FindPanel(id) is not { } panel)
        {
            return;
        }
        if (IsPanelOpen(panel))
        {
            ClosePanel(panel);
        }
        else
        {
            ShowPanel(id);
            if (panel.Content is UIElement content)
            {
                content.Focus();
            }
        }
        UpdateViewsMenu();
    }

    /// <summary>For tests: closes a view as its tab's close button does.</summary>
    internal void CloseView(string id)
    {
        if (FindPanel(id) is { } panel)
        {
            ClosePanel(panel);
        }
    }

    /// <summary>Ticks the open views in the Views menu.</summary>
    private void UpdateViewsMenu()
    {
        foreach (var (id, item) in _viewItems)
        {
            if (FindPanel(id) is not { } panel)
            {
                continue;
            }
            bool open = IsPanelOpen(panel);
            item.IsChecked = open;
            item.ToolTip = open ? $"Close {panel.Title} (open it again here)" : $"Open {panel.Title}";
        }
    }

    private readonly HashSet<LayoutContent> _hookedPanels = [];

    /// <summary>Views are drawn when they come to the front (panels are new objects after a layout is loaded).</summary>
    private void HookPanels()
    {
        foreach (var panel in _panels.Concat<LayoutContent>(_toolPanels))
        {
            if (_hookedPanels.Add(panel))
            {
                panel.IsSelectedChanged += (_, _) => Dispatcher.BeginInvoke(() =>
                {
                    FlushPendingTabRenders();
                    UpdateEditorPosition();
                });
            }
        }
    }

    /// <summary>Brings a view to the front (in whichever pane or window it is now).</summary>
    public void SelectWorkspaceTab(int index)
    {
        if (index < _panels.Length)
        {
            OpenPanel(_panels[index]);
            _panels[index].IsSelected = true;
            _panels[index].IsActive = true;
            UpdateLayout();
        }
        FlushPendingTabRenders();
    }

    /// <summary>The panel with this id, as it is in the current layout.</summary>
    internal LayoutContent? FindPanel(string contentId)
        => _panels.Concat<LayoutContent>(_toolPanels).FirstOrDefault(p => p.ContentId == contentId);

    private void ShowPanel(string? contentId)
    {
        var panel = _panels.Concat<LayoutContent>(_toolPanels).FirstOrDefault(p => p.ContentId == contentId);
        if (panel != null)
        {
            OpenPanel(panel);
            panel.IsSelected = true;
            panel.IsActive = true;
        }
        UpdateViewsMenu();
    }

    internal string SerializeLayout()
    {
        var serializer = new XmlLayoutSerializer(DockManager);
        using var stream = new MemoryStream();
        serializer.Serialize(stream);
        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>Replaces the layout, putting each panel's content back by its id.</summary>
    internal bool LoadLayout(string xml)
    {
        try
        {
            var serializer = new XmlLayoutSerializer(DockManager);
            serializer.LayoutSerializationCallback += (_, e) =>
            {
                if (e.Model.ContentId is { } id && _panelContent.TryGetValue(id, out var content))
                {
                    e.Content = content;
                }
            };
            using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(xml));
            serializer.Deserialize(stream);
            RefreshPanelReferences();
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Xml.XmlException or ArgumentException or NullReferenceException)
        {
            ViewModel.AddLog($"The saved window layout could not be restored ({ex.Message}); the default layout is used.");
            return false;
        }
    }

    /// <summary>After a layout is loaded the panels are new objects: find them again by id.</summary>
    private void RefreshPanelReferences()
    {
        var all = DockManager.Layout.Descendents().OfType<LayoutContent>().ToDictionary(c => c.ContentId ?? string.Empty, StringComparer.Ordinal);
        LayoutContent Find(LayoutContent old) => all.TryGetValue(old.ContentId, out var found) ? found : old;
        _panels = _panels.Select(Find).ToArray();
        _toolPanels = _toolPanels.Select(p => Find(p) as LayoutAnchorable ?? p).ToArray();
        // A view the layout leaves out stays closed: take it out of the layout it was in before.
        foreach (var panel in _panels.Where(p => p.Root != DockManager.Layout))
        {
            if (panel.Parent is ILayoutContainer stale)
            {
                stale.RemoveChild(panel);
            }
        }
        _closedFrom.Clear();
        HookPanels();
        UpdateViewsMenu();
    }

    private void RestoreLayout()
    {
        if (!UseSavedLayout || App.IsRenderingScreenshots || !File.Exists(LayoutPath))
        {
            return;
        }
        try
        {
            string xml = File.ReadAllText(LayoutPath);
            // Every panel must be in the saved layout, or listed as closed; otherwise it was saved by an older version.
            var closed = System.Text.RegularExpressions.Regex.Match(xml, @"<!--\s*closed:\s*(?<ids>[^>]*?)\s*-->") is { Success: true } m
                ? m.Groups["ids"].Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.Ordinal)
                : [];
            // A tool panel added in a later version (Notes) may be missing: it is put back beside Checks.
            var toolIds = _toolPanels.Select(p => p.ContentId).ToHashSet(StringComparer.Ordinal);
            if (_panelContent.Keys.All(id => closed.Contains(id) || toolIds.Contains(id) || xml.Contains($"ContentId=\"{id}\"", StringComparison.Ordinal)))
            {
                if (LoadLayout(xml))
                {
                    RestoreMissingToolPanels();
                }
                else if (_defaultLayout != null)
                {
                    LoadLayout(_defaultLayout);
                }
            }
        }
        catch (IOException)
        {
        }
    }

    /// <summary>Tool panels a saved layout does not know go into the pane of the Checks panel (or the first tool pane).</summary>
    private void RestoreMissingToolPanels()
    {
        for (int i = 0; i < _toolPanels.Length; i++)
        {
            var panel = _toolPanels[i];
            if (panel.Root == DockManager.Layout)
            {
                continue;
            }
            if (panel.Parent is ILayoutContainer stale)
            {
                stale.RemoveChild(panel);
            }
            if (panel.Content == null && _panelContent.TryGetValue(panel.ContentId, out var content))
            {
                panel.Content = content;
            }
            var pane = FindPanel("checks")?.Parent as LayoutAnchorablePane
                ?? DockManager.Layout.Descendents().OfType<LayoutAnchorablePane>().FirstOrDefault();
            if (pane != null)
            {
                pane.Children.Add(panel);
            }
        }
        UpdateViewsMenu();
    }

    private void SaveLayout()
    {
        if (!UseSavedLayout || App.IsRenderingScreenshots)
        {
            return;
        }
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LayoutPath)!);
            var closed = ClosedPanelIds().ToList();
            File.WriteAllText(LayoutPath, SerializeLayout() + (closed.Count > 0 ? $"\n<!-- closed: {string.Join(",", closed)} -->\n" : string.Empty));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            // The layout is a convenience; not saving it is not worth interrupting closing.
        }
    }

    private void ResetLayout()
    {
        if (_defaultLayout != null && LoadLayout(_defaultLayout))
        {
            SelectWorkspaceTab(DocumentTabIndex);
        }
    }
}
