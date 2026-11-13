using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TextFixer.Services;

/// <summary>A model the signed-in account can use (GET /v1/models, entries with visibility "list").</summary>
public sealed record ChatGptModel(string Slug, string DisplayName);

/// <summary>A url_citation annotation. Start/End are UTF-16 offsets into <see cref="ResponseResult.Text"/>, the model output.</summary>
public sealed record ResponseCitation(int Start, int End, string Url, string? Title);

/// <summary>Output text with its citations and the final status of each web search call.</summary>
public sealed record ResponseResult(string Text, ResponseCitation[] Citations, string[] SearchCalls);

/// <summary>
/// Responses API over plain HTTP with SSE parsing. Plan usage requires store:false and stream:true,
/// and a request counts as successful only after response.completed (models-and-inference).
/// </summary>
internal sealed class ChatGptResponsesClient
{
    private const int MaxOutputChars = 4 * 1024 * 1024;

    public async Task<IReadOnlyList<ChatGptModel>> ListModelsAsync(string accessToken, CancellationToken cancellationToken)
    {
        var result = await ChatGptHttp.GetJsonAsync(ChatGptProtocol.ApiBase + "/models", accessToken, cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            throw ChatGptHttp.ToException(result);
        }

        if (result.Body is not { ValueKind: JsonValueKind.Object } body
            || !body.TryGetProperty("models", out var models)
            || models.ValueKind != JsonValueKind.Array)
        {
            throw InvalidCatalog();
        }

        var list = new List<ChatGptModel>();
        foreach (var model in models.EnumerateArray())
        {
            if (JsonRead.String(model, "visibility") != "list")
            {
                continue;
            }

            var slug = JsonRead.String(model, "slug");
            var displayName = JsonRead.String(model, "display_name");
            if (string.IsNullOrEmpty(slug) || slug.Length > 200 || string.IsNullOrEmpty(displayName) || displayName.Length > 200)
            {
                throw InvalidCatalog();
            }

            list.Add(new ChatGptModel(slug, displayName));
        }

        if (list.Count == 0)
        {
            throw new ChatGptServiceException("no_models_available", "No ChatGPT model is available for this account.");
        }

        return list;
    }

    /// <summary>Sends a Responses request and returns the concatenated output text.</summary>
    public async Task<string> StreamTextAsync(string accessToken, JsonObject body, CancellationToken cancellationToken) =>
        (await StreamResponseAsync(accessToken, body, ChatGptProtocol.InferenceTimeout, cancellationToken).ConfigureAwait(false)).Text;

    /// <summary>Sends a Responses request and returns the output text, citations and web search status.</summary>
    public async Task<ResponseResult> StreamResponseAsync(string accessToken, JsonObject body, TimeSpan limit, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(limit);

        using var request = new HttpRequestMessage(HttpMethod.Post, ChatGptProtocol.ApiBase + "/responses")
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        try
        {
            using var response = await ChatGptHttp.Client
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                var error = await ChatGptHttp.ReadJsonResultAsync(response, timeout.Token).ConfigureAwait(false);
                throw ChatGptHttp.ToException(error);
            }

            var mediaType = response.Content.Headers.ContentType?.MediaType;
            if (mediaType is not null && !mediaType.Equals("text/event-stream", StringComparison.OrdinalIgnoreCase))
            {
                throw new ChatGptServiceException("invalid_stream", "ChatGPT sent an unexpected response format.");
            }

            return await ReadEventStreamAsync(response, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ChatGptServiceException("network_timeout", "ChatGPT didn't finish in time. Try again.");
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            throw new ChatGptServiceException("stream_interrupted", "The connection to ChatGPT was interrupted. Try again.", ex);
        }
    }

    /// <summary>
    /// Collected stream state. Output text is kept per content part; citations keep offsets relative to their part and
    /// are rebased only in <see cref="Result"/>, once every part's position is known. Annotations may arrive before the
    /// first delta of their part.
    /// </summary>
    private sealed class StreamState
    {
        private readonly SortedDictionary<(int Item, int Content), StringBuilder> _parts = new();
        private readonly Dictionary<(int Item, int Content, int Index), (int Start, int End, string Url, string? Title)> _citations = [];
        private int _length;

        public Dictionary<string, string> SearchCalls { get; } = [];
        public int Length => _length;

        public void Append(int item, int content, string text)
        {
            if (_length + (long)text.Length > MaxOutputChars)
            {
                throw new ChatGptServiceException("response_too_large", "ChatGPT sent a response that is too large.");
            }

            if (!_parts.TryGetValue((item, content), out var part)) _parts[(item, content)] = part = new StringBuilder();
            part.Append(text);
            _length += text.Length;
        }

        /// <summary>The completed response is authoritative: its output_text parts replace the streamed text.</summary>
        public void ReplaceText(IReadOnlyList<(int Item, int Content, string Text)> parts)
        {
            _parts.Clear();
            _length = 0;
            foreach (var (item, content, text) in parts) Append(item, content, text);
        }

        public ResponseResult Result()
        {
            var output = new StringBuilder(_length);
            var starts = new Dictionary<(int, int), (int Start, int Length)>();
            foreach (var (key, part) in _parts)
            {
                starts[key] = (output.Length, part.Length);
                output.Append(part);
            }

            var citations = new List<ResponseCitation>();
            foreach (var (key, c) in _citations.OrderBy(c => c.Key.Item).ThenBy(c => c.Key.Content).ThenBy(c => c.Key.Index))
            {
                // A citation outside its part's text cannot be placed and is dropped.
                if (!starts.TryGetValue((key.Item, key.Content), out var part) || c.End > part.Length) continue;
                citations.Add(new ResponseCitation(part.Start + c.Start, part.Start + c.End, c.Url, c.Title));
            }

            return new(output.ToString(), [.. citations], [.. SearchCalls.Values]);
        }

        /// <summary>Final annotations replace streamed ones with the same index.</summary>
        public void AddCitation(int item, int content, int index, JsonElement annotation)
        {
            if (JsonRead.String(annotation, "type") != "url_citation" || JsonRead.String(annotation, "url") is not { } url) return;
            var start = Int(annotation, "start_index");
            var end = Int(annotation, "end_index");
            if (item < 0 || content < 0 || start < 0 || end <= start) return;
            _citations[(item, content, index)] = (start, end, url, JsonRead.String(annotation, "title"));
        }

        public void AddSearchCall(JsonElement item, int index)
        {
            if (JsonRead.String(item, "type") != "web_search_call") return;
            SearchCalls[JsonRead.String(item, "id") ?? "#" + index] = JsonRead.String(item, "status") ?? "unknown";
        }
    }

    private static int Int(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number : -1;

    internal static async Task<ResponseResult> ReadEventStreamAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var data = new StringBuilder();
        var output = new StreamState();

        while (true)
        {
            var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null)
            {
                break;
            }

            if (line.Length == 0)
            {
                if (data.Length > 0)
                {
                    var completed = HandleEvent(data.ToString(), output);
                    data.Clear();
                    if (completed)
                    {
                        return output.Result();
                    }
                }

                continue;
            }

            if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                var value = line.AsSpan(5);
                if (value.StartsWith(" "))
                {
                    value = value[1..];
                }

                if (data.Length > 0)
                {
                    data.Append('\n');
                }

                data.Append(value);
            }

            // "event:", "id:", "retry:" and ":" comment lines carry nothing needed here.
        }

        if (data.Length > 0 && HandleEvent(data.ToString(), output))
        {
            return output.Result();
        }

        throw new ChatGptServiceException("stream_interrupted", "ChatGPT stopped before the answer was complete. Try again.");
    }

    /// <returns>True when response.completed arrived.</returns>
    private static bool HandleEvent(string data, StreamState state)
    {
        if (data == "[DONE]")
        {
            return false;
        }

        JsonElement evt;
        try
        {
            using var document = JsonDocument.Parse(data);
            evt = document.RootElement.Clone();
        }
        catch (JsonException)
        {
            throw new ChatGptServiceException("invalid_stream", "ChatGPT sent an unreadable response.");
        }

        switch (JsonRead.String(evt, "type"))
        {
            case "response.output_text.delta":
                var delta = JsonRead.String(evt, "delta");
                if (delta is not null)
                {
                    state.Append(Int(evt, "output_index"), Int(evt, "content_index"), delta);
                }

                return false;

            case "response.output_text.annotation.added":
                if (evt.TryGetProperty("annotation", out var annotation))
                {
                    state.AddCitation(Int(evt, "output_index"), Int(evt, "content_index"), Int(evt, "annotation_index"), annotation);
                }

                return false;

            case "response.output_item.done":
                if (evt.TryGetProperty("item", out var doneItem))
                {
                    state.AddSearchCall(doneItem, Int(evt, "output_index"));
                }

                return false;

            case "response.completed":
                if (evt.TryGetProperty("response", out var completed))
                {
                    // Final output text (authoritative when present), citations and search calls.
                    ReadCompletedResponse(completed, state);
                }

                return true;

            case "response.failed":
                throw StreamError(evt.TryGetProperty("response", out var failed) ? failed : evt);

            case "error":
                throw StreamError(evt);

            case "response.incomplete":
                throw new ChatGptServiceException("response_incomplete", "ChatGPT stopped before the answer was complete. Try again.");

            default:
                return false;
        }
    }

    /// <summary>
    /// Reads the final response object: search call status, citations, and the output_text parts, which replace the
    /// streamed text when the final object carries any.
    /// </summary>
    private static void ReadCompletedResponse(JsonElement response, StreamState state)
    {
        if (!response.TryGetProperty("output", out var items) || items.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        var texts = new List<(int, int, string)>();
        var itemIndex = -1;
        foreach (var item in items.EnumerateArray())
        {
            itemIndex++;
            state.AddSearchCall(item, itemIndex);
            if (JsonRead.String(item, "type") != "message"
                || !item.TryGetProperty("content", out var parts)
                || parts.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            var partIndex = -1;
            foreach (var part in parts.EnumerateArray())
            {
                partIndex++;
                if (JsonRead.String(part, "type") != "output_text") continue;
                if (JsonRead.String(part, "text") is { Length: > 0 } text)
                {
                    texts.Add((itemIndex, partIndex, text));
                }

                if (part.TryGetProperty("annotations", out var annotations) && annotations.ValueKind == JsonValueKind.Array)
                {
                    var annotationIndex = -1;
                    foreach (var annotation in annotations.EnumerateArray())
                    {
                        state.AddCitation(itemIndex, partIndex, ++annotationIndex, annotation);
                    }
                }
            }
        }

        if (texts.Count > 0)
        {
            state.ReplaceText(texts);
        }
    }

    private static ChatGptServiceException StreamError(JsonElement source)
    {
        var (code, param) = ChatGptHttp.ExtractError(source);
        code ??= "response_failed";
        return new ChatGptServiceException(code, ChatGptHttp.UserMessage(code, param, null)) { ApiParam = param };
    }

    private static ChatGptServiceException InvalidCatalog() =>
        new("invalid_model_catalog", "ChatGPT returned an unreadable model list.");
}
