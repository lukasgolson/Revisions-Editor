using System.Text;
using System.Text.RegularExpressions;

namespace RevisionsEditor.Core.Bibliography;

/// <summary>Result of parsing a .bib file: entries in file order, warnings with line numbers, and keys defined more than once.</summary>
public sealed class BibParseResult
{
    internal BibParseResult(IReadOnlyList<BibEntry> entries, IReadOnlyList<string> warnings, IReadOnlyList<string> duplicateKeys)
    {
        Entries = entries;
        Warnings = warnings;
        DuplicateKeys = duplicateKeys;
    }

    /// <summary>Parsed entries in file order (for duplicate keys only the first definition is kept).</summary>
    public IReadOnlyList<BibEntry> Entries { get; }

    /// <summary>Problems found, each starting with "Line N:".</summary>
    public IReadOnlyList<string> Warnings { get; }

    /// <summary>Keys defined more than once (case-insensitive), distinct, in order of the first repeat.</summary>
    public IReadOnlyList<string> DuplicateKeys { get; }

    /// <summary>Entries by key, case-insensitive, for <see cref="CitationProcessor"/>.</summary>
    public IReadOnlyDictionary<string, BibEntry> ToLibrary()
    {
        var d = new Dictionary<string, BibEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in Entries) d.TryAdd(e.Key, e);
        return d;
    }
}

/// <summary>Tolerant BibTeX/BibLaTeX parser: never throws, skips malformed entries with a warning and keeps going.</summary>
public static class BibTeXParser
{
    private static readonly Regex LineStartAt = new(@"(?m)^[ \t]*@", RegexOptions.Compiled);
    private static readonly Regex NextEntryAhead = new(@"\G\r?\n[ \t]*@[A-Za-z]+[ \t]*[{(]", RegexOptions.Compiled);

    private static readonly HashSet<string> VerbatimFields = new(StringComparer.Ordinal)
    {
        "url", "doi", "file", "eprint", "urlraw", "pdf", "verba", "verbb", "verbc", "uri",
    };

    private static readonly HashSet<string> KnownTypes = new(StringComparer.Ordinal)
    {
        "article", "book", "mvbook", "inbook", "bookinbook", "suppbook", "booklet", "collection", "mvcollection",
        "incollection", "suppcollection", "manual", "misc", "online", "electronic", "www", "webpage", "patent",
        "periodical", "proceedings", "mvproceedings", "inproceedings", "conference", "reference", "inreference",
        "report", "techreport", "thesis", "phdthesis", "mastersthesis", "unpublished", "dataset", "data", "software",
        "standard", "artwork", "audio", "video", "image", "movie", "music", "performance", "review", "letter",
    };

    private static readonly Dictionary<string, string> MonthMacros = new(StringComparer.OrdinalIgnoreCase)
    {
        ["jan"] = "January", ["feb"] = "February", ["mar"] = "March", ["apr"] = "April", ["may"] = "May", ["jun"] = "June",
        ["jul"] = "July", ["aug"] = "August", ["sep"] = "September", ["oct"] = "October", ["nov"] = "November", ["dec"] = "December",
    };

    /// <summary>Parses BibTeX text. Handles @string macros, # concatenation, @comment/@preamble, () delimiters and malformed entries.</summary>
    public static BibParseResult Parse(string text)
    {
        var p = new Parser(text ?? "");
        p.Run();
        return new BibParseResult(p.Entries, p.Warnings, p.Duplicates);
    }

    /// <summary>Splits a raw (undecoded) name list into names; "others" becomes <see cref="PersonName.Others"/>.</summary>
    public static IReadOnlyList<PersonName> ParseNames(string? raw)
    {
        var result = new List<PersonName>();
        if (string.IsNullOrWhiteSpace(raw)) return result;
        foreach (var part in SplitAnd(raw))
        {
            var n = ParseName(part);
            if (n is not null) result.Add(n);
        }
        return result;
    }

    private sealed class ParseError(string message, int position) : Exception(message)
    {
        public int Position { get; } = position;
    }

    private sealed class Parser(string text)
    {
        private readonly string _t = text;
        private readonly int _n = text.Length;
        private int _pos;
        private readonly int[] _lineStarts = ComputeLineStarts(text);
        private readonly Dictionary<string, string> _macros = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _seen = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _dupSet = new(StringComparer.OrdinalIgnoreCase);
        public List<BibEntry> Entries { get; } = [];
        public List<string> Warnings { get; } = [];
        public List<string> Duplicates { get; } = [];

        private static int[] ComputeLineStarts(string s)
        {
            var list = new List<int> { 0 };
            for (var i = 0; i < s.Length; i++)
            {
                if (s[i] == '\n') list.Add(i + 1);
            }
            return [.. list];
        }

        private int LineOf(int pos)
        {
            var idx = Array.BinarySearch(_lineStarts, Math.Clamp(pos, 0, Math.Max(0, _n)));
            return (idx >= 0 ? idx : ~idx - 1) + 1;
        }

        private void Warn(int pos, string message) => Warnings.Add($"Line {LineOf(pos)}: {message}");

        public void Run()
        {
            while (_pos < _n)
            {
                var at = _t.IndexOf('@', _pos);
                if (at < 0) break;
                _pos = at + 1;
                var ts = _pos;
                while (_pos < _n && (char.IsLetterOrDigit(_t[_pos]) || _t[_pos] == '_')) _pos++;
                var type = _t[ts.._pos].ToLowerInvariant();
                if (type.Length == 0) continue;
                SkipWs();
                if (_pos >= _n || (_t[_pos] != '{' && _t[_pos] != '('))
                {
                    if (KnownTypes.Contains(type)) Warn(at, $"\"@{type}\" is not followed by \"{{\" or \"(\"; skipped.");
                    continue;
                }
                var open = _t[_pos];
                var close = open == '{' ? '}' : ')';
                try
                {
                    switch (type)
                    {
                        case "comment":
                        case "preamble":
                            SkipBalanced(open, close);
                            break;
                        case "string":
                            ParseString(close);
                            break;
                        default:
                            ParseEntry(type, at, close);
                            break;
                    }
                }
                catch (ParseError err)
                {
                    Warn(err.Position, err.Message + " Entry skipped.");
                    var m = LineStartAt.Match(_t, Math.Min(at + 1, _n));
                    _pos = m.Success ? m.Index : _n;
                    // Make sure we never loop on the same '@'.
                    if (_pos <= at) _pos = at + 1;
                }
            }
        }

        private void SkipWs()
        {
            while (_pos < _n && char.IsWhiteSpace(_t[_pos])) _pos++;
        }

        private void SkipBalanced(char open, char close)
        {
            var start = _pos;
            var depth = 0;
            for (; _pos < _n; _pos++)
            {
                var c = _t[_pos];
                if (c == open) depth++;
                else if (c == close && --depth == 0)
                {
                    _pos++;
                    return;
                }
            }
            throw new ParseError("Unterminated @comment/@preamble.", start);
        }

        private void ParseString(char close)
        {
            var start = _pos;
            _pos++; // open delimiter
            SkipWs();
            var name = ReadIdentifier(close);
            if (name.Length == 0) throw new ParseError("@string without a macro name.", start);
            SkipWs();
            if (_pos >= _n || _t[_pos] != '=') throw new ParseError($"@string \"{name}\" is missing \"=\".", _pos);
            _pos++;
            var value = ParseValue(close, start);
            _macros[name] = value;
            SkipWs();
            if (_pos < _n && _t[_pos] == ',') { _pos++; SkipWs(); }
            if (_pos >= _n || _t[_pos] != close) throw new ParseError($"@string \"{name}\" is not closed.", _pos);
            _pos++;
        }

        private string ReadIdentifier(char close)
        {
            var s = _pos;
            while (_pos < _n)
            {
                var c = _t[_pos];
                if (char.IsWhiteSpace(c) || c is '=' or ',' or '{' or '}' or '(' or ')' or '"' or '#' || c == close) break;
                _pos++;
            }
            return _t[s.._pos];
        }

        private void ParseEntry(string type, int at, char close)
        {
            _pos++; // open delimiter
            SkipWs();
            var ks = _pos;
            while (_pos < _n && _t[_pos] != ',' && _t[_pos] != close && !char.IsWhiteSpace(_t[_pos]) && _t[_pos] != '=') _pos++;
            var key = _t[ks.._pos];
            SkipWs();
            if (key.Length == 0 || (_pos < _n && _t[_pos] == '='))
                throw new ParseError($"@{type} entry has no citation key.", at);
            var fields = new Dictionary<string, string>(StringComparer.Ordinal);
            if (_pos >= _n) throw new ParseError($"Entry \"{key}\" is not closed.", at);
            if (_t[_pos] == ',') _pos++;
            else if (_t[_pos] != close) throw new ParseError($"Expected \",\" after the key of entry \"{key}\".", _pos);

            while (true)
            {
                SkipWs();
                if (_pos >= _n) throw new ParseError($"Entry \"{key}\" is not closed (missing \"{close}\").", at);
                var c = _t[_pos];
                if (c == close) { _pos++; break; }
                if (c == ',') { _pos++; continue; }
                if (c == '@') throw new ParseError($"Entry \"{key}\" is not closed before the next entry.", at);
                var fs = _pos;
                var name = ReadIdentifier(close);
                if (name.Length == 0) throw new ParseError($"Unexpected \"{c}\" in entry \"{key}\".", _pos);
                SkipWs();
                if (_pos >= _n || _t[_pos] != '=') throw new ParseError($"Field \"{name}\" in entry \"{key}\" is missing \"=\".", _pos);
                _pos++;
                var value = ParseValue(close, fs);
                var lname = name.ToLowerInvariant();
                if (!fields.TryAdd(lname, value)) Warn(fs, $"Entry \"{key}\" repeats field \"{lname}\"; the first value is kept.");
                SkipWs();
                if (_pos < _n && _t[_pos] != ',' && _t[_pos] != close)
                {
                    if (char.IsLetter(_t[_pos])) Warn(_pos, $"Missing \",\" after field \"{lname}\" in entry \"{key}\".");
                    else throw new ParseError($"Unexpected \"{_t[_pos]}\" after field \"{lname}\" in entry \"{key}\".", _pos);
                }
            }

            if (_seen.TryGetValue(key, out var firstSpelling))
            {
                Warn(at, $"Duplicate key \"{key}\"; the first definition is kept.");
                if (_dupSet.Add(key)) Duplicates.Add(firstSpelling);
                return;
            }
            _seen[key] = key;
            Entries.Add(Build(key, type, fields, LineOf(at)));
        }

        private string ParseValue(char close, int fieldStart)
        {
            var sb = new StringBuilder();
            while (true)
            {
                SkipWs();
                if (_pos >= _n) throw new ParseError("Unexpected end of file in a field value.", fieldStart);
                var c = _t[_pos];
                if (c == '{') sb.Append(ReadDelimited('{', fieldStart));
                else if (c == '"') sb.Append(ReadDelimited('"', fieldStart));
                else if (char.IsDigit(c))
                {
                    var s = _pos;
                    while (_pos < _n && char.IsDigit(_t[_pos])) _pos++;
                    sb.Append(_t, s, _pos - s);
                }
                else
                {
                    var s = _pos;
                    var name = ReadIdentifier(close);
                    if (name.Length == 0) throw new ParseError($"Expected a value but found \"{c}\".", _pos);
                    if (_macros.TryGetValue(name, out var mv)) sb.Append(mv);
                    else if (MonthMacros.TryGetValue(name, out var month)) sb.Append(month);
                    else
                    {
                        Warn(s, $"Undefined @string macro \"{name}\"; its name is used as text.");
                        sb.Append(name);
                    }
                }
                SkipWs();
                if (_pos < _n && _t[_pos] == '#') { _pos++; continue; }
                return sb.ToString();
            }
        }

        /// <summary>Reads {...} or "..." and returns the content without the outer delimiters.</summary>
        private string ReadDelimited(char open, int fieldStart)
        {
            var start = _pos;
            _pos++;
            var depth = 0;
            var s = _pos;
            for (; _pos < _n; _pos++)
            {
                var c = _t[_pos];
                if (c == '\\' && _pos + 1 < _n)
                {
                    _pos++; // escaped char, including \{ \} \"
                    continue;
                }
                if (c == '\n' && NextEntryAhead.IsMatch(_t, _pos))
                    throw new ParseError("Unbalanced braces or quotes in a field value.", fieldStart);
                if (c == '{') depth++;
                else if (c == '}')
                {
                    if (depth == 0)
                    {
                        if (open == '{')
                        {
                            var v = _t[s.._pos];
                            _pos++;
                            return v;
                        }
                        throw new ParseError("Unbalanced \"}\" inside a quoted value.", start);
                    }
                    depth--;
                }
                else if (c == '"' && open == '"' && depth == 0)
                {
                    var v = _t[s.._pos];
                    _pos++;
                    return v;
                }
            }
            throw new ParseError("Unterminated field value (missing closing brace or quote).", fieldStart);
        }
    }

    private static BibEntry Build(string key, string type, Dictionary<string, string> raw, int line)
    {
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, value) in raw)
        {
            fields[name] = VerbatimFields.Contains(name) ? LaTeXDecoder.DecodeVerbatim(value) : LaTeXDecoder.Decode(value);
        }
        raw.TryGetValue("author", out var a);
        raw.TryGetValue("editor", out var ed);
        return new BibEntry(key, type, fields, ParseNames(a), ParseNames(ed), line);
    }

    // ---------------------------------------------------------------- names

    private static List<string> SplitAnd(string raw)
    {
        var parts = new List<string>();
        var depth = 0;
        var s = 0;
        for (var i = 0; i < raw.Length; i++)
        {
            var c = raw[i];
            if (c == '{') depth++;
            else if (c == '}') depth = Math.Max(0, depth - 1);
            else if (depth == 0 && char.IsWhiteSpace(c) && i + 4 < raw.Length
                     && (raw[i + 1] == 'a' || raw[i + 1] == 'A') && (raw[i + 2] == 'n' || raw[i + 2] == 'N')
                     && (raw[i + 3] == 'd' || raw[i + 3] == 'D') && char.IsWhiteSpace(raw[i + 4]))
            {
                parts.Add(raw[s..i]);
                i += 4;
                s = i + 1;
            }
        }
        parts.Add(raw[s..]);
        return parts.Select(p => p.Trim()).Where(p => p.Length > 0).ToList();
    }

    private static List<string> SplitDepth0(string s, Func<char, bool> isSep)
    {
        var parts = new List<string>();
        var depth = 0;
        var start = 0;
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (c == '{') depth++;
            else if (c == '}') depth = Math.Max(0, depth - 1);
            else if (depth == 0 && isSep(c))
            {
                parts.Add(s[start..i]);
                start = i + 1;
            }
        }
        parts.Add(s[start..]);
        return parts;
    }

    private static bool IsWholeBraced(string s)
    {
        if (s.Length < 2 || s[0] != '{' || s[^1] != '}') return false;
        var depth = 0;
        for (var i = 0; i < s.Length; i++)
        {
            if (s[i] == '\\') { i++; continue; }
            if (s[i] == '{') depth++;
            else if (s[i] == '}' && --depth == 0) return i == s.Length - 1;
        }
        return false;
    }

    /// <summary>BibTeX "von" test: the first letter at brace depth 0 is lowercase; braced words count as uppercase unless they start with a command.</summary>
    private static bool IsLowerWord(string raw)
    {
        if (raw.Length == 0) return false;
        if (raw[0] == '{' && !(raw.Length > 1 && raw[1] == '\\')) return false;
        var decoded = LaTeXDecoder.Decode(raw);
        foreach (var ch in decoded)
        {
            if (char.IsLetter(ch)) return char.IsLower(ch);
        }
        return false;
    }

    private static PersonName? ParseName(string raw)
    {
        raw = raw.Trim().TrimEnd(',').Trim();
        if (raw.Length == 0) return null;
        if (raw.Equals("others", StringComparison.OrdinalIgnoreCase)) return PersonName.Others;
        if (IsWholeBraced(raw))
            return new PersonName { Family = LaTeXDecoder.Decode(raw), IsInstitution = true };

        var commaParts = SplitDepth0(raw, c => c == ',').Select(p => p.Trim()).ToList();
        string? given, suffix = null;
        List<string> vonLast;
        if (commaParts.Count == 1)
        {
            var words = Words(commaParts[0]);
            if (words.Count == 1) return new PersonName { Family = LaTeXDecoder.Decode(words[0]) };
            // First von Last: von starts at the first lowercase word (not the last word).
            var vonStart = -1;
            for (var i = 0; i < words.Count - 1; i++)
            {
                if (IsLowerWord(words[i])) { vonStart = i; break; }
            }
            if (vonStart < 0)
            {
                given = string.Join(" ", words.Take(words.Count - 1));
                vonLast = [words[^1]];
            }
            else
            {
                given = string.Join(" ", words.Take(vonStart));
                vonLast = words.Skip(vonStart).ToList();
            }
        }
        else
        {
            vonLast = Words(commaParts[0]);
            if (commaParts.Count == 2) given = commaParts[1];
            else
            {
                suffix = commaParts[1];
                given = string.Join(", ", commaParts.Skip(2));
            }
        }

        // Split von Last: von = leading lowercase words, Last keeps at least the final word.
        var lastVon = -1;
        for (var i = 0; i < vonLast.Count - 1; i++)
        {
            if (IsLowerWord(vonLast[i])) lastVon = i;
            else break;
        }
        var particle = lastVon >= 0 ? string.Join(" ", vonLast.Take(lastVon + 1)) : null;
        var family = string.Join(" ", vonLast.Skip(lastVon + 1));
        return new PersonName
        {
            Family = LaTeXDecoder.Decode(family),
            Given = NullIfEmpty(LaTeXDecoder.Decode(given)),
            Particle = NullIfEmpty(LaTeXDecoder.Decode(particle)),
            Suffix = NullIfEmpty(LaTeXDecoder.Decode(suffix)),
        };
    }

    private static List<string> Words(string s) =>
        SplitDepth0(s, c => char.IsWhiteSpace(c) || c == '~').Where(w => w.Length > 0).ToList();

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}

/// <summary>Converts BibTeX field text (LaTeX) to Unicode with the manuscript's Markdown-like inline markup.</summary>
public static class LaTeXDecoder
{
    private static readonly Regex Whitespace = new(@"[ \t\r\n\f\v]+", RegexOptions.Compiled);
    private static readonly Regex SwitchItalic = new(@"^\\(?:em|it|itshape|sl|slshape)(?![A-Za-z])\s*", RegexOptions.Compiled);
    private static readonly Regex SwitchBold = new(@"^\\(?:bf|bfseries)(?![A-Za-z])\s*", RegexOptions.Compiled);
    private static readonly Regex SwitchOther = new(@"^\\(?:sc|scshape|rm|sf|tt|upshape|normalfont|relax|small|footnotesize|large|Large)(?![A-Za-z])\s*", RegexOptions.Compiled);

    private static readonly Dictionary<char, char> AccentMarks = new()
    {
        ['\''] = '́', ['`'] = '̀', ['^'] = '̂', ['"'] = '̈', ['~'] = '̃', ['='] = '̄', ['.'] = '̇',
    };

    private static readonly Dictionary<string, char> LetterAccents = new(StringComparer.Ordinal)
    {
        ["c"] = '̧', ["v"] = '̌', ["u"] = '̆', ["H"] = '̋', ["k"] = '̨', ["r"] = '̊',
        ["d"] = '̣', ["b"] = '̱',
    };

    private static readonly Dictionary<string, string> Symbols = new(StringComparer.Ordinal)
    {
        ["aa"] = "å", ["AA"] = "Å", ["o"] = "ø", ["O"] = "Ø", ["ae"] = "æ", ["AE"] = "Æ", ["oe"] = "œ", ["OE"] = "Œ",
        ["ss"] = "ß", ["l"] = "ł", ["L"] = "Ł", ["i"] = "ı", ["j"] = "ȷ", ["dh"] = "ð", ["DH"] = "Ð", ["th"] = "þ",
        ["TH"] = "Þ", ["ng"] = "ŋ", ["NG"] = "Ŋ", ["dj"] = "đ", ["DJ"] = "Đ",
        ["textendash"] = "–", ["textemdash"] = "—", ["textquoteleft"] = "‘", ["textquoteright"] = "’",
        ["textquotedblleft"] = "“", ["textquotedblright"] = "”", ["textdegree"] = "°", ["textasciitilde"] = "~",
        ["textbackslash"] = "\\", ["textunderscore"] = "_", ["textregistered"] = "®", ["texttrademark"] = "™",
        ["textcopyright"] = "©", ["textbullet"] = "•", ["ldots"] = "…", ["dots"] = "…", ["textellipsis"] = "…",
        ["S"] = "§", ["P"] = "¶", ["pounds"] = "£", ["euro"] = "€", ["texteuro"] = "€", ["textmu"] = "µ",
        ["guillemotleft"] = "«", ["guillemotright"] = "»", ["LaTeX"] = "LaTeX", ["TeX"] = "TeX", ["textperthousand"] = "‰",
        ["textpm"] = "±", ["texttimes"] = "×", ["textless"] = "<", ["textgreater"] = ">", ["textbar"] = "|",
        ["alpha"] = "α", ["beta"] = "β", ["gamma"] = "γ", ["delta"] = "δ", ["epsilon"] = "ε", ["varepsilon"] = "ε",
        ["zeta"] = "ζ", ["eta"] = "η", ["theta"] = "θ", ["iota"] = "ι", ["kappa"] = "κ", ["lambda"] = "λ", ["mu"] = "μ",
        ["nu"] = "ν", ["xi"] = "ξ", ["pi"] = "π", ["rho"] = "ρ", ["sigma"] = "σ", ["tau"] = "τ", ["upsilon"] = "υ",
        ["phi"] = "φ", ["varphi"] = "φ", ["chi"] = "χ", ["psi"] = "ψ", ["omega"] = "ω", ["Gamma"] = "Γ", ["Delta"] = "Δ",
        ["Theta"] = "Θ", ["Lambda"] = "Λ", ["Xi"] = "Ξ", ["Pi"] = "Π", ["Sigma"] = "Σ", ["Phi"] = "Φ", ["Psi"] = "Ψ",
        ["Omega"] = "Ω", ["times"] = "×", ["pm"] = "±", ["cdot"] = "·", ["le"] = "≤", ["leq"] = "≤", ["ge"] = "≥",
        ["geq"] = "≥", ["approx"] = "≈", ["sim"] = "∼", ["infty"] = "∞", ["circ"] = "°", ["degree"] = "°", ["prime"] = "′",
        ["ne"] = "≠", ["neq"] = "≠", ["rightarrow"] = "→", ["to"] = "→", ["leftarrow"] = "←", ["ell"] = "ℓ",
        ["textasciicircum"] = "^", ["quad"] = " ", ["qquad"] = " ",
    };

    private static readonly HashSet<string> ItalicCommands = new(StringComparer.Ordinal) { "textit", "emph", "textsl", "mathit" };
    private static readonly HashSet<string> BoldCommands = new(StringComparer.Ordinal) { "textbf", "mathbf", "boldsymbol" };
    private static readonly HashSet<string> RawArgCommands = new(StringComparer.Ordinal) { "url", "path", "nolinkurl" };

    /// <summary>Decodes LaTeX to Unicode: accents, special letters, escapes, dashes, ~ (no-break space), \textit/\emph → *x*,
    /// \textbf → **x**, subscripts → ~x~, superscripts → ^x^; removes protective braces and collapses whitespace.</summary>
    public static string Decode(string? latex)
    {
        if (string.IsNullOrEmpty(latex)) return "";
        var sb = new StringBuilder(latex.Length);
        try
        {
            DecodeInto(latex, 0, latex.Length, false, sb);
        }
        catch (Exception)
        {
            // Never throw on user input: fall back to a brace-stripped copy.
            sb.Clear().Append(latex.Replace("{", "", StringComparison.Ordinal).Replace("}", "", StringComparison.Ordinal));
        }
        return Whitespace.Replace(sb.ToString(), " ").Trim(' ');
    }

    /// <summary>For url/doi/file fields: removes braces and the backslash of escaped characters (\_ \% \& \# \$ \~), nothing else.</summary>
    public static string DecodeVerbatim(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        var sb = new StringBuilder(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (c == '\\' && i + 1 < value.Length && "_%&#$~{}".Contains(value[i + 1], StringComparison.Ordinal))
            {
                sb.Append(value[++i]);
                continue;
            }
            if (c is '{' or '}') continue;
            sb.Append(c);
        }
        return Whitespace.Replace(sb.ToString(), " ").Trim();
    }

    private static int MatchBrace(string s, int open, int end)
    {
        var depth = 0;
        for (var i = open; i < end; i++)
        {
            var c = s[i];
            if (c == '\\') { i++; continue; }
            if (c == '{') depth++;
            else if (c == '}' && --depth == 0) return i;
        }
        return -1;
    }

    /// <summary>Reads one TeX argument at <paramref name="i"/> (skipping spaces): a braced group, a control sequence, or one character.</summary>
    private static (int start, int length, int next) ReadArg(string s, int i, int end)
    {
        while (i < end && s[i] == ' ') i++;
        if (i >= end) return (i, 0, i);
        if (s[i] == '{')
        {
            var close = MatchBrace(s, i, end);
            if (close < 0) return (i + 1, end - i - 1, end);
            return (i + 1, close - i - 1, close + 1);
        }
        if (s[i] == '\\' && i + 1 < end)
        {
            var j = i + 1;
            if (char.IsAsciiLetter(s[j]))
            {
                while (j < end && char.IsAsciiLetter(s[j])) j++;
            }
            else j++;
            return (i, j - i, j);
        }
        return (i, 1, i + 1);
    }

    private static string Sub(string s, int start, int length, bool math)
    {
        var sb = new StringBuilder();
        DecodeInto(s, start, start + length, math, sb);
        return sb.ToString();
    }

    private static string ApplyAccent(string decoded, char mark)
    {
        if (decoded.Length == 0) return mark.ToString();
        var first = decoded[0] switch { 'ı' => 'i', 'ȷ' => 'j', var c => c };
        return (first.ToString() + mark).Normalize(NormalizationForm.FormC) + decoded[1..];
    }

    private static void DecodeInto(string s, int i, int end, bool math, StringBuilder sb)
    {
        while (i < end)
        {
            var c = s[i];
            switch (c)
            {
                case '{':
                {
                    var close = MatchBrace(s, i, end);
                    var innerEnd = close < 0 ? end : close;
                    var inner = s[(i + 1)..innerEnd];
                    var mi = SwitchItalic.Match(inner);
                    var mb = SwitchBold.Match(inner);
                    var mo = SwitchOther.Match(inner);
                    if (mi.Success) sb.Append('*').Append(Sub(inner, mi.Length, inner.Length - mi.Length, math).Trim()).Append('*');
                    else if (mb.Success) sb.Append("**").Append(Sub(inner, mb.Length, inner.Length - mb.Length, math).Trim()).Append("**");
                    else if (mo.Success) DecodeInto(inner, mo.Length, inner.Length, math, sb);
                    else DecodeInto(s, i + 1, innerEnd, math, sb);
                    i = close < 0 ? end : close + 1;
                    break;
                }
                case '}':
                    i++;
                    break;
                case '$':
                {
                    if (math) { i++; break; }
                    var j = i + 1;
                    while (j < end && !(s[j] == '$' && s[j - 1] != '\\')) j++;
                    if (j >= end)
                    {
                        sb.Append('$');
                        i++;
                        break;
                    }
                    DecodeInto(s, i + 1, j, true, sb);
                    i = j + 1;
                    break;
                }
                case '_' or '^' when math:
                {
                    var (st, len, next) = ReadArg(s, i + 1, end);
                    var arg = Sub(s, st, len, true).Trim();
                    if (c == '^' && arg == "°") sb.Append('°');
                    else if (arg.Length > 0) sb.Append(c == '_' ? '~' : '^').Append(arg).Append(c == '_' ? '~' : '^');
                    i = next;
                    break;
                }
                case '\\':
                    i = Command(s, i, end, math, sb);
                    break;
                case '-' when !math:
                {
                    var j = i;
                    while (j < end && s[j] == '-') j++;
                    var run = j - i;
                    sb.Append(run >= 3 ? "—" : run == 2 ? "–" : "-");
                    if (run > 3) sb.Append('-', run - 3);
                    i = j;
                    break;
                }
                case '~':
                    sb.Append(' ');
                    i++;
                    break;
                case '`' when i + 1 < end && s[i + 1] == '`':
                    sb.Append('“');
                    i += 2;
                    break;
                case '\'' when i + 1 < end && s[i + 1] == '\'':
                    sb.Append('”');
                    i += 2;
                    break;
                default:
                    sb.Append(char.IsWhiteSpace(c) ? ' ' : c);
                    i++;
                    break;
            }
        }
    }

    private static int SkipSpacesAfterControlWord(string s, int i, int end)
    {
        while (i < end && (s[i] == ' ' || s[i] == '\t' || s[i] == '\r' || s[i] == '\n')) i++;
        return i;
    }

    /// <summary>Handles a backslash command at <paramref name="i"/>; returns the index after it.</summary>
    private static int Command(string s, int i, int end, bool math, StringBuilder sb)
    {
        if (i + 1 >= end)
        {
            sb.Append('\\');
            return end;
        }
        var c = s[i + 1];
        if (AccentMarks.TryGetValue(c, out var mark))
        {
            var (st, len, next) = ReadArg(s, i + 2, end);
            sb.Append(ApplyAccent(Sub(s, st, len, math), mark));
            return next;
        }
        if (!char.IsAsciiLetter(c))
        {
            switch (c)
            {
                case '&' or '%' or '_' or '$' or '#' or '{' or '}':
                    sb.Append(c);
                    break;
                case ',':
                    sb.Append(' ');
                    break;
                case ' ' or ';' or ':' or '\\' or '\n' or '\r':
                    sb.Append(' ');
                    break;
                case '-' or '/' or '@' or '!':
                    break;
                default:
                    sb.Append(c);
                    break;
            }
            return i + 2;
        }

        var j = i + 1;
        while (j < end && char.IsAsciiLetter(s[j])) j++;
        var name = s[(i + 1)..j];

        if (LetterAccents.TryGetValue(name, out var lmark))
        {
            var (st, len, next) = ReadArg(s, j, end);
            sb.Append(ApplyAccent(Sub(s, st, len, math), lmark));
            return next;
        }
        if (Symbols.TryGetValue(name, out var sym))
        {
            sb.Append(sym);
            var k = SkipSpacesAfterControlWord(s, j, end);
            if (k + 1 < end && s[k] == '{' && s[k + 1] == '}') k += 2;
            // Keep a space when the control word ended a word, e.g. "Stra\ss e" → "Straße" but "\o rest" → "ørest" is TeX behaviour.
            return k;
        }
        if (ItalicCommands.Contains(name) || BoldCommands.Contains(name) || name is "textsubscript" or "textsuperscript")
        {
            var (st, len, next) = ReadArg(s, j, end);
            var inner = Sub(s, st, len, math).Trim();
            var marker = ItalicCommands.Contains(name) ? "*" : BoldCommands.Contains(name) ? "**" : name == "textsubscript" ? "~" : "^";
            if (inner.Length > 0) sb.Append(marker).Append(inner).Append(marker);
            return next;
        }
        if (RawArgCommands.Contains(name))
        {
            var (st, len, next) = ReadArg(s, j, end);
            sb.Append(DecodeVerbatim(s.Substring(st, len)));
            return next;
        }
        if (name == "href")
        {
            var (_, _, afterUrl) = ReadArg(s, j, end);
            var (st, len, next) = ReadArg(s, afterUrl, end);
            DecodeInto(s, st, st + len, math, sb);
            return next;
        }
        // Unknown command (\textsc, \mbox, \NoCaseChange, \mathrm…): drop the name and keep any braced argument's text.
        var after = SkipSpacesAfterControlWord(s, j, end);
        if (after < end && s[after] == '{')
        {
            var (st, len, next) = ReadArg(s, after, end);
            DecodeInto(s, st, st + len, math, sb);
            return next;
        }
        return after;
    }
}
