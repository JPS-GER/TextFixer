using System.Diagnostics;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using TextFixer.Native;
using TextFixer.Services;
using TextFixer.Bridge;
using System.Runtime.InteropServices;

namespace TextFixer;

internal sealed class ReviewWindow : Form
{
    private readonly ChatGptReviewService _service;
    private readonly WebView2 _web = new() { Dock = DockStyle.Fill, DefaultBackgroundColor = Color.FromArgb(0x0a, 0x25, 0x32) };
    private readonly Icon _windowIcon = Branding.CreateWindowIcon();
    private CapturedText? _capture;
    private sealed record ChromeCapture(string SessionId, string CaptureId, string Text, bool HasFormatting);
    private ChromeCapture? _chromeCapture;
    private ChromeBridgeServer? _chrome;
    private TaskCompletionSource<JsonElement>? _chromeApply;
    private ChromeCapture? _applyingChromeCapture;
    private bool _ready, _shutdown;
    private string? _pendingNotice;
    private CancellationTokenSource? _operation;
    private bool _initializing;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    internal bool Busy => _operation is not null;

    internal ReviewWindow(ChatGptReviewService service)
    {
        _service = service;
        Text = "TextFixer";
        BackColor = Color.FromArgb(0x0a, 0x25, 0x32);
        ForeColor = Color.FromArgb(0xe8, 0xee, 0xf1);
        Icon = _windowIcon;
        Disposed += (_, _) => _windowIcon.Dispose();
        MinimumSize = new Size(740, 600);
        StartPosition = FormStartPosition.Manual;
        Controls.Add(_web);
        Shown += async (_, _) => { if (!_ready && !_initializing) await InitializeAsync(); };
        FormClosing += (_, e) => { if (!_shutdown) { e.Cancel = true; Hide(); } };
    }

    protected override void OnLoad(EventArgs e)
    {
        // OnLoad runs once, after WinForms has established the window's DPI.
        var workingArea = Screen.FromPoint(Cursor.Position).WorkingArea;
        Location = workingArea.Location;
        var layout = WindowLayout.Initial(workingArea, DeviceDpi);
        MinimumSize = layout.Minimum;
        Bounds = layout.Bounds;
        base.OnLoad(e);
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint window, int attribute, ref int value, int size);

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        var dark = 1;
        try
        {
            if (DwmSetWindowAttribute(Handle, 20, ref dark, sizeof(int)) != 0)
                DwmSetWindowAttribute(Handle, 19, ref dark, sizeof(int));
        }
        catch (DllNotFoundException) { }
        catch (EntryPointNotFoundException) { }
    }

    internal void Open()
    {
        Show();
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        if (!WindowFocus.BringOwnWindowForward(Handle)) Diagnostic("Foreground activation was refused by Windows.");
    }
    internal void Shutdown() { _shutdown = true; _operation?.Cancel(); Close(); Dispose(); }
    internal void SetCapture(CapturedText capture) { _chromeCapture = null; _capture = capture; SendCapture(); }
    internal void ConfigureChrome(ChromeBridgeServer chrome) => _chrome = chrome;
    internal void ChromeConnected() => SendChromeStatus();
    internal void ReceiveChromeMessage(string sessionId, JsonElement message)
    {
        try
        {
            var kind = message.GetProperty("kind").GetString();
            if (kind == "capture")
            {
                if (Busy) { Notify("Finish or cancel the current operation, then capture your Chrome selection again."); return; }
                var captureId = message.GetProperty("captureId").GetString();
                var text = message.GetProperty("text").GetString();
                if (!Guid.TryParse(captureId, out _) || string.IsNullOrWhiteSpace(text) || text.Length > 30000) return;
                _chromeCapture = new ChromeCapture(sessionId, captureId!, text, message.TryGetProperty("hasFormatting", out var formatting) && formatting.ValueKind == JsonValueKind.True);
                _capture = null; SendCapture(); Open();
            }
            else if (kind == "applyResult" && _applyingChromeCapture is { } applying
                && sessionId == applying.SessionId && message.GetProperty("captureId").GetString() == applying.CaptureId)
                _chromeApply?.TrySetResult(message.Clone());
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException)
        { Notify("Chrome sent an invalid message. Capture the text again."); }
    }
    internal void ChromeDisconnected(string sessionId)
    {
        SendChromeStatus();
        if (_chromeCapture?.SessionId == sessionId)
        {
            _chromeCapture = null;
            Send(new { type = "targetLost" });
            Notify("Chrome disconnected. Capture the text again before applying corrections.");
        }
        if (_applyingChromeCapture?.SessionId == sessionId)
            _chromeApply?.TrySetException(new IOException("Chrome disconnected while applying. Check the editor before trying again."));
    }
    internal void Notify(string message)
    {
        if (_ready) Send(new { type = "notice", message });
        else _pendingNotice = message;
    }
    private void Send(object value) { if (_ready && !_web.IsDisposed) _web.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(value, JsonOptions)); }
    private void SendCapture() => Send(new { type = "capture", text = _chromeCapture?.Text ?? _capture?.Text ?? "", canReplace = _chromeCapture is not null || _capture is not null, hasFormatting = _chromeCapture?.HasFormatting ?? _capture?.HasFormatting ?? false, canPreserveFormatting = _capture?.Html is not null, mode = _chromeCapture is null ? "clipboard" : "chrome" });
    private void SendChromeStatus() => Send(new { type = "chromeStatus", connected = _chrome?.SessionIds.Count > 0, registered = ChromeSetup.IsRegistered(), folder = ChromeSetup.ExtensionFolder, extensionId = ChromeSetup.ExtensionId });

    private async Task InitializeAsync()
    {
        _initializing = true;
        try
        {
            var profile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TextFixer", "WebView2");
            Diagnostic("Initializing WebView2; runtime=" + CoreWebView2Environment.GetAvailableBrowserVersionString());
            var environment = await CoreWebView2Environment.CreateAsync(null, profile);
            environment.BrowserProcessExited += (_, e) => Diagnostic("BrowserProcessExited: " + e.BrowserProcessExitKind);
            await _web.EnsureCoreWebView2Async(environment);
            var core = _web.CoreWebView2;
            core.Profile.PreferredColorScheme = CoreWebView2PreferredColorScheme.Dark;
            core.ProcessFailed += (_, e) =>
            {
                Diagnostic($"ProcessFailed: {e.ProcessFailedKind}; reason={e.Reason}; exit={e.ExitCode}");
                if (e.ProcessFailedKind is CoreWebView2ProcessFailedKind.BrowserProcessExited or CoreWebView2ProcessFailedKind.RenderProcessExited or CoreWebView2ProcessFailedKind.RenderProcessUnresponsive)
                    ShowInterfaceFailure("The WebView2 interface stopped responding. Close TextFixer from its tray menu, then start it again. No automatic replacement will be attempted.");
            };
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.AreHostObjectsAllowed = false;
            core.AddWebResourceRequestedFilter("https://textfixer.local/*", CoreWebView2WebResourceContext.All);
            core.WebResourceRequested += (_, request) =>
            {
                var asset = WebAssets.Find(new Uri(request.Request.Uri).AbsolutePath);
                request.Response = asset is { } found
                    ? core.Environment.CreateWebResourceResponse(new MemoryStream(found.Data), 200, "OK", "Content-Type: " + found.Type + "\r\nCache-Control: no-store")
                    : core.Environment.CreateWebResourceResponse(null, 404, "Not Found", "Cache-Control: no-store");
            };
            core.NavigationStarting += (_, e) => { if (!e.Uri.StartsWith("https://textfixer.local/", StringComparison.Ordinal)) e.Cancel = true; };
            core.NewWindowRequested += (_, e) => e.Handled = true;
            core.PermissionRequested += (_, e) => e.State = CoreWebView2PermissionState.Deny;
            core.WebMessageReceived += HandleMessage;
            core.Navigate("https://textfixer.local/index.html");
        }
        catch (Exception ex)
        {
            Diagnostic($"Initialization failed: {ex.GetType().Name}; HRESULT=0x{ex.HResult:X8}");
            ShowInterfaceFailure("TextFixer could not open its WebView2 interface. Check that Microsoft Edge WebView2 Runtime is installed.\n\n" + ex.Message);
        }
        finally { _initializing = false; }
    }

    private void ShowInterfaceFailure(string message)
    {
        _ready = false;
        _operation?.Cancel();
        _web.Visible = false;
        var label = new Label { Dock = DockStyle.Fill, Padding = new Padding(32), Text = message + "\n\nDiagnostic log: %LOCALAPPDATA%\\TextFixer\\diagnostics.log", Font = new Font("Segoe UI", 12) };
        Controls.Add(label); label.BringToFront();
    }

    private static void Diagnostic(string message)
    {
        try
        {
            var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TextFixer");
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, "diagnostics.log");
            if (File.Exists(path) && new FileInfo(path).Length > 100000) File.WriteAllText(path, "");
            File.AppendAllText(path, DateTimeOffset.Now.ToString("O") + " " + message + Environment.NewLine);
        }
        catch { /* Diagnostics must never prevent startup. No text or credentials are logged. */ }
    }

    private async void HandleMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (e.Source != "https://textfixer.local/index.html") return;
        try
        {
            using var document = JsonDocument.Parse(e.WebMessageAsJson);
            var root = document.RootElement;
            var action = root.GetProperty("action").GetString();
            if (action == "ready")
            {
                _ready = true; SendCapture(); SendChromeStatus(); await SendStatusAsync();
                if (_pendingNotice is not null) { Notify(_pendingNotice); _pendingNotice = null; }
                return;
            }
            if (action == "cancel") { _operation?.Cancel(); return; }
            if (action == "usage") { Process.Start(new ProcessStartInfo(ChatGptReviewService.ManageUsageUrl) { UseShellExecute = true }); return; }
            if (Busy) return;
            if (action == "chromeStatus") { SendChromeStatus(); return; }
            if (action == "registerChrome")
            {
                ChromeSetup.Register(); SendChromeStatus();
                Notify("Chrome connection registered for this executable. Load or reload the TextFixer extension in chrome://extensions, then use its right-click review action.");
                return;
            }
            if (action == "extensionFolder")
            {
                var folder = ChromeSetup.ExtensionFolder ?? throw new InvalidOperationException("The chrome-extension folder was not found alongside this build.");
                Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true }); return;
            }
            if (action is not ("signin" or "signout" or "check" or "copy" or "apply")) return;
            _operation = new CancellationTokenSource();
            Send(new { type = "busy", busy = true, action });
            try
            {
                switch (action)
                {
                    case "signin":
                        Notify("Complete sign-in in your browser. You can cancel here.");
                        await _service.SignInAsync(_operation.Token);
                        Send(new { type = "connected" });
                        break;
                    case "signout": await _service.SignOutAsync(_operation.Token); break;
                    case "check":
                        var original = ReadText(root);
                        if (_capture is not null && _capture.Text != original) _capture = null;
                        if (_chromeCapture is not null && _chromeCapture.Text != original) _chromeCapture = null;
                        var result = await _service.CheckAsync(original, _operation.Token);
                        Send(new { type = "result", original, result, formattingSuggestions = _capture?.Html?.FindFormatting(original) ?? [], canReplace = _capture is not null || _chromeCapture is not null, canPreserveFormatting = _capture?.Html is not null, model = _service.ActiveModel, mode = _chromeCapture is null ? "clipboard" : "chrome" });
                        break;
                    case "copy":
                        var copyText = ReadText(root);
                        var copyHtml = PrepareClipboardHtml(root, copyText);
                        TextTransfer.CopyRevision(copyText, copyHtml);
                        Notify(copyHtml is null ? "Revised plain text copied to the clipboard." : "Revised text copied with its original paragraph markup and formatting.");
                        break;
                    case "apply":
                        var revision = ReadText(root);
                        if (_chromeCapture is not null)
                        {
                            await ApplyChromeAsync(root, revision, _operation.Token);
                            break;
                        }
                        if (_capture is null) throw new InvalidOperationException("Capture a selection with Alt+Q first, or use Copy revised text.");
                        var explicitPlainText = root.TryGetProperty("allowPlainText", out var allowPlainText) && allowPlainText.ValueKind == JsonValueKind.True;
                        if (_capture.Html is null && !explicitPlainText)
                            throw new InvalidOperationException("This is clipboard mode. Use Chrome's right-click review action for direct editing, or explicitly enable plain-text replacement.");
                        if (!root.TryGetProperty("original", out var originalElement) || originalElement.GetString() != _capture.Text)
                            throw new InvalidOperationException("The review no longer matches the captured selection. Check the text again.");
                        var capture = _capture;
                        var pasteHtml = PrepareClipboardHtml(root, revision);
                        bool consumed;
                        Hide();
                        try { consumed = await TextTransfer.ApplyAsync(capture, revision, pasteHtml); _capture = null; }
                        catch { Open(); throw; }
                        Send(new { type = "applied", clipboardRestored = consumed, formatted = pasteHtml is not null });
                        if (!consumed) { Open(); Notify("Paste was sent, but the target's clipboard read could not be confirmed. The revision stays on the clipboard. Check the editor before pasting again."); }
                        break;
                }
            }
            finally
            {
                _operation.Dispose(); _operation = null;
                Send(new { type = "busy", busy = false });
                await SendStatusAsync();
            }
        }
        catch (OperationCanceledException) { Notify("Operation cancelled."); }
        catch (ChatGptServiceException ex)
        {
            Send(new { type = "error", message = ex.Message, code = ex.Code, param = ex.ApiParam, usageLimit = ex.IsUsageLimit, requiresSignIn = ex.RequiresSignIn });
        }
        catch (Exception ex) { Notify(ex.Message); }
    }

    private string? PrepareClipboardHtml(JsonElement root, string revision)
    {
        if (_capture?.Html is not { } html || (root.TryGetProperty("allowPlainText", out var plain) && plain.ValueKind == JsonValueKind.True)) return null;
        if (!root.TryGetProperty("original", out var original) || original.GetString() != _capture.Text)
            throw new InvalidOperationException("The review no longer matches the copied formatting. Capture the text again.");
        var edits = EditorEdits.Validate(_capture.Text, revision, root.GetProperty("edits"));
        var ids = root.TryGetProperty("formattingIds", out var selections) ? selections.EnumerateArray().Select(x => x.GetInt32()).ToArray() : [];
        return html.FixFormatting(_capture.Text, ids, edits).Rewrite(_capture.Text, edits);
    }

    private async Task ApplyChromeAsync(JsonElement root, string revision, CancellationToken cancellationToken)
    {
        var capture = _chromeCapture!;
        if (root.GetProperty("original").GetString() != capture.Text)
            throw new InvalidOperationException("The review no longer matches the Chrome selection.");
        var edits = EditorEdits.Validate(capture.Text, revision, root.GetProperty("edits"));
        if (edits.Length == 0) { Notify("No corrections selected. The editor was not changed."); return; }
        _chromeApply = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _applyingChromeCapture = capture;
        _chromeCapture = null; // Consume once, even if disconnected or cancelled after sending.
        Hide();
        try
        {
            if (_chrome is null || !await _chrome.SendAsync(capture.SessionId, new { kind = "apply", capture.CaptureId, original = capture.Text, edits }, cancellationToken))
                throw new IOException("Chrome is not connected. Capture the text again.");
            var response = await _chromeApply.Task.WaitAsync(TimeSpan.FromSeconds(90), cancellationToken);
            var success = response.TryGetProperty("success", out var ok) && ok.ValueKind == JsonValueKind.True;
            var message = response.TryGetProperty("message", out var text) && text.ValueKind == JsonValueKind.String ? text.GetString()! : "Check the original editor for the result.";
            Send(new { type = "chromeApplied", success, message });
            if (!success) { Open(); Notify(message); }
        }
        catch
        {
            Open(); Send(new { type = "targetLost" });
            Notify("The Chrome operation did not complete normally. Some edits may have been applied. Check the editor before capturing again.");
            throw;
        }
        finally { _chromeApply = null; _applyingChromeCapture = null; }
    }

    private static string ReadText(JsonElement root)
    {
        var text = root.GetProperty("text").GetString() ?? "";
        if (string.IsNullOrWhiteSpace(text) || text.Length > 30000) throw new InvalidOperationException("Enter between 1 and 30,000 characters.");
        return text;
    }
    private async Task SendStatusAsync()
    {
        var status = await _service.GetStatusAsync();
        Send(new { type = "status", status });
    }
}
