using System.Globalization;
using System.Text;

namespace TextFixer.Native;

/// <summary>
/// Writes accepted corrections into copied RTF (Word, Outlook, WordPad, rich text fields), the RTF counterpart of
/// <see cref="HtmlClipboard.Rewrite"/>. The RTF is read as tokens: groups, control words, escapes and text. Hidden
/// groups (font and color tables, style sheets, document info, pictures, field codes, "\*" destinations) are
/// skipped; the visible characters are aligned with the plain text of the same copy, and a correction replaces only
/// characters of one text run. Formatting tokens, escapes elsewhere and everything hidden stay byte for byte. When
/// the visible text does not match the plain text, or a correction crosses a formatting change, the RTF is not
/// changed and the caller pastes without it.
/// </summary>
internal static class RtfClipboard
{
    /// <summary>A visible character and the RTF source it came from. Token numbers change at every formatting
    /// boundary, so one token is one run of equally formatted text. Uc is the \uc value there; AfterWord is true when
    /// the character directly follows a control word without the delimiting space. Optional characters are list numbers
    /// and bullets, which some apps put into the plain text and others leave out.</summary>
    private sealed record Unit(char Character, int Start, int End, int Token, int Uc, bool AfterWord, bool Optional = false);

    // Groups whose content is not visible text when this control word starts them.
    private static readonly HashSet<string> HiddenDestinations = new(StringComparer.Ordinal)
    {
        "fonttbl", "colortbl", "stylesheet", "info", "pict", "object", "objdata", "fldinst", "datastore", "themedata",
        "colorschememapping", "latentstyles", "listtable", "listoverridetable", "rsidtbl", "generator", "xmlnsdecls",
        "mmathPr", "filetbl", "revtbl", "pgdsctbl", "header", "headerl", "headerr", "headerf", "footer", "footerl",
        "footerr", "footerf", "footnote", "annotation", "atnid", "atnauthor", "xe", "tc", "txe", "bkmkstart", "bkmkend",
        "nonshppict", "shpinst", "sp", "sn", "sv", "userprops", "docvar", "wgrffmtfilter", "passwordhash", "pnseclvl",
    };

    // Control words that stand for one visible character.
    private static readonly Dictionary<string, char> Symbols = new(StringComparer.Ordinal)
    {
        ["emdash"] = '—', ["endash"] = '–', ["emspace"] = ' ', ["enspace"] = ' ', ["qmspace"] = ' ',
        ["bullet"] = '•', ["lquote"] = '‘', ["rquote"] = '’', ["ldblquote"] = '“', ["rdblquote"] = '”',
    };

    // Control words that end a line or cell; they become a line break that no correction may cross.
    private static readonly HashSet<string> Breaks = new(StringComparer.Ordinal) { "par", "line", "sect", "page", "row", "cell", "nestcell", "column" };

    static RtfClipboard() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    /// <summary>The RTF with the corrections, or null when they cannot be placed safely.</summary>
    internal static string? TryRewrite(string? rtf, string original, EditorEdit[] edits)
    {
        if (rtf is null) return null;
        try { return Rewrite(rtf, original, edits); }
        catch (InvalidOperationException) { return null; }
    }

    internal static string Rewrite(string rtf, string original, EditorEdit[] edits)
    {
        if (!rtf.StartsWith("{\\rtf", StringComparison.Ordinal) || rtf.Length > 8_000_000) throw CannotMap();
        if (edits.Length == 0) return rtf;
        var units = ReadUnits(rtf);
        int[] map;
        try { map = Align(original, units); }
        catch (InvalidOperationException) when (units.Any(u => u.Optional))
        {
            // The plain text has no list numbers: align without them; their units keep their indexes for the patches.
            map = Align(original, units, skipOptional: true);
        }
        var patches = new List<(int Start, int End, string After)>();
        foreach (var whole in edits)
        {
            if (whole.Start < 0 || whole.End < whole.Start || whole.End > original.Length || original[whole.Start..whole.End] != whole.Before
                || (whole.Before + whole.After).IndexOfAny(['\r', '\n']) >= 0) throw CannotMap();
            var edit = Narrow(whole);
            Unit at;
            int start, end;
            if (edit.Start == edit.End)
            {
                var previous = edit.Start > 0 ? map[edit.Start - 1] : -1;
                var next = edit.Start < map.Length ? map[edit.Start] : -1;
                if (previous >= 0)
                {
                    // After a character written as a control word without its space ("\lquote"), the same rule applies.
                    var source = rtf[units[previous].Start..units[previous].End];
                    var word = source.Length > 1 && source[0] == '\\' && char.IsAsciiLetter(source[1]) && char.IsAsciiLetterOrDigit(source[^1]);
                    start = end = units[previous].End;
                    at = units[previous] with { AfterWord = word, Start = start };
                }
                else if (next >= 0) { at = units[next]; start = end = at.Start; }
                else throw CannotMap();
            }
            else
            {
                var first = map[edit.Start]; var last = map[edit.End - 1];
                if (first < 0 || last < first || units[first].Token != units[last].Token) throw CannotMap();
                for (var i = edit.Start; i < edit.End; i++)
                    if (map[i] != first + i - edit.Start) throw CannotMap();
                at = units[first]; start = at.Start; end = units[last].End;
            }
            // A text right after a control word without a space would otherwise run into it: "\b0" + "5" is "\b05".
            patches.Add((start, end, (at.AfterWord && start == at.Start ? " " : "") + Encode(edit.After, at.Uc)));
        }
        var ordered = patches.OrderBy(p => p.Start).ToArray();
        for (var i = 1; i < ordered.Length; i++) if (ordered[i].Start < ordered[i - 1].End) throw CannotMap();
        var result = new StringBuilder(rtf);
        foreach (var patch in ordered.Reverse()) result.Remove(patch.Start, patch.End - patch.Start).Insert(patch.Start, patch.After);
        return result.ToString();
    }

    /// <summary>
    /// The correction without the characters it keeps at its start and end: "Häusern" to "Hauser" changes only
    /// "äusern", so a formatting change after "H" (rich edit fields switch fonts for some characters) does not matter.
    /// </summary>
    private static EditorEdit Narrow(EditorEdit edit)
    {
        int prefix = 0, suffix = 0;
        var before = edit.Before; var after = edit.After;
        while (prefix < before.Length && prefix < after.Length && before[prefix] == after[prefix] && !char.IsSurrogate(before[prefix])) prefix++;
        while (suffix < before.Length - prefix && suffix < after.Length - prefix && before[^(suffix + 1)] == after[^(suffix + 1)] && !char.IsSurrogate(before[^(suffix + 1)])) suffix++;
        if (prefix == 0 && suffix == 0) return edit;
        return new EditorEdit(edit.Start + prefix, edit.End - suffix, before[prefix..^suffix], after[prefix..^suffix]);
    }

    /// <summary>The clipboard bytes of an RTF text, null-terminated. RTF is ANSI text (Windows-1252 here).</summary>
    internal static byte[] Bytes(string rtf) => Encoding.GetEncoding(1252).GetBytes(rtf + "\0");

    /// <summary>The visible text of the RTF, for tests and diagnostics.</summary>
    internal static string VisibleText(string rtf) => new(ReadUnits(rtf).Select(u => u.Character).ToArray());

    /// <summary>
    /// Plain-text position to unit index. Characters that are not whitespace must match one to one; whitespace maps
    /// only inside the same run, so a correction never consumes a paragraph or cell break.
    /// </summary>
    private static int[] Align(string original, List<Unit> units, bool skipOptional = false)
    {
        static bool Counts(char c) => !char.IsWhiteSpace(c) && CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.Format;
        var sourcePositions = Enumerable.Range(0, original.Length).Where(i => Counts(original[i])).ToArray();
        var rtfPositions = Enumerable.Range(0, units.Count).Where(i => Counts(units[i].Character) && !(skipOptional && units[i].Optional)).ToArray();
        if (sourcePositions.Length != rtfPositions.Length) throw CannotMap();
        var map = Enumerable.Repeat(-1, original.Length).ToArray();
        for (var i = 0; i < sourcePositions.Length; i++)
        {
            if (!Same(original[sourcePositions[i]], units[rtfPositions[i]].Character)) throw CannotMap();
            map[sourcePositions[i]] = rtfPositions[i];
        }
        foreach (var position in sourcePositions)
            foreach (var direction in new[] { -1, 1 })
            {
                var source = position + direction; var unit = map[position] + direction;
                while (source >= 0 && source < original.Length && unit >= 0 && unit < units.Count
                    && char.IsWhiteSpace(original[source]) && original[source] is not ('\r' or '\n')
                    && units[unit].Token == units[map[position]].Token && Same(original[source], units[unit].Character))
                {
                    if (map[source] == -2 || (map[source] != -1 && map[source] != unit)) map[source] = -2;
                    else map[source] = unit;
                    source += direction; unit += direction;
                }
            }
        return map;
    }

    private static bool Same(char left, char right) => (left == ' ' ? ' ' : left) == (right == ' ' ? ' ' : right);

    /// <summary>Plain text as RTF: ASCII stays, "\", "{" and "}" are escaped, anything else is \uN with the
    /// replacement characters the current \uc expects.</summary>
    private static string Encode(string text, int uc)
    {
        var result = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (c is '\\' or '{' or '}') result.Append('\\').Append(c);
            else if (c is >= ' ' and <= '~') result.Append(c);
            else if (c == '\t') result.Append("\\tab ");
            else result.Append("\\u").Append(((int)c > 32767 ? c - 65536 : c).ToString(CultureInfo.InvariantCulture)).Append(uc > 0 ? new string('?', uc) : " ");
        }
        return result.ToString();
    }

    private struct State { public bool Hidden; public bool Optional; public int Uc; }

    private static List<Unit> ReadUnits(string rtf)
    {
        var units = new List<Unit>();
        var stack = new Stack<State>();
        var state = new State { Uc = 1 };
        var encoding = Encoding.GetEncoding(1252);
        int position = 0, token = 0, fallback = 0, fallbackUnit = -1;
        var groupStart = false; var afterWord = false;

        void Add(char c, int start, int end)
        {
            if (state.Hidden) return;
            if (units.Count >= 200_000) throw CannotMap();
            units.Add(new Unit(c, start, end, token, state.Uc, afterWord, state.Optional));
        }
        // The replacement characters after \uN belong to that character: they are skipped and widen its source.
        bool Fallback(int end)
        {
            if (fallback == 0) return false;
            fallback--;
            if (fallbackUnit >= 0) units[fallbackUnit] = units[fallbackUnit] with { End = end };
            return true;
        }

        while (position < rtf.Length)
        {
            var c = rtf[position];
            if (c == '{') { stack.Push(state); position++; token++; groupStart = true; fallback = 0; afterWord = false; continue; }
            if (c == '}')
            {
                if (stack.Count == 0) throw CannotMap();
                state = stack.Pop(); position++; token++; groupStart = false; fallback = 0; afterWord = false; continue;
            }
            if (c is '\r' or '\n') { position++; continue; }
            if (c != '\\')
            {
                groupStart = false;
                if (!Fallback(position + 1)) Add(c, position, position + 1);
                afterWord = false; position++; continue;
            }
            if (position + 1 >= rtf.Length) throw CannotMap();
            var next = rtf[position + 1];
            if (next == '\'')
            {
                if (position + 4 > rtf.Length || !byte.TryParse(rtf.AsSpan(position + 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value)) throw CannotMap();
                groupStart = false;
                if (!Fallback(position + 4))
                {
                    var decoded = encoding.GetString([value]);
                    if (decoded.Length != 1) throw CannotMap();
                    Add(decoded[0], position, position + 4);
                }
                afterWord = false; position += 4; continue;
            }
            if (next is '\\' or '{' or '}' or '~' or '_')
            {
                groupStart = false;
                if (!Fallback(position + 2)) Add(next switch { '~' => ' ', '_' => '‑', _ => next }, position, position + 2);
                afterWord = false; position += 2; continue;
            }
            if (next == '*') { state.Hidden = true; position += 2; continue; }
            if (!char.IsAsciiLetter(next))
            {
                // \- optional hyphen, \| \: and other symbols: no visible text, but a boundary.
                position += 2; token++; groupStart = false; afterWord = false; continue;
            }
            var wordStart = position + 1; var cursor = wordStart;
            while (cursor < rtf.Length && char.IsAsciiLetter(rtf[cursor]) && cursor - wordStart < 32) cursor++;
            var name = rtf[wordStart..cursor];
            int? parameter = null;
            var parameterStart = cursor;
            if (cursor < rtf.Length && (rtf[cursor] == '-' || char.IsAsciiDigit(rtf[cursor])))
            {
                cursor++;
                while (cursor < rtf.Length && char.IsAsciiDigit(rtf[cursor]) && cursor - parameterStart < 11) cursor++;
                if (!int.TryParse(rtf.AsSpan(parameterStart, cursor - parameterStart), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var number)) throw CannotMap();
                parameter = number;
            }
            var spaced = cursor < rtf.Length && rtf[cursor] == ' ';
            var end = spaced ? cursor + 1 : cursor;
            if (groupStart && HiddenDestinations.Contains(name)) state.Hidden = true;
            if (groupStart && name is "pntext" or "listtext") state.Optional = true;
            groupStart = false;
            switch (name)
            {
                case "u" when parameter is { } code:
                    var character = (char)(code < 0 ? code + 65536 : code);
                    if (!Fallback(end)) { Add(character, position, end); fallbackUnit = state.Hidden ? -1 : units.Count - 1; fallback = state.Uc; }
                    position = end; afterWord = !spaced; continue;
                case "uc" when parameter is >= 0 and <= 10:
                    state.Uc = parameter.Value; break;
                case "ansicpg" when parameter is { } codePage:
                    try { encoding = Encoding.GetEncoding(codePage); } catch (ArgumentException) { throw CannotMap(); } catch (NotSupportedException) { throw CannotMap(); }
                    if (!encoding.IsSingleByte) throw CannotMap();
                    break;
                case "bin" when parameter is >= 0:
                    if (end + parameter.Value > rtf.Length) throw CannotMap();
                    position = end + parameter.Value; token++; afterWord = false; continue;
                case "tab":
                    token++; Add('\t', position, end); token++; position = end; afterWord = false; continue;
                default:
                    if (Breaks.Contains(name)) { token++; Add('\n', position, end); token++; position = end; afterWord = false; continue; }
                    if (Symbols.TryGetValue(name, out var symbol))
                    {
                        if (!Fallback(end)) Add(symbol, position, end);
                        position = end; afterWord = !spaced; continue;
                    }
                    break;
            }
            // Any other control word changes formatting or document settings: it ends the current text run.
            token++; fallback = 0; position = end; afterWord = !spaced;
        }
        return units;
    }

    private static InvalidOperationException CannotMap() => new("The corrections cannot be mapped safely to the copied RTF formatting.");
}
