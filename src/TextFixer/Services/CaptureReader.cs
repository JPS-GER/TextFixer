using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace TextFixer.Services;

/// <summary>
/// An element the model must not change: a link, embedded video or image, mention, code, or an object such as a poll.
/// <see cref="Html"/> keeps the site's own markup where re-pasting it matters (mentions, polls).
/// </summary>
internal sealed record ProtectedItem(string Id, string Kind, string Label, string? Url = null, string? Html = null);

/// <summary>
/// The header of a forum post copied from the read view. It is forum page detail, not part of the post: author, date
/// and title are context for the tasks; <see cref="Texts"/> are all
/// texts of the header (avatar name, badges, stats), once per occurrence; they are page details, not part of the post.
/// </summary>
internal sealed record PostHeader(string? Author, string? Date, string? Title, IReadOnlyList<string> Stats, IReadOnlyList<string> Texts)
{
    /// <summary>"Post by name, date" for the window and as context for the model; null without author and date.</summary>
    internal string? Byline => (Author, Date) switch
    {
        ({ Length: > 0 } a, { Length: > 0 } d) => $"Post by {a}, {d}",
        ({ Length: > 0 } a, _) => $"Post by {a}",
        (_, { Length: > 0 } d) => $"Post from {d}",
        _ => null,
    };

    /// <summary>"Post by name, date. Title: ..." for tasks that read the text as context; null when nothing is known.</summary>
    internal string? ContextLine => string.Join(". ", new[] { Byline, Title is { Length: > 0 } t ? "Title: " + t : null }.OfType<string>()) is { Length: > 0 } line ? line : null;
}

/// <summary>
/// The captured HTML read as text for the model. <see cref="ModelText"/> uses the window's Markdown subset with quotes
/// as "> " lines and ⟦n⟧ placeholders for <see cref="Items"/> (Translate, Rewrite). <see cref="ContextText"/> spells the
/// items out (full link addresses, video titles) for tasks that only read the text. <see cref="ProtectedRanges"/> are
/// [start, end, kind] in the captured plain text that Review must leave alone: kind <see cref="ElementRange"/> for link
/// texts, code and mentions, <see cref="QuoteRange"/> for quotes.
/// </summary>
internal sealed record CaptureDocument(string ModelText, string ContextText, IReadOnlyList<ProtectedItem> Items, bool HasQuotes, IReadOnlyList<int[]> ProtectedRanges)
{
    internal const char Open = '\u27E6', Close = '\u27E7';
    internal const int ElementRange = 0, QuoteRange = 1;
    internal static string Token(string id) => $"{Open}{id}{Close}";
    internal bool Structured => Items.Count > 0 || HasQuotes || Post is not null;
    internal PostHeader? Post { get; init; }

    /// <summary>The quoted passages of <paramref name="plain"/>, the text this document was read with.</summary>
    internal List<string> Quoted(string plain) => [.. ProtectedRanges.Where(r => r[2] == QuoteRange && r[1] <= plain.Length).Select(r => plain[r[0]..r[1]])];

    /// <summary>Links with their full address, for tasks that read the plain text.</summary>
    internal List<string> Links => [.. Items.Where(i => i.Kind is "link" or "video" or "image" && i.Url is { Length: > 0 }).Select(CaptureReader.Describe).Distinct().Take(50)];
}

internal static partial class CaptureReader
{
    private static readonly HashSet<string> Skipped = new(StringComparer.OrdinalIgnoreCase) { "svg", "button", "style", "script", "head", "title", "template", "noscript" };
    private static readonly HashSet<string> Blocks = new(StringComparer.OrdinalIgnoreCase)
        { "p", "div", "li", "h1", "h2", "h3", "h4", "h5", "h6", "tr", "table", "section", "article", "header", "footer", "figure", "figcaption", "dd", "dt", "ul", "ol", "blockquote" };

    private sealed class Frame(string tag, string? close) { public string Tag = tag; public string? Close = close; }

    /// <summary>Reads a CF_HTML fragment. Returns null when it holds no readable text. No HTML is executed or loaded.</summary>
    internal static CaptureDocument? Read(string fragment, string plain)
    {
        if (fragment.Length > 2_000_000) return null;
        var items = new List<ProtectedItem>();
        var lines = new List<(int Depth, string Text, bool Blank)>();
        var segments = new List<(string Text, bool Element, bool Quote)>();
        var line = new StringBuilder();
        var stack = new List<Frame>();
        var lists = new List<(bool Ordered, int Next)>();
        var depth = 0; var skip = 0; var hasQuotes = false;
        // A forum post header is page detail: left out of the text; author, date and title are context only.
        var post = ReadHeader(fragment);
        var header = 0;
        // Page parts selected above the first post header (such as the "Beitrag" divider) are not part of the post.
        var beforeHeader = post is not null;
        StringBuilder? capture = null; string? captureKind = null; Dictionary<string, string>? captureAttributes = null; var captureTag = "";
        // Editors such as the M! Forum write one paragraph per line without spacing and an empty paragraph as the empty
        // line. There, paragraphs are lines and an empty paragraph separates them.
        var lineParagraphs = EmptyParagraphPattern().IsMatch(fragment);
        var paragraphStarts = new List<int>();
        string listPrefix = "";

        void Flush(bool blank)
        {
            var text = line.ToString().Trim();
            line.Clear();
            if (text.Length > 0) { lines.Add((depth, listPrefix + text, false)); listPrefix = ""; }
            if (blank && lines.Count > 0 && !lines[^1].Blank) lines.Add((depth, "", true));
        }
        string Add(string kind, string label, string? url = null, string? html = null)
        {
            var id = (items.Count + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
            items.Add(new ProtectedItem(id, kind, label, url, html));
            return CaptureDocument.Token(id);
        }
        void Text(string value, bool isProtected)
        {
            if (value.Length == 0) return;
            if (line.Length == 0 || char.IsWhiteSpace(line[^1])) value = value.TrimStart();
            line.Append(value);
            segments.Add((value, isProtected, depth > 0));
        }

        foreach (Match token in TokenPattern().Matches(fragment))
        {
            if (token.Groups["comment"].Success) continue;
            if (token.Groups["text"].Success)
            {
                var value = WebUtility.HtmlDecode(token.Value).Replace("​", "").Replace("﻿", "");
                if (skip > 0) continue;
                if (capture is not null) { capture.Append(value); continue; }
                if (header > 0 || beforeHeader) continue;
                value = captureKind == "pre" ? value : WhitespacePattern().Replace(value.Replace(' ', '\u0001'), " ").Replace('\u0001', ' ');
                Text(value, false);
                continue;
            }
            var closing = token.Groups["slash"].Value == "/";
            var tag = token.Groups["tag"].Value.ToLowerInvariant();
            var attributes = Attributes(token.Groups["attrs"].Value);
            var selfClosing = token.Groups["attrs"].Value.TrimEnd().EndsWith('/') || tag is "br" or "img" or "hr" or "wbr" or "input" or "meta" or "col" or "source";
            string? Attr(string name) => attributes.TryGetValue(name, out var v) ? v : null;
            var cls = Attr("class") ?? "";

            if (closing)
            {
                var index = stack.FindLastIndex(f => f.Tag == tag);
                if (index < 0) continue;
                for (var i = stack.Count - 1; i >= index; i--)
                {
                    var frame = stack[i]; stack.RemoveAt(i);
                    if (frame.Close == "skip") { skip--; continue; }
                    if (frame.Close == "header")
                    {
                        header--;
                        continue;
                    }
                    if (frame.Close == "capture" && capture is not null)
                    {
                        var captured = capture.ToString(); capture = null;
                        var kind = captureKind; captureKind = null;
                        var attrs = captureAttributes ?? []; captureAttributes = null;
                        if (kind == "link")
                        {
                            var label = WhitespacePattern().Replace(captured, " ").Trim();
                            var href = attrs.TryGetValue("href", out var h) ? h : "";
                            if (label.Length == 0 && href.Length == 0) continue;
                            line.Append(Add("link", label.Length > 0 ? label : href, href));
                            segments.Add((label, true, depth > 0));
                        }
                        else if (kind == "code")
                        {
                            line.Append(Add("code", captured));
                            segments.Add((captured, true, depth > 0));
                        }
                        else if (kind == "pre")
                        {
                            Flush(true);
                            line.Append(Add("codeblock", captured.TrimEnd('\r', '\n')));
                            segments.Add((captured, true, depth > 0));
                            Flush(true);
                        }
                        else if (kind == "mention")
                        {
                            var label = captured.Trim().Length > 0 ? captured.Trim() : "@" + (attrs.TryGetValue("data-label", out var l) ? l : "");
                            line.Append(Add("mention", label, null, captureTag));
                            segments.Add((label, true, depth > 0));
                        }
                        continue;
                    }
                    if (frame.Close == "quote") { Flush(true); depth--; if (lines.Count > 0 && lines[^1].Blank) lines[^1] = (depth, "", true); continue; }
                    if (frame.Close == "list") { Flush(false); if (lists.Count > 0) lists.RemoveAt(lists.Count - 1); if (lists.Count == 0) Flush(true); continue; }
                    if (frame.Close == "line")
                    {
                        var start = paragraphStarts[^1]; paragraphStarts.RemoveAt(paragraphStarts.Count - 1);
                        var empty = lines.Count == start && line.ToString().Trim().Length == 0;
                        Flush(false);
                        if (empty && lines.Count > 0 && !lines[^1].Blank) lines.Add((depth, "", true));
                        continue;
                    }
                    if (frame.Close == "block") { Flush(frame.Tag != "li" && frame.Tag != "tr" && frame.Tag != "td" && frame.Tag != "th"); continue; }
                    if (frame.Close == "cell") { line.Append(" | "); continue; }
                    if (frame.Close is { Length: > 0 } marker && marker != "none") line.Append(marker);
                }
                continue;
            }

            if (skip > 0) { if (!selfClosing) { stack.Add(new Frame(tag, "skip")); skip++; } continue; }
            if (capture is not null) { if (tag == "br") capture.Append('\n'); else if (!selfClosing) stack.Add(new Frame(tag, "none")); continue; }

            if (Attr("data-testid") == "post-header" && !selfClosing) { Flush(true); header++; beforeHeader = false; stack.Add(new Frame(tag, "header")); continue; }
            if (header > 0 || beforeHeader) { if (!selfClosing) stack.Add(new Frame(tag, "none")); continue; }
            if (Skipped.Contains(tag) || cls.Contains("ProseMirror-widget", StringComparison.Ordinal) || Attr("data-testid") == "image-card-caption")
            {
                if (!selfClosing) { stack.Add(new Frame(tag, "skip")); skip++; }
                continue;
            }
            switch (tag)
            {
                case "br":
                    if (!cls.Contains("ProseMirror-trailingBreak", StringComparison.Ordinal) && !cls.Contains("Apple-interchange-newline", StringComparison.Ordinal)) Flush(false);
                    continue;
                case "img":
                    if (cls.Contains("ProseMirror-separator", StringComparison.Ordinal)) continue;
                    var src = Attr("src") ?? "";
                    if (src.Length > 0) { line.Append(Add("image", src, src)); segments.Add((Attr("alt") ?? "", true, depth > 0)); }
                    continue;
                case "hr":
                    Flush(true); lines.Add((depth, "---", false)); Flush(true);
                    continue;
                case "iframe":
                case "video":
                    var videoUrl = VideoUrl(Attr("src") ?? "");
                    line.Append(Add("video", Attr("title") ?? videoUrl, videoUrl));
                    if (!selfClosing) { stack.Add(new Frame(tag, "skip")); skip++; }
                    continue;
            }
            if (selfClosing) continue;

            if (tag == "a" && Attr("href") is { Length: > 0 })
            { capture = new StringBuilder(); captureKind = "link"; captureAttributes = attributes; stack.Add(new Frame(tag, "capture")); continue; }
            if (tag == "pre")
            { Flush(true); capture = new StringBuilder(); captureKind = "pre"; stack.Add(new Frame(tag, "capture")); continue; }
            if (tag == "code")
            { capture = new StringBuilder(); captureKind = "code"; stack.Add(new Frame(tag, "capture")); continue; }
            if (tag == "mention-component" || Attr("data-mention") is not null || Attr("data-type") == "mention")
            { capture = new StringBuilder(); captureKind = "mention"; captureAttributes = attributes; captureTag = token.Value + "</" + tag + ">"; stack.Add(new Frame(tag, "capture")); continue; }
            if (Attr("data-poll-id") is not null)
            { line.Append(Add("poll", "Poll", null, token.Value + "</" + tag + ">")); stack.Add(new Frame(tag, "skip")); skip++; continue; }
            if (tag == "blockquote") { Flush(true); depth++; hasQuotes = true; stack.Add(new Frame(tag, "quote")); continue; }
            if (tag is "ul" or "ol")
            {
                Flush(lists.Count == 0);
                lists.Add((tag == "ol", int.TryParse(Attr("start"), out var start) && start > 0 ? start : 1));
                stack.Add(new Frame(tag, "list")); continue;
            }
            if (tag == "li")
            {
                Flush(false);
                var indent = new string(' ', Math.Max(0, lists.Count - 1) * 3);
                if (lists.Count > 0)
                {
                    var current = lists[^1];
                    listPrefix = indent + (current.Ordered ? current.Next + ". " : "- ");
                    if (current.Ordered) lists[^1] = (true, current.Next + 1);
                }
                stack.Add(new Frame(tag, "block")); continue;
            }
            if (tag is "td" or "th") { stack.Add(new Frame(tag, "cell")); continue; }
            if (tag is "h1" or "h2" or "h3" or "h4" or "h5" or "h6") { Flush(true); line.Append(new string('#', tag[1] - '0')).Append(' '); stack.Add(new Frame(tag, "block")); continue; }
            if (lineParagraphs && tag == "p") { Flush(false); paragraphStarts.Add(lines.Count); stack.Add(new Frame(tag, "line")); continue; }
            if (Blocks.Contains(tag)) { if (line.Length > 0) Flush(tag != "div"); stack.Add(new Frame(tag, "block")); continue; }

            // Inline marks in the Markdown subset; a mark opened before text still closes in order.
            var mark = tag switch
            {
                "b" or "strong" => "**",
                "i" or "em" => "*",
                "u" or "ins" => "__",
                "s" or "strike" or "del" => "~~",
                "spoiler" => "||",
                _ => Attr("data-spoiler") is not null || Attr("data-type") == "spoiler" ? "||" : null,
            };
            if (mark is not null) { line.Append(mark); stack.Add(new Frame(tag, mark)); }
            else stack.Add(new Frame(tag, "none"));
        }
        Flush(true);
        while (lines.Count > 0 && lines[^1].Blank) lines.RemoveAt(lines.Count - 1);
        if (lines.Count == 0) return null;

        string Render(Func<ProtectedItem, string> item)
        {
            var output = new StringBuilder();
            foreach (var (lineDepth, text, _) in lines)
            {
                var prefix = string.Concat(Enumerable.Repeat("> ", lineDepth)).TrimEnd();
                var body = TokenPattern2().Replace(text, m => items.FirstOrDefault(i => i.Id == m.Groups[1].Value) is { } found ? item(found) : m.Value);
                output.Append(body.Length > 0 ? (prefix.Length > 0 ? prefix + " " : "") + body : prefix).Append('\n');
            }
            return output.ToString().TrimEnd('\n');
        }
        var model = Render(i => CaptureDocument.Token(i.Id));
        var context = Render(Describe);
        if (post?.ContextLine is { } about) context = $"[{about}]\n\n{context}";
        return new CaptureDocument(model, context, items, hasQuotes, Align(plain, segments)) { Post = post };
    }

    /// <summary>
    /// The numbers next to the icons of a forum post header (for example readers, edits, read share), in order. They are
    /// page statistics, not part of the post.
    /// </summary>
    internal static List<string> HeaderStats(string fragment) => HeaderHtml(fragment) is { } header ? Stats(header) : [];

    private static List<string> Stats(string header) =>
        [.. StatPattern().Matches(header).Select(m => WebUtility.HtmlDecode(m.Groups[1].Value).Trim()).Where(v => v.Length is > 0 and < 20)];

    /// <summary>The markup of a forum post header (<c>data-testid="post-header"</c>) up to the post body.</summary>
    private static string? HeaderHtml(string fragment)
    {
        var start = HeaderStart(fragment);
        if (start < 0) return null;
        var end = fragment.IndexOf("id=\"editor\"", start, StringComparison.Ordinal);
        return fragment[start..(end > start ? end : Math.Min(fragment.Length, start + 20_000))];
    }

    private static int HeaderStart(string fragment)
    {
        var attribute = fragment.IndexOf("data-testid=\"post-header\"", StringComparison.Ordinal);
        return attribute < 0 ? -1 : Math.Max(0, fragment.LastIndexOf('<', attribute));
    }

    /// <summary>Reads the post header: title, author (name next to the avatar), date and stats. Null without a header.</summary>
    internal static PostHeader? ReadHeader(string fragment)
    {
        if (HeaderHtml(fragment) is not { } header) return null;
        static string? Group(Match m) => m.Success && WebUtility.HtmlDecode(m.Groups[1].Value).Replace("\u200B", "").Trim() is { Length: > 0 and < 200 } v ? v : null;
        var texts = new List<string>();
        // Header texts, and texts of short page parts selected above the header (a divider label). A longer part
        // above it is content, such as an earlier post, and stays.
        var above = fragment[..HeaderStart(fragment)];
        var aboveTexts = HeaderTextPattern().Matches(above).Select(Group).OfType<string>().ToList();
        if (HeaderTextPattern().Matches(above).Sum(m => m.Groups[1].Value.Trim().Length) <= 200) texts.AddRange(aboveTexts);
        foreach (Match m in HeaderTextPattern().Matches(header)) if (Group(m) is { } t) texts.Add(t);
        var author = Group(AuthorPattern().Match(header)) ?? Group(AvatarPattern().Match(header));
        // The avatar's alt text appears as a line of its own in the copied plain text.
        foreach (Match m in ImageAltPattern().Matches(header)) if (Group(m) is { } alt) texts.Add(alt);
        // A selection that starts inside the name cuts it; the avatar's alt text, when selected, holds the full name.
        if (author is not null && ImageAltPattern().Matches(header).Select(Group).OfType<string>().FirstOrDefault(a => a.Length > author.Length && a.EndsWith(author, StringComparison.Ordinal)) is { } full) author = full;
        return new PostHeader(author, Group(DatePattern().Match(header)), Group(TitlePattern().Match(header)), Stats(header), texts);
    }

    /// <summary>
    /// Removes the post header from a captured plain text: the leading lines that are header texts (avatar name,
    /// author, badges, date, title, stats). The text from the first other line on stays unchanged.
    /// </summary>
    internal static string WithoutHeader(string plain, PostHeader header)
    {
        // A selection can start anywhere in the header; the copy holds the same (possibly cut) texts as the header
        // markup, in order. Skip blank lines and header texts (title and stats included) up to the post text.
        var lines = Regex.Split(plain, @"(?<=\n)");
        static string Clean(string line) => line.Replace("\u200B", "").Trim();
        // Each header text is skipped as often as the header holds it, so a post line that equals one stays.
        var left = header.Texts.GroupBy(t => t, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        bool Take(string text) { if (!left.TryGetValue(text, out var n) || n == 0) return false; left[text] = n - 1; return true; }
        var i = 0;
        while (i < Math.Min(lines.Length, 40) && Clean(lines[i]) is var text && (text.Length == 0 || Take(text))) i++;
        return i == 0 ? plain : string.Concat(lines.Skip(i));
    }

    /// <summary>
    /// Removes header stats from a captured plain text: the lines that hold exactly these values, in this order, among
    /// the first lines. Returns the text unchanged when they are not found there.
    /// </summary>
    internal static string WithoutStats(string plain, IReadOnlyList<string> stats)
    {
        if (stats.Count == 0) return plain;
        var lines = Regex.Split(plain, @"(?<=\n)");
        for (var i = 0; i + stats.Count <= Math.Min(lines.Length, 8 + stats.Count); i++)
            if (stats.Select((s, k) => lines[i + k].Trim() == s).All(x => x))
                return string.Concat(lines.Take(i).Concat(lines.Skip(i + stats.Count)));
        return plain;
    }

    /// <summary>How an item reads for tasks that only read the text.</summary>
    internal static string Describe(ProtectedItem item) => item.Kind switch
    {
        "link" => item.Url is { Length: > 0 } url && !SameAddress(item.Label, url) ? $"{item.Label} ({url})" : item.Url ?? item.Label,
        "video" => $"[Video: {item.Label}, {item.Url}]",
        "image" => $"[Image: {item.Url}]",
        "code" => "`" + item.Label + "`",
        "codeblock" => "```\n" + item.Label + "\n```",
        "poll" => "[Poll]",
        _ => item.Label,
    };

    /// <summary>True when a visible link text is just the address, possibly shortened ("www.example.com/pa...").</summary>
    internal static bool SameAddress(string label, string url)
    {
        static string Bare(string s) => Regex.Replace(s.Trim(), @"^https?://", "", RegexOptions.IgnoreCase).TrimEnd('/');
        var visible = Bare(label);
        if (visible.EndsWith("...", StringComparison.Ordinal) || visible.EndsWith('…')) visible = visible.TrimEnd('.', '…');
        return visible.Length > 0 && Bare(url).StartsWith(visible, StringComparison.OrdinalIgnoreCase);
    }

    private static string VideoUrl(string src)
    {
        var youtube = Regex.Match(src, @"youtube(?:-nocookie)?\.com/embed/([\w-]{6,})");
        return youtube.Success ? "https://www.youtube.com/watch?v=" + youtube.Groups[1].Value : src;
    }

    private static Dictionary<string, string> Attributes(string text)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in AttributePattern().Matches(text))
            result[m.Groups[1].Value] = WebUtility.HtmlDecode(m.Groups[3].Success ? m.Groups[3].Value : m.Groups[4].Success ? m.Groups[4].Value : m.Groups[5].Value);
        return result;
    }

    /// <summary>
    /// Finds protected and quoted text in the captured plain text, in reading order, ignoring whitespace. A segment
    /// that cannot be found is skipped; the ranges never guess.
    /// </summary>
    private static List<int[]> Align(string plain, List<(string Text, bool Element, bool Quote)> segments)
    {
        var positions = new List<int>(); var compact = new StringBuilder();
        for (var i = 0; i < plain.Length; i++)
            if (!char.IsWhiteSpace(plain[i]) && plain[i] is not ('​' or '﻿')) { positions.Add(i); compact.Append(plain[i]); }
        var haystack = compact.ToString(); var cursor = 0; var ranges = new List<int[]>();
        foreach (var (text, element, quote) in segments)
        {
            var needle = string.Concat(text.Where(c => !char.IsWhiteSpace(c) && c is not ('​' or '﻿')));
            if (needle.Length == 0) continue;
            var found = haystack.IndexOf(needle, cursor, StringComparison.Ordinal);
            if (found < 0) continue;
            cursor = found + needle.Length;
            if (!element && !quote) continue;
            // A quote wins over an element inside it: Review leaves the whole quote alone either way.
            var kind = quote ? CaptureDocument.QuoteRange : CaptureDocument.ElementRange;
            int start = positions[found], end = positions[found + needle.Length - 1] + 1;
            if (ranges.Count > 0 && ranges[^1][2] == kind && plain[ranges[^1][1]..start].All(char.IsWhiteSpace)) ranges[^1][1] = end;
            else ranges.Add([start, end, kind]);
        }
        return ranges;
    }

    [GeneratedRegex(@"(?<comment><!--[\s\S]*?-->)|<(?<slash>/?)(?<tag>[a-zA-Z][\w:-]*)(?<attrs>(?:[^>""']|""[^""]*""|'[^']*')*)>|(?<text>[^<]+)|(?<text><)")]
    private static partial Regex TokenPattern();
    [GeneratedRegex(@"([\w:-]+)\s*=\s*(""([^""]*)""|'([^']*)'|([^\s>]+))")]
    private static partial Regex AttributePattern();
    [GeneratedRegex(@"<img\b[^>]*\balt=""([^""]+)""")]
    private static partial Regex ImageAltPattern();
    [GeneratedRegex(@">([^<]+)<")]
    private static partial Regex HeaderTextPattern();
    [GeneratedRegex(@"class=""[^""]*\bfont-medium\b[^""]*""[^>]*>\s*([^<\s][^<]*)<")]
    private static partial Regex AuthorPattern();
    [GeneratedRegex(@"href=""[^""]*/profile/[^""]*""[^>]*>[\s\S]{0,400}?<img[^>]*\balt=""([^""]+)""")]
    private static partial Regex AvatarPattern();
    /// <summary>
    /// Paragraph gaps of a line-block editor (one paragraph per line without spacing, an empty paragraph per blank
    /// line, as in the M! Forum). Chrome's plain text gives every paragraph a blank line, so one blank line on the page
    /// arrives as 2 + 2 x (empty paragraphs) line breaks. Returns each such run in <paramref name="plain"/> as
    /// [start, length, empty paragraphs], for display only; the text stays unchanged. Returns null unless every
    /// paragraph boundary of the fragment matches one run exactly; lists, quotes, headings, tables and code are left out.
    /// </summary>
    internal static int[][]? LineGaps(string fragment, string plain)
    {
        try
        {
            if (UnsupportedBlockPattern().IsMatch(fragment)) return null;
            var paragraphs = ParagraphPattern().Matches(fragment).Select(m => EmptyParagraphPattern().IsMatch(m.Value)).ToList();
            while (paragraphs.Count > 0 && paragraphs[0]) paragraphs.RemoveAt(0);
            while (paragraphs.Count > 0 && paragraphs[^1]) paragraphs.RemoveAt(paragraphs.Count - 1);
            var empties = new List<int>();
            for (var i = 1; i < paragraphs.Count; i++)
            {
                if (!paragraphs[i - 1]) empties.Add(0);
                if (paragraphs[i]) empties[^1]++;
            }
            if (empties.Count == 0) return null;
            var trimmed = plain.TrimEnd('\r', '\n');
            var runs = LineBreakRunPattern().Matches(trimmed).Where(m => m.Index > 0).ToList();
            if (runs.Count != empties.Count) return null;
            var gaps = new int[runs.Count][];
            for (var i = 0; i < runs.Count; i++)
            {
                var breaks = runs[i].Value.Count(c => c == '\n');
                if (breaks != 2 + 2 * empties[i]) return null;
                gaps[i] = [runs[i].Index, runs[i].Length, empties[i]];
            }
            return gaps;
        }
        catch (RegexMatchTimeoutException) { return null; }
    }
    [GeneratedRegex(@"<(ul|ol|li|blockquote|pre|code|h[1-6]|table)\b", RegexOptions.IgnoreCase, 500)]
    private static partial Regex UnsupportedBlockPattern();
    [GeneratedRegex(@"<p\b[^>]*>.*?</p>", RegexOptions.IgnoreCase | RegexOptions.Singleline, 500)]
    private static partial Regex ParagraphPattern();
    [GeneratedRegex(@"(?:\r?\n){2,}", RegexOptions.None, 500)]
    private static partial Regex LineBreakRunPattern();
    [GeneratedRegex(@">\s*(\d{1,2}\.\d{1,2}\.\d{2,4},?\s+\d{1,2}:\d{2})\s*<")]
    private static partial Regex DatePattern();
    [GeneratedRegex(@"class=""[^""]*\bfont-bold\b[^""]*""[^>]*>\s*([^<\s][^<]*)<")]
    private static partial Regex TitlePattern();
    [GeneratedRegex(@"</svg>\s*<span[^>]*>([^<]*)</span>")]
    private static partial Regex StatPattern();
    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespacePattern();
    [GeneratedRegex(@"<p\b[^>]*>(?:\s|&nbsp;|&#160;|\u00a0|<br\b[^>]*>)*</p>", RegexOptions.IgnoreCase, 500)]
    private static partial Regex EmptyParagraphPattern();
    [GeneratedRegex("⟦(\\d+)⟧")]
    private static partial Regex TokenPattern2();

    /// <summary>The ids of all placeholders in a model answer, in order.</summary>
    internal static List<string> Tokens(string text) => [.. TokenPattern2().Matches(text).Select(m => m.Groups[1].Value)];

    /// <summary>True when every item's placeholder appears exactly once and no unknown placeholder was added.</summary>
    internal static bool Intact(CaptureDocument document, string answer)
    {
        var found = Tokens(answer);
        return found.Count == document.Items.Count && document.Items.All(i => found.Count(f => f == i.Id) == 1);
    }
}
