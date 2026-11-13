using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace TextFixer.Services;

internal sealed record HttpJsonResult(HttpStatusCode Status, JsonElement? Body, string? RequestId)
{
    public bool IsSuccess => (int)Status is >= 200 and < 300;
}

/// <summary>Shared HTTP plumbing: one client, timeouts, size caps, API/OAuth error mapping.</summary>
internal static class ChatGptHttp
{
    private const int MaxResponseBytes = 1024 * 1024;

    public static readonly HttpClient Client = CreateClient();

    private static HttpClient CreateClient()
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        };
        var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("TextFixer", "0.1"));
        return client;
    }

    public static Task<HttpJsonResult> GetJsonAsync(string url, string? bearerToken, CancellationToken cancellationToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (bearerToken is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
        }

        return SendForJsonAsync(request, cancellationToken);
    }

    public static Task<HttpJsonResult> PostFormAsync(string url, IEnumerable<KeyValuePair<string, string>> form, CancellationToken cancellationToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = new FormUrlEncodedContent(form) };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return SendForJsonAsync(request, cancellationToken);
    }

    private static async Task<HttpJsonResult> SendForJsonAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using var _ = request;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ChatGptProtocol.HttpTimeout);
        try
        {
            using var response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            return await ReadJsonResultAsync(response, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ChatGptServiceException("network_timeout", "ChatGPT didn't respond in time. Try again.");
        }
        catch (HttpRequestException ex)
        {
            throw new ChatGptServiceException("network_error", "ChatGPT can't be reached. Check the internet connection.", ex);
        }
    }

    public static async Task<HttpJsonResult> ReadJsonResultAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var bytes = await ReadCappedAsync(response.Content, cancellationToken).ConfigureAwait(false);
        JsonElement? body = null;
        if (bytes.Length > 0)
        {
            try
            {
                using var document = JsonDocument.Parse(bytes);
                body = document.RootElement.Clone();
            }
            catch (JsonException)
            {
            }
        }

        var requestId = response.Headers.TryGetValues("x-request-id", out var values) ? values.FirstOrDefault() : null;
        return new HttpJsonResult(response.StatusCode, body, requestId);
    }

    private static async Task<byte[]> ReadCappedAsync(HttpContent content, CancellationToken cancellationToken)
    {
        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > MaxResponseBytes)
            {
                throw new ChatGptServiceException("response_too_large", "ChatGPT sent a response that is too large.");
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    public static ChatGptServiceException ToException(HttpJsonResult result)
    {
        var (code, param) = ExtractError(result.Body);
        var status = (int)result.Status;
        code ??= "http_" + status;
        return new ChatGptServiceException(code, UserMessage(code, param, status))
        {
            ApiParam = param,
            HttpStatus = status,
            RequestId = result.RequestId,
        };
    }

    /// <summary>
    /// Unwraps nested error/detail objects (up to 4 levels). OAuth errors use a string "error",
    /// Responses errors use "code" (errors-and-recovery: use the exact error.code and error.param).
    /// </summary>
    public static (string? Code, string? Param) ExtractError(JsonElement? body)
    {
        if (body is not { ValueKind: JsonValueKind.Object } current)
        {
            return (null, null);
        }

        for (var depth = 0; depth < 4; depth++)
        {
            if (current.TryGetProperty("error", out var nested) && nested.ValueKind == JsonValueKind.Object)
            {
                current = nested;
                continue;
            }

            if (current.TryGetProperty("detail", out var detail) && detail.ValueKind == JsonValueKind.Object)
            {
                current = detail;
                continue;
            }

            break;
        }

        var code = JsonRead.String(current, "code") ?? JsonRead.String(current, "error") ?? JsonRead.String(current, "type");
        return (code, JsonRead.String(current, "param"));
    }

    public static string UserMessage(string code, string? param, int? status)
    {
        if (ChatGptProtocol.ReauthenticationErrors.Contains(code))
        {
            return "The ChatGPT sign-in has expired. Sign in again.";
        }

        return code switch
        {
            ChatGptProtocol.UsageLimitError => "The ChatGPT plan usage limit is reached. Check usage in ChatGPT settings.",
            ChatGptProtocol.UsageUnavailableError => "ChatGPT plan usage isn't available for this account right now. Check usage in ChatGPT settings.",
            ChatGptProtocol.UnsupportedCapabilityError => param is null
                ? "ChatGPT rejected an option of the request."
                : $"ChatGPT rejected the request option \"{param}\".",
            "invalid_client" => "The TextFixer registration with ChatGPT isn't valid. Sign in again.",
            _ => status switch
            {
                401 => "The ChatGPT sign-in has expired. Sign in again.",
                429 => "ChatGPT is busy or rate-limited. Try again in a moment.",
                >= 500 => "ChatGPT had a server error. Try again in a moment.",
                _ => $"ChatGPT returned an error ({code}).",
            },
        };
    }
}

internal static class JsonRead
{
    public static string? String(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    public static long? UnixSeconds(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetDouble(out var seconds)
            ? (long)seconds
            : null;

    public static JsonElement ParseUtf8(byte[] utf8)
    {
        using var document = JsonDocument.Parse(utf8);
        return document.RootElement.Clone();
    }
}
