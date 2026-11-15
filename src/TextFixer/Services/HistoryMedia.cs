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

    // Documents the history may open in their default app. Programs, scripts and shortcuts are never opened.
    private static readonly HashSet<string> Documents = new(StringComparer.OrdinalIgnoreCase)
        { ".pdf", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx", ".txt", ".md", ".csv", ".rtf", ".odt", ".ods", ".odp" };

    /// <summary>Images, videos and the listed documents; everything else only shows its folder.</summary>
    internal static bool IsOpenable(string path) => ContentType(path) is not null || Documents.Contains(Path.GetExtension(path));

    private static readonly System.Text.RegularExpressions.Regex VideoId = new(@"^[\w-]{11}$");

    /// <returns>The video id of a YouTube watch, short, embed, live or youtu.be link, or null.</returns>
    internal static string? YouTubeId(string text)
    {
        if (!Uri.TryCreate(text.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http")) return null;
        var host = uri.Host.ToLowerInvariant();
        var segments = uri.AbsolutePath.Trim('/').Split('/');
        string? id = null;
        if (host == "youtu.be") id = segments[0];
        else if (host is "youtube.com" or "www.youtube.com" or "m.youtube.com" or "music.youtube.com")
        {
            if (segments[0] == "watch")
                id = uri.Query.TrimStart('?').Split('&').Select(p => p.Split('=', 2)).FirstOrDefault(p => p[0] == "v" && p.Length == 2)?[1];
            else if (segments.Length >= 2 && segments[0] is "shorts" or "embed" or "live") id = segments[1];
        }
        return id is not null && VideoId.IsMatch(id) ? id : null;
    }

    /// <returns>A web address the history may open in the browser: http or https only.</returns>
    internal static Uri? WebUrl(string text) =>
        Uri.TryCreate(text.Trim(), UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http" && !text.Trim().Any(char.IsWhiteSpace) ? uri : null;

    private static readonly System.Text.RegularExpressions.Regex Mail = new(@"^(?:mailto:)?([^\s@<>()"",;:]+@[^\s@<>()"",;:]+\.[A-Za-z]{2,})$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <returns>The address of a single e-mail address entry, or null.</returns>
    internal static string? MailAddress(string text) => Mail.Match(text.Trim()) is { Success: true } m ? m.Groups[1].Value : null;

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
