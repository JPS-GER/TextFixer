using System.Buffers.Binary;

namespace TextFixer.Native;

/// <summary>Reads image sizes and turns a clipboard bitmap (CF_DIB) into a BMP file without converting pixels.</summary>
internal static class ClipboardImage
{
    private static ReadOnlySpan<byte> PngSignature => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <returns>Width and height from the PNG header, or (0, 0) when the bytes are not a PNG.</returns>
    internal static (int Width, int Height) PngSize(byte[] png)
    {
        if (png.Length < 24 || !png.AsSpan(0, 8).SequenceEqual(PngSignature)) return (0, 0);
        return (BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16)), BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(20)));
    }

    internal static (int Width, int Height) DibSize(byte[] dib) => dib.Length < 12 ? (0, 0)
        : (BinaryPrimitives.ReadInt32LittleEndian(dib.AsSpan(4)), Math.Abs(BinaryPrimitives.ReadInt32LittleEndian(dib.AsSpan(8))));

    /// <summary>A BMP file is the bitmap with a 14-byte file header that says where the pixels start.</summary>
    /// <returns>Null when the bitmap header is not one Windows writes (BITMAPINFOHEADER, V4 or V5).</returns>
    internal static byte[]? BmpFromDib(byte[] dib)
    {
        if (dib.Length < 40) return null;
        var headerSize = BinaryPrimitives.ReadInt32LittleEndian(dib);
        if (headerSize is not (40 or 108 or 124) || headerSize > dib.Length) return null;
        var (width, height) = DibSize(dib);
        if (width <= 0 || height <= 0) return null;
        int bitCount = BinaryPrimitives.ReadUInt16LittleEndian(dib.AsSpan(14));
        var compression = BinaryPrimitives.ReadInt32LittleEndian(dib.AsSpan(16));
        var colorsUsed = BinaryPrimitives.ReadInt32LittleEndian(dib.AsSpan(32));
        var colors = colorsUsed != 0 ? colorsUsed : bitCount <= 8 ? 1 << bitCount : 0;
        // BI_BITFIELDS with the small header keeps its three color masks after the header.
        var masks = compression == 3 && headerSize == 40 ? 12 : 0;
        var offset = 14 + headerSize + masks + colors * 4;
        if (offset > dib.Length + 14) return null;
        var bmp = new byte[dib.Length + 14];
        bmp[0] = (byte)'B'; bmp[1] = (byte)'M';
        BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(2), bmp.Length);
        BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(10), offset);
        dib.CopyTo(bmp, 14);
        return bmp;
    }
}
