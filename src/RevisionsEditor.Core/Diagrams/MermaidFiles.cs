using System.Text;
using RevisionsEditor.Core.Scholarly;

namespace RevisionsEditor.Core.Diagrams;

/// <summary>
/// A Mermaid figure's files: its source (figures/&lt;name&gt;.mmd, edited and versioned) and the PDF Mermaid drew from
/// it (figures/&lt;name&gt;.pdf: vector in the LaTeX output, drawn as an image for the editor and Word).
/// </summary>
public static class MermaidFiles
{
    public const string Extension = ".mmd";

    /// <summary>Whether a figure is drawn by Mermaid: its source= attribute names a .mmd file.</summary>
    public static string? SourceOf(FigureBlock figure)
        => figure.Attributes.TryGetValue("source", out var source) && source.EndsWith(Extension, StringComparison.OrdinalIgnoreCase) ? source : null;

    /// <summary>Saves the source and the drawing (from <paramref name="pdfFile"/>); a new figure gets figures/&lt;label&gt;.mmd.</summary>
    public static (string SourcePath, string PdfPath) Save(string workspace, string? existingSource, string? label, string source, string pdfFile)
    {
        string relative = !string.IsNullOrWhiteSpace(existingSource)
            ? existingSource.Replace('\\', '/')
            : FreeName(workspace, label);
        string pdfRelative = Path.ChangeExtension(relative, ".pdf").Replace('\\', '/');
        string full = Full(workspace, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, source.Replace("\r\n", "\n"), new UTF8Encoding(false));
        File.Copy(pdfFile, Full(workspace, pdfRelative), overwrite: true);
        return (relative, pdfRelative);
    }

    private static string FreeName(string workspace, string? label)
    {
        string stem = label is { Length: > 4 } l && l.StartsWith("fig:", StringComparison.Ordinal) ? l[4..] : label ?? "mermaid";
        stem = new string(stem.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray()).Trim('-');
        if (stem.Length == 0) stem = "mermaid";
        string relative = $"{PaperSettings.FiguresFolder}/{stem}{Extension}";
        for (int n = 2; File.Exists(Full(workspace, relative)) || File.Exists(Full(workspace, Path.ChangeExtension(relative, ".pdf"))); n++)
        {
            relative = $"{PaperSettings.FiguresFolder}/{stem}-{n}{Extension}";
        }
        return relative;
    }

    private static string Full(string workspace, string relative)
        => Path.IsPathRooted(relative) ? relative : Path.Combine(workspace, relative.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>Starting points: a flowchart, a Gantt chart and a sequence diagram.</summary>
    public static IReadOnlyList<(string Name, string Source)> Templates { get; } =
    [
        ("Flowchart", """
            flowchart LR
                A[Drone video] --> B(Frame extraction)
                B --> C{Enough overlap?}
                C -->|yes| D[Feature matching]
                C -- no --> B
                subgraph rec [Reconstruction]
                  D --> E[(Point cloud)]
                end
            """),
        ("Gantt chart", """
            gantt
                title Thesis plan
                dateFormat YYYY-MM-DD
                axisFormat %b %Y
                section Fieldwork
                Drone flights           :done,    f1, 2025-06-01, 2025-08-15
                Tree measurements       :done,    f2, 2025-06-15, 60d
                section Analysis
                Reconstruction pipeline :active,  a1, 2025-09-01, 2025-12-20
                Dieback model           :         a2, after a1, 60d
                section Writing
                Paper 3                 :         w1, 2026-01-15, 90d
                Thesis defence          :milestone, m1, 2026-06-15, 0d
            """),
        ("Sequence diagram", """
            sequenceDiagram
                participant App as Revisions Editor
                participant Docs as Google Docs
                App->>Docs: Apply merged changes
                Docs-->>App: Current text of the tab
                App->>App: Three-way merge
            """)
    ];
}
