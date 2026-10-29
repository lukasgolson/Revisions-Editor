using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using RevisionsEditor.Core.Formatting;
using RevisionsEditor.Core.Models;
using RevisionsEditor.Core.Scholarly;
using RevisionsEditor.Core.Serialization;

namespace RevisionsEditor.Core.Docx;

public static class DocxIngestionService
{
    private static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";

    private sealed record CommentMetadata(string Id, string Author, DateTimeOffset Date, string Text);

    /// <summary>
    /// Ingests a .docx file stream or path, extracting semantic blocks, stripping foreign inline styling,
    /// and extracting all Word Track Changes (w:ins, w:del, w:moveFrom, w:moveTo) and comments (word/comments.xml).
    /// </summary>
    /// <param name="titlePage">Recognises the title page written from the form, for copies that lost the export's
    /// markers (saved by Google Docs, LibreOffice or a journal's system).</param>
    public static ManuscriptDocument IngestDocx(
        string docxFilePath,
        ManuscriptDocument? baselineForUntrackedComparison = null,
        FormattingRuleProfile? formattingProfile = null,
        TitlePageFilter? titlePage = null,
        ImportedObjects? objects = null)
    {
        using var fs = File.OpenRead(docxFilePath);
        return IngestDocx(fs, Path.GetFileNameWithoutExtension(docxFilePath), baselineForUntrackedComparison, formattingProfile, titlePage, objects);
    }

    /// <param name="objects">Turns pictures into figures and tables into table blocks (their files collected in it, for
    /// the caller to write). Without it, pictures are left out and table cells are read as paragraphs.</param>
    public static ManuscriptDocument IngestDocx(
        Stream docxStream,
        string fallbackTitle = "Imported Manuscript",
        ManuscriptDocument? baselineForUntrackedComparison = null,
        FormattingRuleProfile? formattingProfile = null,
        TitlePageFilter? titlePage = null,
        ImportedObjects? objects = null)
    {
        using var archive = new ZipArchive(docxStream, ZipArchiveMode.Read, leaveOpen: true);

        var commentsById = ParseCommentsPart(archive);
        var variables = DocxRoundTrip.ReadDocumentVariables(archive);
        var docEntry = archive.GetEntry("word/document.xml")
            ?? throw new InvalidOperationException("Invalid .docx package: missing word/document.xml.");

        using var docXmlStream = docEntry.Open();
        var xdoc = XDocument.Load(docXmlStream);

        var manuscript = new ManuscriptDocument
        {
            Title = fallbackTitle
        };

        var body = xdoc.Root?.Element(W + "body");
        if (body == null)
        {
            return manuscript;
        }

        var paragraphs = TopLevelParagraphs(body).Where(p => !DocxRoundTrip.IsInsideSkippedContent(p)).ToList();
        var titlePageParagraphs = titlePage != null ? FindTitlePage(paragraphs, titlePage, commentsById, variables) : [];
        var ordered = paragraphs.Where(p => !titlePageParagraphs.Contains(p)).ToList();
        var layout = objects != null ? FindObjects(ordered, archive, commentsById, variables) : null;

        int pIndex = 0;
        string NextId() => baselineForUntrackedComparison != null && pIndex < baselineForUntrackedComparison.Blocks.Count
            ? baselineForUntrackedComparison.Blocks[pIndex].ParagraphId
            : $"p-{pIndex + 1:D3}";

        // A figure or table block, with the caption paragraph's id, text and changes when it has one.
        void AddObject(XElement? captionParagraph, Func<string, string> build, bool figure)
        {
            DocumentBlock block;
            List<RevisionHunk> hunks = [];
            string caption = string.Empty;
            if (captionParagraph != null && ParseParagraphElement(captionParagraph, pIndex + 1, commentsById, baselineForUntrackedComparison, variables) is { Block: { } parsed } result)
            {
                block = parsed;
                hunks = result.Hunks;
                _ = figure ? ImportedObjects.TryFigureCaption(parsed.Text, out caption) : ImportedObjects.TryTableCaption(parsed.Text, out caption);
            }
            else
            {
                block = new DocumentBlock { ParagraphId = NextId() };
            }
            block.Role = figure ? BlockStyleRole.Figure : BlockStyleRole.Table;
            block.Text = build(caption);
            pIndex++;
            manuscript.Blocks.Add(block);
            manuscript.Hunks.AddRange(hunks);
        }

        var emittedTables = new HashSet<XElement>();
        foreach (var pElem in ordered)
        {
            if (layout != null)
            {
                if (layout.CaptionOf.TryGetValue(pElem, out var captioned))
                {
                    // A "Table 1." caption above its table: the table goes here. (A caption under its table or
                    // picture was read with it.)
                    if (captioned.Name == W + "tbl" && emittedTables.Add(captioned))
                    {
                        var rows = layout.Rows[captioned];
                        AddObject(pElem, caption => objects!.Table(rows, caption), figure: false);
                    }
                    continue; // otherwise it went with its image or table
                }
                if (layout.Images.TryGetValue(pElem, out var image))
                {
                    layout.ImageCaption.TryGetValue(pElem, out var captionParagraph);
                    AddObject(captionParagraph, caption => objects!.Figure(image.Bytes, image.Extension, caption), figure: true);
                    continue;
                }
                if (layout.TableOf.TryGetValue(pElem, out var table))
                {
                    if (emittedTables.Add(table))
                    {
                        layout.TableCaption.TryGetValue(table, out var captionParagraph);
                        var rows = layout.Rows[table];
                        AddObject(captionParagraph, caption => objects!.Table(rows, caption), figure: false);
                    }
                    continue;
                }
            }

            var marker = DocxRoundTrip.BlockMarkerOf(pElem, variables);
            if (marker is { Skip: true })
            {
                continue; // generated by the export (a figure's image paragraph)
            }
            var (block, paragraphHunks) = ParseParagraphElement(pElem, pIndex + 1, commentsById, baselineForUntrackedComparison, variables);
            if (block == null && marker != null)
            {
                // A figure with an empty caption or an equation still is a block of the manuscript.
                block = new DocumentBlock
                {
                    ParagraphId = baselineForUntrackedComparison != null && pIndex < baselineForUntrackedComparison.Blocks.Count
                        ? baselineForUntrackedComparison.Blocks[pIndex].ParagraphId
                        : $"p-{pIndex + 1:D3}"
                };
            }
            if (block == null)
            {
                continue;
            }

            pIndex++;
            if (marker != null)
            {
                DocxRoundTrip.ApplyBlockMarker(block, paragraphHunks, marker);
            }
            if (pIndex == 1 && block.Role == BlockStyleRole.Heading1)
            {
                block.Role = BlockStyleRole.Title;
            }

            if (block.Role == BlockStyleRole.Title)
            {
                manuscript.Title = CriticMarkupSerializer.ResolveCriticMarkupText(block.Text, MarkupDisplayMode.NoMarkupFinal);
            }

            manuscript.Blocks.Add(block);
            manuscript.Hunks.AddRange(paragraphHunks);
        }

        if (baselineForUntrackedComparison != null)
        {
            Diffing.DiffEngineService.DetectUntrackedEdits(baselineForUntrackedComparison, manuscript);
        }

        var activeProfile = formattingProfile ?? FormattingRuleProfile.GetBuiltInProfiles()[0];
        FormattingRuleEngine.ApplyFormattingRules(manuscript, activeProfile);

        return manuscript;
    }

    private static readonly XNamespace A = "http://schemas.openxmlformats.org/drawingml/2006/main";
    private static readonly XNamespace R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static readonly XNamespace V = "urn:schemas-microsoft-com:vml";
    private static readonly XNamespace OfficeMath = "http://schemas.openxmlformats.org/officeDocument/2006/math";
    private static readonly XNamespace PkgRel = "http://schemas.openxmlformats.org/package/2006/relationships";

    /// <summary>Where the pictures and tables are, and which paragraphs caption them.</summary>
    private sealed class ObjectLayout
    {
        public Dictionary<XElement, (byte[] Bytes, string Extension)> Images { get; } = [];
        /// <summary>Image paragraph → the "Figure 2." paragraph under it.</summary>
        public Dictionary<XElement, XElement> ImageCaption { get; } = [];
        /// <summary>Paragraph inside a table → that (outermost) table.</summary>
        public Dictionary<XElement, XElement> TableOf { get; } = [];
        public Dictionary<XElement, IReadOnlyList<IReadOnlyList<string>>> Rows { get; } = [];
        /// <summary>Table → the "Table 1." paragraph above (or under) it.</summary>
        public Dictionary<XElement, XElement> TableCaption { get; } = [];
        /// <summary>Caption paragraph → the image paragraph or table it captions.</summary>
        public Dictionary<XElement, XElement> CaptionOf { get; } = [];
    }

    private static ObjectLayout FindObjects(
        List<XElement> paragraphs,
        ZipArchive archive,
        Dictionary<string, CommentMetadata> commentsById,
        IReadOnlyDictionary<string, string> variables)
    {
        var layout = new ObjectLayout();
        var targets = ReadImageRelationships(archive);
        string Final(XElement p) => ParseParagraphElement(p, 1, commentsById, null, variables).Block is { } b
            ? CriticMarkupSerializer.ResolveCriticMarkupText(b.Text, MarkupDisplayMode.NoMarkupFinal).Trim()
            : string.Empty;

        foreach (var p in paragraphs)
        {
            // A table holding an equation lays out a numbered equation (as Word and the export do), not data.
            if (p.Ancestors(W + "tbl").LastOrDefault() is { } table && !table.Descendants(OfficeMath + "oMath").Any())
            {
                layout.TableOf[p] = table;
                if (!layout.Rows.ContainsKey(table))
                {
                    layout.Rows[table] = table.Elements(W + "tr")
                        .Select(tr => (IReadOnlyList<string>)tr.Elements(W + "tc").Select(tc => string.Join(" ", tc.Elements(W + "p").Select(Final).Where(t => t.Length > 0))).ToList())
                        .Where(r => r.Count > 0)
                        .ToList();
                }
                continue;
            }
            if (DocxRoundTrip.BlockMarkerOf(p, variables) is { Skip: true })
            {
                continue; // the export's own figure picture: its caption carries the figure
            }
            string? relId = p.Descendants(A + "blip").Select(b => (string?)b.Attribute(R + "embed")).FirstOrDefault(id => id != null)
                ?? p.Descendants(V + "imagedata").Select(i => (string?)i.Attribute(R + "id")).FirstOrDefault(id => id != null);
            if (relId != null && targets.TryGetValue(relId, out var target) && archive.GetEntry(target) is { } entry)
            {
                using var stream = entry.Open();
                using var ms = new MemoryStream();
                stream.CopyTo(ms);
                layout.Images[p] = (ms.ToArray(), Path.GetExtension(target));
            }
        }

        // Captions: the paragraph under a picture ("Figure 2."); the paragraph above a table ("Table 1."), else under it.
        var units = new List<XElement>(); // paragraphs, with each table once
        foreach (var p in paragraphs)
        {
            var unit = layout.TableOf.TryGetValue(p, out var t) ? t : p;
            if (units.Count == 0 || units[^1] != unit)
            {
                units.Add(unit);
            }
        }
        for (int i = 0; i < units.Count; i++)
        {
            // The export's own captions carry their figure or table in a marker and are never another object's.
            bool IsFree(int k) => k >= 0 && k < units.Count && units[k].Name == W + "p" && !layout.Images.ContainsKey(units[k]) && !layout.CaptionOf.ContainsKey(units[k])
                && DocxRoundTrip.BlockMarkerOf(units[k], variables) == null;
            if (layout.Images.ContainsKey(units[i]) && IsFree(i + 1) && ImportedObjects.TryFigureCaption(Final(units[i + 1]), out _))
            {
                layout.ImageCaption[units[i]] = units[i + 1];
                layout.CaptionOf[units[i + 1]] = units[i];
            }
            else if (units[i].Name == W + "tbl")
            {
                int k = IsFree(i - 1) && ImportedObjects.TryTableCaption(Final(units[i - 1]), out _) ? i - 1
                    : IsFree(i + 1) && ImportedObjects.TryTableCaption(Final(units[i + 1]), out _) ? i + 1
                    : -1;
                if (k >= 0)
                {
                    layout.TableCaption[units[i]] = units[k];
                    layout.CaptionOf[units[k]] = units[i];
                }
            }
        }
        return layout;
    }

    /// <summary>Relationship id → package path of the document's embedded pictures.</summary>
    private static Dictionary<string, string> ReadImageRelationships(ZipArchive archive)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (archive.GetEntry("word/_rels/document.xml.rels") is not { } entry)
        {
            return result;
        }
        using var stream = entry.Open();
        foreach (var rel in XDocument.Load(stream).Descendants(PkgRel + "Relationship"))
        {
            string? id = (string?)rel.Attribute("Id");
            string? target = (string?)rel.Attribute("Target");
            if (id == null || target == null || string.Equals((string?)rel.Attribute("TargetMode"), "External", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            // Targets are relative to word/ (or absolute from the package root).
            var parts = new List<string>(target.StartsWith('/') ? [] : ["word"]);
            foreach (string part in target.TrimStart('/').Split('/'))
            {
                if (part == "..") { if (parts.Count > 0) parts.RemoveAt(parts.Count - 1); }
                else if (part is not "." and not "") parts.Add(part);
            }
            result[id] = string.Join('/', parts);
        }
        return result;
    }

    /// <summary>The paragraphs of the title page written from the form, found by their text before ids are given.</summary>
    private static HashSet<XElement> FindTitlePage(
        List<XElement> paragraphs,
        TitlePageFilter titlePage,
        Dictionary<string, CommentMetadata> commentsById,
        IReadOnlyDictionary<string, string> variables)
    {
        var parsed = new List<(XElement Element, BlockStyleRole Role, string Text)>();
        foreach (var p in paragraphs)
        {
            if (DocxRoundTrip.BlockMarkerOf(p, variables) is { Skip: true }
                || ParseParagraphElement(p, 1, commentsById, null, variables).Block is not { } block)
            {
                continue;
            }
            var role = parsed.Count == 0 && block.Role == BlockStyleRole.Heading1 ? BlockStyleRole.Title : block.Role;
            parsed.Add((p, role, block.Text));
        }
        var drop = titlePage.Apply(parsed.Select(x => (x.Role, x.Text)).ToList());
        return [.. drop.Select(i => parsed[i].Element)];
    }

    private static Dictionary<string, CommentMetadata> ParseCommentsPart(ZipArchive archive)
    {
        var result = new Dictionary<string, CommentMetadata>(StringComparer.Ordinal);
        var commentsEntry = archive.GetEntry("word/comments.xml");
        if (commentsEntry == null)
        {
            return result;
        }

        using var stream = commentsEntry.Open();
        var xdoc = XDocument.Load(stream);
        if (xdoc.Root == null)
        {
            return result;
        }

        foreach (var c in xdoc.Root.Elements(W + "comment"))
        {
            string id = c.Attribute(W + "id")?.Value ?? string.Empty;
            string author = c.Attribute(W + "author")?.Value ?? "Supervisor";
            string? dateStr = c.Attribute(W + "date")?.Value;
            DateTimeOffset date = DateTimeOffset.TryParse(dateStr, out var parsedDate)
                ? parsedDate
                : DateTimeOffset.UtcNow;

            string text = string.Concat(c.Descendants(W + "t").Select(t => t.Value)).Trim();
            if (!string.IsNullOrEmpty(id))
            {
                result[id] = new CommentMetadata(id, author, date, text);
            }
        }

        return result;
    }

    private static (DocumentBlock? Block, List<RevisionHunk> Hunks) ParseParagraphElement(
        XElement pElem,
        int paragraphNumber,
        Dictionary<string, CommentMetadata> commentsById,
        ManuscriptDocument? baselineDoc,
        IReadOnlyDictionary<string, string> variables)
    {
        string paragraphId = baselineDoc != null && paragraphNumber - 1 < baselineDoc.Blocks.Count
            ? baselineDoc.Blocks[paragraphNumber - 1].ParagraphId
            : $"p-{paragraphNumber:D3}";

        BlockStyleRole role = DetectParagraphStyleRole(pElem);
        var hunks = new List<RevisionHunk>();
        var sb = new StringBuilder();

        string? activeCommentId = null;
        var activeCommentAnchorSb = new StringBuilder();
        int hunkCounter = 0;

        var childNodes = FlattenParagraphContent(pElem, variables);
        for (int i = 0; i < childNodes.Count; i++)
        {
            var el = childNodes[i];
            XName name = el.Name;

            if (name == W + "pPr")
            {
                continue;
            }

            if (name == W + "commentRangeStart")
            {
                activeCommentId = el.Attribute(W + "id")?.Value;
                activeCommentAnchorSb.Clear();
                continue;
            }

            if (name == W + "commentRangeEnd")
            {
                string? endedId = el.Attribute(W + "id")?.Value;
                if (endedId != null && commentsById.TryGetValue(endedId, out var cMeta))
                {
                    hunkCounter++;
                    string anchorText = activeCommentAnchorSb.ToString();
                    var commentHunk = new RevisionHunk
                    {
                        HunkId = $"{paragraphId}-h{hunkCounter:D2}",
                        ParagraphId = paragraphId,
                        HunkType = RevisionHunkType.Comment,
                        OriginalText = anchorText,
                        CommentText = cMeta.Text,
                        Author = cMeta.Author,
                        TimestampUtc = cMeta.Date,
                        Status = RevisionHunkStatus.Pending
                    };
                    hunks.Add(commentHunk);

                    // Replace the plain anchor text at the tail of sb with CriticMarkup highlight+comment
                    if (!string.IsNullOrEmpty(anchorText) && sb.ToString().EndsWith(anchorText, StringComparison.Ordinal))
                    {
                        sb.Length -= anchorText.Length;
                        sb.Append($"{{=={anchorText}==}}{{>>{cMeta.Author}: {cMeta.Text}<<}}");
                    }
                    else
                    {
                        sb.Append($"{{>>{cMeta.Author}: {cMeta.Text}<<}}");
                    }
                }
                activeCommentId = null;
                activeCommentAnchorSb.Clear();
                continue;
            }

            if (name == W + "r")
            {
                // Standalone commentReference when no commentRangeStart/End was used
                var commentRef = el.Element(W + "commentReference");
                if (commentRef != null)
                {
                    string? refId = commentRef.Attribute(W + "id")?.Value;
                    if (refId != null && commentsById.TryGetValue(refId, out var cMeta)
                        && hunks.All(h => h.CommentText != cMeta.Text))
                    {
                        hunkCounter++;
                        hunks.Add(new RevisionHunk
                        {
                            HunkId = $"{paragraphId}-h{hunkCounter:D2}",
                            ParagraphId = paragraphId,
                            HunkType = RevisionHunkType.Comment,
                            OriginalText = string.Empty,
                            CommentText = cMeta.Text,
                            Author = cMeta.Author,
                            TimestampUtc = cMeta.Date,
                            Status = RevisionHunkStatus.Pending
                        });
                        sb.Append($"{{>>{cMeta.Author}: {cMeta.Text}<<}}");
                    }
                }

                string runText = ExtractRunText(el);
                sb.Append(runText);
                if (activeCommentId != null)
                {
                    activeCommentAnchorSb.Append(runText);
                }
                continue;
            }

            // Tracked Deletion followed immediately by Tracked Insertion -> Coalesce as Replacement {~~old~>new~~}
            if ((name == W + "del" || name == W + "moveFrom")
                && i + 1 < childNodes.Count
                && (childNodes[i + 1].Name == W + "ins" || childNodes[i + 1].Name == W + "moveTo"))
            {
                var delEl = el;
                var insEl = childNodes[i + 1];
                string delText = ExtractDeletedText(delEl);
                string insText = ExtractInsertedText(insEl);
                string author = insEl.Attribute(W + "author")?.Value
                    ?? delEl.Attribute(W + "author")?.Value
                    ?? "Supervisor";
                DateTimeOffset date = ParseAttrDate(insEl.Attribute(W + "date")?.Value ?? delEl.Attribute(W + "date")?.Value);

                hunkCounter++;
                var repHunk = new RevisionHunk
                {
                    HunkId = $"{paragraphId}-h{hunkCounter:D2}",
                    ParagraphId = paragraphId,
                    HunkType = RevisionHunkType.Replacement,
                    OriginalText = delText,
                    ProposedText = insText,
                    Author = author,
                    TimestampUtc = date,
                    Status = RevisionHunkStatus.Pending
                };
                hunks.Add(repHunk);
                sb.Append($"{{~~{delText}~>{insText}~~}}");
                i++; // Consumed insEl as well
                continue;
            }

            if (name == W + "ins" || name == W + "moveTo")
            {
                string insText = ExtractInsertedText(el);
                if (!string.IsNullOrEmpty(insText))
                {
                    string author = el.Attribute(W + "author")?.Value ?? "Supervisor";
                    DateTimeOffset date = ParseAttrDate(el.Attribute(W + "date")?.Value);
                    hunkCounter++;
                    hunks.Add(new RevisionHunk
                    {
                        HunkId = $"{paragraphId}-h{hunkCounter:D2}",
                        ParagraphId = paragraphId,
                        HunkType = RevisionHunkType.Insertion,
                        ProposedText = insText,
                        Author = author,
                        TimestampUtc = date,
                        Status = RevisionHunkStatus.Pending
                    });
                    sb.Append($"{{++{insText}++}}");
                }
                continue;
            }

            if (name == W + "del" || name == W + "moveFrom")
            {
                string delText = ExtractDeletedText(el);
                if (!string.IsNullOrEmpty(delText))
                {
                    string author = el.Attribute(W + "author")?.Value ?? "Supervisor";
                    DateTimeOffset date = ParseAttrDate(el.Attribute(W + "date")?.Value);
                    hunkCounter++;
                    hunks.Add(new RevisionHunk
                    {
                        HunkId = $"{paragraphId}-h{hunkCounter:D2}",
                        ParagraphId = paragraphId,
                        HunkType = RevisionHunkType.Deletion,
                        OriginalText = delText,
                        Author = author,
                        TimestampUtc = date,
                        Status = RevisionHunkStatus.Pending
                    });
                    sb.Append($"{{--{delText}--}}");
                }
            }
        }

        string finalBlockText = sb.ToString().Trim();
        if (string.IsNullOrWhiteSpace(finalBlockText))
        {
            return (null, []);
        }

        string cleanContext = CriticMarkupSerializer.ResolveCriticMarkupText(finalBlockText, MarkupDisplayMode.NoMarkupFinal);
        foreach (var h in hunks)
        {
            h.ContextSnippet = cleanContext;
        }

        var block = new DocumentBlock
        {
            ParagraphId = paragraphId,
            Role = role,
            Text = finalBlockText
        };

        return (block, hunks);
    }

    private static BlockStyleRole DetectParagraphStyleRole(XElement pElem)
    {
        string? styleVal = pElem.Element(W + "pPr")?.Element(W + "pStyle")?.Attribute(W + "val")?.Value;
        if (string.IsNullOrWhiteSpace(styleVal))
        {
            return BlockStyleRole.BodyParagraph;
        }

        string s = styleVal.Trim().ToLowerInvariant();
        if (s.Contains("title") && !s.Contains("subtitle")) return BlockStyleRole.Title;
        if (s.Contains("abstract")) return BlockStyleRole.Abstract;
        if (s is "heading1" or "heading 1" or "h1") return BlockStyleRole.Heading1;
        if (s is "heading2" or "heading 2" or "h2") return BlockStyleRole.Heading2;
        if (s is "heading3" or "heading 3" or "h3") return BlockStyleRole.Heading3;
        if (s.Contains("quote")) return BlockStyleRole.BlockQuote;
        if (s.Contains("caption")) return BlockStyleRole.Caption;
        if (s.Contains("bib") || s.Contains("reference")) return BlockStyleRole.ReferenceItem;
        return BlockStyleRole.BodyParagraph;
    }

    private static string ExtractRunText(XElement rElem)
    {
        var sb = new StringBuilder();
        foreach (var child in rElem.Elements())
        {
            if (child.Name == W + "t")
            {
                sb.Append(child.Value);
            }
            else if (child.Name == W + "tab" || child.Name == W + "br" || child.Name == W + "cr")
            {
                // Tabs and soft line breaks separate words; dropping them would merge adjacent words.
                sb.Append(' ');
            }
            else if (child.Name == W + "noBreakHyphen")
            {
                sb.Append('-');
            }
        }

        string raw = sb.ToString();
        if (string.IsNullOrEmpty(raw))
        {
            return string.Empty;
        }

        // Preserve only semantic emphasis (bold/italic), ignoring all font/size/color overrides
        var rPr = rElem.Element(W + "rPr");
        bool isBold = IsToggleOn(rPr?.Element(W + "b"));
        bool isItalic = IsToggleOn(rPr?.Element(W + "i"));

        // Superscript and subscript as Pandoc writes them (m^2^, CO~2~); only for text without spaces.
        string? vertAlign = rPr?.Element(W + "vertAlign")?.Attribute(W + "val")?.Value;
        if (vertAlign is "superscript" or "subscript" && raw.Trim().Length > 0 && !raw.Trim().Any(char.IsWhiteSpace))
        {
            string script = vertAlign == "superscript" ? "^" : "~";
            string lead = raw[..(raw.Length - raw.TrimStart().Length)];
            string trail = raw[raw.TrimEnd().Length..];
            raw = $"{lead}{script}{raw.Trim()}{script}{trail}";
        }

        string marker = isBold && isItalic ? "***" : isBold ? "**" : isItalic ? "*" : string.Empty;
        string core = raw.Trim();
        if (marker.Length == 0 || core.Length == 0)
        {
            return raw;
        }

        // Google Docs often ends a bold run with the following space ("bold "). Keep spaces outside the
        // markers: "**bold **" is not valid emphasis and its asterisks would survive as literal text.
        string leading = raw[..(raw.Length - raw.TrimStart().Length)];
        string trailing = raw[raw.TrimEnd().Length..];
        return $"{leading}{marker}{core}{marker}{trailing}";
    }

    /// <summary>
    /// A WordprocessingML on/off property such as &lt;w:b/&gt;. Google Docs writes explicit w:val="0" for "off".
    /// </summary>
    private static bool IsToggleOn(XElement? toggle)
    {
        if (toggle == null)
        {
            return false;
        }
        string? val = toggle.Attribute(W + "val")?.Value;
        return val is null || !(val == "0" || val.Equals("false", StringComparison.OrdinalIgnoreCase) || val.Equals("off", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Paragraphs in document order, including those inside tables, but not paragraphs nested inside another
    /// paragraph (text boxes), which would otherwise be read twice.
    /// </summary>
    private static IEnumerable<XElement> TopLevelParagraphs(XElement body)
        => body.Descendants(W + "p").Where(p => !p.Ancestors(W + "p").Any());

    /// <summary>
    /// The paragraph's content elements, with containers that only wrap runs (hyperlinks, smart tags, content
    /// controls, simple fields) replaced by their children. Google Docs puts all link text inside w:hyperlink.
    /// </summary>
    private static List<XElement> FlattenParagraphContent(XElement pElem, IReadOnlyDictionary<string, string> variables)
    {
        var result = new List<XElement>();
        void Visit(XElement container)
        {
            foreach (var child in container.Elements())
            {
                if (child.Name == W + "hyperlink" || child.Name == W + "smartTag" || child.Name == W + "customXml"
                    || child.Name == W + "fldSimple" || child.Name == W + "sdtContent")
                {
                    Visit(child);
                }
                else if (child.Name == W + "sdt" && DocxRoundTrip.ReplacementForContentControl(child, variables, out var replacement))
                {
                    // A citation, number or label the export rendered: take its source (or nothing for a label).
                    if (replacement != null)
                    {
                        result.Add(replacement);
                    }
                }
                else if (child.Name == W + "sdt")
                {
                    var content = child.Element(W + "sdtContent");
                    if (content != null)
                    {
                        Visit(content);
                    }
                }
                else
                {
                    result.Add(child);
                }
            }
        }
        Visit(pElem);
        return result;
    }

    private static string ExtractInsertedText(XElement insElem)
    {
        return string.Concat(insElem.Descendants(W + "t").Select(t => t.Value));
    }

    private static string ExtractDeletedText(XElement delElem)
    {
        return string.Concat(delElem.Descendants().Where(e => e.Name == W + "delText" || e.Name == W + "t").Select(t => t.Value));
    }

    private static DateTimeOffset ParseAttrDate(string? dateStr)
    {
        return DateTimeOffset.TryParse(dateStr, out var dt) ? dt : DateTimeOffset.UtcNow;
    }
}
