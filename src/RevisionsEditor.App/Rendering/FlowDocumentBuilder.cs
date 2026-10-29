using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using RevisionsEditor.Core.Diffing;
using RevisionsEditor.Core.Formatting;
using RevisionsEditor.Core.Models;
using RevisionsEditor.Core.Scholarly;
using RevisionsEditor.Core.Serialization;

namespace RevisionsEditor.App.Rendering;

/// <summary>
/// Identifies the manuscript block behind a paragraph in the editor. Editable paragraphs carry their id and
/// style; paragraphs with pending review changes are read-only and carry the original block unchanged.
/// </summary>
public sealed class EditorBlockTag
{
    public string? ParagraphId { get; set; }
    public BlockStyleRole Role { get; set; } = BlockStyleRole.BodyParagraph;
    /// <summary>Set for read-only paragraphs that still have pending changes.</summary>
    public DocumentBlock? LockedBlock { get; set; }
}

/// <summary>
/// Renders manuscripts and diffs as paper documents. Revision marks follow Microsoft Word's conventions:
/// insertions are underlined, deletions struck through, a change bar marks changed paragraphs, and comments
/// appear as highlighted anchors with an inline [initials#] reference. Bold and italic (stored as Markdown
/// emphasis) are drawn as formatting.
/// </summary>
public static class FlowDocumentBuilder
{
    private const double PointsToPixels = 96.0 / 72.0;

    private static readonly Regex CriticTokenRegex = new(
        @"(\{~~(?<repOld>.*?)~>(?<repNew>.*?)~~\})|(\{\+\+(?<ins>.*?)\+\+\})|(\{--(?<del>.*?)--\})|(\{==(?<hl>.*?)==\}\{>>(?<hlComment>.*?)<<\})|(\{>>(?<comment>.*?)<<\})",
        RegexOptions.Singleline | RegexOptions.Compiled);

    // Emphasis as written by the Word importer and the editor: ***bold italic***, **bold**, *italic*.
    private static readonly Regex EmphasisRegex = new(@"(\*{1,3})([^*\n]+?)\1", RegexOptions.Compiled);

    // Paper is always white with dark ink, like a printed page, independent of the app's light/dark theme.
    public static readonly Brush PaperBrush = Frozen(Colors.White);
    public static readonly Brush InkBrush = Frozen(Color.FromRgb(0x1F, 0x1F, 0x1F));
    private static readonly Brush SecondaryInkBrush = Frozen(Color.FromRgb(0x5F, 0x5F, 0x5F));
    private static readonly Brush InsertionInk = Frozen(Color.FromRgb(0x0B, 0x6A, 0x0B));
    private static readonly Brush DeletionInk = Frozen(Color.FromRgb(0xC4, 0x2B, 0x1C));
    private static readonly Brush ModifiedInk = Frozen(Color.FromRgb(0x00, 0x5F, 0xB8));
    private static readonly Brush CommentAnchorFill = Frozen(Color.FromRgb(0xFC, 0xE4, 0xEC));
    private static readonly Brush CommentInk = Frozen(Color.FromRgb(0xA4, 0x26, 0x2C));
    private static readonly Brush ChangeBarBrush = Frozen(Color.FromRgb(0xC4, 0x2B, 0x1C));
    private static readonly Brush RuleBrush = Frozen(Color.FromRgb(0xD1, 0xD1, 0xD1));

    private static readonly FontFamily UiFont = new("Segoe UI");
    private static readonly FontFamily MonoFont = new("Cascadia Mono, Consolas");
    private static readonly Dictionary<string, FontFamily> FontCache = new(StringComparer.OrdinalIgnoreCase);

    private const string LockedParagraphHint =
        "This paragraph has pending changes. Accept or reject them in the Changes pane to edit it.";

    /// <summary>
    /// Builds the editable manuscript page, styled by the active FormattingRuleProfile.
    /// Paragraphs without pending changes are ordinary editable paragraphs. Paragraphs with pending changes
    /// are read-only blocks rendered according to <paramref name="displayMode"/>, so review marks cannot be
    /// edited by accident.
    /// </summary>
    /// <param name="scholarly">Numbers figures, tables and equations and explains citations and linked numbers on hover.</param>
    /// <param name="workspacePath">Where figure images and table files are.</param>
    /// <param name="onScholarlyBlockActivated">Called when a figure, table or equation is double-clicked (to edit it).</param>
    /// <param name="preview">Shows the paper as it will be published: everything rendered, nothing editable.</param>
    public static FlowDocument BuildManuscriptEditorDocument(
        ManuscriptDocument document,
        FormattingRuleProfile profile,
        MarkupDisplayMode displayMode,
        Action<string>? onLockedParagraphClicked = null,
        ScholarlyContext? scholarly = null,
        string? workspacePath = null,
        Action<string>? onScholarlyBlockActivated = null,
        bool preview = false,
        Action? onFrontMatterActivated = null)
    {
        var workingDoc = document.Clone();
        FormattingRuleEngine.ApplyFormattingRules(workingDoc, profile);

        var flowDoc = new FlowDocument
        {
            Background = PaperBrush,
            Foreground = InkBrush,
            FontFamily = GetFont(profile.BodyFontFamily),
            FontSize = profile.BodyFontSizePt * PointsToPixels,
            PagePadding = new Thickness(profile.PageMarginInches * 72.0),
            ColumnWidth = double.PositiveInfinity
        };

        var hunksByParagraph = CriticMarkupSerializer.GroupHunksByParagraph(workingDoc.Hunks);
        var commentNumbers = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var originalBlocks = document.Blocks.ToDictionary(b => b.ParagraphId, b => b, StringComparer.Ordinal);

        bool bibliographyShown = false;
        bool titlePageShown = false;
        // The title page's keywords follow the abstract, unless the manuscript has its own keywords paragraph.
        var structure = new Core.Checks.ManuscriptStructure(workingDoc.Blocks);
        string? keywordsAfter = scholarly is { Project.FrontMatter.Keywords.Count: > 0 } && structure.Keywords().Block == null
            ? structure.AbstractBlocks().LastOrDefault()?.ParagraphId
            : null;

        foreach (var block in workingDoc.Blocks)
        {
            AddBlock(block);
            // The title page from the form, under the title (read-only: it is edited in the Paper panel), then the
            // draft notes.
            if (block.Role == BlockStyleRole.Title && !titlePageShown && scholarly != null)
            {
                titlePageShown = true;
                AddTitlePage(flowDoc, scholarly.Project.FrontMatter, profile, onFrontMatterActivated);
                AddDraftNotes(flowDoc, scholarly.Project.FrontMatter, profile, onFrontMatterActivated);
            }
            if (block.ParagraphId == keywordsAfter)
            {
                AddKeywords(flowDoc, scholarly!, profile, onFrontMatterActivated);
            }
        }

        void AddBlock(DocumentBlock block)
        {
            var hunks = CriticMarkupSerializer.HunksFor(hunksByParagraph, block.ParagraphId);
            if (block.Role is BlockStyleRole.Figure or BlockStyleRole.Table or BlockStyleRole.Equation)
            {
                var original = originalBlocks.TryGetValue(block.ParagraphId, out var o) ? o : block;
                flowDoc.Blocks.Add(ScholarlyBlockView.Build(original, block, profile, scholarly, workspacePath, onScholarlyBlockActivated));
                return;
            }

            if (preview)
            {
                flowDoc.Blocks.Add(PreviewParagraph(block, profile, scholarly));
                if (scholarly is { HasCitations: true } && !bibliographyShown && IsReferencesHeading(block))
                {
                    bibliographyShown = true;
                    AddPreviewBibliography(flowDoc, profile, scholarly);
                }
                return;
            }
            string rendered = CriticMarkupSerializer.RenderBlockWithHunks(block, hunks);
            string number = block.Role is BlockStyleRole.Heading1 or BlockStyleRole.Heading2 or BlockStyleRole.Heading3
                ? block.ComputedSectionNumber
                : string.Empty;

            if (hunks.Any(h => h.Status == RevisionHunkStatus.Pending))
            {
                var original = originalBlocks.TryGetValue(block.ParagraphId, out var o) ? o : block;
                flowDoc.Blocks.Add(BuildLockedBlock(original, block.Role, number + rendered, profile, displayMode, commentNumbers, onLockedParagraphClicked));
                return;
            }

            if (block.Role == BlockStyleRole.Title)
            {
                // Like the authors under it, the title is edited in the Paper panel's title page form. It is kept
                // unchanged when the page is read back.
                var original = originalBlocks.TryGetValue(block.ParagraphId, out var o) ? o : block;
                flowDoc.Blocks.Add(ReadOnlyTitle(original, rendered, profile, scholarly, onFrontMatterActivated));
                return;
            }

            var para = CreateStyledParagraph(block.Role, profile);
            para.Tag = new EditorBlockTag { ParagraphId = block.ParagraphId, Role = block.Role };
            if (number.Length > 0)
            {
                para.Inlines.Add(SectionNumberLabel(number));
            }
            // No pending changes: any leftover markup is already resolved, so show the final text.
            AddScholarlyRuns(para.Inlines, CriticMarkupSerializer.ResolveCriticMarkupText(rendered, MarkupDisplayMode.NoMarkupFinal), scholarly);
            flowDoc.Blocks.Add(para);
        }

        if (preview && scholarly is { HasCitations: true } && !bibliographyShown)
        {
            var heading = CreateStyledParagraph(BlockStyleRole.Heading1, profile);
            heading.Inlines.Add(new Run("References"));
            flowDoc.Blocks.Add(heading);
            AddPreviewBibliography(flowDoc, profile, scholarly);
        }

        if (flowDoc.Blocks.Count == 0)
        {
            // An empty manuscript still needs a paragraph to type into.
            var empty = CreateStyledParagraph(BlockStyleRole.BodyParagraph, profile);
            empty.Tag = new EditorBlockTag { Role = BlockStyleRole.BodyParagraph };
            flowDoc.Blocks.Add(empty);
        }

        return flowDoc;
    }

    /// <summary>
    /// Section numbers are computed from the heading structure, so they are shown as a label that cannot be
    /// edited (and is ignored when the page is read back).
    /// </summary>
    private static InlineUIContainer SectionNumberLabel(string number) => new(new TextBlock
    {
        Text = number,
        Foreground = InkBrush,
        Focusable = false,
        IsHitTestVisible = false
    })
    {
        BaselineAlignment = BaselineAlignment.Baseline
    };

    private static BlockUIContainer BuildLockedBlock(
        DocumentBlock original,
        BlockStyleRole role,
        string textWithCriticMarkup,
        FormattingRuleProfile profile,
        MarkupDisplayMode mode,
        Dictionary<string, int> commentNumbers,
        Action<string>? onClicked)
    {
        var style = CreateStyledParagraph(role, profile);
        var text = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            FontSize = style.FontSize,
            TextAlignment = style.TextAlignment,
            LineHeight = style.LineHeight,
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
            Cursor = Cursors.Arrow,
            ToolTip = LockedParagraphHint
        };
        // Copy only what the role sets; everything else is inherited from the page, like a paragraph would.
        foreach (var property in new[] { TextElement.FontFamilyProperty, TextElement.FontWeightProperty, TextElement.FontStyleProperty, TextElement.ForegroundProperty })
        {
            object value = style.ReadLocalValue(property);
            if (value != DependencyProperty.UnsetValue)
            {
                text.SetValue(property, value);
            }
        }
        PopulateRuns(text.Inlines, textWithCriticMarkup, mode, commentNumbers, style.FontSize);

        if (onClicked != null)
        {
            string id = original.ParagraphId;
            text.MouseLeftButtonDown += (_, _) => onClicked(id);
        }

        // As in Word, a change bar in the margin marks paragraphs with revisions.
        return new BlockUIContainer(text)
        {
            Margin = style.Margin,
            BorderBrush = ChangeBarBrush,
            BorderThickness = new Thickness(2, 0, 0, 0),
            Padding = new Thickness(10, 0, 0, 0),
            Tag = new EditorBlockTag { ParagraphId = original.ParagraphId, Role = role, LockedBlock = original.Clone() }
        };
    }

    /// <summary>
    /// Restyles an existing paragraph for <paramref name="role"/> (used when the editor creates a paragraph).
    /// Copies the role's local values and clears the ones the role does not set.
    /// </summary>
    public static void ApplyParagraphStyle(Paragraph target, BlockStyleRole role, FormattingRuleProfile profile)
    {
        var template = CreateStyledParagraph(role, profile);
        DependencyProperty[] properties =
        [
            TextElement.FontFamilyProperty, TextElement.FontSizeProperty, TextElement.FontWeightProperty,
            TextElement.FontStyleProperty, TextElement.ForegroundProperty, Block.TextAlignmentProperty,
            Block.LineHeightProperty, Block.LineStackingStrategyProperty, Block.MarginProperty,
            Paragraph.TextIndentProperty
        ];
        foreach (var property in properties)
        {
            object value = template.ReadLocalValue(property);
            if (value == DependencyProperty.UnsetValue)
            {
                target.ClearValue(property);
            }
            else
            {
                target.SetValue(property, value);
            }
        }
    }

    private static Paragraph CreateStyledParagraph(BlockStyleRole role, FormattingRuleProfile profile)
    {
        double bodyPx = profile.BodyFontSizePt * PointsToPixels;
        double afterSpacePx = profile.ParagraphSpacingAfterPt * PointsToPixels;
        double lineHeightPx = Math.Max(16.0, bodyPx * profile.LineSpacingMultiplier);

        var para = new Paragraph
        {
            FontSize = bodyPx,
            LineHeight = lineHeightPx,
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
            Margin = new Thickness(0, 0, 0, afterSpacePx)
        };

        switch (role)
        {
            case BlockStyleRole.Title:
                para.FontFamily = GetFont(profile.HeadingFontFamily);
                para.FontSize = profile.TitleFontSizePt * PointsToPixels;
                para.FontWeight = FontWeights.Bold;
                para.TextAlignment = TextAlignment.Center;
                para.LineHeight = para.FontSize * 1.3;
                para.Margin = new Thickness(0, 4, 0, afterSpacePx + 14);
                break;

            case BlockStyleRole.Abstract:
                para.FontStyle = FontStyles.Italic;
                para.TextAlignment = TextAlignment.Justify;
                para.Margin = new Thickness(36, 4, 36, afterSpacePx + 12);
                break;

            case BlockStyleRole.Heading1:
                para.FontFamily = GetFont(profile.HeadingFontFamily);
                para.FontSize = profile.Heading1FontSizePt * PointsToPixels;
                para.FontWeight = FontWeights.Bold;
                para.LineHeight = para.FontSize * 1.35;
                para.Margin = new Thickness(0, 14, 0, afterSpacePx + 4);
                break;

            case BlockStyleRole.Heading2:
                para.FontFamily = GetFont(profile.HeadingFontFamily);
                para.FontSize = profile.Heading2FontSizePt * PointsToPixels;
                para.FontWeight = FontWeights.SemiBold;
                para.LineHeight = para.FontSize * 1.3;
                para.Margin = new Thickness(0, 10, 0, afterSpacePx + 2);
                break;

            case BlockStyleRole.Heading3:
                para.FontFamily = GetFont(profile.HeadingFontFamily);
                para.FontSize = profile.Heading3FontSizePt * PointsToPixels;
                para.FontWeight = FontWeights.SemiBold;
                para.FontStyle = FontStyles.Italic;
                para.LineHeight = para.FontSize * 1.25;
                para.Margin = new Thickness(0, 8, 0, afterSpacePx);
                break;

            case BlockStyleRole.BlockQuote:
                para.FontStyle = FontStyles.Italic;
                para.Margin = new Thickness(36, 4, 18, afterSpacePx);
                break;

            case BlockStyleRole.Caption:
                para.FontSize = Math.Max(11.0, bodyPx - 1.5);
                para.FontStyle = FontStyles.Italic;
                para.Foreground = SecondaryInkBrush;
                break;

            default:
                para.TextIndent = profile.FirstLineIndentInches * 72.0;
                para.TextAlignment = TextAlignment.Justify;
                break;
        }

        return para;
    }

    /// <summary>
    /// Adds <paramref name="text"/> as runs, turning Markdown emphasis into bold and italic formatting.
    /// <paramref name="makeRun"/> creates each run so revision marks can style it as well.
    /// </summary>
    public static void AddEmphasizedRuns(InlineCollection inlines, string text, Func<string, Run> makeRun)
    {
        int last = 0;
        foreach (Match m in EmphasisRegex.Matches(text))
        {
            if (m.Index > last)
            {
                inlines.Add(makeRun(text[last..m.Index]));
            }
            var run = makeRun(m.Groups[2].Value);
            int level = m.Groups[1].Length;
            if (level >= 2)
            {
                run.FontWeight = FontWeights.Bold;
            }
            if (level != 2)
            {
                run.FontStyle = FontStyles.Italic;
            }
            inlines.Add(run);
            last = m.Index + m.Length;
        }
        if (last < text.Length)
        {
            inlines.Add(makeRun(text[last..]));
        }
    }

    private static void PopulateRuns(
        InlineCollection inlines,
        string textWithCriticMarkup,
        MarkupDisplayMode mode,
        Dictionary<string, int> commentNumbers,
        double fontSize)
    {
        if (mode != MarkupDisplayMode.AllMarkup)
        {
            AddEmphasizedRuns(inlines, CriticMarkupSerializer.ResolveCriticMarkupText(textWithCriticMarkup, mode), t => new Run(t));
            return;
        }

        int lastPos = 0;
        foreach (Match m in CriticTokenRegex.Matches(textWithCriticMarkup))
        {
            if (m.Index > lastPos)
            {
                AddEmphasizedRuns(inlines, textWithCriticMarkup[lastPos..m.Index], t => new Run(t));
            }

            if (m.Groups["repOld"].Success)
            {
                AddEmphasizedRuns(inlines, m.Groups["repOld"].Value, DeletionRun);
                AddEmphasizedRuns(inlines, m.Groups["repNew"].Value, InsertionRun);
            }
            else if (m.Groups["ins"].Success)
            {
                AddEmphasizedRuns(inlines, m.Groups["ins"].Value, InsertionRun);
            }
            else if (m.Groups["del"].Success)
            {
                AddEmphasizedRuns(inlines, m.Groups["del"].Value, DeletionRun);
            }
            else if (m.Groups["hl"].Success)
            {
                string comment = m.Groups["hlComment"].Value;
                AddEmphasizedRuns(inlines, m.Groups["hl"].Value, t => new Run(t) { Background = CommentAnchorFill, ToolTip = comment });
                inlines.Add(CommentReferenceRun(comment, fontSize, commentNumbers));
            }
            else if (m.Groups["comment"].Success)
            {
                inlines.Add(CommentReferenceRun(m.Groups["comment"].Value, fontSize, commentNumbers));
            }

            lastPos = m.Index + m.Length;
        }

        if (lastPos < textWithCriticMarkup.Length)
        {
            AddEmphasizedRuns(inlines, textWithCriticMarkup[lastPos..], t => new Run(t));
        }
    }

    private static Run InsertionRun(string text) => new(text)
    {
        Foreground = InsertionInk,
        TextDecorations = TextDecorations.Underline
    };

    private static Run DeletionRun(string text) => new(text)
    {
        Foreground = DeletionInk,
        TextDecorations = TextDecorations.Strikethrough
    };

    /// <summary>
    /// Word-style inline comment reference, e.g. [JS1], with the full comment in its tooltip.
    /// </summary>
    private static Run CommentReferenceRun(string rawComment, double paragraphFontSize, Dictionary<string, int> commentNumbers)
    {
        int colon = rawComment.IndexOf(':');
        string author = colon > 0 && colon < 40 ? rawComment[..colon].Trim() : string.Empty;
        string initials = Initials(author);
        int number = commentNumbers.TryGetValue(initials, out int n) ? n + 1 : 1;
        commentNumbers[initials] = number;

        return new Run($"[{initials}{number}]")
        {
            Foreground = CommentInk,
            Background = CommentAnchorFill,
            FontFamily = UiFont,
            FontSize = Math.Max(9.0, paragraphFontSize * 0.7),
            BaselineAlignment = BaselineAlignment.Superscript,
            ToolTip = rawComment.Trim(),
            Cursor = Cursors.Help
        };
    }

    private static string Initials(string author)
    {
        var letters = author
            .Split([' ', '.', '-', '_'], StringSplitOptions.RemoveEmptyEntries)
            .Where(part => char.IsLetter(part[0]))
            .Select(part => char.ToUpperInvariant(part[0]))
            .Take(3)
            .ToArray();
        return letters.Length == 0 ? "C" : new string(letters);
    }

    /// <summary>
    /// Builds an inline word diff between two manuscript versions, using the same revision marks as the manuscript view.
    /// </summary>
    public static FlowDocument BuildUnifiedDiffFlowDocument(DocumentDiffResult diffResult)
    {
        var doc = new FlowDocument
        {
            Background = PaperBrush,
            Foreground = InkBrush,
            FontFamily = UiFont,
            FontSize = 14,
            PagePadding = new Thickness(40, 32, 40, 32),
            ColumnWidth = double.PositiveInfinity
        };

        if (diffResult.Entries.Count == 0)
        {
            doc.Blocks.Add(new Paragraph(new Run("Select two commits to compare."))
            {
                Foreground = SecondaryInkBrush
            });
            return doc;
        }

        doc.Blocks.Add(new Paragraph
        {
            Inlines =
            {
                new Run(diffResult.BaseLabel),
                new Run("  →  ") { Foreground = SecondaryInkBrush },
                new Run(diffResult.TargetLabel)
            },
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 4)
        });
        doc.Blocks.Add(new Paragraph(new Run(
            $"{diffResult.ChangedParagraphCount} changed paragraphs, {diffResult.TotalAddedWords} words added, {diffResult.TotalRemovedWords} words removed"))
        {
            Foreground = SecondaryInkBrush,
            FontSize = 12.5,
            Margin = new Thickness(0, 0, 0, 12),
            Padding = new Thickness(0, 0, 0, 10),
            BorderBrush = RuleBrush,
            BorderThickness = new Thickness(0, 0, 0, 1)
        });

        foreach (var entry in diffResult.Entries)
        {
            var p = new Paragraph
            {
                Margin = new Thickness(0, 0, 0, 12),
                LineHeight = 22,
                Padding = new Thickness(10, 0, 0, 0)
            };

            if (entry.Kind != ParagraphDiffKind.Unchanged)
            {
                // Change bar in the margin, colored by kind of change.
                p.BorderThickness = new Thickness(2, 0, 0, 0);
                p.BorderBrush = entry.Kind switch
                {
                    ParagraphDiffKind.Added => InsertionInk,
                    ParagraphDiffKind.Removed => DeletionInk,
                    _ => ModifiedInk
                };
            }
            else
            {
                p.Padding = new Thickness(12, 0, 0, 0);
            }

            p.Inlines.Add(new Run(entry.ParagraphId + "  ")
            {
                Foreground = SecondaryInkBrush,
                FontFamily = MonoFont,
                FontSize = 11
            });

            foreach (var tok in entry.InlineTokens)
            {
                p.Inlines.Add(tok.Kind switch
                {
                    DiffTokenKind.Inserted => InsertionRun(tok.Text),
                    DiffTokenKind.Deleted => DeletionRun(tok.Text),
                    _ => new Run(tok.Text)
                });
            }

            doc.Blocks.Add(p);
        }

        return doc;
    }

    private static readonly Brush CitationInk = Frozen(Color.FromRgb(0x00, 0x5F, 0xB8));
    private static readonly Brush CrossReferenceInk = Frozen(Color.FromRgb(0x0B, 0x6A, 0x6A));
    private static readonly Brush NumberInk = Frozen(Color.FromRgb(0x6B, 0x2F, 0xA0));
    internal static readonly Brush ErrorInk = Frozen(Color.FromRgb(0xC4, 0x2B, 0x1C));

    /// <summary>
    /// Adds editable text: emphasis as formatting, and citations, cross-references, linked numbers and inline
    /// equations kept as typed (so editing them stays simple) but coloured, with what they render as on hover.
    /// </summary>
    public static void AddScholarlyRuns(InlineCollection inlines, string text, ScholarlyContext? scholarly)
    {
        if (scholarly == null)
        {
            AddEmphasizedRuns(inlines, text, t => new Run(t));
            return;
        }
        int last = 0;
        foreach (Match m in EmphasisRegex.Matches(text))
        {
            if (m.Index > last)
            {
                AddTokenRuns(inlines, text[last..m.Index], scholarly, FontWeights.Normal, FontStyles.Normal);
            }
            int level = m.Groups[1].Length;
            AddTokenRuns(inlines, m.Groups[2].Value, scholarly, level >= 2 ? FontWeights.Bold : FontWeights.Normal, level != 2 ? FontStyles.Italic : FontStyles.Normal);
            last = m.Index + m.Length;
        }
        if (last < text.Length)
        {
            AddTokenRuns(inlines, text[last..], scholarly, FontWeights.Normal, FontStyles.Normal);
        }
    }

    private static void AddTokenRuns(InlineCollection inlines, string text, ScholarlyContext scholarly, FontWeight weight, FontStyle style)
    {
        foreach (var piece in scholarly.Tokenize(text))
        {
            var run = new Run(piece.Source);
            if (weight != FontWeights.Normal) run.FontWeight = weight;
            if (style != FontStyles.Normal) run.FontStyle = style;
            if (piece.Kind == InlinePieceKind.Placeholder)
            {
                // Its source, so it can be typed over; double-click (or Ctrl+Alt+P) edits its note.
                run.Background = PlaceholderFill;
                run.Foreground = PlaceholderInk;
                run.Tag = PlaceholderTag;
                run.ToolTip = PlaceholderTip(piece) + "\nDouble-click to edit it, or type over it.";
            }
            else if (piece.Kind != InlinePieceKind.Text)
            {
                run.Foreground = piece.IsError ? ErrorInk : piece.Kind switch
                {
                    InlinePieceKind.Citation => CitationInk,
                    InlinePieceKind.CrossReference => CrossReferenceInk,
                    _ => NumberInk
                };
                run.ToolTip = piece.Detail is { Length: > 0 } detail && detail != piece.Display ? $"{piece.Display}\n{detail}" : piece.Display;
            }
            inlines.Add(run);
        }
    }

    private static readonly Regex ReferencesHeadingRegex = new(
        "^(references|bibliography|literature cited|works cited|reference list)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static bool IsReferencesHeading(DocumentBlock block)
        => block.Role is BlockStyleRole.Heading1 or BlockStyleRole.Heading2
           && ReferencesHeadingRegex.IsMatch(ScholarlyBlocks.StripHeadingLabel(CriticMarkupSerializer.ResolveCriticMarkupText(block.Text, MarkupDisplayMode.NoMarkupFinal)).Trim());

    /// <summary>A paragraph as it will read in the paper: pending changes accepted, everything rendered.</summary>
    private static Paragraph PreviewParagraph(DocumentBlock block, FormattingRuleProfile profile, ScholarlyContext? scholarly)
    {
        var para = CreateStyledParagraph(block.Role, profile);
        string text = CriticMarkupSerializer.ResolveCriticMarkupText(block.Text, MarkupDisplayMode.NoMarkupFinal);
        if (block.Role is BlockStyleRole.Heading1 or BlockStyleRole.Heading2 or BlockStyleRole.Heading3)
        {
            text = block.ComputedSectionNumber + ScholarlyBlocks.StripHeadingLabel(text);
        }
        if (scholarly == null)
        {
            AddFormattedRuns(para.Inlines, text);
            return para;
        }
        // Rendered as published, except that placeholders stay marked (with what they wait for on hover).
        var rendered = new System.Text.StringBuilder();
        foreach (var piece in scholarly.Tokenize(text))
        {
            if (piece.Kind != InlinePieceKind.Placeholder)
            {
                rendered.Append(piece.Display);
                continue;
            }
            AddFormattedRuns(para.Inlines, rendered.ToString());
            rendered.Clear();
            var span = new Span { Background = PlaceholderFill, Foreground = PlaceholderInk, ToolTip = PlaceholderTip(piece) };
            AddFormattedRuns(span.Inlines, piece.Display);
            para.Inlines.Add(span);
        }
        AddFormattedRuns(para.Inlines, rendered.ToString());
        return para;
    }

    /// <summary>Marks the runs of placeholders in the editor, so a double-click on one edits it.</summary>
    public static readonly object PlaceholderTag = new();
    internal static readonly Brush PlaceholderFill = Frozen(Color.FromRgb(0xFF, 0xEC, 0xB3));
    internal static readonly Brush PlaceholderInk = Frozen(Color.FromRgb(0x8A, 0x45, 0x00));

    private static string PlaceholderTip(InlinePiece piece)
        => $"Placeholder: {piece.Display}" + (piece.Detail is { Length: > 0 } note ? $"\n{note}" : string.Empty);

    /// <summary>Runs for text with emphasis, superscript and subscript markers.</summary>
    public static void AddFormattedRuns(InlineCollection inlines, string text, double? fontSize = null)
    {
        foreach (var span in InlineFormatting.Parse(text))
        {
            var run = new Run(span.Text.Replace('\t', ' '));
            if (span.Bold) run.FontWeight = FontWeights.Bold;
            if (span.Italic) run.FontStyle = FontStyles.Italic;
            if (span.Superscript || span.Subscript)
            {
                run.BaselineAlignment = span.Superscript ? BaselineAlignment.Superscript : BaselineAlignment.Subscript;
                run.FontSize = (fontSize ?? 16) * 0.7;
            }
            inlines.Add(run);
        }
    }

    private static void AddPreviewBibliography(FlowDocument flowDoc, FormattingRuleProfile profile, ScholarlyContext scholarly)
    {
        foreach (var reference in scholarly.Citations.References)
        {
            var para = CreateStyledParagraph(BlockStyleRole.ReferenceItem, profile);
            para.TextIndent = -36;
            para.Margin = new Thickness(36, 0, 0, para.Margin.Bottom);
            para.TextAlignment = TextAlignment.Left;
            AddFormattedRuns(para.Inlines, reference.Label.Length > 0 ? $"{reference.Label} {reference.Text}" : reference.Text, para.FontSize);
            flowDoc.Blocks.Add(para);
        }
    }

    internal static Paragraph StyledParagraph(BlockStyleRole role, FormattingRuleProfile profile) => CreateStyledParagraph(role, profile);

    private const string TitlePageHint = "From the title page form: edit it in the Paper panel.";
    private const string TitleHint = "The title is edited in the Paper panel's title page form, with the authors. Click to go there.";

    /// <summary>Clicking front matter (title, authors, draft notes, keywords) opens the form it is edited in.</summary>
    private static void ActivatesForm(FrameworkElement element, Action? onActivated)
    {
        if (onActivated != null)
        {
            element.Cursor = Cursors.Hand;
            element.MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;
                onActivated();
            };
        }
    }

    /// <summary>The title, styled as on the page but read-only, carrying its block so it is read back unchanged.</summary>
    private static BlockUIContainer ReadOnlyTitle(DocumentBlock original, string rendered, FormattingRuleProfile profile, ScholarlyContext? scholarly, Action? onActivated)
    {
        var style = CreateStyledParagraph(BlockStyleRole.Title, profile);
        var text = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            FontSize = style.FontSize,
            FontFamily = style.FontFamily,
            FontWeight = style.FontWeight,
            TextAlignment = style.TextAlignment,
            LineHeight = style.LineHeight,
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
            Foreground = InkBrush,
            Cursor = Cursors.Arrow,
            ToolTip = TitleHint
        };
        string final = CriticMarkupSerializer.ResolveCriticMarkupText(rendered, MarkupDisplayMode.NoMarkupFinal);
        AddFormattedRuns(text.Inlines, scholarly?.Render(final) ?? final, style.FontSize);
        ActivatesForm(text, onActivated);
        return new BlockUIContainer(text)
        {
            Margin = style.Margin,
            Tag = new EditorBlockTag { ParagraphId = original.ParagraphId, Role = BlockStyleRole.Title, LockedBlock = original.Clone() }
        };
    }

    private static readonly Brush DraftNoteFill = Frozen(Color.FromRgb(0xFF, 0xF8, 0xDC));
    private static readonly Brush DraftNoteFrame = Frozen(Color.FromRgb(0xC9, 0xA2, 0x27));

    /// <summary>The draft notes in a shaded box: for the authors, left out of copies for the journal.</summary>
    private static void AddDraftNotes(FlowDocument flowDoc, FrontMatter front, FormattingRuleProfile profile, Action? onActivated)
    {
        if (front.DraftNoteLines.Count == 0)
        {
            return;
        }
        double size = profile.BodyFontSizePt * PointsToPixels * 0.85;
        var text = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = size, Foreground = InkBrush, FontFamily = UiFont };
        text.Inlines.Add(new Run(Core.Scholarly.TitlePageText.DraftNotesHeading) { FontWeight = FontWeights.SemiBold });
        foreach (string line in front.DraftNoteLines)
        {
            text.Inlines.Add(new LineBreak());
            AddFormattedRuns(text.Inlines, line, size);
        }
        var box = new Border
        {
            Background = DraftNoteFill,
            BorderBrush = DraftNoteFrame,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(10, 6, 10, 6),
            Child = text,
            ToolTip = "Draft notes, from the Paper panel's title page form. Draft exports include them; copies for the journal leave them out."
        };
        ActivatesForm(box, onActivated);
        flowDoc.Blocks.Add(new BlockUIContainer(box) { Margin = new Thickness(0, 0, 0, 18) });
    }

    /// <summary>
    /// Authors with affiliation marks, the affiliations and the corresponding author, as the exports lay them
    /// out. Shown read-only (it is not part of the manuscript text) and ignored when the page is read back.
    /// </summary>
    private static void AddTitlePage(FlowDocument flowDoc, FrontMatter front, FormattingRuleProfile profile, Action? onActivated)
    {
        if (front.Authors.Count == 0 && front.Affiliations.Count == 0)
        {
            return;
        }
        double size = profile.BodyFontSizePt * PointsToPixels;
        var panel = new StackPanel { ToolTip = TitlePageHint, Cursor = Cursors.Arrow };

        var authors = new TextBlock { TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, FontSize = size * 1.1, Foreground = InkBrush };
        for (int i = 0; i < front.Authors.Count; i++)
        {
            var author = front.Authors[i];
            if (i > 0)
            {
                authors.Inlines.Add(new Run(", "));
            }
            authors.Inlines.Add(new Run(author.Name));
            var marks = front.PrintedAffiliationNumbers(author).Select(n => n.ToString()).ToList();
            if (author.Corresponding) marks.Add("*");
            if (author.EqualContribution) marks.Add("†");
            if (marks.Count > 0)
            {
                authors.Inlines.Add(new Run(string.Join(",", marks)) { BaselineAlignment = BaselineAlignment.Superscript, FontSize = size * 0.75 });
            }
        }
        panel.Children.Add(authors);

        var details = new TextBlock { TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, FontSize = size * 0.85, Foreground = SecondaryInkBrush, Margin = new Thickness(0, 6, 0, 0) };
        void Line(string mark, string text)
        {
            if (details.Inlines.Count > 0)
            {
                details.Inlines.Add(new LineBreak());
            }
            details.Inlines.Add(new Run(mark) { BaselineAlignment = BaselineAlignment.Superscript, FontSize = size * 0.65 });
            details.Inlines.Add(new Run(" " + text));
        }
        foreach (var (number, affiliation) in front.AffiliationsInOrder())
        {
            Line(number.ToString(), affiliation.Text);
        }
        foreach (var author in front.CorrespondingAuthors)
        {
            Line("*", $"Corresponding author: {author.Name}{(author.Email.Length > 0 ? ", " + author.Email : string.Empty)}");
        }
        if (front.Authors.Any(a => a.EqualContribution))
        {
            Line("†", "These authors contributed equally.");
        }
        if (details.Inlines.Count > 0)
        {
            panel.Children.Add(details);
        }
        ActivatesForm(panel, onActivated);
        flowDoc.Blocks.Add(new BlockUIContainer(panel) { Margin = new Thickness(0, 0, 0, 18) });
    }

    private static void AddKeywords(FlowDocument flowDoc, ScholarlyContext scholarly, FormattingRuleProfile profile, Action? onActivated)
    {
        var text = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            FontSize = profile.BodyFontSizePt * PointsToPixels,
            Foreground = InkBrush,
            Margin = new Thickness(36, 0, 36, 0),
            ToolTip = TitlePageHint
        };
        text.Inlines.Add(new Run(scholarly.Project.Journal.KeywordsHeading + ": ") { FontWeight = FontWeights.Bold });
        text.Inlines.Add(new Run(string.Join("; ", scholarly.Project.FrontMatter.Keywords)));
        ActivatesForm(text, onActivated);
        flowDoc.Blocks.Add(new BlockUIContainer(text) { Margin = new Thickness(0, 0, 0, 14) });
    }

    private static FontFamily GetFont(string familyName)
    {
        if (!FontCache.TryGetValue(familyName, out var font))
        {
            font = new FontFamily(familyName);
            FontCache[familyName] = font;
        }
        return font;
    }

    private static SolidColorBrush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
