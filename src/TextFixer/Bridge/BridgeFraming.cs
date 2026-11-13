using System.Buffers.Binary;
using System.Text.Json;

namespace TextFixer.Bridge;

public sealed class BridgeProtocolException(string message) : Exception(message);

/// <summary>
/// Chrome native messaging framing, also used on the pipe: 4-byte little-endian length, then UTF-8 JSON.
/// Every frame must be a JSON object of at most <see cref="BridgeProtocol.MaxMessageBytes"/> bytes.
/// </summary>
public static class BridgeFraming
{
    /// <returns>The payload, or null on a clean end of stream before a new frame.</returns>
    public static async Task<byte[]?> ReadFrameAsync(Stream stream, CancellationToken cancellationToken, int maxBytes = BridgeProtocol.MaxMessageBytes)
    {
        var header = new byte[4];
        var headerRead = await ReadFullyAsync(stream, header, cancellationToken).ConfigureAwait(false);
        if (headerRead == 0)
        {
            return null;
        }

        if (headerRead < header.Length)
        {
            throw new BridgeProtocolException("Truncated frame header.");
        }

        var length = BinaryPrimitives.ReadUInt32LittleEndian(header);
        if (length == 0 || length > (uint)maxBytes)
        {
            throw new BridgeProtocolException($"Invalid frame length {length}.");
        }

        var payload = new byte[length];
        if (await ReadFullyAsync(stream, payload, cancellationToken).ConfigureAwait(false) < payload.Length)
        {
            throw new BridgeProtocolException("Truncated frame payload.");
        }

        return payload;
    }

    /// <summary>Reads a frame and validates that it is a JSON object.</summary>
    public static async Task<(byte[] Payload, JsonElement Message)?> ReadMessageAsync(Stream stream, CancellationToken cancellationToken, int maxBytes = BridgeProtocol.MaxMessageBytes)
    {
        var payload = await ReadFrameAsync(stream, cancellationToken, maxBytes).ConfigureAwait(false);
        return payload is null ? null : (payload, ParseObject(payload));
    }

    public static async Task WriteFrameAsync(Stream stream, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken, int maxBytes = BridgeProtocol.MaxMessageBytes)
    {
        if (payload.Length == 0 || payload.Length > maxBytes)
        {
            throw new BridgeProtocolException($"Invalid frame length {payload.Length}.");
        }

        // One buffer, one write: a frame is never interleaved with another writer's bytes.
        var frame = new byte[4 + payload.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(frame, (uint)payload.Length);
        payload.CopyTo(frame.AsMemory(4));
        await stream.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public static JsonElement ParseObject(byte[] payload)
    {
        try
        {
            using var document = JsonDocument.Parse(payload, new JsonDocumentOptions { MaxDepth = 32 });
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new BridgeProtocolException("Frame is not a JSON object.");
            }

            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            throw new BridgeProtocolException("Frame is not valid UTF-8 JSON.");
        }
    }

    public static string? Kind(JsonElement message) =>
        message.ValueKind == JsonValueKind.Object
        && message.TryGetProperty("kind", out var kind)
        && kind.ValueKind == JsonValueKind.String
            ? kind.GetString()
            : null;

    private static async Task<int> ReadFullyAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        return total;
    }
}
