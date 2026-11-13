using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Web;

namespace TextFixer.Services;

internal sealed record OAuthCallback(string? Code, string? Error, string? ClientId);

/// <summary>
/// Minimal HTTP listener on 127.0.0.1 for the OAuth redirect. Uses a raw socket instead of
/// HttpListener so no http.sys URL reservation is needed. Ignores requests that don't match
/// host, method and path; rejects a wrong state without ending the attempt.
/// </summary>
internal sealed class LoopbackCallbackListener : IDisposable
{
    private const int MaxRequestHeadBytes = 16 * 1024;

    private const string SuccessPage =
        "<!doctype html><html><head><meta charset=\"utf-8\"><title>Return to TextFixer</title></head>" +
        "<body style=\"font-family:system-ui,sans-serif;margin:3rem\"><h1>Return to TextFixer</h1>" +
        "<p>TextFixer finishes checking your ChatGPT connection. You can close this tab.</p></body></html>";

    private const string FailurePage =
        "<!doctype html><html><head><meta charset=\"utf-8\"><title>Sign-in not completed</title></head>" +
        "<body style=\"font-family:system-ui,sans-serif;margin:3rem\"><h1>Sign-in not completed</h1>" +
        "<p>Return to TextFixer to try again. You can close this tab.</p></body></html>";

    private const string InvalidStatePage =
        "<!doctype html><html><head><meta charset=\"utf-8\"><title>Invalid sign-in</title></head>" +
        "<body style=\"font-family:system-ui,sans-serif;margin:3rem\"><h1>Invalid sign-in state</h1>" +
        "<p>This sign-in link doesn't belong to the current TextFixer sign-in.</p></body></html>";

    private readonly TcpListener _listener;

    private LoopbackCallbackListener(TcpListener listener)
    {
        _listener = listener;
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    public int Port { get; }

    public string RedirectUri => $"http://{ChatGptProtocol.CallbackHost}:{Port}{ChatGptProtocol.CallbackPath}";

    public static LoopbackCallbackListener Start()
    {
        try
        {
            return StartOn(ChatGptProtocol.PreferredCallbackPort);
        }
        catch (SocketException)
        {
            try
            {
                return StartOn(0);
            }
            catch (SocketException ex)
            {
                throw new ChatGptServiceException("callback_port_unavailable", "TextFixer can't open a local port for the ChatGPT sign-in.", ex);
            }
        }
    }

    private static LoopbackCallbackListener StartOn(int port)
    {
        var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Server.ExclusiveAddressUse = true;
        listener.Start();
        return new LoopbackCallbackListener(listener);
    }

    public async Task<OAuthCallback> WaitForCallbackAsync(string expectedState, CancellationToken cancellationToken)
    {
        while (true)
        {
            using var client = await _listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
            var callback = await HandleAsync(client, expectedState, cancellationToken).ConfigureAwait(false);
            if (callback is not null)
            {
                return callback;
            }
        }
    }

    private async Task<OAuthCallback?> HandleAsync(TcpClient client, string expectedState, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ChatGptProtocol.CallbackRequestTimeout);
        try
        {
            var stream = client.GetStream();
            var head = await ReadRequestHeadAsync(stream, timeout.Token).ConfigureAwait(false);
            if (head is null)
            {
                return null;
            }

            var lines = head.Split("\r\n");
            var requestLine = lines[0].Split(' ');
            var host = lines.Skip(1)
                .Select(line => line.Split(':', 2))
                .Where(pair => pair.Length == 2 && pair[0].Trim().Equals("Host", StringComparison.OrdinalIgnoreCase))
                .Select(pair => pair[1].Trim())
                .FirstOrDefault();

            var target = requestLine.Length == 3 ? requestLine[1] : string.Empty;
            var queryStart = target.IndexOf('?');
            var path = queryStart < 0 ? target : target[..queryStart];
            if (requestLine.Length != 3
                || requestLine[0] != "GET"
                || path != ChatGptProtocol.CallbackPath
                || !string.Equals(host, $"{ChatGptProtocol.CallbackHost}:{Port}", StringComparison.OrdinalIgnoreCase))
            {
                await WriteResponseAsync(stream, 404, "Not Found", "Not found", timeout.Token).ConfigureAwait(false);
                return null;
            }

            var query = HttpUtility.ParseQueryString(queryStart < 0 ? string.Empty : target[(queryStart + 1)..]);
            var states = query.GetValues("state");
            if (states is not { Length: 1 } || !FixedTimeEquals(states[0], expectedState))
            {
                await WriteResponseAsync(stream, 400, "Bad Request", InvalidStatePage, timeout.Token).ConfigureAwait(false);
                return null;
            }

            var error = Single(query, "error", out var duplicated);
            var code = Single(query, "code", out var codeDuplicated);
            var clientId = Single(query, "client_id", out var clientIdDuplicated);
            if (duplicated || codeDuplicated || clientIdDuplicated)
            {
                error ??= "invalid_callback";
            }

            await WriteResponseAsync(stream, 200, "OK", error is null ? SuccessPage : FailurePage, timeout.Token).ConfigureAwait(false);
            return new OAuthCallback(code, error, clientId);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static string? Single(System.Collections.Specialized.NameValueCollection query, string name, out bool duplicated)
    {
        var values = query.GetValues(name);
        duplicated = values is { Length: > 1 };
        return values is { Length: 1 } && values[0].Length > 0 ? values[0] : null;
    }

    private static bool FixedTimeEquals(string actual, string expected) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(actual), Encoding.UTF8.GetBytes(expected));

    private static async Task<string?> ReadRequestHeadAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[MaxRequestHeadBytes];
        var length = 0;
        while (length < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(length), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return null;
            }

            length += read;
            var text = Encoding.ASCII.GetString(buffer, 0, length);
            var end = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            if (end >= 0)
            {
                return text[..end];
            }
        }

        return null;
    }

    private static async Task WriteResponseAsync(NetworkStream stream, int status, string reason, string html, CancellationToken cancellationToken)
    {
        var body = Encoding.UTF8.GetBytes(html);
        var head =
            $"HTTP/1.1 {status} {reason}\r\n" +
            "Content-Type: text/html; charset=utf-8\r\n" +
            $"Content-Length: {body.Length}\r\n" +
            "Cache-Control: no-store\r\n" +
            "Referrer-Policy: no-referrer\r\n" +
            "Content-Security-Policy: default-src 'none'; style-src 'unsafe-inline'\r\n" +
            "Connection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head), cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(body, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public void Dispose() => _listener.Stop();
}
