using System.Text;
using System.Text.Json;
using TextFixer.Contracts;

namespace TextFixer.Services;

/// <summary>
/// Validates model output against the original text. Findings survive only with at least one quote
/// that exists in the original; quotes are returned as the exact original substring.
/// </summary>
internal static class ReviewResultParser
{
    private const int MaxFindings = 30;
    private const int MaxQuotesPerFinding = 4;
    private const int MaxCorrections = 500;

    public static ReviewResult Parse(string original, string modelOutput)
    {
        JsonElement root;
        try
        {
            using var document = JsonDocument.Parse(ExtractJsonObject(modelOutput));
            root = document.RootElement.Clone();
        }
        catch (JsonException)
        {
            throw InvalidOutput();
        }

        var corrected = JsonRead.String(root, "corrected_text");
        if (corrected is null || (corrected.Trim().Length == 0 && original.Trim().Length > 0))
        {
            throw InvalidOutput();
        }

        var findings = new List<ReviewFinding>();
        if (root.TryGetProperty("findings", out var items) && items.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in items.EnumerateArray())
            {
                if (findings.Count >= MaxFindings)
                {
                    break;
                }

                if (ParseFinding(original, item) is { } finding)
                {
                    findings.Add(finding);
                }
            }
        }

        var correctedText = MatchEdges(original, corrected);
        return new ReviewResult(correctedText, findings.ToArray(), ParseCorrections(original, correctedText, root));
    }

    /// <summary>
    /// Resolves the model's labelled changes to original offsets. Entries that can't be located, don't
    /// appear in the corrected text, or overlap an earlier entry are dropped: the UI categorizes those
    /// changes locally, so an incomplete list is safe and a wrong position is not.
    /// </summary>
    private static ReviewCorrection[] ParseCorrections(string original, string corrected, JsonElement root)
    {
        if (!root.TryGetProperty("corrections", out var items) || items.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var normalizedCorrected = QuoteLocator.Normalize(corrected).Text;
        var result = new List<ReviewCorrection>();
        var cursor = 0;
        foreach (var item in items.EnumerateArray())
        {
            if (result.Count >= MaxCorrections)
            {
                break;
            }

            var category = JsonRead.String(item, "category")?.Trim().ToLowerInvariant();
            var before = JsonRead.String(item, "before");
            var after = JsonRead.String(item, "after");
            if (category is null || !ReviewCorrectionCategories.All.Contains(category)
                || string.IsNullOrEmpty(before) || after is null || before == after)
            {
                continue;
            }

            var normalizedAfter = QuoteLocator.Normalize(after).Text.Trim();
            if (normalizedAfter.Length > 0 && !normalizedCorrected.Contains(normalizedAfter, StringComparison.Ordinal))
            {
                continue;
            }

            // Entries arrive in text order; search forward first, then accept an out-of-order entry only
            // when its wording occurs exactly once in the whole text.
            var range = QuoteLocator.Locate(original, before, cursor);
            if (range is null && QuoteLocator.Locate(original, before, 0) is { } anywhere
                && QuoteLocator.Locate(original, before, anywhere.Start + 1) is null)
            {
                range = anywhere;
            }

            if (range is not { } found || result.Any(c => found.Start < c.End && c.Start < found.End))
            {
                continue;
            }

            result.Add(new ReviewCorrection(found.Start, found.End, after, category));
            cursor = Math.Max(cursor, found.End);
        }

        return result.OrderBy(c => c.Start).ToArray();
    }

    private static ReviewFinding? ParseFinding(string original, JsonElement item)
    {
        var kind = JsonRead.String(item, "kind")?.Trim().ToLowerInvariant().Replace(' ', '_').Replace('-', '_');
        if (kind is null || !ReviewFindingKinds.All.Contains(kind))
        {
            return null;
        }

        var explanation = JsonRead.String(item, "explanation")?.Trim();
        if (string.IsNullOrEmpty(explanation))
        {
            return null;
        }

        var quotes = new List<string>();
        if (item.TryGetProperty("quotes", out var quoteArray) && quoteArray.ValueKind == JsonValueKind.Array)
        {
            foreach (var quote in quoteArray.EnumerateArray())
            {
                if (quote.ValueKind == JsonValueKind.String
                    && QuoteLocator.Find(original, quote.GetString()!) is { } located
                    && !quotes.Contains(located))
                {
                    quotes.Add(located);
                    if (quotes.Count == MaxQuotesPerFinding)
                    {
                        break;
                    }
                }
            }
        }

        if (quotes.Count == 0)
        {
            return null;
        }

        var rewrite = JsonRead.String(item, "suggested_rewrite")?.Trim();
        return new ReviewFinding(kind, explanation, quotes.ToArray(), string.IsNullOrEmpty(rewrite) ? null : rewrite);
    }

    /// <summary>Accepts a bare object, a Markdown-fenced object, or an object surrounded by stray text.</summary>
    private static string ExtractJsonObject(string output)
    {
        var start = output.IndexOf('{');
        var end = output.LastIndexOf('}');
        if (start < 0 || end <= start)
        {
            throw InvalidOutput();
        }

        return output[start..(end + 1)];
    }

    /// <summary>
    /// Keeps the original line-ending style and leading/trailing whitespace, which models tend to
    /// normalize or trim; the corrected text replaces the selection one-to-one.
    /// </summary>
    private static string MatchEdges(string original, string corrected)
    {
        if (original.Contains("\r\n", StringComparison.Ordinal))
        {
            corrected = corrected.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\n", "\r\n", StringComparison.Ordinal);
        }

        var leading = original[..(original.Length - original.TrimStart().Length)];
        var trailing = original[original.TrimEnd().Length..];
        return leading + corrected.Trim() + trailing;
    }

    private static ChatGptServiceException InvalidOutput() =>
        new("invalid_model_output", "ChatGPT returned an answer TextFixer can't read. Try again.");
}

/// <summary>
/// Finds a model-supplied quote in the original text: exact match first, then a match that ignores
/// whitespace differences and typographic quote/dash variants. Returns the original substring.
/// </summary>
internal static class QuoteLocator
{
    private const int MinQuoteLength = 2;

    public static string? Find(string original, string quote)
    {
        quote = quote.Trim().Trim('…').Trim();
        if (quote.EndsWith("...", StringComparison.Ordinal))
        {
            quote = quote[..^3].TrimEnd();
        }

        if (quote.Length < MinQuoteLength)
        {
            return null;
        }

        if (original.Contains(quote, StringComparison.Ordinal))
        {
            return quote;
        }

        var (normalizedOriginal, map) = Normalize(original);
        var (normalizedQuote, _) = Normalize(quote);
        normalizedQuote = normalizedQuote.Trim();
        if (normalizedQuote.Length < MinQuoteLength)
        {
            return null;
        }

        var index = normalizedOriginal.IndexOf(normalizedQuote, StringComparison.Ordinal);
        if (index < 0)
        {
            return null;
        }

        var start = map[index];
        var end = map[index + normalizedQuote.Length - 1];
        return original[start..(end + 1)];
    }

    /// <summary>
    /// Finds a verbatim fragment at or after <paramref name="from"/>: exact match first, then the same
    /// whitespace- and quote-tolerant match as <see cref="Find"/>. Returns original offsets [Start, End).
    /// </summary>
    public static (int Start, int End)? Locate(string original, string fragment, int from)
    {
        if (fragment.Length == 0 || from > original.Length)
        {
            return null;
        }

        var exact = original.IndexOf(fragment, from, StringComparison.Ordinal);
        if (exact >= 0)
        {
            return (exact, exact + fragment.Length);
        }

        var (normalizedOriginal, map) = Normalize(original);
        var normalizedFragment = Normalize(fragment).Text.Trim();
        if (normalizedFragment.Length == 0)
        {
            return null;
        }

        var normalizedFrom = map.FindIndex(i => i >= from);
        if (normalizedFrom < 0)
        {
            return null;
        }

        var index = normalizedOriginal.IndexOf(normalizedFragment, normalizedFrom, StringComparison.Ordinal);
        if (index < 0)
        {
            return null;
        }

        var start = map[index];
        return (start, map[index + normalizedFragment.Length - 1] + 1);
    }

    /// <returns>Normalized text and, for each normalized char, the index of its source char.</returns>
    internal static (string Text, List<int> Map) Normalize(string value)
    {
        var builder = new StringBuilder(value.Length);
        var map = new List<int>(value.Length);
        var previousWasSpace = false;
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (char.IsWhiteSpace(c))
            {
                if (!previousWasSpace)
                {
                    builder.Append(' ');
                    map.Add(i);
                }

                previousWasSpace = true;
                continue;
            }

            previousWasSpace = false;
            builder.Append(c switch
            {
                '‘' or '’' or '‚' or '‛' or '′' => '\'',
                '“' or '”' or '„' or '‟' or '«' or '»' or '″' => '"',
                '–' or '—' or '−' => '-',
                _ => c,
            });
            map.Add(i);
        }

        return (builder.ToString(), map);
    }
}
