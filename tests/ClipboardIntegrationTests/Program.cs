using System.Runtime.InteropServices;
using System.Text;
using TextFixer;
using TextFixer.Native;

internal static class Program
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint CreateWindowStation(string name, uint flags, uint access, nint security);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetProcessWindowStation(nint station);
    [DllImport("user32.dll")] private static extern nint GetProcessWindowStation();
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint CreateDesktop(string name, nint device, nint deviceMode, uint flags, uint access, nint security);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetThreadDesktop(nint desktop);
    [DllImport("user32.dll")] private static extern bool OpenClipboard(nint window);
    [DllImport("user32.dll")] private static extern bool CloseClipboard();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterClipboardFormat(string name);
    [DllImport("user32.dll")] private static extern nint GetClipboardData(uint format);
    [DllImport("kernel32.dll")] private static extern nint GlobalLock(nint memory);
    [DllImport("kernel32.dll")] private static extern bool GlobalUnlock(nint memory);
    [DllImport("kernel32.dll")] private static extern nuint GlobalSize(nint memory);

    private sealed class Owner : NativeWindow, IDisposable
    {
        internal Owner() => CreateHandle(new CreateParams { Parent = (nint)(-3) });
        public void Dispose() => DestroyHandle();
    }

    [STAThread]
    private static int Main()
    {
        // A window station owns its clipboard. Never touch the user's interactive clipboard.
        // Isolation must succeed before any Clipboard, DataObject, OLE or window operation.
        var originalStation = GetProcessWindowStation();
        var station = CreateWindowStation("TextFixerClipboardTest-" + Guid.NewGuid().ToString("N"), 0, 0x000f037f, 0);
        if (station == 0 || !SetProcessWindowStation(station) || GetProcessWindowStation() == originalStation)
        {
            Console.WriteLine("SKIPPED: private clipboard station unavailable; user clipboard was not touched.");
            return 2;
        }
        var desktop = CreateDesktop("TextFixerTest", 0, 0, 0, 0x000f01ff, 0);
        if (desktop == 0 || !SetThreadDesktop(desktop))
        {
            Console.WriteLine("SKIPPED: private test desktop unavailable; user clipboard was not touched.");
            return 2;
        }
        // No desktop is made visible. The OS releases this test process's station at exit.
        using var owner = new Owner();
        const string original = "Größe - \"Zitat\" 😀 &";
        const string revised = "Größe - \"Zitate\" 😀 &";
        var document = new HtmlClipboard("<html><body><!--StartFragment-->", "<p>Größe - &quot;Zitat&quot; 😀 &amp;</p>", "<!--EndFragment--></body></html>", "https://example.test/base/");
        var raw = new DataObject(); raw.SetText(original, TextDataFormat.UnicodeText);
        raw.SetData(DataFormats.Html, false, new MemoryStream(Encoding.UTF8.GetBytes(document.Encode() + "\0")));
        Clipboard.SetDataObject(raw, true);
        var read = HtmlClipboard.Parse(Clipboard.GetText(TextDataFormat.Html)) ?? throw new Exception("WinForms could not decode raw CF_HTML as UTF-8.");
        if (read != document || Clipboard.GetText() != original) throw new Exception("Raw-to-WinForms Unicode round trip failed.");
        var start = original.IndexOf("Zitat", StringComparison.Ordinal);
        var encoded = read.Rewrite(original, [new EditorEdit(start, start + 5, "Zitat", "Zitate")]);
        TextTransfer.CopyRevision(revised, encoded);
        var format = RegisterClipboardFormat("HTML Format");
        var copied = ReadRaw(owner.Handle, format);
        if (copied != encoded || HtmlClipboard.Parse(copied)?.Fragment != "<p>Größe - &quot;Zitate&quot; 😀 &amp;</p>")
            throw new Exception("CopyRevision raw UTF-8 bytes or offsets were changed by WinForms.");
        using (var delayed = new DelayedPaste(revised, owner.Handle, encoded))
        {
            delayed.Publish();
            if (ReadRaw(owner.Handle, format) != encoded) throw new Exception("Delayed HTML rendering failed.");
            // Disposing materializes the still-delayed Unicode format.
        }
        if (ReadRaw(owner.Handle, format) != encoded || Clipboard.GetText() != revised)
            throw new Exception("Delayed-format materialization did not survive owner disposal.");
        Clipboard.Clear();
        Console.WriteLine("PASS: isolated real WinForms/raw clipboard UTF-8, CF_HTML offsets, rich copy, and delayed-format round trips. User clipboard untouched.");
        return 0;
    }

    private static string ReadRaw(nint owner, uint format)
    {
        if (!OpenClipboard(owner)) throw new Exception("Could not open private test clipboard.");
        try
        {
            var memory = GetClipboardData(format);
            if (memory == 0) throw new Exception("Missing private clipboard format.");
            var pointer = GlobalLock(memory);
            if (pointer == 0) throw new Exception("Could not lock private clipboard memory.");
            try
            {
                var bytes = new byte[checked((int)GlobalSize(memory))]; Marshal.Copy(pointer, bytes, 0, bytes.Length);
                var end = Array.IndexOf(bytes, (byte)0);
                return new UTF8Encoding(false, true).GetString(bytes, 0, end < 0 ? bytes.Length : end);
            }
            finally { GlobalUnlock(memory); }
        }
        finally { CloseClipboard(); }
    }
}
