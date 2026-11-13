using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace TextFixer.Native;

// CF_HTML is UTF-8 with byte offsets, not .NET character offsets. No HTML is executed.
internal sealed partial record HtmlClipboard(string Prefix, string Fragment, string Suffix, string? SourceUrl = null)
{
    internal static HtmlClipboard? Parse(string value)
    {
        if (value.Length > 2_000_000) return null;
        var bytes = Encoding.UTF8.GetBytes(value);
        int Offset(string name)
        {
            var match = Regex.Match(value, "(?m)^" + name + @":\s*(\d+)");
            return match.Success && int.TryParse(match.Groups[1].Value, out var offset) ? offset : -1;
        }
        var start = Offset("StartHTML"); var end = Offset("EndHTML");
        var fragmentStart = Offset("StartFragment"); var fragmentEnd = Offset("EndFragment");
        if (start < 0 || end > bytes.Length || fragmentStart < start || fragmentEnd < fragmentStart || end < fragmentEnd) return null;
        try
        {
            var utf8 = new UTF8Encoding(false, true);
            var header = utf8.GetString(bytes, 0, start);
            var sourceUrl = Regex.Match(header, @"(?m)^SourceURL:([^\r\n]*)");
            return new HtmlClipboard(utf8.GetString(bytes, start, fragmentStart - start),
                utf8.GetString(bytes, fragmentStart, fragmentEnd - fragmentStart),
                utf8.GetString(bytes, fragmentEnd, end - fragmentEnd), sourceUrl.Success ? sourceUrl.Groups[1].Value : null);
        }
        catch (DecoderFallbackException) { return null; }
    }

    internal string Encode()
    {
        const string template = "Version:1.0\r\nStartHTML:{0:D10}\r\nEndHTML:{1:D10}\r\nStartFragment:{2:D10}\r\nEndFragment:{3:D10}\r\n";
        if (SourceUrl?.IndexOfAny(['\r', '\n']) >= 0) throw CannotMap();
        var extraHeader = SourceUrl is null ? "" : "SourceURL:" + SourceUrl + "\r\n";
        var start = Encoding.UTF8.GetByteCount(string.Format(System.Globalization.CultureInfo.InvariantCulture, template, 0, 0, 0, 0) + extraHeader);
        var fragmentStart = start + Encoding.UTF8.GetByteCount(Prefix);
        var fragmentEnd = fragmentStart + Encoding.UTF8.GetByteCount(Fragment);
        var end = fragmentEnd + Encoding.UTF8.GetByteCount(Suffix);
        return string.Format(System.Globalization.CultureInfo.InvariantCulture, template, start, end, fragmentStart, fragmentEnd) + extraHeader + Prefix + Fragment + Suffix;
    }

    private sealed record Unit(char Character, int Start, int End, int Token);

    internal string Rewrite(string original, EditorEdit[] edits)
    {
        if (edits.Length == 0) return Encode();
        var units = ReadTextUnits(Fragment);
        var sourcePositions = Enumerable.Range(0, original.Length).Where(i => !char.IsWhiteSpace(original[i])).ToArray();
        var htmlPositions = Enumerable.Range(0, units.Count).Where(i => !char.IsWhiteSpace(units[i].Character)).ToArray();
        if (sourcePositions.Length != htmlPositions.Length) throw CannotMap();
        var map = Enumerable.Repeat(-1, original.Length).ToArray();
        for (var i = 0; i < sourcePositions.Length; i++)
        {
            if (original[sourcePositions[i]] != units[htmlPositions[i]].Character) throw CannotMap();
            map[sourcePositions[i]] = htmlPositions[i];
        }
        // Associate literal whitespace only inside the same HTML text token. Paragraph separators
        // have no such mapping and can never be accidentally consumed by a text correction.
        foreach (var position in sourcePositions)
        {
            foreach (var direction in new[] { -1, 1 })
            {
                var source = position + direction; var html = map[position] + direction;
                while (source >= 0 && source < original.Length && html >= 0 && html < units.Count
                    && char.IsWhiteSpace(original[source]) && original[source] is not ('\r' or '\n')
                    && units[html].Token == units[map[position]].Token && Equivalent(original[source], units[html].Character))
                {
                    // Collapsed spaces can have more than one raw representation. Keep those
                    // positions unmappable without rejecting unrelated word corrections.
                    if (map[source] == -2 || (map[source] != -1 && map[source] != html)) map[source] = -2;
                    else map[source] = html;
                    source += direction; html += direction;
                }
            }
        }
        var patches = new List<(int Start, int End, string After)>();
        foreach (var edit in edits)
        {
            if (edit.Start < 0 || edit.End < edit.Start || edit.End > original.Length || original[edit.Start..edit.End] != edit.Before
                || (edit.Before + edit.After).IndexOfAny(['\r', '\n']) >= 0) throw CannotMap();
            int start, end;
            if (edit.Start == edit.End)
            {
                var previous = edit.Start > 0 ? map[edit.Start - 1] : -1;
                var next = edit.Start < map.Length ? map[edit.Start] : -1;
                if (previous >= 0) start = end = units[previous].End;
                else if (next >= 0) start = end = units[next].Start;
                else throw CannotMap();
            }
            else
            {
                var first = map[edit.Start]; var last = map[edit.End - 1];
                if (first < 0 || last < first || units[first].Token != units[last].Token) throw CannotMap();
                for (var i = edit.Start; i < edit.End; i++)
                    if (map[i] != first + i - edit.Start) throw CannotMap();
                start = units[first].Start; end = units[last].End;
                if (Normalize(WebUtility.HtmlDecode(Fragment[start..end])) != Normalize(edit.Before)) throw CannotMap();
            }
            patches.Add((start, end, WebUtility.HtmlEncode(edit.After)));
        }
        var ordered = patches.OrderBy(p => p.Start).ToArray();
        for (var i = 1; i < ordered.Length; i++) if (ordered[i].Start < ordered[i - 1].End) throw CannotMap();
        var fragment = new StringBuilder(Fragment);
        foreach (var patch in ordered.Reverse()) fragment.Remove(patch.Start, patch.End - patch.Start).Insert(patch.Start, patch.After);
        return (this with { Fragment = fragment.ToString() }).Encode();
    }

    private static string Normalize(string text) => text.Replace('\u00a0', ' ');
    private static bool Equivalent(char left, char right) => (left == '\u00a0' ? ' ' : left) == (right == '\u00a0' ? ' ' : right);
    private static InvalidOperationException CannotMap() => new("The corrections cannot be mapped safely to the copied formatting. Omit the affected correction, use the Chrome extension, or select the plain-text option for copy or paste.");

    private static List<Unit> ReadTextUnits(string html)
    {
        var result = new List<Unit>();
        var position = 0; var token = 0; string? skip = null;
        while (position < html.Length)
        {
            if (html.AsSpan(position).StartsWith("<!--", StringComparison.Ordinal))
            {
                var end = html.IndexOf("-->", position + 4, StringComparison.Ordinal);
                if (end < 0) throw CannotMap(); position = end + 3; continue;
            }
            if (html[position] == '<' && position + 1 < html.Length && (char.IsAsciiLetter(html[position + 1]) || html[position + 1] is '/' or '!' or '?'))
            {
                var start = position++; char quote = '\0';
                while (position < html.Length)
                {
                    var c = html[position++];
                    if (quote != '\0') { if (c == quote) quote = '\0'; }
                    else if (c is '\'' or '"') quote = c;
                    else if (c == '>') break;
                }
                if (position > html.Length || html[position - 1] != '>') throw CannotMap();
                var tag = Regex.Match(html[start..position], @"^<\s*(/?)\s*([A-Za-z0-9]+)");
                if (tag.Success)
                {
                    var name = tag.Groups[2].Value.ToLowerInvariant(); var closing = tag.Groups[1].Value == "/";
                    if (closing && name == skip) skip = null;
                    else if (!closing && name is "style" or "script" or "noscript") skip = name;
                }
                token++; continue;
            }
            var textStart = position++;
            while (position < html.Length && html[position] != '<') position++;
            if (skip is not null) continue;
            var textEnd = position;
            for (var i = textStart; i < textEnd;)
            {
                var end = i + 1; string decoded = html[i].ToString();
                if (html[i] == '&')
                {
                    var semicolon = html.IndexOf(';', i + 1, Math.Min(40, textEnd - i - 1));
                    if (semicolon >= 0)
                    {
                        var entity = html[i..(semicolon + 1)]; var candidate = WebUtility.HtmlDecode(entity);
                        if (candidate != entity) { decoded = candidate; end = semicolon + 1; }
                    }
                }
                foreach (var c in decoded)
                {
                    if (result.Count >= 100000) throw CannotMap();
                    result.Add(new Unit(c, i, end, token));
                }
                i = end;
            }
            token++;
        }
        return result;
    }
}

