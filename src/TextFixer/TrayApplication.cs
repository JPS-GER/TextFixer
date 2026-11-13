using System.Runtime.InteropServices;
using TextFixer.Native;
using TextFixer.Services;
using TextFixer.Bridge;
using Microsoft.Win32;

namespace TextFixer;

internal sealed class TrayApplication : ApplicationContext
{
    private readonly NotifyIcon _tray;
    private Icon _trayIcon = Branding.CreateTrayIcon();
    private readonly HotkeyWindow _hotkey = new();
    private readonly ReviewWindow _review = new(new ChatGptReviewService());
    private readonly ChromeBridgeServer _chrome = new();
    private readonly AppSettings _settings = AppSettings.Load();
    private readonly ClipboardHistory _history = new();
    private readonly ClipboardMonitor _clipboard = new();
    private bool _capturing;
    private bool _exiting;

    public TrayApplication(bool startHidden)
    {
        var menu = new ContextMenuStrip
        {
            ForeColor = PetrolMenuColors.Text,
            BackColor = PetrolMenuColors.Background,
            Renderer = new PetrolMenuRenderer()
        };
        menu.Items.Add("Open TextFixer", null, (_, _) => _review.Open());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitThread());
        _tray = new NotifyIcon { Text = "TextFixer - Alt+Q review, Alt+E translate, Alt+W fact check", Icon = _trayIcon, ContextMenuStrip = menu, Visible = true };
        _tray.DoubleClick += (_, _) => _review.Open();
        _hotkey.Pressed += async (_, task) => await CaptureAsync(task);
        _review.FormClosed += (_, _) => ExitThread();
        if (!startHidden) _review.Open();
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        _review.ConfigureChrome(_chrome);
        _review.ConfigureHistory(_history, _clipboard, _settings);
        _clipboard.Copied += (_, c) => _history.Add(c.Text, c.Html, c.Rtf, DateTime.UtcNow);
        _clipboard.Enabled = _settings.ClipboardHistory;
        _chrome.MessageReceived += (_, e) => Dispatch(() => _review.ReceiveChromeMessage(e.SessionId, e.Message));
        _chrome.SessionConnected += (_, _) => Dispatch(_review.ChromeConnected);
        _chrome.SessionDisconnected += (_, e) => Dispatch(() => _review.ChromeDisconnected(e.SessionId));
        try { _chrome.Start(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { _review.Notify("Chrome connection unavailable. Another TextFixer instance may be using it. Clipboard capture remains available."); }
        var review = _hotkey.Register(HotkeyWindow.ReviewId, 0x51);
        var translate = _hotkey.Register(HotkeyWindow.TranslateId, 0x45);
        var factCheck = _hotkey.Register(HotkeyWindow.FactCheckId, 0x57);
        if (!review)
        {
            _review.Open();
            _review.Notify("Alt+Q is already in use. Close the conflicting app and restart TextFixer. You can still paste text into this window.");
        }
        else if (!translate) _review.Notify("Alt+E is already in use by another app. Close it and restart TextFixer to translate with Alt+E.");
        else if (!factCheck) _review.Notify("Alt+W is already in use by another app. Close it and restart TextFixer to fact-check with Alt+W.");
    }

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e) => Dispatch(() =>
    {
        var next = Branding.CreateTrayIcon();
        var previous = _trayIcon;
        _tray.Icon = next;
        _trayIcon = next;
        previous.Dispose();
    });

    private void Dispatch(Action action)
    {
        if (_exiting || _review.IsDisposed) return;
        try { _review.BeginInvoke((Action)(() => { if (!_exiting) action(); })); }
        catch (InvalidOperationException) { }
    }

    /// <param name="task">review, translate or factcheck.</param>
    private async Task CaptureAsync(string task)
    {
        if (_review.Busy) { _review.Open(); return; }
        if (_capturing) return;
        var target = TextTransfer.GetForegroundWindow();
        if (target == _review.Handle) { _review.Open(); return; }
        _capturing = true;
        _clipboard.Paused = true;
        try
        {
            var text = await TextTransfer.CaptureAsync(target);
            _review.SetCapture(text, task);
            _review.Open();
        }
        catch (Exception ex)
        {
            ReviewWindow.Diagnostic($"{task switch { "translate" => "Alt+E", "factcheck" => "Alt+W", _ => "Alt+Q" }} capture failed in {ProcessName(target)}: {ex.Message}");
            _review.Open(); _review.Notify(ex.Message);
        }
        finally { _capturing = false; _clipboard.Paused = false; }
    }

    private static string ProcessName(nint window)
    {
        try { GetWindowThreadProcessId(window, out var id); using var process = System.Diagnostics.Process.GetProcessById((int)id); return process.ProcessName; }
        catch { return "unknown app"; }
    }

    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint processId);

    protected override void ExitThreadCore()
    {
        if (_exiting) return;
        _exiting = true;
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        _tray.Visible = false;
        _tray.Dispose();
        _trayIcon.Dispose();
        _hotkey.Dispose();
        _chrome.Dispose();
        _clipboard.Dispose();
        _history.Dispose();
        _review.Shutdown();
        base.ExitThreadCore();
    }

    private sealed class HotkeyWindow : NativeWindow, IDisposable
    {
        [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterHotKey(nint window, int id, uint modifiers, uint key);
        [DllImport("user32.dll")] private static extern bool UnregisterHotKey(nint window, int id);
        internal const int ReviewId = 46, TranslateId = 47, FactCheckId = 48;
        /// <summary>Raised with the task of the pressed hotkey: review, translate or factcheck.</summary>
        internal event EventHandler<string>? Pressed;
        public HotkeyWindow() => CreateHandle(new CreateParams());
        /// <summary>Alt + key, no repeat.</summary>
        internal bool Register(int id, uint key) => RegisterHotKey(Handle, id, 0x4001, key);
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x0312 && (m.WParam == ReviewId || m.WParam == TranslateId || m.WParam == FactCheckId))
            {
                try { TextTransfer.MaskAltMenu(); }
                catch { return; }
                Pressed?.Invoke(this, m.WParam == TranslateId ? "translate" : m.WParam == FactCheckId ? "factcheck" : "review");
            }
            base.WndProc(ref m);
        }
        public void Dispose() { UnregisterHotKey(Handle, ReviewId); UnregisterHotKey(Handle, TranslateId); UnregisterHotKey(Handle, FactCheckId); DestroyHandle(); }
    }
}
