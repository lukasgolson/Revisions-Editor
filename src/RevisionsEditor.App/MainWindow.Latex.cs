using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using RevisionsEditor.App.Dialogs;
using RevisionsEditor.Core.Latex;

namespace RevisionsEditor.App;

/// <summary>Typesetting with LaTeX: PDF and LaTeX-source export.</summary>
public partial class MainWindow
{
    private void InitializeLatex()
    {
        Bind(AppCommands.ExportPdf, _ => ExportPdf(), _ => CanExportDocument());
        Bind(AppCommands.ExportLatexSource, _ => ExportLatexSource(), _ => CanExportDocument());
        Bind(AppCommands.ImportKeywords, _ =>
        {
            if (!Paper.ImportKeywordsFromManuscript())
            {
                ShowEditorNotice("The manuscript has no “Keywords:” paragraph.");
            }
        }, _ => ViewModel.HasWorkspace);
    }

    private LatexTemplate SuggestedTemplate() => Paper.Project.Journal.Publisher switch
    {
        "Elsevier" => LatexTemplate.Elsevier,
        "IEEE" => LatexTemplate.Ieee,
        _ => LatexTemplate.JournalTwoColumn
    };

    /// <summary>The engine, downloading Tectonic (with the author's consent) when the app was built without it.</summary>
    private async Task<LatexEngine?> EnsureEngineAsync()
    {
        if (LatexCompiler.FindEngine() is { } engine)
        {
            return engine;
        }
        var answer = MessageBox.Show(this,
            $"Revisions Editor typesets PDFs with Tectonic, a self-contained LaTeX engine, which is not included in this copy of the app.\n\n"
            + $"Download Tectonic {LatexCompiler.TectonicVersion} (about 19 MB) from its GitHub release now? It is kept in your user folder. The first PDF then also downloads the LaTeX packages it needs.",
            "Export PDF", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes)
        {
            return null;
        }
        LatexEngine? downloaded = null;
        await RunAsync(async () => downloaded = await LatexCompiler.DownloadTectonicAsync(), "Download Tectonic");
        return downloaded;
    }

    /// <summary>Images LaTeX cannot read (TIFF, BMP, GIF) are converted to PNG for the export.</summary>
    private static string? ConvertImageToPng(string source, string folder)
    {
        try
        {
            var decoder = BitmapDecoder.Create(new Uri(source), BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(decoder.Frames[0]);
            string name = Path.GetFileNameWithoutExtension(source) + ".png";
            using var stream = File.Create(Path.Combine(folder, name));
            encoder.Save(stream);
            return name;
        }
        catch (Exception ex) when (ex is NotSupportedException or IOException or InvalidOperationException or ArgumentException)
        {
            return null;
        }
    }

    private async void ExportPdf()
    {
        var engine = await EnsureEngineAsync();
        if (engine == null)
        {
            return;
        }
        var dialog = new ExportPdfDialog(SuggestedTemplate(), engine.ToString(), Paper.Authors.Count > 0, Paper.Project.FrontMatter.DraftNoteLines.Count > 0) { Owner = this };
        if (dialog.ShowDialog() != true)
        {
            return;
        }
        var save = new SaveFileDialog
        {
            Title = "Export PDF",
            Filter = "PDF document (*.pdf)|*.pdf",
            FileName = SuggestedFileName(string.Empty, ".pdf"),
            InitialDirectory = ViewModel.WorkspacePath
        };
        if (save.ShowDialog(this) != true)
        {
            return;
        }

        string? sourceFolder = dialog.KeepSource
            ? Path.Combine(Path.GetDirectoryName(save.FileName)!, Path.GetFileNameWithoutExtension(save.FileName) + " (LaTeX)")
            : null;
        LatexCompileResult? result = null;
        if (!await RunAsync(async () => result = await ViewModel.ExportPdfAsync(save.FileName, engine, dialog.LatexTemplate, dialog.LineNumbers, sourceFolder, ConvertImageToPng, dialog.DraftNotes), "Export PDF"))
        {
            return;
        }

        if (result!.Success)
        {
            if (MessageBox.Show(this, $"The PDF was typeset with {engine.Name}.\n\nOpen it now?", "Export PDF", MessageBoxButton.YesNo, MessageBoxImage.Information) == MessageBoxResult.Yes)
            {
                Process.Start(new ProcessStartInfo(save.FileName) { UseShellExecute = true });
            }
            return;
        }

        string logPath = Path.ChangeExtension(save.FileName, ".log");
        File.WriteAllText(logPath, result.Log);
        MessageBox.Show(this,
            "LaTeX could not typeset the manuscript:\n\n" + string.Join("\n", result.Errors.Take(8))
            + $"\n\nThe full log is in {Path.GetFileName(logPath)}. If this is the first export, check the internet connection: the LaTeX packages are downloaded on first use.",
            "Export PDF", MessageBoxButton.OK, MessageBoxImage.Error);
    }

    private void ExportLatexSource()
    {
        var dialog = new ExportPdfDialog(SuggestedTemplate(), "none needed for the source", Paper.Authors.Count > 0, Paper.Project.FrontMatter.DraftNoteLines.Count > 0) { Owner = this, Title = "Export LaTeX Source" };
        if (dialog.ShowDialog() != true)
        {
            return;
        }
        var folder = new OpenFolderDialog { Title = "Choose a folder for the LaTeX source", InitialDirectory = ViewModel.WorkspacePath };
        if (folder.ShowDialog(this) == true)
        {
            Run(() =>
            {
                string tex = ViewModel.ExportLatexSource(folder.FolderName, dialog.LatexTemplate, dialog.LineNumbers, ConvertImageToPng, dialog.DraftNotes);
                ShowEditorNotice($"Wrote {Path.GetFileName(tex)} and its figures to {folder.FolderName}. Typeset it with XeLaTeX or LuaLaTeX.");
            }, "Export LaTeX Source");
        }
    }
}
