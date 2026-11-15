namespace TextFixer.Services;

/// <summary>Media types the clipboard view may show from copied files, and byte ranges for video seeking.</summary>
internal static class HistoryMedia
{
    private static readonly Dictionary<string, string> Types = new(StringComparer.OrdinalIgnoreCase)
    {
        [".png"] = "image/png", [".jpg"] = "image/jpeg", [".jpeg"] = "image/jpeg", [".gif"] = "image/gif",
        [".webp"] = "image/webp", [".bmp"] = "image/bmp", [".avif"] = "image/avif",
        [".mp4"] = "video/mp4", [".m4v"] = "video/mp4", [".webm"] = "video/webm", [".mov"] = "video/quicktime",
        [".mkv"] = "video/x-matroska", [".avi"] = "video/x-msvideo", [".wmv"] = "video/x-ms-wmv",
    };

    /// <returns>The content type of an image or video file, or null for any other file.</returns>
    internal static string? ContentType(string path) => Types.GetValueOrDefault(Path.GetExtension(path));

    /// <summary>Reads "bytes=start-end", "bytes=start-" or "bytes=-suffix" for a file of <paramref name="length"/> bytes.</summary>
    internal static (long Start, long End)? ParseRange(string header, long length)
    {
        if (length <= 0 || !header.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase) || header.Contains(',')) return null;
        var parts = header[6..].Split('-', 2);
        if (parts.Length != 2) return null;
        long start, end;
        if (parts[0].Length == 0)
        {
            if (!long.TryParse(parts[1], out var suffix) || suffix <= 0) return null;
            start = Math.Max(0, length - suffix); end = length - 1;
        }
        else
        {
            if (!long.TryParse(parts[0], out start)) return null;
            end = parts[1].Length == 0 ? length - 1 : long.TryParse(parts[1], out var e) ? Math.Min(e, length - 1) : -1;
        }
        return start >= 0 && start <= end && end < length ? (start, end) : null;
    }

    /// <summary>Reads at most <paramref name="count"/> bytes from the current position, then closes the file.</summary>
    internal sealed class Slice(Stream inner, long count) : Stream
    {
        private readonly long _count = count;
        private long _left = count;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _count;
        public override long Position { get => _count - _left; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int size)
        {
            if (_left <= 0) return 0;
            var read = inner.Read(buffer, offset, (int)Math.Min(size, _left));
            _left -= read;
            return read;
        }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int size) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
    }
}
