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

        return new ReviewResult(MatchEdges(original, corrected), findings.ToArray());
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

    /// <returns>Normalized text and, for each normalized char, the index of its source char.</returns>
    private static (string Text, List<int> Map) Normalize(string value)
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
