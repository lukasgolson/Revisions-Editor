using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using RevisionsEditor.Core.Equations;
using RevisionsEditor.Core.Formatting;
using RevisionsEditor.Core.Models;
using RevisionsEditor.Core.Scholarly;
using RevisionsEditor.Core.Serialization;

namespace RevisionsEditor.Core.Docx;

/// <summary>How a manuscript is written to Word.</summary>
public sealed class DocxExportOptions
{
    public FormattingRuleProfile? Profile { get; set; }
    /// <summary>Pending changes become Word tracked changes and comments; otherwise the final text is written.</summary>
    public bool IncludeTrackedChanges { get; set; }
    /// <summary>
    /// Pending changes are shown as highlighted new text instead of tracked changes (the "highlighted revised
    /// manuscript" some journals ask for). Deleted text is left out.
    /// </summary>
    public bool HighlightChanges { get; set; }
    /// <summary>Renders citations, cross-references, linked numbers, figures, tables and the reference list; null writes the text as typed.</summary>
    public ScholarlyContext? Scholarly { get; set; }
    /// <summary>Where figure images and table files are found (the workspace).</summary>
    public string? WorkspacePath { get; set; }
    /// <summary>
    /// Records the source of rendered citations, numbers, figures and tables in the file (content controls,
    /// hidden bookmarks and document variables), so a copy reviewed in Word can be imported back losslessly.
    /// </summary>
    public bool RoundTripMarkers { get; set; } = true;
    public bool LineNumbers { get; set; }
    /// <summary>Restart line numbers on each page (so text can be cited as "page 4, lines 12–18").</summary>
    public bool RestartLineNumbersEachPage { get; set; }
    /// <summary>Overrides the profile's line spacing with double spacing.</summary>
    public bool DoubleSpacing { get; set; }
    public bool PageNumbers { get; set; }
    /// <summary>Adds the generated reference list (after a "References" heading, or at the end).</summary>
    public bool IncludeBibliography { get; set; } = true;
    /// <summary>Puts the title page form's draft notes in a box under the title page (for drafts, not for the journal).</summary>
    public bool IncludeDraftNotes { get; set; }
    /// <summary>
    /// Wraps each paragraph in a hidden bookmark and records its name here (paragraph id → bookmark), so Word
    /// can report the page and lines each paragraph ended up on.
    /// </summary>
    public Dictionary<string, string>? ParagraphBookmarks { get; set; }
}

public static class DocxExportService
{
    private static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private static readonly XNamespace R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static readonly XNamespace M = "http://schemas.openxmlformats.org/officeDocument/2006/math";
    private static readonly XNamespace WP = "http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing";
    private static readonly XNamespace A = "http://schemas.openxmlformats.org/drawingml/2006/main";
    private static readonly XNamespace Pic = "http://schemas.openxmlformats.org/drawingml/2006/picture";
    /// <summary>Office 2016's SVG extension of a picture.</summary>
    private static readonly XNamespace Asvg = "http://schemas.microsoft.com/office/drawing/2016/SVG/main";
    private static readonly XNamespace PkgRel = "http://schemas.openxmlformats.org/package/2006/relationships";
    private static readonly XNamespace ContentTypesNs = "http://schemas.openxmlformats.org/package/2006/content-types";

    /// <summary>Prefix of hidden bookmarks that carry a paragraph's source syntax (see <see cref="DocxRoundTrip"/>).</summary>
    internal const string BlockBookmarkPrefix = "_reB";
    internal const string InlineTagPrefix = "re:i:";
    internal const string LabelTag = "re:label";
    internal const string SkipTag = "re:skip";

    private static readonly Regex CriticTokenRegex = new(
        @"(\{~~(?<repOld>.*?)~>(?<repNew>.*?)~~\})|(\{\+\+(?<ins>.*?)\+\+\})|(\{--(?<del>.*?)--\})|(\{==(?<hl>.*?)==\}\{>>(?<hlComment>.*?)<<\})|(\{>>(?<comment>.*?)<<\})",
        RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex ReferencesHeadingRegex = new(
        @"^\s*(references|bibliography|literature cited|works cited|reference list|cited literature)\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Exports a ManuscriptDocument to a valid .docx file styled strictly by the provided FormattingRuleProfile.
    /// When includeTrackedChanges is true, pending CriticMarkup hunks are written as native OpenXML w:ins, w:del, and w:comment elements.
    /// </summary>
    public static void ExportToDocx(
        ManuscriptDocument document,
        string outputFilePath,
        FormattingRuleProfile? formattingProfile = null,
        bool includeTrackedChanges = false)
        => Export(document, outputFilePath, new DocxExportOptions { Profile = formattingProfile, IncludeTrackedChanges = includeTrackedChanges });

    public static void ExportToDocx(
        ManuscriptDocument document,
        Stream outputStream,
        FormattingRuleProfile? formattingProfile = null,
        bool includeTrackedChanges = false)
        => Export(document, outputStream, new DocxExportOptions { Profile = formattingProfile, IncludeTrackedChanges = includeTrackedChanges });

    public static void Export(ManuscriptDocument document, string outputFilePath, DocxExportOptions options)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputFilePath))!);
        using var fs = new FileStream(outputFilePath, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        Export(document, fs, options);
    }

    public static void Export(ManuscriptDocument document, Stream outputStream, DocxExportOptions options)
    {
        var profile = (options.Profile ?? FormattingRuleProfile.GetBuiltInProfiles()[0]).Clone();
        if (options.DoubleSpacing)
        {
            profile.LineSpacingMultiplier = 2.0;
        }
        var workingDoc = document.Clone();
        FormattingRuleEngine.ApplyFormattingRules(workingDoc, profile);

        var writer = new Writer(workingDoc, profile, options);
        var body = writer.BuildBody();

        using var archive = new ZipArchive(outputStream, ZipArchiveMode.Create, leaveOpen: true);
        WriteXmlEntry(archive, "[Content_Types].xml", BuildContentTypesXml(writer.Images, options.PageNumbers));
        WriteXmlEntry(archive, "_rels/.rels", BuildRootRelsXml());
        WriteXmlEntry(archive, "word/_rels/document.xml.rels", BuildDocumentRelsXml(writer.Images, options.PageNumbers));
        WriteXmlEntry(archive, "word/styles.xml", BuildRuleDrivenStylesXml(profile));
        WriteXmlEntry(archive, "word/settings.xml", BuildSettingsXml(writer.DocumentVariables));
        WriteXmlEntry(archive, "word/document.xml", new XDocument(
            new XDeclaration("1.0", "UTF-8", "yes"),
            new XElement(W + "document",
                new XAttribute(XNamespace.Xmlns + "w", W),
                new XAttribute(XNamespace.Xmlns + "r", R),
                new XAttribute(XNamespace.Xmlns + "m", M),
                new XAttribute(XNamespace.Xmlns + "wp", WP),
                new XAttribute(XNamespace.Xmlns + "a", A),
                new XAttribute(XNamespace.Xmlns + "pic", Pic),
                body)));
        WriteXmlEntry(archive, "word/comments.xml", new XDocument(new XDeclaration("1.0", "UTF-8", "yes"), writer.Comments));
        if (options.PageNumbers)
        {
            WriteXmlEntry(archive, "word/footer1.xml", BuildFooterXml());
        }
        foreach (var image in writer.Images)
        {
            var entry = archive.CreateEntry($"word/{image.PartName}", CompressionLevel.NoCompression);
            using var s = entry.Open();
            s.Write(image.Bytes);
        }
    }

    private sealed record EmbeddedImage(string RelationshipId, string PartName, string Extension, string ContentType, byte[] Bytes);

    /// <summary>Builds the document body, collecting comments, images and document variables on the way.</summary>
    private sealed class Writer
    {
        private readonly ManuscriptDocument _doc;
        private readonly FormattingRuleProfile _profile;
        private readonly DocxExportOptions _options;
        private readonly ScholarlyContext? _scholarly;
        private readonly Dictionary<string, List<RevisionHunk>> _hunksByParagraph;
        private int _revId = 1;
        private int _commentId = 1;
        private int _bookmarkId = 1;
        private int _markerId = 1;
        private int _drawingId = 1;
        private bool _bibliographyWritten;

        public XElement Comments { get; } = new(W + "comments", new XAttribute(XNamespace.Xmlns + "w", W));
        public List<EmbeddedImage> Images { get; } = [];
        public List<(string Name, string Value)> DocumentVariables { get; } = [];

        public Writer(ManuscriptDocument doc, FormattingRuleProfile profile, DocxExportOptions options)
        {
            _doc = doc;
            _profile = profile;
            _options = options;
            _scholarly = options.Scholarly;
            _hunksByParagraph = CriticMarkupSerializer.GroupHunksByParagraph(doc.Hunks);
        }

        private bool Markers => _options.RoundTripMarkers;
        private bool Tracked => _options.IncludeTrackedChanges || _options.HighlightChanges;

        private bool _titlePageWritten;
        private string? _lastAbstractId;

        public XElement BuildBody()
        {
            var body = new XElement(W + "body");
            if (_scholarly is { Project.FrontMatter.Keywords.Count: > 0 })
            {
                var structure = new Checks.ManuscriptStructure(_doc.Blocks);
                // Keywords typed in the manuscript take precedence over the form's.
                _lastAbstractId = structure.Keywords().Block == null ? structure.AbstractBlocks().LastOrDefault()?.ParagraphId : null;
            }
            foreach (var block in _doc.Blocks)
            {
                int before = body.Elements().Count();
                switch (block.Role)
                {
                    case BlockStyleRole.Figure when _scholarly != null:
                        WriteFigure(body, block);
                        break;
                    case BlockStyleRole.Table when _scholarly != null:
                        WriteTable(body, block);
                        break;
                    case BlockStyleRole.Equation when _scholarly != null:
                        WriteEquation(body, block);
                        break;
                    default:
                        WriteParagraph(body, block);
                        break;
                }

                if (_options.ParagraphBookmarks != null)
                {
                    BookmarkParagraphs(block, body.Elements().Skip(before).ToList());
                }

                // The title page's authors and affiliations follow the title; the keywords follow the abstract.
                if (block.Role == BlockStyleRole.Title && !_titlePageWritten && _scholarly != null)
                {
                    WriteTitlePage(body);
                }
                if (block.ParagraphId == _lastAbstractId)
                {
                    WriteKeywords(body);
                }

                if (_options.IncludeBibliography && _scholarly is { HasCitations: true } && !_bibliographyWritten && IsReferencesHeading(block))
                {
                    WriteBibliography(body);
                }
            }

            if (_options.IncludeBibliography && _scholarly is { HasCitations: true } && !_bibliographyWritten)
            {
                body.Add(Paragraph("Heading1", Run("References")));
                WriteBibliography(body);
            }

            body.Add(SectionProperties());
            return body;
        }

        private void BookmarkParagraphs(DocumentBlock block, List<XElement> written)
        {
            var paragraphs = written.SelectMany(e => e.Name == W + "p" ? [e] : e.Descendants(W + "p")).ToList();
            if (paragraphs.Count == 0)
            {
                return;
            }
            int id = _bookmarkId++;
            string name = $"_reP{id}";
            _options.ParagraphBookmarks![block.ParagraphId] = name;
            var first = paragraphs[0];
            var start = new XElement(W + "bookmarkStart", new XAttribute(W + "id", id), new XAttribute(W + "name", name));
            if (first.Element(W + "pPr") is { } pPr)
            {
                pPr.AddAfterSelf(start);
            }
            else
            {
                first.AddFirst(start);
            }
            paragraphs[^1].Add(new XElement(W + "bookmarkEnd", new XAttribute(W + "id", id)));
        }

        /// <summary>Generated content, skipped when a reviewed copy is imported back.</summary>
        private XElement Generated(IEnumerable<XElement> paragraphs)
            => Markers
                ? new XElement(W + "sdt",
                    new XElement(W + "sdtPr", new XElement(W + "tag", new XAttribute(W + "val", SkipTag))),
                    new XElement(W + "sdtContent", paragraphs))
                : new XElement(W + "dummy", paragraphs);

        private void AddGenerated(XElement body, List<XElement> paragraphs)
        {
            if (paragraphs.Count == 0)
            {
                return;
            }
            var generated = Generated(paragraphs);
            if (generated.Name == W + "dummy")
            {
                body.Add(generated.Elements());
            }
            else
            {
                body.Add(generated);
            }
        }

        /// <summary>Authors with affiliation marks, the affiliations and the corresponding author, from the title-page form.</summary>
        private void WriteTitlePage(XElement body)
        {
            _titlePageWritten = true;
            // The first line is the authors; the rest are affiliations and footnotes.
            var paragraphs = TitlePageText.HeaderLines(_scholarly!.Project.FrontMatter)
                .Select((line, i) => TitlePageParagraph(i == 0 ? "Authors" : "Affiliation", line))
                .ToList();
            if (_options.IncludeDraftNotes)
            {
                paragraphs.AddRange(TitlePageText.DraftNoteLines(_scholarly.Project.FrontMatter).Select(line => DraftNoteParagraph(line)));
            }
            AddGenerated(body, paragraphs);
        }

        /// <summary>A line of the draft-notes box: shaded and framed, so it reads as a note and not as the paper.</summary>
        private XElement DraftNoteParagraph(string line)
        {
            var p = TitlePageParagraph("Normal", line);
            var pPr = p.Element(W + "pPr")!;
            var border = (string side) => new XElement(W + side, new XAttribute(W + "val", "single"), new XAttribute(W + "sz", 6), new XAttribute(W + "space", 4), new XAttribute(W + "color", "C9A227"));
            pPr.Add(
                new XElement(W + "pBdr", border("top"), border("left"), border("bottom"), border("right")),
                new XElement(W + "shd", new XAttribute(W + "val", "clear"), new XAttribute(W + "color", "auto"), new XAttribute(W + "fill", "FFF8DC")),
                new XElement(W + "spacing", new XAttribute(W + "before", 0), new XAttribute(W + "after", 0), new XAttribute(W + "line", 276), new XAttribute(W + "lineRule", "auto")),
                new XElement(W + "ind", new XAttribute(W + "firstLine", 0)));
            return p;
        }

        private void WriteKeywords(XElement body)
        {
            string line = TitlePageText.KeywordsLine(_scholarly!.Project.FrontMatter, _scholarly.Project.Journal.KeywordsHeading)!;
            AddGenerated(body, [TitlePageParagraph("Keywords", line)]);
        }

        /// <summary>
        /// A title-page paragraph. Besides the skipped content control around it, each carries a skip bookmark, which
        /// stays when a reviewer removes the content control. (Copies saved by Google Docs keep neither: there the
        /// importer recognises the title page by its text.)
        /// </summary>
        private XElement TitlePageParagraph(string styleId, string line)
        {
            var p = Paragraph(styleId, FormattedRuns(line));
            AddBlockMarker(p, null, string.Empty, string.Empty, skip: true);
            return p;
        }

        private static bool IsReferencesHeading(DocumentBlock block)
            => block.Role is BlockStyleRole.Heading1 or BlockStyleRole.Heading2
               && ReferencesHeadingRegex.IsMatch(ScholarlyBlocks.StripHeadingLabel(CriticMarkupSerializer.ResolveCriticMarkupText(block.Text, MarkupDisplayMode.NoMarkupFinal)));

        private static string StyleIdOf(BlockStyleRole role) => role switch
        {
            BlockStyleRole.Title => "Title",
            BlockStyleRole.Abstract => "Abstract",
            BlockStyleRole.Heading1 => "Heading1",
            BlockStyleRole.Heading2 => "Heading2",
            BlockStyleRole.Heading3 => "Heading3",
            BlockStyleRole.BlockQuote => "Quote",
            BlockStyleRole.Caption => "Caption",
            BlockStyleRole.ReferenceItem => "Bibliography",
            BlockStyleRole.Figure => "Figure",
            BlockStyleRole.Equation => "Equation",
            _ => "Normal"
        };

        // ---- Paragraphs -----------------------------------------------------------------------------------

        private void WriteParagraph(XElement body, DocumentBlock block)
        {
            var p = Paragraph(StyleIdOf(block.Role));
            bool isHeading = block.Role is BlockStyleRole.Heading1 or BlockStyleRole.Heading2 or BlockStyleRole.Heading3;
            string text = block.Text;

            if (isHeading)
            {
                string? label = ScholarlyBlocks.HeadingLabel(CriticMarkupSerializer.ResolveCriticMarkupText(text, MarkupDisplayMode.NoMarkupFinal));
                if (label != null)
                {
                    text = ScholarlyBlocks.StripHeadingLabel(text);
                    AddBlockMarker(p, block, string.Empty, $" {{#{label}}}");
                }
                if (block.ComputedSectionNumber.Length > 0)
                {
                    p.Add(Run(block.ComputedSectionNumber));
                }
            }

            AppendContent(p, block, text);
            body.Add(p);
        }

        /// <summary>Adds a paragraph's text: pending changes as tracked changes (or highlights), the rest rendered.</summary>
        private void AppendContent(XElement p, DocumentBlock block, string text)
        {
            if (!Tracked)
            {
                AppendRendered(p, CriticMarkupSerializer.ResolveCriticMarkupText(text, MarkupDisplayMode.NoMarkupFinal));
                return;
            }

            var hunksByToken = new Dictionary<string, RevisionHunk>(StringComparer.Ordinal);
            foreach (var hunk in CriticMarkupSerializer.HunksFor(_hunksByParagraph, block.ParagraphId))
            {
                hunksByToken.TryAdd(CriticMarkupSerializer.FormatHunkAsCriticMarkup(hunk), hunk);
            }
            AppendRunsWithTrackedChanges(p, text, hunksByToken);
        }

        private void AppendRunsWithTrackedChanges(XElement p, string textWithCriticMarkup, IReadOnlyDictionary<string, RevisionHunk> hunksByToken)
        {
            string fallbackDateIso = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");
            int lastPos = 0;
            foreach (Match m in CriticTokenRegex.Matches(textWithCriticMarkup))
            {
                if (m.Index > lastPos)
                {
                    AppendRendered(p, textWithCriticMarkup[lastPos..m.Index]);
                }

                // Attribute the revision to the hunk it came from; tokens typed directly into the source have no hunk.
                hunksByToken.TryGetValue(m.Value, out var sourceHunk);
                string author = string.IsNullOrWhiteSpace(sourceHunk?.Author) ? "Supervisor" : sourceHunk.Author;
                string date = sourceHunk != null ? sourceHunk.TimestampUtc.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ") : fallbackDateIso;

                if (_options.HighlightChanges)
                {
                    // Revised text with what changed highlighted; removed text and comments are left out.
                    string added = m.Groups["repNew"].Success ? m.Groups["repNew"].Value : m.Groups["ins"].Success ? m.Groups["ins"].Value : string.Empty;
                    if (m.Groups["hl"].Success)
                    {
                        AppendRendered(p, m.Groups["hl"].Value);
                    }
                    else if (added.Length > 0)
                    {
                        foreach (var run in FormattedRuns(added, highlight: true))
                        {
                            p.Add(run);
                        }
                    }
                }
                else if (m.Groups["repOld"].Success)
                {
                    p.Add(Deletion(m.Groups["repOld"].Value, author, date));
                    p.Add(Insertion(m.Groups["repNew"].Value, author, date));
                }
                else if (m.Groups["ins"].Success)
                {
                    p.Add(Insertion(m.Groups["ins"].Value, author, date));
                }
                else if (m.Groups["del"].Success)
                {
                    p.Add(Deletion(m.Groups["del"].Value, author, date));
                }
                else if (m.Groups["hl"].Success || m.Groups["comment"].Success)
                {
                    AddComment(p, m, date);
                }

                lastPos = m.Index + m.Length;
            }

            if (lastPos < textWithCriticMarkup.Length)
            {
                AppendRendered(p, textWithCriticMarkup[lastPos..]);
            }
        }

        // Changed text keeps its citation keys as typed, so accepting the change in Word imports them unchanged.
        private XElement Insertion(string text, string author, string date)
            => new(W + "ins", new XAttribute(W + "id", _revId++), new XAttribute(W + "author", author), new XAttribute(W + "date", date),
                FormattedRuns(text));

        private XElement Deletion(string text, string author, string date)
            => new(W + "del", new XAttribute(W + "id", _revId++), new XAttribute(W + "author", author), new XAttribute(W + "date", date),
                FormattedRuns(text, deleted: true));

        private void AddComment(XElement p, Match m, string date)
        {
            string anchor = m.Groups["hl"].Success ? m.Groups["hl"].Value : string.Empty;
            string rawComment = m.Groups["hl"].Success ? m.Groups["hlComment"].Value : m.Groups["comment"].Value;
            string author = "Supervisor";
            string bodyText = rawComment;
            int colon = rawComment.IndexOf(':');
            if (colon > 0 && colon < 40)
            {
                author = rawComment[..colon].Trim();
                bodyText = rawComment[(colon + 1)..].Trim();
            }

            string cid = (_commentId++).ToString();
            if (!string.IsNullOrEmpty(anchor))
            {
                p.Add(new XElement(W + "commentRangeStart", new XAttribute(W + "id", cid)));
                AppendRendered(p, anchor);
                p.Add(new XElement(W + "commentRangeEnd", new XAttribute(W + "id", cid)));
            }
            p.Add(new XElement(W + "r", new XElement(W + "commentReference", new XAttribute(W + "id", cid))));

            Comments.Add(new XElement(W + "comment",
                new XAttribute(W + "id", cid),
                new XAttribute(W + "author", author),
                new XAttribute(W + "date", date),
                new XElement(W + "p", Run(bodyText))));
        }

        /// <summary>Text with citations, cross-references, linked numbers and inline equations rendered.</summary>
        private void AppendRendered(XElement parent, string text)
        {
            if (_scholarly == null)
            {
                parent.Add(FormattedRuns(text));
                return;
            }

            foreach (var piece in _scholarly.Tokenize(text))
            {
                switch (piece.Kind)
                {
                    case InlinePieceKind.Text:
                        parent.Add(FormattedRuns(piece.Display));
                        break;
                    case InlinePieceKind.Math:
                        var math = LatexToOmml.Convert(piece.Detail ?? string.Empty, out _);
                        parent.Add(Markers ? InlineMarker(piece.Source, math) : math);
                        break;
                    case InlinePieceKind.Placeholder:
                        // Highlighted, with its note after it; co-authors can type over it in Word.
                        var shown = FormattedRuns(piece.Display, highlight: true);
                        if (piece.Detail is { Length: > 0 } note)
                        {
                            shown.AddRange(FormattedRuns($" *[{note}]*", highlight: true, sizeHalfPts: Math.Max(14, (int)(_profile.BodyFontSizePt * 2 * 0.8))));
                        }
                        parent.Add(Markers ? InlineMarker(piece.Source, shown, editable: true) : shown);
                        break;
                    default:
                        var runs = FormattedRuns(piece.Display);
                        parent.Add(Markers ? InlineMarker(piece.Source, runs) : runs);
                        break;
                }
            }
        }

        /// <summary>
        /// A content control whose document variable holds the source it was rendered from: locked, or (for a
        /// placeholder) editable, with the text it was written with recorded so an edited one is read as written.
        /// </summary>
        private XElement InlineMarker(string source, object content, bool editable = false)
        {
            int id = _markerId++;
            DocumentVariables.Add(($"reI{id}", source));
            var sdtContent = new XElement(W + "sdtContent", content);
            if (editable)
            {
                DocumentVariables.Add(($"reT{id}", string.Concat(sdtContent.Descendants(W + "t").Select(t => t.Value))));
            }
            return new XElement(W + "sdt",
                new XElement(W + "sdtPr",
                    new XElement(W + "tag", new XAttribute(W + "val", $"{InlineTagPrefix}{id}")),
                    editable ? null : new XElement(W + "lock", new XAttribute(W + "val", "sdtContentLocked"))),
                sdtContent);
        }

        /// <summary>"[Placeholder: synthetic data]" after the caption label of a figure or table that is a placeholder.</summary>
        private void AddPlaceholderLabel(XElement caption, string? note)
        {
            if (note == null)
            {
                return;
            }
            var runs = FormattedRuns($"**[Placeholder{(note.Length > 0 ? ": " + note : string.Empty)}]** ", highlight: true);
            caption.Add(Markers
                ? new XElement(W + "sdt",
                    new XElement(W + "sdtPr",
                        new XElement(W + "tag", new XAttribute(W + "val", LabelTag)),
                        new XElement(W + "lock", new XAttribute(W + "val", "sdtContentLocked"))),
                    new XElement(W + "sdtContent", runs))
                : runs);
        }

        /// <summary>Text that is generated (caption labels such as "Fig. 2. "), ignored when a reviewed copy is imported.</summary>
        private XElement LabelRuns(string text, bool bold)
        {
            var runs = FormattedRuns(bold ? $"**{text.TrimEnd()}**" + text[text.TrimEnd().Length..] : text);
            if (!Markers)
            {
                return new XElement(W + "dummy", runs);
            }
            return new XElement(W + "sdt",
                new XElement(W + "sdtPr",
                    new XElement(W + "tag", new XAttribute(W + "val", LabelTag)),
                    new XElement(W + "lock", new XAttribute(W + "val", "sdtContentLocked"))),
                new XElement(W + "sdtContent", runs));
        }

        /// <summary>
        /// Marks a paragraph with a hidden bookmark whose document variable records the syntax around its text
        /// (prefix and suffix), its style, or that it is generated and must be skipped on import.
        /// </summary>
        private void AddBlockMarker(XElement p, DocumentBlock? block, string prefix, string suffix, bool skip = false, bool noText = false)
        {
            if (!Markers)
            {
                return;
            }
            int id = _markerId++;
            var payload = new Dictionary<string, object?>();
            if (skip)
            {
                payload["k"] = "skip";
            }
            else
            {
                payload["p"] = prefix;
                payload["s"] = suffix;
                payload["r"] = block?.Role.ToString();
                if (noText)
                {
                    payload["t"] = 0;
                }
            }
            DocumentVariables.Add(($"reB{id}", JsonSerializer.Serialize(payload)));
            int bookmark = _bookmarkId++;
            var pPr = p.Element(W + "pPr");
            var start = new XElement(W + "bookmarkStart", new XAttribute(W + "id", bookmark), new XAttribute(W + "name", $"{BlockBookmarkPrefix}{id}"));
            var end = new XElement(W + "bookmarkEnd", new XAttribute(W + "id", bookmark));
            if (pPr != null)
            {
                pPr.AddAfterSelf(start, end);
            }
            else
            {
                p.AddFirst(start, end);
            }
        }

        // ---- Figures, tables, equations ----------------------------------------------------------------

        private void WriteFigure(XElement body, DocumentBlock block)
        {
            string final = CriticMarkupSerializer.ResolveCriticMarkupText(block.Text, MarkupDisplayMode.NoMarkupFinal);
            var figure = ScholarlyBlocks.ParseFigure(final);
            var split = ScholarlyBlocks.SplitCaption(block.Text);
            if (figure == null || split == null)
            {
                WriteParagraph(body, block);
                return;
            }

            var imageParagraph = Paragraph("Figure");
            AddBlockMarker(imageParagraph, null, string.Empty, string.Empty, skip: true);
            imageParagraph.Add(ImageRun(figure, final) ?? Run($"[Image not found: {figure.ImagePath}]"));
            body.Add(imageParagraph);

            var caption = Paragraph("Caption");
            AddBlockMarker(caption, block, split.Value.Prefix, split.Value.Suffix);
            caption.Add(UnwrapDummy(LabelRuns(_scholarly!.CrossReferences.CaptionLabel(block), bold: true)));
            AddPlaceholderLabel(caption, DraftPlaceholders.NoteOf(figure.Attributes));
            AppendContent(caption, block, split.Value.Caption);
            body.Add(caption);
        }

        private XElement? ImageRun(FigureBlock figure, string alt)
        {
            string? path = ResolvePath(figure.ImagePath);
            if (path == null)
            {
                return null;
            }

            // Word reads PNG, JPEG, GIF, BMP and TIFF as they are. SVG goes in as itself (Word 2016 and later draw
            // it as a vector) with a PNG for older readers; PDF and WebP figures go in as a 300 dpi PNG.
            var kind = FigureImages.KindOf(path);
            byte[] bytes;
            string ext, contentType;
            double widthIn, heightIn;
            string? svgRelId = null;
            if (kind == FigureImageKind.Raster && ImageInfo.TryRead(path) is { } info)
            {
                bytes = File.ReadAllBytes(path);
                ext = info.Extension;
                contentType = info.ContentType;
                widthIn = info.Width / Math.Max(info.DpiX, 1);
                heightIn = info.Height / Math.Max(info.DpiY, 1);
            }
            else if (FigureImages.RenderPng(path, 300) is { } png && FigureImages.NaturalSizePoints(path) is { } size)
            {
                bytes = png;
                ext = "png";
                contentType = "image/png";
                widthIn = size.Width / 72;
                heightIn = size.Height / 72;
                if (kind == FigureImageKind.Svg)
                {
                    svgRelId = $"rIdImg{Images.Count + 1}";
                    Images.Add(new EmbeddedImage(svgRelId, $"media/image{Images.Count + 1}.svg", "svg", "image/svg+xml", File.ReadAllBytes(path)));
                }
            }
            else
            {
                return null;
            }

            string relId = $"rIdImg{Images.Count + 1}";
            string part = $"media/image{Images.Count + 1}.{ext}";
            Images.Add(new EmbeddedImage(relId, part, ext, contentType, bytes));

            // Natural size from the image's resolution, limited to the text width (or the requested fraction of it).
            double textWidthIn = 8.5 - 2 * _profile.PageMarginInches;
            double maxWidth = textWidthIn * (figure.WidthFraction ?? 1.0);
            if (figure.WidthFraction != null || widthIn > maxWidth)
            {
                heightIn *= maxWidth / widthIn;
                widthIn = maxWidth;
            }
            double maxHeight = 11 - 2 * _profile.PageMarginInches - 1.5;
            if (heightIn > maxHeight)
            {
                widthIn *= maxHeight / heightIn;
                heightIn = maxHeight;
            }
            long cx = (long)(widthIn * 914400);
            long cy = (long)(heightIn * 914400);
            int docPrId = _drawingId++;
            string description = Diffing.DiffEngineService.ToGoogleDocsText(figure.Caption);

            return new XElement(W + "r",
                new XElement(W + "drawing",
                    new XElement(WP + "inline",
                        new XAttribute("distT", 0), new XAttribute("distB", 0), new XAttribute("distL", 0), new XAttribute("distR", 0),
                        new XElement(WP + "extent", new XAttribute("cx", cx), new XAttribute("cy", cy)),
                        new XElement(WP + "effectExtent", new XAttribute("l", 0), new XAttribute("t", 0), new XAttribute("r", 0), new XAttribute("b", 0)),
                        new XElement(WP + "docPr", new XAttribute("id", docPrId), new XAttribute("name", $"Figure {docPrId}"), new XAttribute("descr", description)),
                        new XElement(WP + "cNvGraphicFramePr",
                            new XElement(A + "graphicFrameLocks", new XAttribute("noChangeAspect", 1))),
                        new XElement(A + "graphic",
                            new XElement(A + "graphicData", new XAttribute("uri", "http://schemas.openxmlformats.org/drawingml/2006/picture"),
                                new XElement(Pic + "pic",
                                    new XElement(Pic + "nvPicPr",
                                        new XElement(Pic + "cNvPr", new XAttribute("id", 0), new XAttribute("name", Path.GetFileName(path))),
                                        new XElement(Pic + "cNvPicPr")),
                                    new XElement(Pic + "blipFill",
                                        new XElement(A + "blip", new XAttribute(R + "embed", relId),
                                            svgRelId == null ? null : new XElement(A + "extLst",
                                                new XElement(A + "ext", new XAttribute("uri", "{96DAC541-7B7A-43D3-8B79-37D633B846F1}"),
                                                    new XElement(Asvg + "svgBlip", new XAttribute(XNamespace.Xmlns + "asvg", Asvg), new XAttribute(R + "embed", svgRelId))))),
                                        new XElement(A + "stretch", new XElement(A + "fillRect"))),
                                    new XElement(Pic + "spPr",
                                        new XElement(A + "xfrm",
                                            new XElement(A + "off", new XAttribute("x", 0), new XAttribute("y", 0)),
                                            new XElement(A + "ext", new XAttribute("cx", cx), new XAttribute("cy", cy))),
                                        new XElement(A + "prstGeom", new XAttribute("prst", "rect"), new XElement(A + "avLst")))))))));
        }

        private string? ResolvePath(string relativePath)
        {
            if (string.IsNullOrWhiteSpace(relativePath))
            {
                return null;
            }
            string path = Path.IsPathRooted(relativePath) || _options.WorkspacePath == null
                ? relativePath
                : Path.Combine(_options.WorkspacePath, relativePath.Replace('/', Path.DirectorySeparatorChar));
            return File.Exists(path) ? path : null;
        }

        private void WriteTable(XElement body, DocumentBlock block)
        {
            string final = CriticMarkupSerializer.ResolveCriticMarkupText(block.Text, MarkupDisplayMode.NoMarkupFinal);
            var table = ScholarlyBlocks.ParseTable(final);
            var split = ScholarlyBlocks.SplitCaption(block.Text);
            if (table == null || split == null)
            {
                WriteParagraph(body, block);
                return;
            }

            // Tables are captioned above, figures below.
            var caption = Paragraph("Caption", keepNext: true);
            AddBlockMarker(caption, block, split.Value.Prefix, split.Value.Suffix);
            caption.Add(UnwrapDummy(LabelRuns(_scholarly!.CrossReferences.CaptionLabel(block), bold: true)));
            AddPlaceholderLabel(caption, DraftPlaceholders.NoteOf(table.Attributes));
            AppendContent(caption, block, split.Value.Caption);
            body.Add(caption);

            string? path = table.CsvPath != null ? ResolvePath(table.CsvPath) : null;
            List<string[]> rows;
            try
            {
                rows = path != null ? CsvTable.Parse(File.ReadAllText(path)) : [];
            }
            catch (IOException)
            {
                rows = [];
            }

            XElement content;
            if (rows.Count == 0)
            {
                var missing = Paragraph("Normal");
                missing.Add(Run(table.CsvPath == null ? "[Table has no file]" : $"[Table file not found: {table.CsvPath}]"));
                content = missing;
            }
            else
            {
                content = BuildTable(rows, table);
            }

            body.Add(Markers
                ? new XElement(W + "sdt",
                    new XElement(W + "sdtPr", new XElement(W + "tag", new XAttribute(W + "val", SkipTag))),
                    new XElement(W + "sdtContent", content))
                : content);
        }

        /// <summary>A three-line academic table: rules above and below the header and below the last row.</summary>
        private XElement BuildTable(List<string[]> rows, TableBlock table)
        {
            int columns = rows.Max(r => r.Length);
            int fontHalfPts = (int)Math.Round(Math.Max(8, _profile.BodyFontSizePt - 1) * 2);
            var tbl = new XElement(W + "tbl",
                new XElement(W + "tblPr",
                    new XElement(W + "tblW", new XAttribute(W + "w", 5000), new XAttribute(W + "type", "pct")),
                    new XElement(W + "jc", new XAttribute(W + "val", "center")),
                    new XElement(W + "tblLayout", new XAttribute(W + "type", "autofit")),
                    new XElement(W + "tblCellMar",
                        new XElement(W + "left", new XAttribute(W + "w", 80), new XAttribute(W + "type", "dxa")),
                        new XElement(W + "right", new XAttribute(W + "w", 80), new XAttribute(W + "type", "dxa")))),
                new XElement(W + "tblGrid", Enumerable.Range(0, columns).Select(_ => new XElement(W + "gridCol", new XAttribute(W + "w", 9000 / columns)))));

            bool[] numeric = new bool[columns];
            for (int c = 0; c < columns; c++)
            {
                var values = rows.Skip(1).Select(r => c < r.Length ? r[c].Trim() : string.Empty).Where(v => v.Length > 0).ToList();
                numeric[c] = c > 0 && values.Count > 0 && values.All(LooksNumeric);
            }

            for (int r = 0; r < rows.Count; r++)
            {
                bool header = r == 0;
                bool last = r == rows.Count - 1;
                var tr = new XElement(W + "tr");
                if (header)
                {
                    tr.Add(new XElement(W + "trPr", new XElement(W + "tblHeader")));
                }
                for (int c = 0; c < columns; c++)
                {
                    string cell = c < rows[r].Length ? rows[r][c].Trim() : string.Empty;
                    var borders = new XElement(W + "tcBorders");
                    if (header)
                    {
                        borders.Add(Border("top", 8), Border("bottom", 4));
                    }
                    if (last)
                    {
                        borders.Add(Border("bottom", 8));
                    }
                    var p = new XElement(W + "p",
                        new XElement(W + "pPr",
                            new XElement(W + "pStyle", new XAttribute(W + "val", "TableText")),
                            new XElement(W + "jc", new XAttribute(W + "val", numeric[c] ? "right" : c == 0 ? "left" : "center"))),
                        FormattedRuns(header ? $"**{cell}**" : cell, sizeHalfPts: fontHalfPts));
                    if (cell.Length == 0 || (header && cell.Length == 0))
                    {
                        p = new XElement(W + "p", p.Element(W + "pPr"));
                    }
                    tr.Add(new XElement(W + "tc",
                        new XElement(W + "tcPr",
                            new XElement(W + "tcW", new XAttribute(W + "w", 0), new XAttribute(W + "type", "auto")),
                            borders.HasElements ? borders : null),
                        p));
                }
                tbl.Add(tr);
            }
            return tbl;
        }

        private static XElement Border(string side, int size)
            => new(W + side, new XAttribute(W + "val", "single"), new XAttribute(W + "sz", size), new XAttribute(W + "space", 0), new XAttribute(W + "color", "000000"));

        private static bool LooksNumeric(string value)
            => Regex.IsMatch(value.Replace("−", "-"), @"^[-+±]?\(?[\d.,]+\)?\s*(%|[±]\s*[\d.,]+)?\*{0,3}$");

        private void WriteEquation(XElement body, DocumentBlock block)
        {
            string final = CriticMarkupSerializer.ResolveCriticMarkupText(block.Text, MarkupDisplayMode.NoMarkupFinal);
            var equation = ScholarlyBlocks.ParseEquation(final);
            if (equation == null)
            {
                WriteParagraph(body, block);
                return;
            }

            // A display equation centred in a borderless three-column table with its number at the right
            // (an equation sharing a paragraph with a number would be laid out at text size).
            int textWidth = (int)Math.Round((8.5 - 2 * _profile.PageMarginInches) * 1440);
            int side = textWidth / 10;
            var math = Paragraph("Equation");
            AddBlockMarker(math, block, block.Text, string.Empty, noText: true);
            math.Add(LatexToOmml.ConvertDisplay(equation.Latex, out _));
            var numberParagraph = Paragraph("Equation");
            string? number = _scholarly!.CrossReferences.NumberOf(block.ParagraphId);
            if (number != null)
            {
                AddBlockMarker(numberParagraph, null, string.Empty, string.Empty, skip: true);
                numberParagraph.Element(W + "pPr")!.Add(new XElement(W + "jc", new XAttribute(W + "val", "right")));
                numberParagraph.Add(Run($"({number})"));
            }

            XElement Cell(int width, XElement content) => new(W + "tc",
                new XElement(W + "tcPr",
                    new XElement(W + "tcW", new XAttribute(W + "w", width), new XAttribute(W + "type", "dxa")),
                    new XElement(W + "vAlign", new XAttribute(W + "val", "center"))),
                content);

            body.Add(new XElement(W + "tbl",
                new XElement(W + "tblPr",
                    new XElement(W + "tblW", new XAttribute(W + "w", textWidth), new XAttribute(W + "type", "dxa")),
                    new XElement(W + "tblBorders",
                        new[] { "top", "left", "bottom", "right", "insideH", "insideV" }.Select(b => new XElement(W + b, new XAttribute(W + "val", "nil")))),
                    new XElement(W + "tblLayout", new XAttribute(W + "type", "fixed"))),
                new XElement(W + "tblGrid",
                    new XElement(W + "gridCol", new XAttribute(W + "w", side)),
                    new XElement(W + "gridCol", new XAttribute(W + "w", textWidth - 2 * side)),
                    new XElement(W + "gridCol", new XAttribute(W + "w", side))),
                new XElement(W + "tr",
                    Cell(side, Paragraph("Equation")),
                    Cell(textWidth - 2 * side, math),
                    Cell(side, numberParagraph))));
        }

        private void WriteBibliography(XElement body)
        {
            _bibliographyWritten = true;
            var paragraphs = new List<XElement>();
            foreach (var reference in _scholarly!.Citations.References)
            {
                string text = reference.Label.Length > 0 ? $"{reference.Label}\t{reference.Text}" : reference.Text;
                paragraphs.Add(Paragraph(reference.Label.Length > 0 ? "BibliographyNumbered" : "Bibliography", FormattedRuns(text)));
            }
            if (paragraphs.Count == 0)
            {
                return;
            }
            body.Add(Markers
                ? new XElement(W + "sdt",
                    new XElement(W + "sdtPr", new XElement(W + "tag", new XAttribute(W + "val", SkipTag))),
                    new XElement(W + "sdtContent", paragraphs))
                : paragraphs);
        }

        // ---- Runs -----------------------------------------------------------------------------------------

        private static object UnwrapDummy(XElement element) => element.Name == W + "dummy" ? element.Elements().ToList() : element;

        private static XElement Paragraph(string styleId, params object[] content) => Paragraph(styleId, false, content);

        private static XElement Paragraph(string styleId, bool keepNext, params object[] content)
        {
            var pPr = new XElement(W + "pPr", new XElement(W + "pStyle", new XAttribute(W + "val", styleId)));
            if (keepNext)
            {
                pPr.Add(new XElement(W + "keepNext"));
            }
            return new XElement(W + "p", pPr, content);
        }

        private static XElement Run(string text) => new(W + "r", TextAndTabs(text, deleted: false));

        private static XElement Text(string text) => new(W + "t", new XAttribute(XNamespace.Xml + "space", "preserve"), text);

        private static IEnumerable<XElement> TextAndTabs(string text, bool deleted)
        {
            string[] parts = text.Split('\t');
            for (int i = 0; i < parts.Length; i++)
            {
                if (i > 0)
                {
                    yield return new XElement(W + "tab");
                }
                if (parts[i].Length > 0)
                {
                    yield return new XElement(W + (deleted ? "delText" : "t"), new XAttribute(XNamespace.Xml + "space", "preserve"), parts[i]);
                }
            }
        }

        /// <summary>Runs for text with emphasis, superscript and subscript markers.</summary>
        private static List<XElement> FormattedRuns(string text, bool deleted = false, bool highlight = false, int? sizeHalfPts = null)
        {
            var runs = new List<XElement>();
            foreach (var span in InlineFormatting.Parse(text))
            {
                if (span.Text.Length == 0)
                {
                    continue;
                }
                var rPr = new XElement(W + "rPr");
                if (span.Bold) rPr.Add(new XElement(W + "b"));
                if (span.Italic) rPr.Add(new XElement(W + "i"));
                if (sizeHalfPts is { } size)
                {
                    rPr.Add(new XElement(W + "sz", new XAttribute(W + "val", size)), new XElement(W + "szCs", new XAttribute(W + "val", size)));
                }
                if (highlight) rPr.Add(new XElement(W + "highlight", new XAttribute(W + "val", "yellow")));
                if (span.Superscript || span.Subscript)
                {
                    rPr.Add(new XElement(W + "vertAlign", new XAttribute(W + "val", span.Superscript ? "superscript" : "subscript")));
                }
                runs.Add(new XElement(W + "r", rPr.HasElements ? rPr : null, TextAndTabs(span.Text, deleted)));
            }
            return runs;
        }

        private XElement SectionProperties()
        {
            int marginTwips = (int)Math.Round(_profile.PageMarginInches * 1440);
            var sectPr = new XElement(W + "sectPr");
            if (_options.PageNumbers)
            {
                sectPr.Add(new XElement(W + "footerReference", new XAttribute(W + "type", "default"), new XAttribute(R + "id", "rIdFooter1")));
            }
            sectPr.Add(
                new XElement(W + "pgSz", new XAttribute(W + "w", 12240), new XAttribute(W + "h", 15840)),
                new XElement(W + "pgMar",
                    new XAttribute(W + "top", marginTwips),
                    new XAttribute(W + "right", marginTwips),
                    new XAttribute(W + "bottom", marginTwips),
                    new XAttribute(W + "left", marginTwips),
                    new XAttribute(W + "header", 720),
                    new XAttribute(W + "footer", 720),
                    new XAttribute(W + "gutter", 0)));
            if (_options.LineNumbers)
            {
                sectPr.Add(new XElement(W + "lnNumType",
                    new XAttribute(W + "countBy", 1),
                    new XAttribute(W + "distance", 360),
                    new XAttribute(W + "restart", _options.RestartLineNumbersEachPage ? "newPage" : "continuous")));
            }
            return sectPr;
        }
    }

    // ---- Package parts ---------------------------------------------------------------------------------

    private static void WriteXmlEntry(ZipArchive archive, string entryPath, XDocument xmlDoc)
    {
        var entry = archive.CreateEntry(entryPath, CompressionLevel.Optimal);
        using var entryStream = entry.Open();
        using var writer = new StreamWriter(entryStream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        xmlDoc.Save(writer);
    }

    private static XDocument BuildContentTypesXml(List<EmbeddedImage> images, bool footer)
    {
        var types = new XElement(ContentTypesNs + "Types",
            new XElement(ContentTypesNs + "Default",
                new XAttribute("Extension", "rels"),
                new XAttribute("ContentType", "application/vnd.openxmlformats-package.relationships+xml")),
            new XElement(ContentTypesNs + "Default",
                new XAttribute("Extension", "xml"),
                new XAttribute("ContentType", "application/xml")));
        foreach (var image in images.DistinctBy(i => i.Extension))
        {
            types.Add(new XElement(ContentTypesNs + "Default", new XAttribute("Extension", image.Extension), new XAttribute("ContentType", image.ContentType)));
        }
        types.Add(
            Override("/word/document.xml", "application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml"),
            Override("/word/styles.xml", "application/vnd.openxmlformats-officedocument.wordprocessingml.styles+xml"),
            Override("/word/settings.xml", "application/vnd.openxmlformats-officedocument.wordprocessingml.settings+xml"),
            Override("/word/comments.xml", "application/vnd.openxmlformats-officedocument.wordprocessingml.comments+xml"));
        if (footer)
        {
            types.Add(Override("/word/footer1.xml", "application/vnd.openxmlformats-officedocument.wordprocessingml.footer+xml"));
        }
        return new XDocument(new XDeclaration("1.0", "UTF-8", "yes"), types);

        static XElement Override(string part, string type)
            => new(ContentTypesNs + "Override", new XAttribute("PartName", part), new XAttribute("ContentType", type));
    }

    private static XDocument BuildRootRelsXml() =>
        new(new XDeclaration("1.0", "UTF-8", "yes"),
            new XElement(PkgRel + "Relationships",
                new XElement(PkgRel + "Relationship",
                    new XAttribute("Id", "rId1"),
                    new XAttribute("Type", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument"),
                    new XAttribute("Target", "word/document.xml"))));

    private static XDocument BuildDocumentRelsXml(List<EmbeddedImage> images, bool footer)
    {
        const string rel = "http://schemas.openxmlformats.org/officeDocument/2006/relationships/";
        var root = new XElement(PkgRel + "Relationships",
            Relationship("rIdStyles", rel + "styles", "styles.xml"),
            Relationship("rIdSettings", rel + "settings", "settings.xml"),
            Relationship("rIdComments", rel + "comments", "comments.xml"));
        if (footer)
        {
            root.Add(Relationship("rIdFooter1", rel + "footer", "footer1.xml"));
        }
        foreach (var image in images)
        {
            root.Add(Relationship(image.RelationshipId, rel + "image", image.PartName));
        }
        return new XDocument(new XDeclaration("1.0", "UTF-8", "yes"), root);

        static XElement Relationship(string id, string type, string target)
            => new(PkgRel + "Relationship", new XAttribute("Id", id), new XAttribute("Type", type), new XAttribute("Target", target));
    }

    private static XDocument BuildSettingsXml(List<(string Name, string Value)> variables)
    {
        var settings = new XElement(W + "settings",
            new XAttribute(XNamespace.Xmlns + "w", W),
            new XElement(W + "defaultTabStop", new XAttribute(W + "val", 720)),
            new XElement(W + "compat",
                new XElement(W + "compatSetting",
                    new XAttribute(W + "name", "compatibilityMode"),
                    new XAttribute(W + "uri", "http://schemas.microsoft.com/office/word"),
                    new XAttribute(W + "val", 15))));
        if (variables.Count > 0)
        {
            settings.Add(new XElement(W + "docVars",
                variables.Select(v => new XElement(W + "docVar", new XAttribute(W + "name", v.Name), new XAttribute(W + "val", v.Value)))));
        }
        return new XDocument(new XDeclaration("1.0", "UTF-8", "yes"), settings);
    }

    private static XDocument BuildFooterXml() =>
        new(new XDeclaration("1.0", "UTF-8", "yes"),
            new XElement(W + "ftr",
                new XAttribute(XNamespace.Xmlns + "w", W),
                new XElement(W + "p",
                    new XElement(W + "pPr", new XElement(W + "jc", new XAttribute(W + "val", "center"))),
                    new XElement(W + "r", new XElement(W + "fldChar", new XAttribute(W + "fldCharType", "begin"))),
                    new XElement(W + "r", new XElement(W + "instrText", new XAttribute(XNamespace.Xml + "space", "preserve"), " PAGE ")),
                    new XElement(W + "r", new XElement(W + "fldChar", new XAttribute(W + "fldCharType", "separate"))),
                    new XElement(W + "r", new XElement(W + "t", "1")),
                    new XElement(W + "r", new XElement(W + "fldChar", new XAttribute(W + "fldCharType", "end"))))));

    /// <summary>
    /// Generates a deterministic, clean word/styles.xml from the active FormattingRuleProfile so Word and Google Docs
    /// never suffer from mismatched inline fonts, sizes, or paragraph spacing.
    /// </summary>
    private static XDocument BuildRuleDrivenStylesXml(FormattingRuleProfile profile)
    {
        int bodyHalfPts = (int)Math.Round(profile.BodyFontSizePt * 2);
        int titleHalfPts = (int)Math.Round(profile.TitleFontSizePt * 2);
        int h1HalfPts = (int)Math.Round(profile.Heading1FontSizePt * 2);
        int h2HalfPts = (int)Math.Round(profile.Heading2FontSizePt * 2);
        int h3HalfPts = (int)Math.Round(profile.Heading3FontSizePt * 2);
        int smallHalfPts = Math.Max(16, bodyHalfPts - 2);

        int lineSpacingTwips = (int)Math.Round(profile.LineSpacingMultiplier * 240);
        int afterSpacingTwips = (int)Math.Round(profile.ParagraphSpacingAfterPt * 20);
        int firstLineIndentTwips = (int)Math.Round(profile.FirstLineIndentInches * 1440);
        int textWidthTwips = (int)Math.Round((8.5 - 2 * profile.PageMarginInches) * 1440);

        var figure = CreateStyleElement("Figure", "Figure", profile.BodyFontFamily, bodyHalfPts, false, false, 0, 120, 240);
        figure.Element(W + "pPr")!.Add(new XElement(W + "keepNext"), new XElement(W + "jc", new XAttribute(W + "val", "center")));
        var equation = CreateStyleElement("Equation", "Equation", profile.BodyFontFamily, bodyHalfPts, false, false, 0, afterSpacingTwips, 240);
        equation.Element(W + "pPr")!.AddFirst(new XElement(W + "tabs",
            new XElement(W + "tab", new XAttribute(W + "val", "center"), new XAttribute(W + "pos", textWidthTwips / 2)),
            new XElement(W + "tab", new XAttribute(W + "val", "right"), new XAttribute(W + "pos", textWidthTwips))));
        var bibliography = CreateStyleElement("Bibliography", "Bibliography", profile.BodyFontFamily, bodyHalfPts, false, false, 0, afterSpacingTwips, lineSpacingTwips);
        bibliography.Element(W + "pPr")!.Add(new XElement(W + "ind", new XAttribute(W + "left", 720), new XAttribute(W + "hanging", 720)));
        var numbered = CreateStyleElement("BibliographyNumbered", "Bibliography (numbered)", profile.BodyFontFamily, bodyHalfPts, false, false, 0, afterSpacingTwips, lineSpacingTwips);
        numbered.Element(W + "pPr")!.AddFirst(new XElement(W + "tabs", new XElement(W + "tab", new XAttribute(W + "val", "left"), new XAttribute(W + "pos", 567))));
        numbered.Element(W + "pPr")!.Add(new XElement(W + "ind", new XAttribute(W + "left", 567), new XAttribute(W + "hanging", 567)));
        var tableText = CreateStyleElement("TableText", "Table Text", profile.BodyFontFamily, smallHalfPts, false, false, 0, 0, 240);
        var caption = CreateStyleElement("Caption", "Caption", profile.BodyFontFamily, smallHalfPts, false, false, 0, afterSpacingTwips + 60, 240);
        var authors = CreateStyleElement("Authors", "Authors", profile.BodyFontFamily, bodyHalfPts + 2, false, false, 0, 120, 240);
        authors.Element(W + "pPr")!.Add(new XElement(W + "jc", new XAttribute(W + "val", "center")));
        var affiliation = CreateStyleElement("Affiliation", "Affiliation", profile.BodyFontFamily, smallHalfPts, false, false, 0, 40, 240);
        affiliation.Element(W + "pPr")!.Add(new XElement(W + "jc", new XAttribute(W + "val", "center")));
        var keywords = CreateStyleElement("Keywords", "Keywords", profile.BodyFontFamily, bodyHalfPts, false, false, 0, afterSpacingTwips + 80, lineSpacingTwips);

        return new XDocument(
            new XDeclaration("1.0", "UTF-8", "yes"),
            new XElement(W + "styles",
                new XAttribute(XNamespace.Xmlns + "w", W),
                new XElement(W + "docDefaults",
                    new XElement(W + "rPrDefault",
                        new XElement(W + "rPr",
                            new XElement(W + "rFonts",
                                new XAttribute(W + "ascii", profile.BodyFontFamily),
                                new XAttribute(W + "hAnsi", profile.BodyFontFamily),
                                new XAttribute(W + "cs", profile.BodyFontFamily)),
                            new XElement(W + "sz", new XAttribute(W + "val", bodyHalfPts)),
                            new XElement(W + "szCs", new XAttribute(W + "val", bodyHalfPts)))),
                    new XElement(W + "pPrDefault",
                        new XElement(W + "pPr",
                            new XElement(W + "spacing",
                                new XAttribute(W + "after", afterSpacingTwips),
                                new XAttribute(W + "line", lineSpacingTwips),
                                new XAttribute(W + "lineRule", "auto"))))),
                CreateStyleElement("Normal", "Normal", profile.BodyFontFamily, bodyHalfPts, false, false, firstLineIndentTwips, afterSpacingTwips, lineSpacingTwips),
                CreateStyleElement("Title", "Title", profile.HeadingFontFamily, titleHalfPts, true, false, 0, afterSpacingTwips + 120, 240),
                CreateStyleElement("Abstract", "Abstract", profile.BodyFontFamily, bodyHalfPts, false, true, 0, afterSpacingTwips + 80, lineSpacingTwips),
                CreateStyleElement("Heading1", "heading 1", profile.HeadingFontFamily, h1HalfPts, true, false, 0, afterSpacingTwips + 60, 240),
                CreateStyleElement("Heading2", "heading 2", profile.HeadingFontFamily, h2HalfPts, true, false, 0, afterSpacingTwips + 40, 240),
                CreateStyleElement("Heading3", "heading 3", profile.HeadingFontFamily, h3HalfPts, true, true, 0, afterSpacingTwips + 20, 240),
                CreateStyleElement("Quote", "Quote", profile.BodyFontFamily, bodyHalfPts, false, true, 720, afterSpacingTwips, lineSpacingTwips),
                caption,
                authors,
                affiliation,
                keywords,
                figure,
                equation,
                tableText,
                bibliography,
                numbered));
    }

    private static XElement CreateStyleElement(
        string styleId,
        string styleName,
        string fontFamily,
        int sizeHalfPts,
        bool bold,
        bool italic,
        int firstLineIndentTwips,
        int afterSpacingTwips,
        int lineSpacingTwips)
    {
        var pPr = new XElement(W + "pPr",
            new XElement(W + "spacing",
                new XAttribute(W + "after", afterSpacingTwips),
                new XAttribute(W + "line", lineSpacingTwips),
                new XAttribute(W + "lineRule", "auto")));

        if (firstLineIndentTwips > 0)
        {
            pPr.Add(new XElement(W + "ind", new XAttribute(W + "firstLine", firstLineIndentTwips)));
        }

        var rPr = new XElement(W + "rPr",
            new XElement(W + "rFonts",
                new XAttribute(W + "ascii", fontFamily),
                new XAttribute(W + "hAnsi", fontFamily)));

        if (bold) rPr.Add(new XElement(W + "b"));
        if (italic) rPr.Add(new XElement(W + "i"));
        rPr.Add(new XElement(W + "sz", new XAttribute(W + "val", sizeHalfPts)));

        return new XElement(W + "style",
            new XAttribute(W + "type", "paragraph"),
            new XAttribute(W + "styleId", styleId),
            new XElement(W + "name", new XAttribute(W + "val", styleName)),
            pPr,
            rPr);
    }
}
