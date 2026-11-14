using System.Text.RegularExpressions;

namespace TextFixer.Services;

/// <summary>
/// Markup written as plain text: Markdown, BBCode, HTML tags, Reddit and Discord syntax, URLs, hashtags and
/// mentions. A correction may change the words between the markup but never the markup itself, so a pasted or copied
/// revision keeps its formatting. The text is the copied plain text; a correction that touches markup is refused
/// with a message that names it, nothing is dropped silently.
/// </summary>
internal static partial class MarkupGuard
{
    // Whole spans: their content is code, an address or an object, not prose.
    [GeneratedRegex(@"(?ms)^[ \t]*(```|~~~).*?^[ \t]*\1[ \t]*$|`[^`\n]+`|!\[[^\]\n]*\]\([^)\n]*\)|<https?://[^>\s]+>|https?://[^\s<>\[\]()]+|\[(url|img|code|email|youtube|video|media)\][^\[]*\[/\2\]|<[@#][!&]?\d+>|<a?:\w+:\d+>|(?<![\w&])[#@][\p{L}\p{N}_][\p{L}\p{N}_.]*", RegexOptions.IgnoreCase, 500)]
    private static partial Regex WholeSpans();

    // Markers around prose: only the marker characters are protected.
    [GeneratedRegex(@"(?m)^[ \t]*(#{1,6}[ \t]|>+[ \t]?|[-*+][ \t]|\d{1,3}[.)][ \t])|^[ \t]*(=+|-{2,}|\*{3,}|_{3,})[ \t]*$|\*{1,3}(?=\S)|(?<=\S)\*{1,3}|(?<![\p{L}\p{N}_])_{1,3}(?=\S)|(?<=\S)_{1,3}(?![\p{L}\p{N}_])|~~|>!|!<|\|\||\\[\\`*_{}\[\]()#+\-.!>~|]|</?[A-Za-z][^>\n]*>|\[/?[A-Za-z*][A-Za-z0-9]*(=[^\]\n]*)?\]|\]\([^)\n]*\)|\]\[[^\]\n]*\]", RegexOptions.None, 500)]
    private static partial Regex Markers();

    // A Markdown link or reference keeps its "[" too; "](url)" is matched above.
    [GeneratedRegex(@"\[(?=[^\]\n]*\](\(|\[))", RegexOptions.None, 500)]
    private static partial Regex LinkOpen();

    /// <summary>The protected character ranges of <paramref name="text"/>, sorted and merged.</summary>
    internal static List<(int Start, int End)> Protected(string text)
    {
        var ranges = new List<(int Start, int End)>();
        try
        {
            foreach (var regex in new[] { WholeSpans(), Markers(), LinkOpen() })
                foreach (Match match in regex.Matches(text))
                {
                    if (match.Length == 0) continue;
                    // Line-start markers: protect the marker, not the indentation.
                    var start = match.Index; var end = match.Index + match.Length;
                    while (start < end && text[start] is ' ' or '\t') start++;
                    if (start < end) ranges.Add((start, end));
                }
        }
        catch (RegexMatchTimeoutException) { return []; }
        ranges.Sort();
        var merged = new List<(int Start, int End)>();
        foreach (var range in ranges)
            if (merged.Count > 0 && range.Start <= merged[^1].End) merged[^1] = (merged[^1].Start, Math.Max(merged[^1].End, range.End));
            else merged.Add(range);
        return merged;
    }

    /// <summary>The first correction that would change markup, or null when all leave it intact.</summary>
    internal static EditorEdit? FirstConflict(string text, IEnumerable<EditorEdit> edits)
    {
        var ranges = Protected(text);
        if (ranges.Count == 0) return null;
        foreach (var edit in edits)
            foreach (var (start, end) in ranges)
            {
                var touches = edit.Start == edit.End ? start < edit.Start && edit.Start < end : edit.Start < end && edit.End > start;
                if (touches) return edit;
            }
        return null;
    }

    /// <summary>Throws when a correction would change markup.</summary>
    internal static void Check(string text, IEnumerable<EditorEdit> edits)
    {
        if (FirstConflict(text, edits) is { } edit)
            throw new InvalidOperationException($"The correction of \"{Shorten(edit.Before)}\" would change formatting markup, a link or a tag in the text. Uncheck it, or edit that spot by hand.");
    }

    private static string Shorten(string text) => text.Length <= 40 ? text : text[..37] + "...";
}
