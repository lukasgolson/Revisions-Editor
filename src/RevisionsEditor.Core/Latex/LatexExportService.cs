using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using RevisionsEditor.Core.Checks;
using RevisionsEditor.Core.Docx;
using RevisionsEditor.Core.Formatting;
using RevisionsEditor.Core.Models;
using RevisionsEditor.Core.Scholarly;
using RevisionsEditor.Core.Serialization;

namespace RevisionsEditor.Core.Latex;

/// <summary>The look of the typeset document.</summary>
public enum LatexTemplate
{
    /// <summary>A two-column journal article with a full-width title block (article class).</summary>
    JournalTwoColumn,
    /// <summary>Elsevier's own class (elsarticle, 5p two-column), as RSE, ISPRS, JAG and FEM typeset.</summary>
    Elsevier,
    /// <summary>IEEE Transactions (IEEEtran, journal mode).</summary>
    Ieee,
    /// <summary>One column, double spaced, with line numbers and a title page: the copy reviewers read.</summary>
    ReviewManuscript
}

public sealed class LatexExportOptions
{
    public LatexTemplate Template { get; set; } = LatexTemplate.JournalTwoColumn;
    public bool LineNumbers { get; set; }
    public string? JournalName { get; set; }
    /// <summary>Where figure images and table files are (the workspace).</summary>
    public string WorkspacePath { get; set; } = string.Empty;
    /// <summary>
    /// Converts an image the engine cannot read (TIFF, BMP, GIF) to PNG: (source, destination folder) → file name
    /// written, or null. The app supplies it; without it such figures are left out with a note.
    /// </summary>
    public Func<string, string, string?>? ConvertImage { get; set; }
    /// <summary>Prints the title page form's draft notes in a box after the title (for drafts, not for the journal).</summary>
    public bool IncludeDraftNotes { get; set; }
}

/// <summary>The LaTeX source of a manuscript and the files it needs.</summary>
public sealed record LatexDocument(string Source, IReadOnlyList<(string SourcePath, string RelativePath)> Files, IReadOnlyList<string> Warnings);

/// <summary>
/// Writes a manuscript as LaTeX for typesetting: the title page from the title-page form, the abstract,
/// sections, figures, tables (from their CSV files), equations as written, citations and cross-references as
/// rendered in the journal's style (linked to their targets), and the reference list. Pending changes are
/// accepted. Fonts are TeX Gyre Termes (a Times design) with microtype.
/// </summary>
public static class LatexExportService
{
    private static readonly Regex InlineMathRegex = new(@"(?<![\\$])\$(?=[^\s$])(?<tex>[^$\n]*?[^\s\\$])\$(?![\d$])", RegexOptions.Compiled);
    private static readonly HashSet<string> EngineImageTypes = new(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg", ".pdf" };

    public static LatexDocument Build(ManuscriptDocument document, ScholarlyContext context, FormattingRuleProfile profile, LatexExportOptions options)
    {
        var doc = CriticMarkupSerializer.BakeResolvedHunks(document, acceptRemainingPending: true);
        FormattingRuleEngine.ApplyFormattingRules(doc, profile);
        var writer = new Writer(doc, context, profile, options);
        return writer.Build();
    }

    /// <summary>Writes the .tex file and copies figures next to it. Returns the .tex path.</summary>
    public static string WriteTo(LatexDocument latex, string folder, string fileName = "manuscript.tex")
    {
        Directory.CreateDirectory(folder);
        foreach (var (source, relative) in latex.Files)
        {
            string target = Path.Combine(folder, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            if (!string.Equals(Path.GetFullPath(source), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase))
            {
                File.Copy(source, target, overwrite: true);
            }
        }
        string path = Path.Combine(folder, fileName);
        File.WriteAllText(path, latex.Source, new UTF8Encoding(false));
        return path;
    }

    // ---- Escaping ---------------------------------------------------------------------------------------

    private const string Minus = "−";
    /// <summary>U+202F, between a number and its unit: written as a thin space that does not break.</summary>
    private const char ThinNoBreakSpace = (char)0x202F;

    private static readonly Regex UrlInProseRegex = new(@"(?:https?://|www\.|doi:)\S+", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex NumberRangeRegex = new(@"(?<![\p{L}\p{N}.,/\-–])(\d+(?:\.\d+)?)-(\d+(?:\.\d+)?)(?![\p{L}\p{N}\-–]|[.,]\d)", RegexOptions.Compiled);
    private static readonly Regex UnitRegex = new(
        @"(?<=\d) (?=(?:mm|cm|km|nm|µm|μm|m|ha|kg|mg|g|t|ms|s|min|h|yr|Hz|kHz|MHz|GHz|K|°C|W|kW|MW|J|kJ|MJ|Pa|kPa|MPa|ppm|ppb|dB|px|MB|GB|TB|L|mL)(?![\p{L}\p{N}]))",
        RegexOptions.Compiled);
    /// <summary>The space before a unit at the start of text that follows a number rendered on its own (a linked number).</summary>
    private static readonly Regex LeadingUnitRegex = new(
        @"^ (?=(?:mm|cm|km|nm|µm|μm|m|ha|kg|mg|g|t|ms|s|min|h|yr|Hz|kHz|MHz|GHz|K|°C|W|kW|MW|J|kJ|MJ|Pa|kPa|MPa|ppm|ppb|dB|px|MB|GB|TB|L|mL)(?![\p{L}\p{N}]))",
        RegexOptions.Compiled);
    private static readonly Regex OpeningDoubleQuoteRegex = new(@"(^|[\s(\[{*/—–])""", RegexOptions.Compiled);
    private static readonly Regex OpeningSingleQuoteRegex = new(@"(^|[\s(\[{*/—–])'(?=\S)", RegexOptions.Compiled);

    /// <summary>
    /// Prose as a typesetter sets it: curly quotes and apostrophes, an en dash in number ranges (10–20 m) and for a
    /// spaced hyphen, an ellipsis, and a thin space that does not break between a number and its unit (12 m).
    /// Web addresses are left as typed.
    /// </summary>
    public static string Typeset(string text)
    {
        var sb = new StringBuilder(text.Length);
        int last = 0;
        foreach (Match url in UrlInProseRegex.Matches(text))
        {
            sb.Append(TypesetProse(text[last..url.Index])).Append(url.Value);
            last = url.Index + url.Length;
        }
        return sb.Append(TypesetProse(text[last..])).ToString();
    }

    private static string TypesetProse(string text)
    {
        if (text.Length == 0)
        {
            return text;
        }
        text = text.Replace("...", "…");
        text = text.Replace(" - ", " – ");
        text = NumberRangeRegex.Replace(text, "$1–$2");
        text = UnitRegex.Replace(text, ThinNoBreakSpace.ToString());
        text = OpeningDoubleQuoteRegex.Replace(text, "$1“").Replace('"', '”');
        text = OpeningSingleQuoteRegex.Replace(text, "$1‘").Replace('\'', '’');
        return text;
    }

    private const string UnicodeSuperscripts = "⁰¹²³⁴⁵⁶⁷⁸⁹⁺⁻⁼⁽⁾ⁿⁱ";
    private const string UnicodeSubscripts = "₀₁₂₃₄₅₆₇₈₉₊₋₌₍₎";
    private const string ScriptEquivalents = "0123456789+−=()ni";

    /// <summary>
    /// Escapes LaTeX's special characters in plain text. Unicode superscript and subscript characters (m⁻², CO₂)
    /// become \textsuperscript and \textsubscript, since text fonts such as TeX Gyre Termes lack most of them.
    /// </summary>
    public static string Escape(string text)
    {
        var sb = new StringBuilder(text.Length + 16);
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (AppendScriptRun(text, ref i, UnicodeSuperscripts, "textsuperscript", sb)
                || AppendScriptRun(text, ref i, UnicodeSubscripts, "textsubscript", sb)
                || c is >= '' and <= '') // private-use characters (pasted from Word equations): no font has them
            {
                continue;
            }
            sb.Append(c switch
            {
                '\\' => @"\textbackslash{}",
                '&' => @"\&",
                '%' => @"\%",
                '$' => @"\$",
                '#' => @"\#",
                '_' => @"\_",
                '{' => @"\{",
                '}' => @"\}",
                '~' => @"\textasciitilde{}",
                '^' => @"\textasciicircum{}",
                ThinNoBreakSpace => @"\,",
                '\u00A0' => "~",
                '\t' => " ",
                _ => c.ToString()
            });
        }
        return sb.ToString();
    }

    /// <summary>Writes the run of script characters starting at <paramref name="i"/>, leaving i on its last one.</summary>
    private static bool AppendScriptRun(string text, ref int i, string scripts, string command, StringBuilder sb)
    {
        if (scripts.IndexOf(text[i]) < 0)
        {
            return false;
        }
        sb.Append('\\').Append(command).Append('{');
        for (; i < text.Length && scripts.IndexOf(text[i]) is int k and >= 0; i++)
        {
            sb.Append(ScriptEquivalents[k]);
        }
        sb.Append('}');
        i--;
        return true;
    }

    /// <summary>Text with emphasis, superscript and subscript markers as LaTeX.</summary>
    public static string Formatted(string text)
    {
        var sb = new StringBuilder();
        foreach (var span in InlineFormatting.Parse(text))
        {
            string t = Escape(span.Text);
            if (span.Superscript) t = $@"\textsuperscript{{{t}}}";
            if (span.Subscript) t = $@"\textsubscript{{{t}}}";
            if (span.Italic) t = $@"\emph{{{t}}}";
            if (span.Bold) t = $@"\textbf{{{t}}}";
            sb.Append(t);
        }
        return sb.ToString();
    }

    private sealed class Writer
    {
        private readonly ManuscriptDocument _doc;
        private readonly ScholarlyContext _context;
        private readonly FormattingRuleProfile _profile;
        private readonly LatexExportOptions _options;
        private readonly FrontMatter _front;
        private readonly List<(string SourcePath, string RelativePath)> _files = [];
        private readonly List<string> _warnings = [];
        private readonly HashSet<string> _skip = new(StringComparer.Ordinal);
        private readonly StringBuilder _body = new();
        private readonly bool _twoColumn;
        private bool _referencesWritten;
        private bool _usesTikz;

        public Writer(ManuscriptDocument doc, ScholarlyContext context, FormattingRuleProfile profile, LatexExportOptions options)
        {
            _doc = doc;
            _context = context;
            _profile = profile;
            _options = options;
            _front = context.Project.FrontMatter;
            _twoColumn = options.Template != LatexTemplate.ReviewManuscript;
        }

        private LatexTemplate Template => _options.Template;

        public LatexDocument Build()
        {
            var structure = new ManuscriptStructure(_doc.Blocks);
            string title = Inline(ManuscriptStructure.PlainText(_doc.Blocks.FirstOrDefault(b => b.Role == BlockStyleRole.Title)?.Text ?? _doc.Title));
            var abstractBlocks = structure.AbstractBlocks();
            string abstractText = string.Join("\n\n", abstractBlocks.Select(b => Inline(Final(b))));

            // Parts of the manuscript that the title page lays out itself.
            foreach (var b in _doc.Blocks.Where(b => b.Role == BlockStyleRole.Title))
            {
                _skip.Add(b.ParagraphId);
            }
            foreach (var b in abstractBlocks)
            {
                _skip.Add(b.ParagraphId);
            }
            if (structure.FindSection("Abstract") is { Heading: { } abstractHeading })
            {
                _skip.Add(abstractHeading.ParagraphId);
            }
            var (keywordBlock, manuscriptKeywords) = structure.Keywords();
            if (keywordBlock != null)
            {
                _skip.Add(keywordBlock.ParagraphId);
                if (structure.FindSection("Keywords", ["key words", "index terms"]) is { Heading: { } kh })
                {
                    _skip.Add(kh.ParagraphId);
                }
            }
            var keywords = _front.Keywords.Count > 0 ? _front.Keywords : manuscriptKeywords;
            var highlights = structure.Highlights(_context.Render);
            if (structure.FindSection("Highlights", ["research highlights"]) is { } highlightSection)
            {
                _skip.Add(highlightSection.Heading!.ParagraphId);
                foreach (var b in highlightSection.Content.TakeWhile(b => b.Role == BlockStyleRole.BodyParagraph))
                {
                    _skip.Add(b.ParagraphId);
                }
            }

            WriteBody();
            if (_context.HasCitations && !_referencesWritten)
            {
                WriteReferences("References");
            }

            var tex = new StringBuilder();
            WritePreamble(tex, title);
            tex.AppendLine(@"\begin{document}");
            WriteTitlePage(tex, title, abstractText, keywords, highlights?.Select(h => h.Text).ToList() ?? []);
            tex.Append(_body);
            tex.AppendLine(@"\end{document}");
            return new LatexDocument(tex.ToString(), _files, _warnings);
        }

        private static string Final(DocumentBlock block) => CriticMarkupSerializer.ResolveCriticMarkupText(block.Text, MarkupDisplayMode.NoMarkupFinal);

        // ---- Preamble ---------------------------------------------------------------------------------

        private void WritePreamble(StringBuilder tex, string title)
        {
            var names = _context.CrossReferences.Names;
            string runningTitle = _front.ShortTitle.Trim().Length > 0 ? Escape(_front.ShortTitle.Trim()) : title;
            switch (Template)
            {
                case LatexTemplate.Elsevier:
                    tex.AppendLine(@"\documentclass[preprint,5p,twocolumn,10pt]{elsarticle}");
                    break;
                case LatexTemplate.Ieee:
                    tex.AppendLine(@"\documentclass[journal]{IEEEtran}");
                    break;
                case LatexTemplate.ReviewManuscript:
                    tex.AppendLine(@"\documentclass[12pt]{article}");
                    break;
                default:
                    tex.AppendLine(@"\documentclass[10pt,twocolumn]{article}");
                    break;
            }

            // Hyphenation patterns and language-dependent typography (English).
            tex.AppendLine(@"\usepackage[english]{babel}");
            tex.AppendLine(@"\usepackage{amsmath}");
            tex.AppendLine(@"\usepackage{iftex}");
            tex.AppendLine(@"\usepackage{fontspec}");
            tex.AppendLine(@"\usepackage{unicode-math}");
            // By file name: Tectonic (and TeX Live) provide these files, but not as installed system fonts. The text
            // fonts with OpenType kerning and ligatures (fi, fl, ff, ffi; TeX's -- and --- dashes).
            const string TextFeatures = "Ligatures={TeX,Common},Kerning=On";
            tex.AppendLine(@"\setmainfont{texgyretermes}[Extension=.otf,UprightFont=*-regular,BoldFont=*-bold,ItalicFont=*-italic,BoldItalicFont=*-bolditalic,Numbers=Lining," + TextFeatures + "]");
            tex.AppendLine(@"\setsansfont{texgyreheros}[Extension=.otf,UprightFont=*-regular,BoldFont=*-bold,ItalicFont=*-italic,BoldItalicFont=*-bolditalic,Scale=MatchLowercase," + TextFeatures + "]");
            tex.AppendLine(@"\setmonofont{texgyrecursor}[Extension=.otf,UprightFont=*-regular,BoldFont=*-bold,ItalicFont=*-italic,BoldItalicFont=*-bolditalic,Scale=MatchLowercase]");
            tex.AppendLine(@"\setmathfont{texgyretermes-math.otf}");
            // Superscripts and subscripts from the font's own superior and inferior figures.
            tex.AppendLine(@"\usepackage{realscripts}");
            // Character protrusion (optical margins) everywhere; LuaTeX adds font expansion and tracking. XeTeX, the
            // engine inside Tectonic, supports protrusion only.
            tex.AppendLine(@"\ifLuaTeX\usepackage[protrusion=true,expansion=true,tracking=true,final]{microtype}\else\usepackage[protrusion=true,final]{microtype}\fi");
            tex.AppendLine(@"\usepackage{graphicx}");
            if (_usesTikz)
            {
                tex.AppendLine(@"\usepackage{tikz}"); // diagrams drawn from .diagram files
            }
            tex.AppendLine(@"\usepackage{booktabs}");
            tex.AppendLine(@"\usepackage{tabularx}"); // tables whose text columns wrap to fit
            tex.AppendLine(@"\usepackage{adjustbox}");
            // Numbers in table columns aligned on the decimal point.
            tex.AppendLine(@"\usepackage{siunitx}");
            tex.AppendLine(@"\sisetup{group-digits=false,round-mode=none,table-number-alignment=center,detect-all}");
            if (Template != LatexTemplate.Ieee)
            {
                tex.AppendLine(@"\usepackage[font=small,labelfont=bf,labelsep=period,justification=justified,singlelinecheck=false]{caption}");
                // Table captions above the table (spacing for a caption on top), figure captions below.
                tex.AppendLine(@"\captionsetup{skip=6pt,tableposition=top,figureposition=bottom}");
            }
            if (Template == LatexTemplate.ReviewManuscript)
            {
                // Figures stay in their section.
                tex.AppendLine(@"\usepackage[section]{placeins}");
            }
            if (Template is LatexTemplate.JournalTwoColumn or LatexTemplate.ReviewManuscript)
            {
                tex.AppendLine(Template == LatexTemplate.ReviewManuscript
                    ? @"\usepackage[a4paper,margin=25mm]{geometry}"
                    : @"\usepackage[a4paper,top=22mm,bottom=22mm,left=17mm,right=17mm,columnsep=6mm,headsep=6mm]{geometry}");
                tex.AppendLine(@"\usepackage{titlesec}");
                tex.AppendLine(@"\titleformat{\section}{\normalfont\bfseries\normalsize}{\thesection.}{0.5em}{}");
                tex.AppendLine(@"\titleformat{\subsection}{\normalfont\itshape\normalsize}{\thesubsection.}{0.5em}{}");
                tex.AppendLine(@"\titleformat{\subsubsection}{\normalfont\itshape\normalsize}{\thesubsubsection.}{0.5em}{}");
                tex.AppendLine(@"\titlespacing*{\section}{0pt}{1.2\baselineskip plus 0.3\baselineskip minus 0.1\baselineskip}{0.5\baselineskip}");
                tex.AppendLine(@"\titlespacing*{\subsection}{0pt}{0.9\baselineskip plus 0.2\baselineskip minus 0.1\baselineskip}{0.35\baselineskip}");
                tex.AppendLine(@"\titlespacing*{\subsubsection}{0pt}{0.7\baselineskip plus 0.2\baselineskip}{0.25\baselineskip}");
                if (Template == LatexTemplate.JournalTwoColumn)
                {
                    tex.AppendLine(@"\setlength{\parindent}{1em}");
                }
                tex.AppendLine(@"\usepackage{fancyhdr}");
                tex.AppendLine(@"\pagestyle{fancy}");
                tex.AppendLine(@"\fancyhf{}");
                tex.AppendLine($@"\fancyhead[L]{{\footnotesize\itshape {runningTitle}}}");
                tex.AppendLine(@"\fancyhead[R]{\footnotesize\thepage}");
                tex.AppendLine(@"\renewcommand{\headrulewidth}{0.4pt}");
                tex.AppendLine(@"\fancypagestyle{plain}{\fancyhf{}\fancyfoot[C]{\footnotesize\thepage}\renewcommand{\headrulewidth}{0pt}}");
            }
            if (Template == LatexTemplate.ReviewManuscript)
            {
                tex.AppendLine(@"\usepackage{setspace}");
                tex.AppendLine(@"\doublespacing");
            }
            if (_options.LineNumbers || Template == LatexTemplate.ReviewManuscript)
            {
                tex.AppendLine(@"\usepackage[switch]{lineno}");
                tex.AppendLine(@"\renewcommand\linenumberfont{\normalfont\tiny\sffamily\color{gray}}");
            }
            tex.AppendLine(@"\usepackage{xcolor}");
            tex.AppendLine(@"\definecolor{draftframe}{HTML}{C9A227}\definecolor{draftfill}{HTML}{FFF8DC}\definecolor{placeholderink}{HTML}{B45309}");
            // Draft placeholders: the stand-in text underlined in amber, what it waits for in a footnote.
            tex.AppendLine(@"\usepackage[normalem]{ulem}");
            tex.AppendLine(@"\newcommand{\placeholder}[2]{{\color{placeholderink}\uwave{#1}}\if\relax\detokenize{#2}\relax\else\footnote{\textcolor{placeholderink}{\textbf{Placeholder:}} #2}\fi}");
            // A figure that is a placeholder, stamped across.
            tex.AppendLine(@"\newcommand{\placeholderstamp}[1]{\begingroup\sbox0{#1}\usebox0\llap{\makebox[\wd0]{\raisebox{0.4\ht0}{\rotatebox{18}{\textcolor{red!70}{\fontsize{28}{30}\selectfont\bfseries PLACEHOLDER}}}}}\endgroup}");
            tex.AppendLine(@"\usepackage[hidelinks,unicode,pdfdisplaydoctitle]{hyperref}");
            tex.AppendLine(@"\usepackage{xurl}"); // long DOIs and URLs break anywhere in a narrow column
            tex.AppendLine(@"\usepackage{bookmark}");
            tex.AppendLine(@"\urlstyle{same}"); // DOIs in the text font, as journals print them
            tex.AppendLine($@"\hypersetup{{pdftitle={{{PlainForPdf(title)}}},pdfauthor={{{PlainForPdf(string.Join(", ", _front.Authors.Select(a => a.Name)))}}}}}");

            // Justification: one space after a full stop, as journals set it; a little extra stretch rather than a
            // line sticking into the margin; no widows, orphans or hyphenated page ends.
            tex.AppendLine(@"\frenchspacing");
            tex.AppendLine(@"\pretolerance=150 \tolerance=1000 \hbadness=1500");
            tex.AppendLine(_twoColumn ? @"\setlength{\emergencystretch}{2em}" : @"\setlength{\emergencystretch}{1.5em}");
            tex.AppendLine(@"\clubpenalty=10000 \widowpenalty=10000 \displaywidowpenalty=10000 \brokenpenalty=10000");
            if (Template == LatexTemplate.ReviewManuscript)
            {
                tex.AppendLine(@"\raggedbottom");
            }

            // Floats: let figures fill more of a page, and keep the space around them even.
            tex.AppendLine(@"\renewcommand{\topfraction}{0.85}\renewcommand{\bottomfraction}{0.6}\renewcommand{\textfraction}{0.12}\renewcommand{\floatpagefraction}{0.75}");
            tex.AppendLine(@"\renewcommand{\dbltopfraction}{0.85}\renewcommand{\dblfloatpagefraction}{0.75}");
            tex.AppendLine(@"\setcounter{topnumber}{3}\setcounter{bottomnumber}{2}\setcounter{totalnumber}{5}\setcounter{dbltopnumber}{2}");
            tex.AppendLine(@"\setlength{\textfloatsep}{12pt plus 3pt minus 3pt}\setlength{\floatsep}{10pt plus 2pt minus 2pt}\setlength{\intextsep}{10pt plus 2pt minus 2pt}");
            tex.AppendLine(@"\setlength{\dbltextfloatsep}{\textfloatsep}\setlength{\dblfloatsep}{\floatsep}");
            // A page of figures starts at the top, not centred.
            tex.AppendLine(@"\makeatletter\setlength{\@fptop}{0pt}\setlength{\@fpbot}{0pt plus 1fil}\setlength{\@dblfptop}{0pt}\setlength{\@dblfpbot}{0pt plus 1fil}\makeatother");

            // Captions and numbering as the journal writes them.
            tex.AppendLine($@"\renewcommand{{\figurename}}{{{Escape(names.FigureCaptionLabel)}}}");
            tex.AppendLine($@"\renewcommand{{\tablename}}{{{Escape(names.UppercaseTableCaptionLabel ? names.TableCaptionLabel.ToUpperInvariant() : names.TableCaptionLabel)}}}");
            if (names.RomanTableNumbers && Template != LatexTemplate.Ieee)
            {
                tex.AppendLine(@"\renewcommand{\thetable}{\Roman{table}}");
            }
            if (!_profile.AutoNumberHeadings)
            {
                tex.AppendLine(@"\setcounter{secnumdepth}{0}");
            }

            // The reference list, laid out by the journal's style (formatted here, not by BibTeX).
            bool numeric = _context.Citations.References.Any(r => r.Label.Length > 0);
            tex.AppendLine(@"\newenvironment{reflist}[1]{\section*{#1}\footnotesize\begin{list}{}{" + (numeric
                ? @"\setlength{\leftmargin}{2.2em}\setlength{\labelwidth}{2em}\setlength{\labelsep}{0.4em}"
                : @"\setlength{\leftmargin}{1em}\setlength{\itemindent}{-1em}\setlength{\labelwidth}{0pt}\setlength{\labelsep}{0pt}")
                + @"\setlength{\itemsep}{0.15\baselineskip}\setlength{\parsep}{0pt}}\raggedright}{\end{list}}");
        }

        private static string PlainForPdf(string latex) => Regex.Replace(latex, @"\\[a-zA-Z]+\{|[{}]", string.Empty);

        // ---- Title page -------------------------------------------------------------------------------

        private string AuthorMarks(FrontMatterAuthor author, bool withSymbols = true)
        {
            var marks = _front.PrintedAffiliationNumbers(author).Select(n => n.ToString(CultureInfo.InvariantCulture)).ToList();
            if (withSymbols && author.Corresponding)
            {
                marks.Add("*");
            }
            if (withSymbols && author.EqualContribution)
            {
                marks.Add(@"\dag");
            }
            return marks.Count == 0 ? string.Empty : $@"\textsuperscript{{{string.Join(",", marks)}}}";
        }

        private string Orcid(FrontMatterAuthor author)
            => author.Orcid.Trim().Length == 0 ? string.Empty
                : $@"\,\href{{https://orcid.org/{Escape(author.Orcid.Trim())}}}{{\textsuperscript{{\scriptsize iD}}}}";

        /// <summary>The draft notes in a shaded, framed box as wide as the line, or nothing.</summary>
        private string DraftNotesBox()
        {
            if (!_options.IncludeDraftNotes || _front.DraftNoteLines.Count == 0)
            {
                return string.Empty;
            }
            string lines = string.Join(@"\\" + "\n", _front.DraftNoteLines.Select(l => Formatted(l)));
            return @"\par\vspace{0.6em}\noindent\fcolorbox{draftframe}{draftfill}{\parbox{\dimexpr\linewidth-2\fboxsep-2\fboxrule\relax}{\footnotesize\raggedright"
                + $@"\textbf{{{TitlePageText.DraftNotesHeading}}}\\{lines}}}}}\par\vspace{{0.6em}}" + "\n";
        }

        private void WriteTitlePage(StringBuilder tex, string title, string abstractText, IReadOnlyList<string> keywords, IReadOnlyList<string> highlights)
        {
            var affiliations = _front.AffiliationsInOrder();
            var corresponding = _front.CorrespondingAuthors.ToList();
            string keywordText = string.Join("; ", keywords.Select(k => Inline(k)));

            switch (Template)
            {
                case LatexTemplate.Elsevier:
                    // The highlights page is set ragged at the foot, not spread down the page.
                    tex.AppendLine(@"\raggedbottom");
                    tex.AppendLine(@"\begin{frontmatter}");
                    tex.AppendLine($@"\title{{{title}}}");
                    foreach (var author in _front.Authors)
                    {
                        var numbers = _front.PrintedAffiliationNumbers(author);
                        string refs = numbers.Count > 0 ? "[" + string.Join(",", numbers) + "]" : string.Empty;
                        // elsarticle repeats the author's name in the e-mail footnote, so the ORCID link stays out of it.
                        tex.AppendLine($@"\author{refs}{{{Escape(author.Name)}{(author.Corresponding ? @"\corref{cor1}" : string.Empty)}}}");
                        if (author.Email.Trim().Length > 0)
                        {
                            tex.AppendLine($@"\ead{{{Escape(author.Email.Trim())}}}");
                        }
                    }
                    if (corresponding.Count > 0)
                    {
                        tex.AppendLine(@"\cortext[cor1]{Corresponding author.}");
                    }
                    foreach (var (number, affiliation) in affiliations)
                    {
                        tex.AppendLine($@"\affiliation[{number}]{{organization={{{Escape(affiliation.Text)}}}}}");
                    }
                    if (highlights.Count > 0)
                    {
                        tex.AppendLine(@"\begin{highlights}");
                        foreach (string h in highlights)
                        {
                            tex.AppendLine($@"\item {Inline(h)}");
                        }
                        tex.AppendLine(@"\end{highlights}");
                    }
                    if (abstractText.Length > 0)
                    {
                        tex.AppendLine(@"\begin{abstract}").AppendLine(abstractText).AppendLine(@"\end{abstract}");
                    }
                    if (keywords.Count > 0)
                    {
                        tex.AppendLine(@"\begin{keyword}").AppendLine(string.Join(@" \sep ", keywords.Select(k => Inline(k)))).AppendLine(@"\end{keyword}");
                    }
                    tex.AppendLine(@"\end{frontmatter}");
                    tex.AppendLine(@"\flushbottom");
                    tex.Append(DraftNotesBox());
                    if (_options.LineNumbers)
                    {
                        tex.AppendLine(@"\linenumbers");
                    }
                    if (_options.JournalName is { Length: > 0 } journal)
                    {
                        tex.Insert(tex.ToString().IndexOf(@"\begin{document}", StringComparison.Ordinal), $@"\journal{{{Escape(journal)}}}" + "\n");
                    }
                    return;

                case LatexTemplate.Ieee:
                    tex.AppendLine($@"\title{{{title}}}");
                    var ieeeAuthors = string.Join(", ", _front.Authors.Select(a => Escape(a.Name) + Orcid(a)));
                    var thanks = new List<string>();
                    foreach (var (number, affiliation) in affiliations)
                    {
                        var who = _front.Authors.Where(a => _front.PrintedAffiliationNumbers(a).Contains(number)).Select(a => Escape(a.Name)).ToList();
                        thanks.Add($"{string.Join(" and ", who)} {(who.Count == 1 ? "is" : "are")} with {Escape(affiliation.Text)}");
                    }
                    if (corresponding.Count > 0)
                    {
                        thanks.Add("Corresponding author: " + string.Join(", ", corresponding.Select(a => $"{Escape(a.Name)} (e-mail: {Escape(a.Email)})")));
                    }
                    tex.AppendLine($@"\author{{{ieeeAuthors}{(thanks.Count > 0 ? $@"\thanks{{{string.Join(". ", thanks)}.}}" : string.Empty)}}}");
                    tex.AppendLine($@"\markboth{{{Escape(_options.JournalName ?? string.Empty)}}}{{{(_front.ShortTitle.Length > 0 ? Escape(_front.ShortTitle) : title)}}}");
                    tex.AppendLine(@"\maketitle");
                    if (abstractText.Length > 0)
                    {
                        tex.AppendLine(@"\begin{abstract}").AppendLine(abstractText).AppendLine(@"\end{abstract}");
                    }
                    if (keywords.Count > 0)
                    {
                        tex.AppendLine(@"\begin{IEEEkeywords}").AppendLine(string.Join(", ", keywords.Select(k => Inline(k)))).AppendLine(@"\end{IEEEkeywords}");
                    }
                    tex.Append(DraftNotesBox());
                    if (_options.LineNumbers)
                    {
                        tex.AppendLine(@"\linenumbers");
                    }
                    return;
            }

            // Article class: a title block across both columns (or a title page for the review copy).
            var block = new StringBuilder();
            if (_front.ArticleType.Trim().Length > 0)
            {
                block.AppendLine($@"{{\footnotesize\scshape {Escape(_front.ArticleType.Trim())}}}\par\vspace{{0.6em}}");
            }
            block.AppendLine($@"{{\LARGE\bfseries\raggedright {title}\par}}\vspace{{0.9em}}");
            if (_front.Authors.Count > 0)
            {
                block.AppendLine(@"{\large\raggedright " + string.Join(", ", _front.Authors.Select(a => Escape(a.Name) + AuthorMarks(a) + Orcid(a))) + @"\par}\vspace{0.6em}");
            }
            if (affiliations.Count > 0)
            {
                block.AppendLine(@"{\footnotesize\raggedright");
                foreach (var (number, affiliation) in affiliations)
                {
                    block.AppendLine($@"\textsuperscript{{{number}}}\,{Escape(affiliation.Text)}\\");
                }
                foreach (var author in corresponding)
                {
                    block.AppendLine($@"\textsuperscript{{*}}\,Corresponding author: {Escape(author.Name)}{(author.Email.Length > 0 ? $@", \href{{mailto:{Escape(author.Email)}}}{{{Escape(author.Email)}}}" : string.Empty)}\\");
                }
                if (_front.Authors.Any(a => a.EqualContribution))
                {
                    block.AppendLine(@"\textsuperscript{\dag}\,These authors contributed equally.\\");
                }
                if (_front.Notes.Trim().Length > 0)
                {
                    block.AppendLine(Escape(_front.Notes.Trim()) + @"\\");
                }
                block.AppendLine(@"\par}");
            }

            if (Template == LatexTemplate.ReviewManuscript)
            {
                tex.AppendLine(@"\begin{titlepage}");
                tex.AppendLine(@"\singlespacing");
                tex.Append(block);
                if (keywords.Count > 0)
                {
                    tex.AppendLine($@"\vspace{{1.5em}}\noindent\textbf{{Keywords:}} {keywordText}\par");
                }
                if (highlights.Count > 0)
                {
                    tex.AppendLine(@"\vspace{1.5em}\noindent\textbf{Highlights}\begin{itemize}");
                    foreach (string h in highlights)
                    {
                        tex.AppendLine($@"\item {Inline(h)}");
                    }
                    tex.AppendLine(@"\end{itemize}");
                }
                tex.Append(DraftNotesBox());
                tex.AppendLine(@"\end{titlepage}");
                tex.AppendLine(@"\linenumbers");
                if (abstractText.Length > 0)
                {
                    tex.AppendLine(@"\section*{Abstract}").AppendLine(abstractText).AppendLine();
                }
                return;
            }

            // Two columns: the title, a ruled abstract box and keywords across the page, then the text.
            tex.AppendLine(@"\twocolumn[{%");
            tex.Append(block);
            tex.AppendLine(@"\vspace{0.9em}\noindent\rule{\textwidth}{0.6pt}\par\vspace{0.4em}");
            if (abstractText.Length > 0)
            {
                tex.AppendLine(@"\noindent{\small\textbf{Abstract}\quad " + abstractText.Replace("\n\n", @"\par ") + @"\par}");
            }
            if (keywords.Count > 0)
            {
                tex.AppendLine($@"\vspace{{0.5em}}\noindent{{\small\textbf{{Keywords:}} {keywordText}\par}}");
            }
            tex.AppendLine(@"\vspace{0.4em}\noindent\rule{\textwidth}{0.6pt}\vspace{1.2em}");
            tex.Append(DraftNotesBox());
            tex.AppendLine(@"}]");
            tex.AppendLine(@"\thispagestyle{plain}");
            if (_options.LineNumbers)
            {
                tex.AppendLine(@"\linenumbers");
            }
        }

        // ---- Body --------------------------------------------------------------------------------------

        private void WriteBody()
        {
            foreach (var block in _doc.Blocks)
            {
                if (_skip.Contains(block.ParagraphId))
                {
                    continue;
                }
                string text = Final(block);
                switch (block.Role)
                {
                    case BlockStyleRole.Heading1 or BlockStyleRole.Heading2 or BlockStyleRole.Heading3:
                        WriteHeading(block, text);
                        break;
                    case BlockStyleRole.Figure:
                        WriteFigure(block, text);
                        break;
                    case BlockStyleRole.Table:
                        WriteTable(block, text);
                        break;
                    case BlockStyleRole.Equation:
                        WriteEquation(block, text);
                        break;
                    case BlockStyleRole.BlockQuote:
                        _body.AppendLine(@"\begin{quote}").AppendLine(Inline(text)).AppendLine(@"\end{quote}").AppendLine();
                        break;
                    case BlockStyleRole.Caption:
                        _body.AppendLine($@"{{\small\itshape {Inline(text)}\par}}").AppendLine();
                        break;
                    case BlockStyleRole.ReferenceItem:
                        // References typed by hand stay where they are, as a hanging-indent paragraph.
                        _body.AppendLine($@"\noindent\hangindent=1em {{\footnotesize {Inline(text)}\par}}").AppendLine();
                        break;
                    default:
                        _body.AppendLine(Inline(text)).AppendLine();
                        break;
                }
            }
        }

        private static readonly Regex ReferencesHeadingRegex = new(@"^(references|bibliography|literature cited|works cited|reference list)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private void WriteHeading(DocumentBlock block, string text)
        {
            string label = ScholarlyBlocks.HeadingLabel(text) is { } l ? $@"\label{{{l}}}" : string.Empty;
            string heading = ScholarlyBlocks.StripHeadingLabel(text).Trim();
            if (ReferencesHeadingRegex.IsMatch(heading) && _context.HasCitations && !_referencesWritten)
            {
                WriteReferences(Escape(heading));
                return;
            }
            string command = block.Role switch
            {
                BlockStyleRole.Heading1 => "section",
                BlockStyleRole.Heading2 => "subsection",
                _ => "subsubsection"
            };
            bool numbered = block.ComputedSectionNumber.Length > 0 || !_profile.AutoNumberHeadings;
            _body.AppendLine($@"\{command}{(numbered ? string.Empty : "*")}{{{Inline(heading)}}}{label}").AppendLine();
        }

        private void WriteReferences(string heading)
        {
            _referencesWritten = true;
            if (_context.Citations.References.Count == 0)
            {
                return;
            }
            _body.AppendLine($@"\begin{{reflist}}{{{heading}}}");
            foreach (var reference in _context.Citations.References)
            {
                string anchor = $@"\hypertarget{{cite.{AnchorName(reference.Key)}}}{{}}";
                // Braces keep a label such as "[1]" from ending the optional argument early.
                string label = $"[{{{anchor}{Escape(reference.Label)}}}]";
                _body.AppendLine($@"\item{label} {LinkUrls(Formatted(reference.Text))}");
            }
            _body.AppendLine(@"\end{reflist}").AppendLine();
        }

        private static readonly Regex UrlRegex = new(@"https?://[^\s{}\\]+[^\s{}\\.,;]", RegexOptions.Compiled);

        /// <summary>Web addresses in references become links that can break across lines.</summary>
        private static string LinkUrls(string latex)
            => UrlRegex.Replace(latex, m => $@"\url{{{m.Value.Replace(@"\_", "_").Replace(@"\%", "%").Replace(@"\#", "#").Replace(@"\&", "&")}}}");

        private static string AnchorName(string key) => Regex.Replace(key, @"[^A-Za-z0-9:._-]", "-");

        private string ResolveFile(string relative)
            => Path.IsPathRooted(relative) ? relative : Path.Combine(_options.WorkspacePath, relative.Replace('/', Path.DirectorySeparatorChar));

        private void WriteFigure(DocumentBlock block, string text)
        {
            var figure = ScholarlyBlocks.ParseFigure(text);
            if (figure == null)
            {
                _body.AppendLine(Inline(text)).AppendLine();
                return;
            }
            string source = ResolveFile(figure.ImagePath);
            // A diagram is drawn from its source as TikZ, so TeX typesets its labels and math itself.
            string? diagram = Diagrams.DiagramFiles.SourceOf(figure) is { } d && File.Exists(ResolveFile(d)) ? IncludeDiagram(ResolveFile(d), d) : null;
            string? included = diagram == null && File.Exists(source) ? IncludeFigureFile(source, figure.ImagePath) : null;
            if (diagram == null && !File.Exists(source))
            {
                _warnings.Add($"The image {figure.ImagePath} is missing.");
            }

            // Layout. A width (width=60%) is a fraction of the text width, as in Word and the preview. In two
            // columns, a figure wider than about a column spans both, as does one that asks for it (span=full) and a
            // wide panorama; the others fill the column.
            var size = File.Exists(source) ? FigureImages.NaturalSizePoints(source) : null;
            bool spanRequested = figure.Attributes.TryGetValue("span", out var span) && span is "full" or "page" or "2";
            double? fraction = figure.WidthFraction;
            bool wide = _twoColumn && (spanRequested || fraction > 0.6 || fraction == null && size is { } s && s.Width > 2.2 * s.Height);
            string width = !_twoColumn ? Length(fraction ?? 1.0, "linewidth")
                : wide ? Length(fraction is > 0.6 ? fraction.Value : 1.0, "textwidth")
                : Length(fraction is { } f ? Math.Min(1.0, f / 0.5) : 1.0, "columnwidth");
            string environment = wide ? "figure*" : "figure";

            // Full-width floats go to the top of a page (LaTeX cannot put them at the foot).
            _body.AppendLine($@"\begin{{{environment}}}[{(wide ? "tp" : "tbp")}]");
            _body.AppendLine(@"\centering");
            string? placeholder = DraftPlaceholders.NoteOf(figure.Attributes);
            string graphic = diagram != null
                ? $@"\adjustbox{{width={width},max totalheight={(wide ? "0.5" : "0.45")}\textheight}}{{\input{{{diagram}}}}}"
                : $@"\includegraphics[width={width},height={(wide ? "0.5" : "0.45")}\textheight,keepaspectratio]{{{included}}}";
            _body.AppendLine(included != null || diagram != null
                ? (placeholder != null ? $@"\placeholderstamp{{{graphic}}}" : graphic)
                : $@"\fbox{{\parbox{{0.8\linewidth}}{{\centering\small Image {Escape(figure.ImagePath)} is missing.}}}}");
            _body.AppendLine($@"\caption{{{PlaceholderCaption(placeholder)}{CaptionText(figure.Caption)}}}{(figure.Label != null ? $@"\label{{{figure.Label}}}" : string.Empty)}");
            _body.AppendLine($@"\end{{{environment}}}").AppendLine();
        }

        /// <summary>
        /// A caption's text. Captions are moving arguments (written to the .aux and list of figures), where hyperref's
        /// citation links must be protected or LaTeX stops with "Argument of \caption@ydblarg has an extra }".
        /// </summary>
        private string CaptionText(string caption) => Inline(caption).Replace(@"\hyperlink{", @"\protect\hyperlink{");

        /// <summary>"[Placeholder: synthetic data] " before the caption of a figure or table that is one.</summary>
        private static string PlaceholderCaption(string? note)
            => note == null ? string.Empty
                : $@"\textcolor{{placeholderink}}{{\textbf{{[Placeholder{(note.Length > 0 ? ": " + Escape(note) : string.Empty)}]}}}} ";

        private static readonly Regex PlainDecimalRegex = new(@"^[-+]?\d+(\.\d+)?$", RegexOptions.Compiled);

        /// <summary>siunitx's table-format for a column of plain decimals: sign, integer and decimal digits ("-1.2").</summary>
        private static string TableFormat(IReadOnlyList<string> values)
        {
            bool sign = values.Any(v => v.StartsWith('-') || v.StartsWith('+'));
            int integers = values.Max(v => v.TrimStart('-', '+').Split('.')[0].Length);
            int decimals = values.Max(v => v.Contains('.') ? v.Split('.')[1].Length : 0);
            return $"{(sign ? "-" : string.Empty)}{integers}{(decimals > 0 ? "." + decimals : string.Empty)}";
        }

        private static string Length(double fraction, string length)
            => Math.Abs(fraction - 1.0) < 0.0005 ? $@"\{length}" : $@"{fraction.ToString("0.###", CultureInfo.InvariantCulture)}\{length}";

        private static string StagingFolder()
        {
            string staging = Path.Combine(Path.GetTempPath(), "RevisionsEditor-latex-images");
            Directory.CreateDirectory(staging);
            return staging;
        }

        /// <summary>
        /// A diagram as a TikZ file in the output (figures/&lt;name&gt;.tikz), or null (with a warning) when its source
        /// cannot be read, in which case the figure's SVG is used.
        /// </summary>
        private string? IncludeDiagram(string sourcePath, string shownPath)
        {
            var parsed = Diagrams.DiagramSource.Parse(File.ReadAllText(sourcePath), out var errors);
            if (errors.Count > 0)
            {
                _warnings.Add($"{shownPath} has errors (line {errors[0].Line}: {errors[0].Message}); its drawing was used instead.");
                return null;
            }
            string staged = Path.Combine(StagingFolder(), $"{Path.GetFileNameWithoutExtension(sourcePath)}-{Guid.NewGuid():N}.tikz");
            File.WriteAllText(staged, Diagrams.DiagramRenderer.ToTikz(parsed));
            string included = FigurePath(Path.GetFileNameWithoutExtension(sourcePath) + ".tikz", staged);
            _files.Add((staged, included));
            _usesTikz = true;
            return included;
        }

        /// <summary>A figure path in the output that no other figure uses (two figures may share a file name).</summary>
        private string FigurePath(string fileName, string source)
        {
            string candidate = $"figures/{fileName}";
            for (int n = 2; _files.Any(f => f.RelativePath == candidate && !string.Equals(f.SourcePath, source, StringComparison.OrdinalIgnoreCase)); n++)
            {
                candidate = $"figures/{Path.GetFileNameWithoutExtension(fileName)}-{n}{Path.GetExtension(fileName)}";
            }
            return candidate;
        }

        /// <summary>
        /// The figure as the engine includes it: PNG, JPEG and PDF as they are, SVG as a vector PDF, WebP as PNG,
        /// TIFF, BMP and GIF converted by the app. Null (with a warning) when it cannot be included.
        /// </summary>
        private string? IncludeFigureFile(string source, string shownPath)
        {
            string extension = Path.GetExtension(source);
            string stem = Path.GetFileNameWithoutExtension(source);
            string? staged = null, name = null;
            switch (FigureImages.KindOf(source))
            {
                case var _ when EngineImageTypes.Contains(extension):
                    staged = source;
                    name = Path.GetFileName(source);
                    break;
                case FigureImageKind.Svg:
                    name = stem + ".pdf";
                    staged = Path.Combine(StagingFolder(), $"{stem}-{Guid.NewGuid():N}.pdf");
                    if (!FigureImages.SvgToPdf(source, staged))
                    {
                        _warnings.Add($"{shownPath} could not be read as an SVG drawing.");
                        return null;
                    }
                    break;
                case FigureImageKind.Eps:
                    _warnings.Add($"{shownPath} is an EPS figure, which needs Ghostscript to typeset; save it as PDF or SVG instead (plotting tools offer both).");
                    return null;
                case FigureImageKind.Raster when extension.Equals(".webp", StringComparison.OrdinalIgnoreCase):
                    if (FigureImages.RenderPng(source) is { } png)
                    {
                        name = stem + ".png";
                        staged = Path.Combine(StagingFolder(), $"{stem}-{Guid.NewGuid():N}.png");
                        File.WriteAllBytes(staged, png);
                    }
                    break;
                default:
                    // Converted into a staging folder by the caller, then copied with the other files.
                    if (_options.ConvertImage?.Invoke(source, StagingFolder()) is { } converted)
                    {
                        name = converted;
                        staged = Path.Combine(StagingFolder(), converted);
                    }
                    break;
            }
            if (staged == null || name == null)
            {
                _warnings.Add($"{shownPath} is a {extension} image, which LaTeX cannot include; convert it to PNG, PDF or SVG.");
                return null;
            }
            string included = FigurePath(name, staged);
            _files.Add((staged, included));
            return included;
        }

        private void WriteTable(DocumentBlock block, string text)
        {
            var table = ScholarlyBlocks.ParseTable(text);
            if (table == null)
            {
                _body.AppendLine(Inline(text)).AppendLine();
                return;
            }
            List<string[]> rows = [];
            if (table.CsvPath != null && File.Exists(ResolveFile(table.CsvPath)))
            {
                rows = CsvTable.Parse(File.ReadAllText(ResolveFile(table.CsvPath)));
            }
            else
            {
                _warnings.Add(table.CsvPath == null ? "A table has no CSV file." : $"The table file {table.CsvPath} is missing.");
            }

            int columns = rows.Count == 0 ? 1 : rows.Max(r => r.Length);
            var values = Enumerable.Range(0, columns)
                .Select(c => rows.Skip(1).Select(r => c < r.Length ? r[c].Trim().Replace(Minus, "-") : string.Empty).Where(v => v.Length > 0).ToList())
                .ToArray();
            var numeric = Enumerable.Range(0, columns)
                .Select(c => c > 0 && values[c].DefaultIfEmpty("x").All(v => Regex.IsMatch(v, @"^[-+±]?\(?[\d.,]+\)?\s*(%|±\s*[\d.,]+)?$")))
                .ToArray();
            // Plain decimals are aligned on the decimal point (siunitx); others (percentages, ± values) right-aligned.
            var decimals = Enumerable.Range(0, columns)
                .Select(c => numeric[c] && values[c].Count > 0 && values[c].All(v => PlainDecimalRegex.IsMatch(v)) ? TableFormat(values[c]) : null)
                .ToArray();

            // Sized by its content: at its natural width in a column if it fits there, across both columns if it
            // fits the page, and otherwise with its text columns wrapping (tabularx), across the page when a single
            // column would make them too narrow. span=full or span=column decides it instead.
            var size = TableSizing.Plan(rows.Select(r => r.Select(cell => PlainCellText(cell)).ToArray()).ToList(), numeric, Template, _twoColumn,
                table.Attributes.GetValueOrDefault("span"));
            bool wide = size.Wide;
            string environment = wide ? "table*" : "table";

            _body.AppendLine($@"\begin{{{environment}}}[tbp]");
            _body.AppendLine(@"\centering");
            _body.AppendLine($@"\caption{{{PlaceholderCaption(DraftPlaceholders.NoteOf(table.Attributes))}{CaptionText(table.Caption)}}}{(table.Label != null ? $@"\label{{{table.Label}}}" : string.Empty)}");
            if (rows.Count > 0)
            {
                string widthMacro = wide ? @"\textwidth" : @"\linewidth";
                string Spec(int i) => size.WrapShare[i] is { } share
                    ? $@">{{\raggedright\arraybackslash\hsize={share.ToString("0.###", CultureInfo.InvariantCulture)}\hsize}}X"
                    : i == 0 ? "l" : decimals[i] is { } format ? $"S[table-format={format}]" : numeric[i] ? "r" : "c";
                string spec = string.Concat(Enumerable.Range(0, columns).Select(Spec));
                // Still a safety net: a table wider than the page even with wrapping is scaled to fit.
                _body.AppendLine($@"\begin{{adjustbox}}{{max width={widthMacro}}}");
                _body.AppendLine(@"\small");
                _body.AppendLine(size.Wraps
                    ? $@"\begin{{tabularx}}{{{widthMacro}}}{{{spec}}}"
                    : @"\begin{tabular}{" + spec + "}");
                _body.AppendLine(@"\toprule");
                for (int r = 0; r < rows.Count; r++)
                {
                    var cells = Enumerable.Range(0, columns).Select(c =>
                    {
                        string cell = c < rows[r].Length ? rows[r][c].Trim() : string.Empty;
                        if (decimals[c] != null)
                        {
                            // siunitx reads the number (and prints a true minus); headings and blanks are text.
                            return r > 0 && cell.Length > 0 ? cell.Replace(Minus, "-") : $"{{{(r == 0 && cell.Length > 0 ? $@"\textbf{{{Formatted(cell)}}}" : Formatted(cell))}}}";
                        }
                        // A leading hyphen in a number is a minus sign.
                        string formatted = Formatted(Regex.Replace(cell, @"^-(?=[\d.])", "−"));
                        return r == 0 && formatted.Length > 0 ? $@"\textbf{{{formatted}}}" : formatted;
                    });
                    _body.AppendLine(string.Join(" & ", cells) + @" \\");
                    if (r == 0)
                    {
                        _body.AppendLine(@"\midrule");
                    }
                }
                _body.AppendLine(@"\bottomrule");
                _body.AppendLine(size.Wraps ? @"\end{tabularx}" : @"\end{tabular}");
                _body.AppendLine(@"\end{adjustbox}");
            }
            _body.AppendLine($@"\end{{{environment}}}").AppendLine();
        }

        /// <summary>A cell as it prints (citations, numbers and cross-references rendered), for measuring.</summary>
        private string PlainCellText(string cell)
            => string.Concat(_context.Tokenize(cell.Trim()).Select(p => p.Kind == InlinePieceKind.Math ? p.Detail ?? string.Empty : p.Display))
                .Replace("**", string.Empty).Replace("*", string.Empty);

        private void WriteEquation(DocumentBlock block, string text)
        {
            var equation = ScholarlyBlocks.ParseEquation(text);
            if (equation == null)
            {
                _body.AppendLine(Inline(text)).AppendLine();
                return;
            }
            _body.AppendLine(@"\begin{equation}");
            _body.AppendLine(equation.Latex);
            if (equation.Label != null)
            {
                _body.AppendLine($@"\label{{{equation.Label}}}");
            }
            _body.AppendLine(@"\end{equation}").AppendLine();
        }

        // ---- Inline text -----------------------------------------------------------------------------

        /// <summary>
        /// A paragraph's text: citations and cross-references rendered in the journal's style and linked,
        /// linked numbers filled in, inline math kept as LaTeX, everything else escaped.
        /// </summary>
        private string Inline(string text)
        {
            var sb = new StringBuilder();
            bool afterNumber = false;
            foreach (var piece in _context.Tokenize(text))
            {
                switch (piece.Kind)
                {
                    case InlinePieceKind.Text:
                        // "{{height.rmse}} m": the unit stays with the number rendered before it.
                        string prose = afterNumber ? LeadingUnitRegex.Replace(piece.Display, ThinNoBreakSpace.ToString()) : piece.Display;
                        sb.Append(Formatted(Typeset(prose)));
                        break;
                    case InlinePieceKind.Math:
                        sb.Append('$').Append(piece.Detail).Append('$');
                        break;
                    case InlinePieceKind.Placeholder:
                        sb.Append($@"\placeholder{{{Formatted(Typeset(piece.Display))}}}{{{Formatted(piece.Detail ?? string.Empty)}}}");
                        break;
                    case InlinePieceKind.Citation:
                        var keys = Bibliography.CitationSyntax.Find(piece.Source).SelectMany(c => c.Items).Select(i => i.Key).ToList();
                        string rendered = Formatted(piece.Display);
                        sb.Append(keys.Count > 0 && !piece.IsError ? $@"\hyperlink{{cite.{AnchorName(keys[0])}}}{{{rendered}}}" : rendered);
                        break;
                    case InlinePieceKind.CrossReference:
                        var use = CrossReferenceIndex.FindUses(piece.Source).FirstOrDefault();
                        var first = use?.Items.FirstOrDefault();
                        string label = first != null ? _context.CrossReferences.Find(first.Value.Kind, first.Value.Label)?.Label ?? string.Empty : string.Empty;
                        string shown = Formatted(piece.Display);
                        sb.Append(label.Length > 0 && !piece.IsError ? $@"\hyperref[{label}]{{{shown}}}" : shown);
                        break;
                    default:
                        sb.Append(Formatted(piece.Display));
                        break;
                }
                afterNumber = piece.Kind != InlinePieceKind.Text && piece.Display.Length > 0 && char.IsDigit(piece.Display[^1]);
            }
            return sb.ToString();
        }
    }
}
