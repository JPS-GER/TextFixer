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
    private readonly AppSettings _settings = AppSettings.Load();
    private readonly HotkeyManager _hotkey;
    private readonly ReviewWindow _review = new(new ChatGptReviewService());
    private readonly ChromeBridgeServer _chrome = new();
    private readonly ClipboardHistory _history = new();
    private readonly ClipboardMonitor _clipboard = new();
    private bool _capturing;
    private bool _exiting;

    public TrayApplication(bool startHidden)
    {
        _hotkey = new HotkeyManager(_settings);
        var menu = new ContextMenuStrip
        {
            ForeColor = PetrolMenuColors.Text,
            BackColor = PetrolMenuColors.Background,
            Renderer = new PetrolMenuRenderer()
        };
        menu.Items.Add("Open TextFixer", null, (_, _) => _review.Open());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitThread());
        _tray = new NotifyIcon { Text = "TextFixer", Icon = _trayIcon, ContextMenuStrip = menu, Visible = true };
        _tray.DoubleClick += (_, _) => _review.Open();
        _hotkey.Pressed += async (_, task) => await CaptureAsync(task);
        _hotkey.Changed += (_, _) => UpdateTooltip();
        _review.FormClosed += (_, _) => ExitThread();
        if (!startHidden) _review.Open();
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        _review.ConfigureChrome(_chrome);
        _review.ConfigureHistory(_history, _clipboard, _settings);
        _review.ConfigureShortcuts(_hotkey);
        _clipboard.Copied += (_, c) => _history.Add(c.Text, c.Html, c.Rtf, DateTime.UtcNow);
        _clipboard.Enabled = _settings.ClipboardHistory;
        _chrome.MessageReceived += (_, e) => Dispatch(() => _review.ReceiveChromeMessage(e.SessionId, e.Message));
        _chrome.SessionConnected += (_, _) => Dispatch(_review.ChromeConnected);
        _chrome.SessionDisconnected += (_, e) => Dispatch(() => _review.ChromeDisconnected(e.SessionId));
        try { _chrome.Start(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { _review.Notify("Chrome connection unavailable. Another TextFixer instance may be using it. Clipboard capture remains available."); }
        _hotkey.RegisterAll();
        var blocked = Shortcuts.Tasks.Where(t => !_hotkey.IsRegistered(t)).ToArray();
        if (blocked.Length > 0)
        {
            if (blocked.Contains(Shortcuts.Open)) _review.Open();
            var names = string.Join(", ", blocked.Select(t => $"{_hotkey.Label(t)} ({Shortcuts.TaskName(t)})"));
            _review.Notify($"{names} {(blocked.Length == 1 ? "is" : "are")} already in use by another app. Choose another shortcut in Settings > Shortcuts. You can still paste text into this window.");
        }
    }

    private void UpdateTooltip()
    {
        var set = Shortcuts.Tasks.Where(t => _hotkey.Label(t).Length > 0).Select(t => $"{_hotkey.Label(t)} {Shortcuts.TaskName(t).ToLowerInvariant()}");
        var text = "TextFixer - " + string.Join(", ", set);
        _tray.Text = text.Length <= 127 ? text : "TextFixer";
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

    /// <param name="task">open or a task from <see cref="Shortcuts.Tasks"/>.</param>
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
            ReviewWindow.Diagnostic($"{_hotkey.Label(task)} capture failed in {ProcessName(target)}: {ex.Message}");
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
}
