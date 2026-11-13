using System.Diagnostics;
using System.Runtime.InteropServices;

namespace TextFixer.Native;

internal sealed record CapturedText(nint Window, string Text, bool HasFormatting, HtmlClipboard? Html = null);

internal static class TextTransfer
{
    [DllImport("user32.dll")] internal static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint window);
    [DllImport("user32.dll")] private static extern bool IsWindow(nint window);
    [DllImport("user32.dll")] private static extern bool IsIconic(nint window);
    [DllImport("user32.dll")] private static extern bool ShowWindow(nint window, int command);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint processId);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern nint OpenProcess(uint access, bool inherit, uint processId);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool OpenProcessToken(nint process, uint access, out nint token);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool GetTokenInformation(nint token, int informationClass, out int elevation, int size, out int returnedSize);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(nint handle);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] private static extern uint GetClipboardSequenceNumber();
    [DllImport("user32.dll")] private static extern nint GetClipboardOwner();
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, INPUT[] inputs, int size);

    [StructLayout(LayoutKind.Sequential)] private struct INPUT { public uint Type; public INPUTUNION Data; }
    [StructLayout(LayoutKind.Explicit)] private struct INPUTUNION
    {
        [FieldOffset(0)] public KEYBDINPUT Keyboard;
        [FieldOffset(0)] public MOUSEINPUT Mouse;
    }
    [StructLayout(LayoutKind.Sequential)] private struct KEYBDINPUT
    { public ushort Key, Scan; public uint Flags, Time; public nuint Extra; }
    [StructLayout(LayoutKind.Sequential)] private struct MOUSEINPUT
    { public int X, Y; public uint Data, Flags, Time; public nuint Extra; }

    internal static async Task WaitForModifiersAsync()
    {
        var timer = Stopwatch.StartNew();
        while (new[] { 0x10, 0x11, 0x12, 0x5B, 0x5C }.Any(k => (GetAsyncKeyState(k) & 0x8000) != 0))
        {
            if (timer.ElapsedMilliseconds > 3000) throw new InvalidOperationException("Release the shortcut keys and try again.");
            await Task.Delay(15);
        }
    }

    private static void Shortcut(ushort key)
    {
        INPUT Make(ushort k, bool up) => new() { Type = 1, Data = new INPUTUNION { Keyboard = new KEYBDINPUT { Key = k, Flags = up ? 2u : 0u } } };
        INPUT[] events = [Make(0x11, false), Make(key, false), Make(key, true), Make(0x11, true)];
        if (SendInput(4, events, Marshal.SizeOf<INPUT>()) != 4)
            throw new InvalidOperationException("Windows blocked keyboard input. Try copying the text into TextFixer manually.");
    }

    internal static void MaskAltMenu()
    {
        // Alt+Q consumes Q. Mark the held Alt as used before its release can activate a menu.
        if ((GetAsyncKeyState(0x12) & 0x8000) == 0) return;
        INPUT[] events = [
            new() { Type = 1, Data = new INPUTUNION { Keyboard = new KEYBDINPUT { Key = 0xE8 } } },
            new() { Type = 1, Data = new INPUTUNION { Keyboard = new KEYBDINPUT { Key = 0xE8, Flags = 2 } } }
        ];
        if (SendInput(2, events, Marshal.SizeOf<INPUT>()) != 2)
            throw new InvalidOperationException("Windows blocked the shortcut. Paste your text into TextFixer manually.");
    }

    private static void VerifyTargetAccess(nint target)
    {
        GetWindowThreadProcessId(target, out var processId);
        var process = OpenProcess(0x1000, false, processId);
        if (process == 0) throw new InvalidOperationException("Cannot access this app. It may be running as administrator. Copy your text manually instead.");
        nint token = 0;
        try
        {
            if (!OpenProcessToken(process, 8, out token) || !GetTokenInformation(token, 20, out var elevated, 4, out _))
                throw new InvalidOperationException("Cannot verify access to this app. Copy your text manually instead.");
            if (elevated != 0) throw new InvalidOperationException("TextFixer cannot edit an app running as administrator. Copy your text manually instead.");
        }
        finally { if (token != 0) CloseHandle(token); CloseHandle(process); }
    }

    private static DataObject Snapshot()
    {
        var result = new DataObject();
        IDataObject? source;
        try { source = Clipboard.GetDataObject(); }
        catch (ExternalException) { throw new InvalidOperationException("The clipboard is busy. Try again in a moment."); }
        if (source is null) return result;
        foreach (var format in new[] { DataFormats.UnicodeText, DataFormats.Text, DataFormats.Html, DataFormats.Rtf, DataFormats.Bitmap, DataFormats.FileDrop })
        {
            if (!source.GetDataPresent(format, false)) continue;
            var value = source.GetData(format, false);
            if (value is Image image) value = image.Clone();
            if (value is not null) result.SetData(format, false, value);
        }
        return result;
    }

    private static void ExcludeFromHistory(DataObject data)
    {
        data.SetData("ExcludeClipboardContentFromMonitorProcessing", new MemoryStream([0, 0, 0, 0]));
        data.SetData("CanIncludeInClipboardHistory", new MemoryStream([0, 0, 0, 0]));
        data.SetData("CanUploadToCloudClipboard", new MemoryStream([0, 0, 0, 0]));
    }

    private static void Restore(DataObject previous)
    {
        if (previous.GetFormats(false).Length == 0) { Clipboard.Clear(); return; }
        // Never republish a password-manager entry into clipboard history or cloud sync.
        ExcludeFromHistory(previous);
        Clipboard.SetDataObject(previous, true);
    }

    private static void SetTemporaryText(string text)
    {
        var data = new DataObject();
        data.SetText(text, TextDataFormat.UnicodeText);
        ExcludeFromHistory(data);
        Clipboard.SetDataObject(data, true);
    }

    internal static void CopyRevision(string text, string? html = null)
    {
        if (html is null) { Clipboard.SetText(text); return; }
        var data = new DataObject(); data.SetText(text, TextDataFormat.UnicodeText);
        data.SetText(html, TextDataFormat.Html); Clipboard.SetDataObject(data, true);
    }

    internal static async Task<CapturedText> CaptureAsync(nint target)
    {
        await WaitForModifiersAsync();
        if (target == 0 || GetForegroundWindow() != target)
            throw new InvalidOperationException("The active window changed. Select your text and press Alt+Q again.");
        VerifyTargetAccess(target);
        var previous = Snapshot();
        uint ownedSequence = 0;
        try
        {
            SetTemporaryText("TextFixer capture " + Guid.NewGuid());
            ownedSequence = GetClipboardSequenceNumber();
            Shortcut(0x43);
            var timer = Stopwatch.StartNew();
            while (GetClipboardSequenceNumber() == ownedSequence)
            {
                if (timer.ElapsedMilliseconds > 1500) throw new InvalidOperationException("No text was copied. Select text in an editable field and try Alt+Q again.");
                await Task.Delay(20);
            }
            // Copy may empty the clipboard and publish several formats in separate updates.
            var stability = Stopwatch.StartNew();
            var observed = GetClipboardSequenceNumber();
            while (stability.ElapsedMilliseconds < 120)
            {
                if (timer.ElapsedMilliseconds > 3000) throw new InvalidOperationException("The clipboard keeps changing. Try again when other copy operations finish.");
                await Task.Delay(20);
                var next = GetClipboardSequenceNumber();
                if (next != observed) { observed = next; stability.Restart(); }
            }
            ownedSequence = GetClipboardSequenceNumber();
            if (GetForegroundWindow() != target) throw new InvalidOperationException("The active window changed during capture. Try again.");
            var copyOwner = GetClipboardOwner();
            var text = Clipboard.GetText(TextDataFormat.UnicodeText);
            var hasHtml = Clipboard.ContainsText(TextDataFormat.Html);
            var html = hasHtml ? HtmlClipboard.Parse(Clipboard.GetText(TextDataFormat.Html)) : null;
            var hasFormatting = hasHtml || Clipboard.ContainsText(TextDataFormat.Rtf);
            if (GetClipboardOwner() != copyOwner) throw new InvalidOperationException("Another app changed the clipboard during capture. Try again.");
            // Reading a delayed source format can itself increment the sequence number.
            ownedSequence = GetClipboardSequenceNumber();
            if (string.IsNullOrWhiteSpace(text)) throw new InvalidOperationException("The selection contains no text.");
            if (text.Length > 30000) throw new InvalidOperationException("Select at most 30,000 characters for one check.");
            return new CapturedText(target, text, hasFormatting, html);
        }
        finally
        {
            // Do not overwrite an unrelated clipboard change made while we were waiting.
            if (ownedSequence != 0 && GetClipboardSequenceNumber() == ownedSequence)
                Restore(previous);
        }
    }

    internal static async Task<bool> ApplyAsync(CapturedText capture, string revision, string? html = null)
    {
        if (!IsWindow(capture.Window)) throw new InvalidOperationException("The original window is closed. Use Copy revised text instead.");
        VerifyTargetAccess(capture.Window);
        if (IsIconic(capture.Window)) ShowWindow(capture.Window, 9);
        if (!SetForegroundWindow(capture.Window)) throw new InvalidOperationException("Could not return to the original window. Use Copy revised text instead.");
        await Task.Delay(150);
        var current = await CaptureAsync(capture.Window);
        if (!string.Equals(current.Text, capture.Text, StringComparison.Ordinal))
            throw new InvalidOperationException("The selection changed. Nothing was replaced. Use Copy revised text instead.");
        if (html is not null && (current.Html is null || current.Html.Fragment != capture.Html?.Fragment))
            throw new InvalidOperationException("The selection's formatting changed. Capture and check it again before replacing it.");
        var previous = Snapshot();
        if (GetForegroundWindow() != capture.Window) throw new InvalidOperationException("The active window changed. Nothing was replaced.");
        using var paste = new DelayedPaste(revision, capture.Window, html);
        paste.Publish();
        Shortcut(0x56);
        var consumed = await paste.WaitForTargetReadAsync(TimeSpan.FromSeconds(5));
        // A timeout deliberately leaves the revision available, never restores stale content.
        if (!consumed) return false;
        await Task.Delay(200);
        if (paste.StillOwnsClipboard) Restore(previous);
        return true;
    }
}
