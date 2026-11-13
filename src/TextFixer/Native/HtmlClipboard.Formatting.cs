using System.Net;
using System.Text.RegularExpressions;

namespace TextFixer.Native;

internal sealed record FormattingSuggestion(int Id, int Start, int End, string Description, string Context, string Property, int RunStart, int RunEnd);

internal sealed partial record HtmlClipboard
{
    // Deliberately limited to simple explicit runs. Ambiguous CSS and nested runs are ignored.
    internal FormattingSuggestion[] FindFormatting(string original)
    {
        if (Regex.IsMatch(Prefix + Fragment, @"<style\b|\bclass\s*=|text-decoration|\b(?:display|float|position)\s*:", RegexOptions.IgnoreCase)
            || Regex.IsMatch(Prefix, @"<(?:u|ins|s|strike|del|a)\b", RegexOptions.IgnoreCase)) return [];
        // Require the copied HTML text to agree with the captured text before offering fixes.
        if (Fragment.Contains("<!--", StringComparison.Ordinal) || Regex.IsMatch(Fragment, @"<(?:script|noscript)\b|\bhidden\b|display\s*:\s*none", RegexOptions.IgnoreCase)) return [];
        var decodedFragment = WebUtility.HtmlDecode(Regex.Replace(Fragment, @"<[^>]*>", ""));
        static string Letters(string value) => new(value.Where(c => !char.IsWhiteSpace(c)).ToArray());
        if (Letters(decodedFragment) != Letters(original)) return [];
        var found = new List<FormattingSuggestion>();
        foreach (Match m in Regex.Matches(Fragment, @"<(?<tag>u|s|strike|del|a)\b(?<attrs>[^<>]*)>(?<text>[^<>]+)</\k<tag>\s*>", RegexOptions.IgnoreCase))
        {
            var tag = m.Groups["tag"].Value.ToLowerInvariant();
            var attrs = m.Groups["attrs"].Value;
            if (tag == "a" ? !Regex.IsMatch(attrs, @"^\s+href\s*=\s*(?:""[^""]*""|'[^']*')\s*$", RegexOptions.IgnoreCase) : !string.IsNullOrWhiteSpace(attrs)) continue;
            // Any decorating ancestor makes moving the space out of this tag insufficient.
            var stack = new List<string>();
            foreach (Match t in Regex.Matches(Fragment[..m.Index], @"<(?<close>/)?(?<name>[a-z][a-z0-9]*)\b[^>]*>", RegexOptions.IgnoreCase))
            {
                var name = t.Groups["name"].Value.ToLowerInvariant();
                if (t.Groups["close"].Success) { if (stack.Count > 0 && stack[^1] == name) stack.RemoveAt(stack.Count - 1); else { stack.Add("unknown"); break; } }
                else if (name is not ("br" or "img" or "hr" or "meta" or "link")) stack.Add(name);
            }
            if (stack.Any(x => x is not ("p" or "div" or "span" or "b" or "strong" or "i" or "em" or "body" or "html"))) continue;
            var text = WebUtility.HtmlDecode(m.Groups["text"].Value);
            if (text.Length < 2 || text.Contains('\t') || text.Contains('\r') || text.Contains('\n')) continue;
            var leading = text[0] is ' ' or '\u00a0'; var trailing = text[^1] is ' ' or '\u00a0';
            if (leading == trailing) continue;
            if (leading && char.IsWhiteSpace(text[1]) || trailing && char.IsWhiteSpace(text[^2])) continue;
            // Require an adjacent, visibly unformatted character on the affected side.
            var neighbour = leading ? m.Index - 1 : m.Index + m.Length;
            if (neighbour < 0 || neighbour >= Fragment.Length || Fragment[neighbour] is '<' or '>' or '&' || char.IsWhiteSpace(Fragment[neighbour])) continue;
            var start = original.IndexOf(text, StringComparison.Ordinal);
            if (start < 0 || original.IndexOf(text, start + 1, StringComparison.Ordinal) >= 0) continue;
            var at = start + (leading ? 0 : text.Length - 1);
            var property = tag == "a" ? "link" : tag == "u" ? "underline" : "strikethrough";
            found.Add(new(m.Index, at, at + 1, $"Remove {property} from the {(leading ? "leading" : "trailing")} space", text.Replace('\u00a0', ' ').Replace(" ", "\u00b7"), property, start, start + text.Length));
        }
        return found.ToArray();
    }

    internal HtmlClipboard FixFormatting(string original, int[] ids, EditorEdit[] edits)
    {
        var suggestions = FindFormatting(original).ToDictionary(x => x.Id);
        var fragment = Fragment;
        foreach (var id in ids.Distinct().OrderDescending())
        {
            if (!suggestions.TryGetValue(id, out var suggestion)) throw new InvalidOperationException("Formatting suggestions changed. Capture and check again.");
            if (edits.Any(e => e.Start <= suggestion.End && e.End >= suggestion.Start))
                throw new InvalidOperationException("A text correction touches this formatting suggestion. Reject one of them before applying.");
            var m = Regex.Match(fragment[id..], @"^<(?<tag>u|s|strike|del|a)\b[^<>]*>(?<text>[^<>]+)(?<close></\k<tag>\s*>)", RegexOptions.IgnoreCase);
            if (!m.Success) throw new InvalidOperationException("Formatting could not be changed safely.");
            var content = m.Groups["text"]; var decoded = WebUtility.HtmlDecode(content.Value);
            var leading = decoded[0] is ' ' or '\u00a0';
            var space = leading ? Regex.Match(content.Value, @"^(?: |\u00a0|&nbsp;|&#160;|&#x[aA]0;)") : Regex.Match(content.Value, @"(?: |\u00a0|&nbsp;|&#160;|&#x[aA]0;)$");
            if (!space.Success) throw new InvalidOperationException("This whitespace encoding is not supported for formatting fixes.");
            var opening = m.Value[..content.Index]; var closing = m.Groups["close"].Value;
            var body = content.Value.Remove(space.Index, space.Length);
            var replacement = leading ? space.Value + opening + body + closing : opening + body + closing + space.Value;
            fragment = fragment[..id] + replacement + fragment[(id + m.Length)..];
        }
        return this with { Fragment = fragment };
    }
}
