using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace RevisionsEditor.App;

public partial class App : Application
{
    private const string RenderScreenshotsSwitch = "--render-screenshots";

    /// <summary>Screenshot mode leaves the user's settings (such as the window layout) untouched.</summary>
    public static bool IsRenderingScreenshots { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnDispatcherUnhandledException;

        // Usage: RevisionsEditor.App.exe --render-screenshots <output folder> <workspace folder> [light|dark]
        if (e.Args.Length >= 3 && string.Equals(e.Args[0], RenderScreenshotsSwitch, StringComparison.OrdinalIgnoreCase))
        {
            if (e.Args.Length >= 4)
            {
                ThemeMode = string.Equals(e.Args[3], "dark", StringComparison.OrdinalIgnoreCase) ? ThemeMode.Dark : ThemeMode.Light;
            }
            IsRenderingScreenshots = true;
            RenderScreenshots(e.Args[1], e.Args[2]);
            return;
        }

        var window = new MainWindow();
        if (e.Args.Length >= 1 && Directory.Exists(e.Args[0]))
        {
            window.StartupWorkspace = e.Args[0];
        }
        MainWindow = window;
        window.Show();
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show(
            MainWindow is { IsVisible: true } owner ? owner : null!,
            $"An unexpected error occurred:\n\n{e.Exception.Message}",
            "Revisions Editor",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
        e.Handled = true;
    }

    private async void RenderScreenshots(string outDir, string workspace)
    {
        const int width = 1440;
        const int height = 900;

        try
        {
            Directory.CreateDirectory(outDir);
            var window = new MainWindow
            {
                Width = width,
                Height = height,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = 0,
                Top = 0
            };
            MainWindow = window;
            window.Show();
            if (!await window.OpenWorkspaceAsync(workspace, addToRecent: false))
            {
                throw new InvalidOperationException($"Could not open workspace '{workspace}'.");
            }

            string[] tabSlugs =
            [
                "tab1_document.png",
                "tab2_markdown_source.png",
                "tab3_compare.png",
                "tab4_provenance.png",
                "tab5_formatting_and_sync.png",
                "tab6_paper.png",
                "tab7_references.png",
                "tab8_figures_and_data.png",
                "tab9_reviewers.png"
            ];

            // The checks run in the background; give them a moment so the Checks pane has content.
            window.ViewModel.Paper.ScheduleChecks(immediately: true);
            for (int wait = 0; wait < 50 && window.ViewModel.Paper.Issues.Count == 0; wait++)
            {
                await Task.Delay(100);
            }
            window.ShowChecksPane();

            for (int i = 0; i < tabSlugs.Length; i++)
            {
                window.SelectWorkspaceTab(i);
                Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                SaveWindowPng(window, Path.Combine(outDir, tabSlugs[i]));
            }

            window.SelectWorkspaceTab(0);
            window.SetPreview(true);
            Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            SaveWindowPng(window, Path.Combine(outDir, "tab1_preview.png"));
            window.ShowFind("height");
            Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            SaveWindowPng(window, Path.Combine(outDir, "tab1_preview_find.png"));
            window.SetPreview(false);
            window.ShowFind("height", replace: true);
            Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            SaveWindowPng(window, Path.Combine(outDir, "tab1_find.png"));
            window.ShowFind(null);
            window.ShowNotesPane();
            Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            SaveWindowPng(window, Path.Combine(outDir, "panel_notes.png"));
            window.ShowChecksPane();

            // Dialogs, shown without blocking so they can be captured.
            var commits = window.ViewModel.CommitGraph.ToList();
            var dialogs = new List<(Window Dialog, string File)>
            {
                (new GitHubDialog(clone: true, "https://github.com/you/thesis-paper.git", @"C:\Users\you\Documents"), "dialog_open_from_github.png"),
                (new GitHubDialog(clone: false), "dialog_connect_github.png"),
                (new Dialogs.WordCountDialog(window.ViewModel.ComputeStatistics(), window.ViewModel.Paper.Project.Journal.Name, 42, 180), "dialog_word_count.png"),
                (new Dialogs.GoToLineDialog(180, ["submitted-v1"]), "dialog_go_to_line.png"),
                (new Dialogs.ExportPdfDialog(Core.Latex.LatexTemplate.Elsevier, "Tectonic (tools\\tectonic\\tectonic.exe)", titlePageFilled: true, hasDraftNotes: true), "dialog_export_pdf.png"),
                (new Dialogs.EquationDialog(null, new HashSet<string>()), "dialog_equation.png"),
                (new Dialogs.FigureDialog(null, new HashSet<string>()), "dialog_figure.png"),
                (TableEditorSample(window.ViewModel.WorkspacePath), "dialog_table.png"),
                (new Dialogs.CitationPickerDialog(window.ViewModel.Paper.References.ToList(), zoteroAvailable: true), "dialog_citation.png"),
                (new Dialogs.ReviewRoundDialog("Revision 1", [new("submitted-v1  (14/10/2026, Submitted to RSE)", "submitted-v1", null)]), "dialog_review_round.png")
            };
            if (commits.Count >= 2)
            {
                var options = commits.Skip(1).Select((c, n) => new ViewModels.MainViewModel.ImportBaseOption(c, n == 0 ? 0.97 : 0.62)).ToList();
                dialogs.Add((new ImportBaseDialog("Thesis (reviewed by J. Doe).docx", options, bestIsLatest: false), "dialog_import_base.png"));
            }
            foreach (var (dialog, file) in dialogs)
            {
                dialog.Owner = window;
                dialog.Show();
                Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                SaveWindowPng(dialog, Path.Combine(outDir, file));
                dialog.Close();
            }

            window.Close();
            Shutdown(0);
        }
        catch (Exception ex)
        {
            File.WriteAllText(Path.Combine(outDir, "startup_error.txt"), ex.ToString());
            Shutdown(1);
        }
    }

    /// <summary>The table editor on the workspace's first table (or a new table), for the screenshots.</summary>
    private static Window TableEditorSample(string workspace)
    {
        var doc = new Core.Git.GitRepositoryService(workspace).LoadCurrentWorkingDocument();
        var table = doc.Blocks.Where(b => b.Role == Core.Models.BlockStyleRole.Table)
            .Select(b => Core.Scholarly.ScholarlyBlocks.ParseTable(b.Text)).FirstOrDefault(t => t != null);
        return new Dialogs.TableEditorDialog(table, new HashSet<string>(), workspace);
    }

    /// <summary>
    /// Renders the window's client area (content over the window background) to a PNG file.
    /// </summary>
    private static void SaveWindowPng(Window window, string filePath)
    {
        var element = (FrameworkElement)window.Content;
        var dpi = VisualTreeHelper.GetDpi(element);
        var bounds = new Rect(0, 0, element.ActualWidth, element.ActualHeight);

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(window.Background, null, bounds);
            dc.DrawRectangle(new VisualBrush(element), null, bounds);
        }

        var rtb = new RenderTargetBitmap(
            (int)Math.Ceiling(bounds.Width * dpi.DpiScaleX),
            (int)Math.Ceiling(bounds.Height * dpi.DpiScaleY),
            dpi.PixelsPerInchX,
            dpi.PixelsPerInchY,
            PixelFormats.Pbgra32);
        rtb.Render(visual);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(rtb));
        using var fs = File.Create(filePath);
        encoder.Save(fs);
    }
}
