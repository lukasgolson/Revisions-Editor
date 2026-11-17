using System.IO;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace RevisionsEditor.App.Rendering;

/// <summary>A drawn Mermaid diagram: its size in CSS pixels, or what is wrong with it.</summary>
internal sealed record MermaidResult(bool Ok, string? Error, double Width, double Height);

/// <summary>
/// Draws Mermaid diagrams with Mermaid itself (bundled, version 12.3.0) in the Edge WebView2 runtime, offline, and
/// saves them as a PDF page the size of the drawing: vector for LaTeX, drawn as an image for the editor and Word
/// like any PDF figure. The control can be shown (the live preview in the Mermaid dialog).
/// </summary>
internal sealed class MermaidView : IDisposable
{
    private const string Host = "mermaid.revisions-editor.local";
    private readonly TaskCompletionSource<bool> _ready = new();
    private TaskCompletionSource<MermaidResult>? _pending;
    private MermaidResult? _last;

    public WebView2 Control { get; } = new() { DefaultBackgroundColor = System.Drawing.Color.White };

    /// <summary>Where Mermaid and its page are (next to the app).</summary>
    public static string AssetsFolder => Path.Combine(AppContext.BaseDirectory, "Assets", "Mermaid");

    public static bool IsAvailable
    {
        get
        {
            try
            {
                return File.Exists(Path.Combine(AssetsFolder, "mermaid.min.js")) && CoreWebView2Environment.GetAvailableBrowserVersionString() != null;
            }
            catch (WebView2RuntimeNotFoundException)
            {
                return false;
            }
        }
    }

    /// <summary>Starts WebView2 (its data in the app's local folder) and loads Mermaid. The control must be in a window.</summary>
    public async Task InitializeAsync()
    {
        string data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Revisions Editor", "WebView2");
        var environment = await CoreWebView2Environment.CreateAsync(null, data);
        await Control.EnsureCoreWebView2Async(environment);
        var core = Control.CoreWebView2;
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.SetVirtualHostNameToFolderMapping(Host, AssetsFolder, CoreWebView2HostResourceAccessKind.DenyCors);
        core.WebMessageReceived += OnMessage;
        // Nothing outside the bundled page is loaded.
        core.NavigationStarting += (_, e) =>
        {
            if (!e.Uri.StartsWith($"https://{Host}/", StringComparison.OrdinalIgnoreCase)) e.Cancel = true;
        };
        core.Navigate($"https://{Host}/render.html");
        if (await Task.WhenAny(_ready.Task, Task.Delay(TimeSpan.FromSeconds(20))) != _ready.Task)
        {
            throw new TimeoutException("Mermaid did not load.");
        }
    }

    private void OnMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        using var json = JsonDocument.Parse(e.WebMessageAsJson);
        var root = json.RootElement;
        if (root.TryGetProperty("ready", out _))
        {
            _ready.TrySetResult(true);
            return;
        }
        var result = root.GetProperty("ok").GetBoolean()
            ? new MermaidResult(true, null, root.GetProperty("width").GetDouble(), root.GetProperty("height").GetDouble())
            : new MermaidResult(false, root.GetProperty("error").GetString(), 0, 0);
        if (result.Ok) _last = result;
        _pending?.TrySetResult(result);
    }

    /// <summary>Draws a diagram; the page then shows it (or still the last good one, when it has errors).</summary>
    public async Task<MermaidResult> RenderAsync(string code)
    {
        _pending = new TaskCompletionSource<MermaidResult>();
        await Control.CoreWebView2.ExecuteScriptAsync($"draw({JsonSerializer.Serialize(code)})");
        var done = await Task.WhenAny(_pending.Task, Task.Delay(TimeSpan.FromSeconds(20)));
        return done == _pending.Task ? _pending.Task.Result : new MermaidResult(false, "Mermaid took too long to draw this.", 0, 0);
    }

    /// <summary>Saves the diagram last drawn without errors as a one-page PDF the size of the drawing.</summary>
    public async Task<bool> SavePdfAsync(string path)
    {
        if (_last is not { } drawn) return false;
        var settings = Control.CoreWebView2.Environment.CreatePrintSettings();
        settings.Orientation = CoreWebView2PrintOrientation.Portrait;
        settings.PageWidth = (drawn.Width + 2) / 96.0;
        settings.PageHeight = (drawn.Height + 2) / 96.0;
        settings.MarginTop = settings.MarginBottom = settings.MarginLeft = settings.MarginRight = 0;
        settings.ScaleFactor = 1;
        settings.ShouldPrintBackgrounds = true;
        settings.ShouldPrintHeaderAndFooter = false;
        settings.ShouldPrintSelectionOnly = false;
        return await Control.CoreWebView2.PrintToPdfAsync(path, settings);
    }

    public void Dispose() => Control.Dispose();
}
