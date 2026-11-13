using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TextFixer.Services;

/// <summary>A model the signed-in account can use (GET /v1/models, entries with visibility "list").</summary>
public sealed record ChatGptModel(string Slug, string DisplayName);

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
    public async Task<string> StreamTextAsync(string accessToken, JsonObject body, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ChatGptProtocol.InferenceTimeout);

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

    private static async Task<string> ReadEventStreamAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var data = new StringBuilder();
        var output = new StringBuilder();

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
                        return output.ToString();
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
            return output.ToString();
        }

        throw new ChatGptServiceException("stream_interrupted", "ChatGPT stopped before the answer was complete. Try again.");
    }

    /// <returns>True when response.completed arrived.</returns>
    private static bool HandleEvent(string data, StringBuilder output)
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
                    if (output.Length + delta.Length > MaxOutputChars)
                    {
                        throw new ChatGptServiceException("response_too_large", "ChatGPT sent a response that is too large.");
                    }

                    output.Append(delta);
                }

                return false;

            case "response.completed":
                if (output.Length == 0 && evt.TryGetProperty("response", out var completed))
                {
                    AppendOutputText(completed, output);
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

    /// <summary>Fallback when no deltas arrived: collect output_text parts from the final response object.</summary>
    private static void AppendOutputText(JsonElement response, StringBuilder output)
    {
        if (!response.TryGetProperty("output", out var items) || items.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var item in items.EnumerateArray())
        {
            if (JsonRead.String(item, "type") != "message"
                || !item.TryGetProperty("content", out var parts)
                || parts.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var part in parts.EnumerateArray())
            {
                if (JsonRead.String(part, "type") == "output_text" && JsonRead.String(part, "text") is { } text)
                {
                    output.Append(text);
                }
            }
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
