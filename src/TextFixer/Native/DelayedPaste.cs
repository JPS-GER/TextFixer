using System.Runtime.InteropServices;
using System.Text;

namespace TextFixer.Native;

// Keep the revision on the clipboard until the target asks Windows to render Unicode text.
// This avoids restoring the old clipboard before a busy browser has read the paste.
internal sealed class DelayedPaste : NativeWindow, IDisposable
{
    [DllImport("user32.dll")] private static extern bool OpenClipboard(nint owner);
    [DllImport("user32.dll")] private static extern bool CloseClipboard();
    [DllImport("user32.dll")] private static extern bool EmptyClipboard();
    [DllImport("user32.dll")] private static extern nint GetClipboardOwner();
    [DllImport("user32.dll")] private static extern nint GetOpenClipboardWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint processId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterClipboardFormat(string name);
    [DllImport("user32.dll")] private static extern nint SetClipboardData(uint format, nint memory);
    [DllImport("kernel32.dll")] private static extern nint GlobalAlloc(uint flags, nuint bytes);
    [DllImport("kernel32.dll")] private static extern nint GlobalLock(nint memory);
    [DllImport("kernel32.dll")] private static extern bool GlobalUnlock(nint memory);
    [DllImport("kernel32.dll")] private static extern nint GlobalFree(nint memory);

    private readonly string _text;
    private readonly string? _html;
    private readonly uint _htmlFormat;
    private readonly uint _targetProcess;
    private readonly TaskCompletionSource<bool> _read = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _textRendered;
    private bool _htmlRendered;
    internal bool StillOwnsClipboard => Handle != 0 && GetClipboardOwner() == Handle;

    internal DelayedPaste(string text, nint target, string? html = null)
    {
        _text = text;
        _html = html;
        if (html is not null) _htmlFormat = RegisterClipboardFormat("HTML Format");
        if (html is not null && _htmlFormat == 0) throw new InvalidOperationException("Could not prepare the formatted clipboard data.");
        GetWindowThreadProcessId(target, out _targetProcess);
        CreateHandle(new CreateParams());
    }

    internal void Publish()
    {
        if (!OpenClipboard(Handle)) throw new InvalidOperationException("The clipboard is busy. Try again in a moment.");
        try
        {
            if (!EmptyClipboard()) throw new InvalidOperationException("Could not prepare the clipboard.");
            foreach (var format in new[] { "ExcludeClipboardContentFromMonitorProcessing", "CanIncludeInClipboardHistory", "CanUploadToCloudClipboard" })
                Put(RegisterClipboardFormat(format), [0, 0, 0, 0]);
            SetClipboardData(13, 0); // CF_UNICODETEXT, delayed rendering
            if (_htmlFormat != 0) SetClipboardData(_htmlFormat, 0);
        }
        finally { CloseClipboard(); }
    }

    internal async Task<bool> WaitForTargetReadAsync(TimeSpan timeout)
    {
        var completed = await Task.WhenAny(_read.Task, Task.Delay(timeout));
        return completed == _read.Task && await _read.Task;
    }

    private static void Put(uint format, byte[] bytes)
    {
        if (format == 0) throw new InvalidOperationException("Could not register a clipboard format.");
        var memory = GlobalAlloc(2, (nuint)bytes.Length);
        if (memory == 0) throw new OutOfMemoryException();
        var pointer = GlobalLock(memory);
        if (pointer == 0) { GlobalFree(memory); throw new InvalidOperationException("Could not prepare clipboard memory."); }
        try { Marshal.Copy(bytes, 0, pointer, bytes.Length); }
        finally { GlobalUnlock(memory); }
        if (SetClipboardData(format, memory) == 0)
        {
            GlobalFree(memory);
            throw new InvalidOperationException("Could not write the clipboard format.");
        }
    }

    private void Render(uint format)
    {
        if (format == 13 && !_textRendered)
        {
            Put(13, Encoding.Unicode.GetBytes(_text + "\0"));
            _textRendered = true;
        }
        else if (format == _htmlFormat && _html is not null && !_htmlRendered)
        {
            Put(_htmlFormat, Encoding.UTF8.GetBytes(_html + "\0"));
            _htmlRendered = true;
        }
    }

    protected override void WndProc(ref Message message)
    {
        if (message.Msg == 0x0305 && (message.WParam == 13 || (_htmlFormat != 0 && message.WParam == _htmlFormat))) // WM_RENDERFORMAT
        {
            try
            {
                var reader = GetOpenClipboardWindow();
                GetWindowThreadProcessId(reader, out var readerProcess);
                Render((uint)message.WParam); // Render only wParam; the reader already holds the clipboard.
                // Chrome may read from a helper without an HWND. In that case keep the revision.
                if (readerProcess == _targetProcess && readerProcess != 0) _read.TrySetResult(true);
            }
            catch { _read.TrySetResult(false); }
            return;
        }
        if (message.Msg == 0x0306) { PreserveRevision(); return; } // WM_RENDERALLFORMATS
        base.WndProc(ref message);
    }

    private void PreserveRevision()
    {
        if (!OpenClipboard(Handle)) return;
        try
        {
            if (StillOwnsClipboard)
            {
                Render(13);
                if (_htmlFormat != 0) Render(_htmlFormat);
            }
        }
        catch { /* Do not throw through a native window procedure. */ }
        finally { CloseClipboard(); }
    }

    public void Dispose()
    {
        // Materialize the revision on timeout so it survives destruction of this owner window.
        PreserveRevision();
        DestroyHandle();
    }
}
