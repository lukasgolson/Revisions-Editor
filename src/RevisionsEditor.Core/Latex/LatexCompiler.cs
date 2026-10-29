using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace RevisionsEditor.Core.Latex;

/// <summary>A LaTeX engine that can typeset the export.</summary>
public sealed record LatexEngine(string Name, string ExecutablePath, bool IsTectonic)
{
    public override string ToString() => IsTectonic ? $"Tectonic ({ExecutablePath})" : $"{Name} ({ExecutablePath})";
}

public sealed record LatexCompileResult(bool Success, string? PdfPath, string Log, IReadOnlyList<string> Errors);

/// <summary>
/// Typesets a .tex file to PDF. The app ships with Tectonic, a self-contained LaTeX engine (one executable,
/// XeTeX inside) that fetches the LaTeX packages a document needs on first use and caches them. A copy can also
/// be downloaded at run time, and a LuaLaTeX or XeLaTeX already installed (MiKTeX, TeX Live) is used otherwise.
/// </summary>
public static class LatexCompiler
{
    public const string TectonicVersion = "0.15.0";
    public const string TectonicUrl = "https://github.com/tectonic-typesetting/tectonic/releases/download/tectonic%400.15.0/tectonic-0.15.0-x86_64-pc-windows-msvc.zip";
    /// <summary>Size of the release archive, checked after downloading (the release publishes no checksum).</summary>
    public const long TectonicArchiveSize = 19268177;

    private static readonly Regex TexErrorRegex = new(@"^(?:! .+|.+?:\d+: .+|error: .+)$", RegexOptions.Multiline | RegexOptions.Compiled);

    /// <summary>Where the build puts Tectonic, next to the app.</summary>
    public static string BundledTectonicPath => Path.Combine(AppContext.BaseDirectory, "tools", "tectonic", "tectonic.exe");

    /// <summary>Where a copy downloaded at run time is kept.</summary>
    public static string DownloadedTectonicPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Revisions Editor", "tectonic", TectonicVersion, "tectonic.exe");

    /// <summary>The engine to use: the bundled or downloaded Tectonic, else one on the PATH.</summary>
    public static LatexEngine? FindEngine()
    {
        foreach (string path in new[] { BundledTectonicPath, DownloadedTectonicPath })
        {
            if (File.Exists(path))
            {
                return new LatexEngine("Tectonic", path, true);
            }
        }
        foreach (var (name, exe) in new[] { ("Tectonic", "tectonic.exe"), ("LuaLaTeX", "lualatex.exe"), ("XeLaTeX", "xelatex.exe") })
        {
            if (FindOnPath(exe) is { } found)
            {
                return new LatexEngine(name, found, name == "Tectonic");
            }
        }
        return null;
    }

    private static string? FindOnPath(string executable)
    {
        foreach (string dir in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                string candidate = Path.Combine(dir.Trim('"'), executable);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch (ArgumentException)
            {
                // A malformed PATH entry.
            }
        }
        return null;
    }

    /// <summary>Downloads Tectonic from its GitHub release into the user's local application data.</summary>
    public static async Task<LatexEngine> DownloadTectonicAsync(IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        string target = DownloadedTectonicPath;
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        string archive = Path.Combine(Path.GetDirectoryName(target)!, "tectonic.zip");

        progress?.Report($"Downloading Tectonic {TectonicVersion}…");
        using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) })
        {
            http.DefaultRequestHeaders.UserAgent.ParseAdd("RevisionsEditor/1.0");
            await using var response = await http.GetStreamAsync(TectonicUrl, cancellationToken);
            await using var file = File.Create(archive);
            await response.CopyToAsync(file, cancellationToken);
        }

        long size = new FileInfo(archive).Length;
        if (size != TectonicArchiveSize)
        {
            File.Delete(archive);
            throw new InvalidOperationException($"The Tectonic download has an unexpected size ({size:N0} bytes instead of {TectonicArchiveSize:N0}); it was discarded.");
        }

        using (var zip = ZipFile.OpenRead(archive))
        {
            var entry = zip.Entries.FirstOrDefault(e => e.Name.Equals("tectonic.exe", StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException("The Tectonic download does not contain tectonic.exe.");
            entry.ExtractToFile(target, overwrite: true);
        }
        File.Delete(archive);
        return new LatexEngine("Tectonic", target, true);
    }

    /// <summary>
    /// Typesets <paramref name="texPath"/> into a PDF next to it. Tectonic reruns as often as cross-references
    /// need; other engines are run twice. The first Tectonic run downloads the packages it needs (internet).
    /// </summary>
    public static async Task<LatexCompileResult> CompileAsync(string texPath, LatexEngine engine, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        string folder = Path.GetDirectoryName(Path.GetFullPath(texPath))!;
        string file = Path.GetFileName(texPath);
        string pdf = Path.Combine(folder, Path.ChangeExtension(file, ".pdf"));
        if (File.Exists(pdf))
        {
            File.Delete(pdf);
        }

        var log = new StringBuilder();
        IEnumerable<string[]> runs = engine.IsTectonic
            ? [["--keep-logs", "--chatter", "minimal", "--outdir", folder, file]]
            : [["-interaction=nonstopmode", "-halt-on-error", "-file-line-error", file], ["-interaction=nonstopmode", "-halt-on-error", "-file-line-error", file]];

        int exit = 0;
        foreach (string[] arguments in runs)
        {
            (exit, string output) = await RunAsync(engine.ExecutablePath, arguments, folder, timeout, cancellationToken);
            log.AppendLine(output);
            if (exit != 0)
            {
                break;
            }
        }

        string texLog = Path.Combine(folder, Path.ChangeExtension(file, ".log"));
        string fullLog = log + (File.Exists(texLog) ? "\n" + await File.ReadAllTextAsync(texLog, cancellationToken) : string.Empty);
        var errors = TexErrorRegex.Matches(fullLog).Select(m => m.Value.Trim()).Distinct().Take(20).ToList();
        bool success = exit == 0 && File.Exists(pdf);
        if (!success && errors.Count == 0)
        {
            errors.Add(exit == -1 ? "The LaTeX engine took too long and was stopped." : $"The LaTeX engine stopped with exit code {exit}.");
        }
        return new LatexCompileResult(success, success ? pdf : null, fullLog, errors);
    }

    private static async Task<(int ExitCode, string Output)> RunAsync(string executable, IReadOnlyList<string> arguments, string workingDirectory, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (string argument in arguments)
        {
            psi.ArgumentList.Add(argument);
        }

        using var process = Process.Start(psi) ?? throw new InvalidOperationException($"Could not start {executable}.");
        process.StandardInput.Close(); // never wait for TeX's interactive prompt
        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }
            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            return (-1, await stdout + await stderr);
        }
        return (process.ExitCode, await stdout + "\n" + await stderr);
    }
}
