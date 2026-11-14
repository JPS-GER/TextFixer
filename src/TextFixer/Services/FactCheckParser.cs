using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using TextFixer.Contracts;

namespace TextFixer.Services;

/// <summary>
/// Turns a fact-check answer into claims. Sources come only from url_citation annotations that fall inside a claim's
/// JSON object in the model output; URLs written into the JSON text are not provenance and are dropped.
/// A claim judged supported, wrong or disputed without any citation becomes unverifiable.
/// </summary>
internal static partial class FactCheckParser
{
    internal const string Supported = "supported", Wrong = "wrong", Disputed = "disputed", Unverifiable = "unverifiable";
    internal const int MaxClaims = 40, MaxSources = 5;

    public static FactClaim[] Parse(string original, ResponseResult response)
    {
        var text = response.Text;
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        if (start < 0 || end <= start) throw Invalid();
        var json = text[start..(end + 1)];

        JsonDocument document;
        try { document = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip }); }
        catch (JsonException) { throw Invalid(); }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("claims", out var claims)
                || claims.ValueKind != JsonValueKind.Array)
                throw Invalid();

            // Spans are indexed like the array elements; a duplicate "claims" property makes the mapping ambiguous.
            var spans = ClaimSpans(json, start, out var claimsProperties);
            if (claimsProperties != 1 || spans.Count != claims.GetArrayLength()) throw Invalid();
            var result = new List<FactClaim>();
            var index = -1;
            foreach (var claim in claims.EnumerateArray())
            {
                index++;
                if (result.Count >= MaxClaims) break;
                if (claim.ValueKind != JsonValueKind.Object) continue;
                var quote = JsonRead.String(claim, "quote");
                if (string.IsNullOrWhiteSpace(quote) || !original.Contains(quote, StringComparison.Ordinal)) continue;
                var verdict = JsonRead.String(claim, "verdict")?.Trim().ToLowerInvariant();
                if (verdict is not (Supported or Wrong or Disputed or Unverifiable)) verdict = Unverifiable;

                var span = spans[index];
                var cited = response.Citations.Where(c => c.Start >= span.Start && c.End <= span.End && c.Start < c.End && c.End <= text.Length).ToList();
                var explanation = JsonRead.String(claim, "explanation") ?? "";
                foreach (var c in cited)
                    explanation = explanation.Replace(text[c.Start..c.End], "", StringComparison.Ordinal);
                explanation = Clean(explanation);

                var sources = cited.Select(ToSource).OfType<FactSource>().DistinctBy(s => DedupKey(s.Url)).Take(MaxSources).ToArray();
                var noSource = sources.Length == 0 && verdict != Unverifiable;
                if (noSource) verdict = Unverifiable;
                var correction = JsonRead.String(claim, "correction");
                if (verdict != Wrong || string.IsNullOrWhiteSpace(correction) || correction == quote) correction = null;
                result.Add(new FactClaim(quote, verdict, explanation, correction, sources, noSource));
            }

            return [.. result];
        }
    }

    /// <summary>
    /// UTF-16 range of each element of the top-level "claims" array, as offsets into the full model output; (-1, -1)
    /// for an element that is not an object. <paramref name="claimsProperties"/> counts top-level "claims" properties.
    /// </summary>
    internal static List<(int Start, int End)> ClaimSpans(string json, int offset, out int claimsProperties)
    {
        var spans = new List<(int, int)>();
        claimsProperties = 0;
        var bytes = Encoding.UTF8.GetBytes(json);
        var reader = new Utf8JsonReader(bytes, new JsonReaderOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        string? property = null;
        var inClaims = false;
        long objectStart = -1;
        int Chars(long bytePosition) => offset + Encoding.UTF8.GetCharCount(bytes, 0, (int)bytePosition);
        try
        {
            while (reader.Read())
            {
                switch (reader.TokenType)
                {
                    case JsonTokenType.PropertyName when reader.CurrentDepth == 1:
                        property = reader.GetString();
                        if (property == "claims") claimsProperties++;
                        break;
                    case JsonTokenType.StartArray when reader.CurrentDepth == 1:
                        inClaims = property == "claims";
                        break;
                    case JsonTokenType.EndArray when reader.CurrentDepth == 1:
                        inClaims = false;
                        break;
                    case JsonTokenType.StartObject when inClaims && reader.CurrentDepth == 2:
                        objectStart = reader.TokenStartIndex;
                        break;
                    case JsonTokenType.EndObject when inClaims && reader.CurrentDepth == 2 && objectStart >= 0:
                        spans.Add((Chars(objectStart), Chars(reader.BytesConsumed)));
                        objectStart = -1;
                        break;
                    case JsonTokenType.StartArray when inClaims && reader.CurrentDepth == 2:
                        spans.Add((-1, -1));
                        reader.Skip();
                        break;
                    case JsonTokenType.String or JsonTokenType.Number or JsonTokenType.True or JsonTokenType.False or JsonTokenType.Null
                        when inClaims && reader.CurrentDepth == 2:
                        spans.Add((-1, -1));
                        break;
                }
            }
        }
        catch (JsonException) { }
        return spans;
    }

    /// <summary>An exact absolute https URL without user info, control characters or whitespace; otherwise null.</summary>
    internal static FactSource? ToSource(ResponseCitation citation) => ValidUrl(citation.Url) is { } uri
        ? new FactSource(citation.Url, string.IsNullOrWhiteSpace(citation.Title) ? Domain(uri) : Truncate(citation.Title.Trim(), 200), Domain(uri))
        : null;

    internal static Uri? ValidUrl(string? url) =>
        url is { Length: > 0 and <= 2048 }
        && !url.Any(c => char.IsControl(c) || char.IsWhiteSpace(c))
        && Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps
        && string.IsNullOrEmpty(uri.UserInfo)
        && !string.IsNullOrEmpty(uri.Host)
            ? uri : null;

    private static string Domain(Uri uri) => uri.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? uri.Host[4..] : uri.Host;

    /// <summary>Tracking parameters are ignored for de-duplication only; the URL itself is kept as cited.</summary>
    private static string DedupKey(string url) => TrackingParameter().Replace(url, "").TrimEnd('?', '&');

    private static string Clean(string explanation)
    {
        explanation = MarkdownCitation().Replace(explanation, "");
        explanation = MarkdownLink().Replace(explanation, "$1");
        explanation = Spaces().Replace(explanation, " ");
        explanation = SpaceBeforePunctuation().Replace(explanation, "$1");
        return explanation.Trim();
    }

    internal const int MaxDialogSources = 8;

    /// <summary>
    /// A dialog answer without citation markers, and its sources from url_citation annotations only. Line breaks stay.
    /// </summary>
    public static (string Text, FactSource[] Sources) CitedAnswer(ResponseResult response)
    {
        var text = response.Text;
        var cited = response.Citations.Where(c => c.Start >= 0 && c.Start < c.End && c.End <= text.Length).OrderByDescending(c => c.Start).ToList();
        var removeFrom = text.Length;
        foreach (var c in cited)
        {
            if (c.End > removeFrom) continue; // overlaps a range already removed
            text = text[..c.Start] + text[c.End..];
            removeFrom = c.Start;
        }
        text = MarkdownCitation().Replace(text, "");
        text = MarkdownLink().Replace(text, "$1");
        text = Spaces().Replace(text, " ");
        text = InlineSpaceBeforePunctuation().Replace(text, "$1");
        text = string.Join("\n", text.Replace("\r\n", "\n").Split('\n').Select(line => line.TrimEnd()));
        var sources = response.Citations.Select(ToSource).OfType<FactSource>().DistinctBy(s => DedupKey(s.Url)).Take(MaxDialogSources).ToArray();
        return (text.Trim(), sources);
    }

    private static string Truncate(string value, int length) => value.Length <= length ? value : value[..length];

    private static ChatGptServiceException Invalid() =>
        new("invalid_fact_check", "ChatGPT returned an unreadable fact check. Try again.");

    [GeneratedRegex(@"\s*\(\[[^\]]*\]\([^)\s]*\)\)")] private static partial Regex MarkdownCitation();
    [GeneratedRegex(@"\[([^\]]*)\]\(https?://[^)\s]*\)")] private static partial Regex MarkdownLink();
    [GeneratedRegex(@"[ \t]{2,}")] private static partial Regex Spaces();
    [GeneratedRegex(@"\s+([.,;:!?])")] private static partial Regex SpaceBeforePunctuation();
    [GeneratedRegex(@"[ \t]+([.,;:!?])")] private static partial Regex InlineSpaceBeforePunctuation();
    [GeneratedRegex(@"(?<=[?&])utm_[^&]*&?")] private static partial Regex TrackingParameter();
}
