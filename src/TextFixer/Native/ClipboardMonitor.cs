using System.Runtime.InteropServices;
using System.Text;

namespace TextFixer.Native;

/// <summary>A copy: text with optional HTML and RTF, or an image (PNG or BMP bytes), or a list of files.</summary>
internal sealed record ClipboardContent(string Text, string? Html, string? Rtf)
{
    public byte[]? Image { get; init; }
    public string? ImageType { get; init; }
    public int ImageWidth { get; init; }
    public int ImageHeight { get; init; }
    public string[]? Files { get; init; }
}

/// <summary>
/// Reports copies for the clipboard history: files (as paths), text, and images when <see cref="ImagesEnabled"/>. Content marked for exclusion (password managers,
/// TextFixer's own temporary Alt+Q data) is skipped before any of its data is read.
/// </summary>
internal sealed class ClipboardMonitor : NativeWindow, IDisposable
{
    [DllImport("user32.dll", SetLastError = true)] private static extern bool AddClipboardFormatListener(nint window);
    [DllImport("user32.dll")] private static extern bool RemoveClipboardFormatListener(nint window);
    [DllImport("user32.dll")] private static extern bool OpenClipboard(nint owner);
    [DllImport("user32.dll")] private static extern bool CloseClipboard();
    [DllImport("user32.dll")] private static extern bool IsClipboardFormatAvailable(uint format);
    [DllImport("user32.dll")] private static extern nint GetClipboardData(uint format);
    [DllImport("user32.dll")] private static extern uint GetClipboardSequenceNumber();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterClipboardFormat(string name);
    [DllImport("kernel32.dll")] private static extern nint GlobalLock(nint memory);
    [DllImport("kernel32.dll")] private static extern bool GlobalUnlock(nint memory);
    [DllImport("kernel32.dll")] private static extern nuint GlobalSize(nint memory);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern uint DragQueryFile(nint drop, uint index, StringBuilder? file, uint length);

    private const int WmClipboardUpdate = 0x031D;
    private const uint CfUnicodeText = 13, CfDib = 8, CfHdrop = 15;
    internal const int MaxImageBytes = 30 * 1024 * 1024;
    private const int MaxFiles = 200;
    private const int MaxTextChars = 1_000_000;
    private const int MaxFormatBytes = 4 * 1024 * 1024;
    private const int MaxOpenAttempts = 5;

    private readonly System.Windows.Forms.Timer _debounce = new() { Interval = 150 };
    private readonly uint _exclude = RegisterClipboardFormat("ExcludeClipboardContentFromMonitorProcessing");
    private readonly uint _canInclude = RegisterClipboardFormat("CanIncludeInClipboardHistory");
    private readonly uint _html = RegisterClipboardFormat("HTML Format");
    private readonly uint _rtf = RegisterClipboardFormat("Rich Text Format");
    private readonly uint _png = RegisterClipboardFormat("PNG");
    private bool _listening;
    private uint _ignored, _handled;
    private int _openAttempts;

    internal event EventHandler<ClipboardContent>? Copied;

    /// <summary>While true, updates wait (for example during an Alt+Q capture that owns the clipboard).</summary>
    internal bool Paused { get; set; }

    /// <summary>Records copied images (Settings > Clipboard history > Record images).</summary>
    internal bool ImagesEnabled { get; set; } = true;

    internal ClipboardMonitor()
    {
        CreateHandle(new CreateParams());
        _debounce.Tick += (_, _) => OnTick();
    }

    internal bool Enabled
    {
        get => _listening;
        set
        {
            if (value == _listening) return;
            if (value) { _listening = AddClipboardFormatListener(Handle); return; }
            RemoveClipboardFormatListener(Handle);
            _listening = false;
            _debounce.Stop();
        }
    }

    /// <summary>Skips the clipboard content that is current now, such as an entry TextFixer just restored.</summary>
    internal void IgnoreCurrent() => _ignored = GetClipboardSequenceNumber();

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WmClipboardUpdate && _listening)
        {
            _openAttempts = 0;
            _debounce.Stop();
            _debounce.Start();
        }
        base.WndProc(ref m);
    }

    private void OnTick()
    {
        _debounce.Stop();
        if (!_listening) return;
        if (Paused) { _debounce.Start(); return; }
        var sequence = GetClipboardSequenceNumber();
        if (sequence == _ignored || sequence == _handled) return;
        ClipboardContent? content;
        try
        {
            if (!TryRead(out content))
            {
                if (++_openAttempts < MaxOpenAttempts) _debounce.Start();
                return;
            }
        }
        catch { return; } // A misbehaving clipboard owner must never break the app.
        _handled = sequence;
        if (content is not null) Copied?.Invoke(this, content);
    }

    /// <returns>False when the clipboard could not be opened; <paramref name="content"/> is null when nothing should be recorded.</returns>
    private bool TryRead(out ClipboardContent? content)
    {
        content = null;
        if (IsClipboardFormatAvailable(_exclude)) return true;
        var files = IsClipboardFormatAvailable(CfHdrop);
        var text = IsClipboardFormatAvailable(CfUnicodeText);
        var image = ImagesEnabled && ((_png != 0 && IsClipboardFormatAvailable(_png)) || IsClipboardFormatAvailable(CfDib));
        if (!files && !text && !image) return true;
        if (!OpenClipboard(Handle)) return false;
        try
        {
            if (_canInclude != 0 && IsClipboardFormatAvailable(_canInclude)
                && ReadBytes(_canInclude, 4) is { Length: >= 4 } flag && BitConverter.ToInt32(flag) == 0) return true;
            // Files copied in Explorer: only the paths are kept, the files stay where they are.
            if (files && ReadFiles() is { Length: > 0 } paths) { content = new ClipboardContent("", null, null) { Files = paths }; return true; }
            if (text)
            {
                var unicode = ReadBytes(CfUnicodeText, MaxTextChars * 2 + 2);
                if (unicode is null) return true;
                var value = Encoding.Unicode.GetString(unicode);
                var end = value.IndexOf('\0');
                if (end >= 0) value = value[..end];
                if (string.IsNullOrWhiteSpace(value)) return true;
                content = new ClipboardContent(value, ReadString(_html, Encoding.UTF8), ReadString(_rtf, Encoding.Latin1));
                return true;
            }
            // Images are stored as delivered: PNG when the source offers it, otherwise the bitmap with a BMP file header.
            if (_png != 0 && IsClipboardFormatAvailable(_png) && ReadBytes(_png, MaxImageBytes) is { } png && ClipboardImage.PngSize(png) is var (pw, ph) && pw > 0)
            { content = new ClipboardContent("", null, null) { Image = png, ImageType = "image/png", ImageWidth = pw, ImageHeight = ph }; return true; }
            if (IsClipboardFormatAvailable(CfDib) && ReadBytes(CfDib, MaxImageBytes) is { } dib && ClipboardImage.BmpFromDib(dib) is { } bmp)
            {
                var (bw, bh) = ClipboardImage.DibSize(dib);
                content = new ClipboardContent("", null, null) { Image = bmp, ImageType = "image/bmp", ImageWidth = bw, ImageHeight = bh };
            }
            return true;
        }
        finally { CloseClipboard(); }
    }

    private static string[]? ReadFiles()
    {
        var drop = GetClipboardData(CfHdrop);
        if (drop == 0) return null;
        var count = DragQueryFile(drop, 0xFFFFFFFF, null, 0);
        if (count == 0 || count > MaxFiles) return null;
        var files = new List<string>();
        for (uint i = 0; i < count; i++)
        {
            var length = DragQueryFile(drop, i, null, 0);
            if (length == 0 || length > 32767) continue;
            var name = new StringBuilder((int)length + 1);
            if (DragQueryFile(drop, i, name, length + 1) > 0) files.Add(name.ToString());
        }
        return [.. files];
    }

    private string? ReadString(uint format, Encoding encoding)
    {
        if (format == 0 || !IsClipboardFormatAvailable(format)) return null;
        var bytes = ReadBytes(format, MaxFormatBytes);
        if (bytes is null) return null;
        var end = Array.IndexOf(bytes, (byte)0);
        return encoding.GetString(bytes, 0, end >= 0 ? end : bytes.Length) is { Length: > 0 } value ? value : null;
    }

    /// <returns>Null when the format is missing or larger than <paramref name="limit"/>.</returns>
    private static byte[]? ReadBytes(uint format, int limit)
    {
        var memory = GetClipboardData(format);
        if (memory == 0) return null;
        var size = GlobalSize(memory);
        if (size == 0 || size > (nuint)limit) return null;
        var pointer = GlobalLock(memory);
        if (pointer == 0) return null;
        try
        {
            var bytes = new byte[(int)size];
            Marshal.Copy(pointer, bytes, 0, bytes.Length);
            return bytes;
        }
        finally { GlobalUnlock(memory); }
    }

    public void Dispose()
    {
        Enabled = false;
        _debounce.Dispose();
        DestroyHandle();
    }
}
