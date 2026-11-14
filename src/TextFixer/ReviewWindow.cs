using System.Diagnostics;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using TextFixer.Native;
using TextFixer.Services;
using Shortcut = TextFixer.Services.Shortcut;
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
    private ClipboardHistory? _history;
    private ClipboardMonitor? _clipboardMonitor;
    private AppSettings? _settings;
    private HotkeyManager? _hotkeys;
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
        ShowInTaskbar = false; // tray icon only
        Controls.Add(_web);
        Resize += (_, _) => { if (WindowState == FormWindowState.Minimized && Visible) Hide(); };
        Shown += async (_, _) => { if (!_ready && !_initializing) await InitializeAsync(); };
        FormClosing += (_, e) => { if (!_shutdown) { e.Cancel = true; Hide(); } };
        // A shortcut recording never outlives the visible window.
        VisibleChanged += (_, _) => { if (!Visible) _hotkeys?.Resume(); };
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
    /// <param name="task">open shows the task choice; any other task from <see cref="Shortcuts.Tasks"/> starts right away.</param>
    internal void SetCapture(CapturedText capture, string task = "review") { _chromeCapture = null; _capture = capture; _task = task; _captureId++; _factSources.Clear(); SendCapture(); }
    internal void ConfigureChrome(ChromeBridgeServer chrome) => _chrome = chrome;
    internal void ConfigureHistory(ClipboardHistory history, ClipboardMonitor monitor, AppSettings settings)
    {
        _history = history; _clipboardMonitor = monitor; _settings = settings;
        _service.PunctuationStyle = new Dictionary<string, string>(settings.PunctuationStyle);
        _service.PreferredModel = settings.Model;
        _service.PreferredTranslationModel = settings.TranslationModel;
        _service.PreferredFactCheckModel = settings.FactCheckModel;
        _service.EmailSpelling = settings.EmailSpelling;
        _service.AddressForm = settings.AddressForm;
        _service.ReviewDu = settings.ReviewDu;
        history.Changed += (_, _) => SendHistory();
    }
    internal void ConfigureShortcuts(HotkeyManager hotkeys)
    {
        _hotkeys = hotkeys;
        hotkeys.Changed += (_, _) => SendShortcuts();
    }
    private string ShortcutLabel(string task) => _hotkeys?.Label(task) ?? Shortcuts.Default(task)?.ToString() ?? "";
    /// <param name="error">A rejected change: task and reason, shown under that task's row.</param>
    private void SendShortcuts(string? errorTask = null, string? error = null) => Send(new
    {
        type = "shortcuts",
        shortcuts = Shortcuts.Tasks.ToDictionary(t => t, ShortcutLabel),
        defaults = Shortcuts.Tasks.ToDictionary(t => t, t => Shortcuts.Default(t)?.ToString() ?? ""),
        registered = Shortcuts.Tasks.ToDictionary(t => t, t => _hotkeys?.IsRegistered(t) ?? false),
        errorTask,
        error,
    });
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
                _capture = null; _task = "review"; _captureId++; _factSources.Clear(); SendCapture(); Open();
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
    private bool ReadOnlyCapture => _chromeCapture is null && _capture?.Editable == false;
    private bool CanReplace => _chromeCapture is not null || (_capture is not null && !ReadOnlyCapture);
    private string _task = "review";
    /// <summary>Increments with every capture. Task requests echo it so the window drops answers for an older capture.</summary>
    private int _captureId, _replyCaptureId;
    /// <summary>Source URLs of the last fact check for the current capture; openSource opens only these.</summary>
    private readonly HashSet<string> _factSources = new(StringComparer.Ordinal);
    private void SendCapture() => Send(new { type = "capture", captureId = _captureId, task = _task, text = _chromeCapture?.Text ?? _capture?.Text ?? "", canReplace = CanReplace, readOnly = ReadOnlyCapture, hasFormatting = _chromeCapture?.HasFormatting ?? _capture?.HasFormatting ?? false, canPreserveFormatting = _capture?.Html is not null, mode = _chromeCapture is null ? "clipboard" : "chrome" });
    /// <summary>One request per review tab, all sent at once. Each answer is shown as soon as it arrives.</summary>
    private async Task CheckByTabAsync(string original, CancellationToken cancellationToken)
    {
        var tabs = ReviewFocus.All;
        // Continuations run on the UI thread, so the running list and Send need no locking.
        var running = tabs.ToList();
        using var run = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Send(new { type = "tabProgress", captureId = _replyCaptureId, original, running = tabs.ToArray() });

        async Task CheckTabAsync(string focus)
        {
            Contracts.ReviewResult? result = null;
            string? error = null;
            try { result = await _service.CheckAsync(original, focus, run.Token); }
            catch (OperationCanceledException) { throw; }
            catch (ChatGptServiceException ex) when (!ex.RequiresSignIn && ex.Code != ChatGptProtocol.UsageLimitError) { error = ex.Message; }
            catch (HttpRequestException ex) { error = ex.Message; }
            catch
            {
                // Sign-in problems and an exhausted usage limit stop the other requests too.
                run.Cancel();
                throw;
            }
            running.Remove(focus);
            Send(new
            {
                type = "tabResult", captureId = _replyCaptureId, original, focus, result, error,
                pending = running.ToArray(),
                formattingSuggestions = _capture?.Html?.FindFormatting(original) ?? [],
                canReplace = CanReplace, readOnly = ReadOnlyCapture, canPreserveFormatting = _capture?.Html is not null,
                model = _service.ActiveModel, mode = _chromeCapture is null ? "clipboard" : "chrome",
            });
        }

        var requests = tabs.Select(CheckTabAsync).ToArray();
        try { await Task.WhenAll(requests); }
        catch
        {
            // Report the error that stopped the run, not the cancellations it caused.
            var stop = requests.Where(t => t.IsFaulted).Select(t => t.Exception!.InnerException!).FirstOrDefault(e => e is not OperationCanceledException);
            if (stop is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(stop);
            throw;
        }
    }

    private IReadOnlyList<ChatGptModel>? _modelList;

    /// <summary>Models the signed-in account can use, with the saved choice (null: automatic, the first listed).</summary>
    private async Task SendModelsAsync()
    {
        var selected = _settings?.Model;
        var translationSelected = _settings?.TranslationModel;
        var factCheckSelected = _settings?.FactCheckModel;
        if (!(await _service.GetStatusAsync()).Connected) { Send(new { type = "models", connected = false, models = Array.Empty<object>(), selected, translationSelected, factCheckSelected }); return; }
        try
        {
            _modelList = await _service.ListModelsAsync();
            Send(new { type = "models", connected = true, models = _modelList.Select(m => new { m.Slug, m.DisplayName }), selected, translationSelected, factCheckSelected });
        }
        catch (Exception ex) when (ex is ChatGptServiceException or HttpRequestException)
        {
            Send(new { type = "models", connected = true, models = Array.Empty<object>(), selected, translationSelected, factCheckSelected, error = ex.Message });
        }
    }

    private void SendReviewMode() => Send(new { type = "reviewMode", mode = _settings?.ReviewMode ?? AppSettings.SingleReview, punctuationStyle = _settings?.PunctuationStyle ?? Punctuation.Defaults(), emailSpelling = _settings?.EmailSpelling ?? WritingPreferences.Keep, addressForm = _settings?.AddressForm ?? WritingPreferences.Auto, reviewDu = _settings?.ReviewDu ?? WritingPreferences.Keep });

    private void SendHistory()
    {
        if (_history is null || _settings is null) return;
        Send(new
        {
            type = "history",
            enabled = _settings.ClipboardHistory,
            entries = _history.Entries.Select(e => new
            {
                e.Id,
                copiedAt = new DateTimeOffset(DateTime.SpecifyKind(e.CopiedAt, DateTimeKind.Utc)).ToUnixTimeMilliseconds(),
                preview = Preview(e.Text),
                hasFormatting = e.HasFormatting,
            }),
        });
    }

    private static string Preview(string text)
    {
        const int length = 300;
        if (text.Length <= length) return text;
        var end = char.IsHighSurrogate(text[length - 1]) ? length - 1 : length;
        return text[..end];
    }

    private void HandleHistory(string action, JsonElement root)
    {
        if (_history is null || _clipboardMonitor is null || _settings is null) return;
        switch (action)
        {
            case "historyRestore":
                var id = root.GetProperty("id").GetString() ?? "";
                if (_history.Find(id) is not { } entry) { SendHistory(); return; }
                var data = new DataObject();
                data.SetText(entry.Text, TextDataFormat.UnicodeText);
                // "Copy plain text" puts only the text on the clipboard; "Copy with formatting" adds the saved HTML and RTF.
                var plain = !root.TryGetProperty("plain", out var plainElement) || plainElement.ValueKind != JsonValueKind.False;
                if (!plain && entry.Html is not null) data.SetText(entry.Html, TextDataFormat.Html);
                if (!plain && entry.Rtf is not null) data.SetData(DataFormats.Rtf, entry.Rtf);
                try { Clipboard.SetDataObject(data, true); }
                catch (ExternalException) { Notify("The clipboard is busy. Try again in a moment."); return; }
                _clipboardMonitor.IgnoreCurrent();
                _history.MoveToTop(id, DateTime.UtcNow);
                Send(new { type = "historyCopied", id, plain });
                return;
            case "historyDelete": _history.Delete(root.GetProperty("id").GetString() ?? ""); return;
            case "historyClear": _history.Clear(); return;
            case "historyEnabled":
                var enabled = root.GetProperty("enabled").GetBoolean();
                _settings.ClipboardHistory = enabled;
                try { _settings.Save(); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Notify("The setting could not be saved: " + ex.Message); }
                _clipboardMonitor.Enabled = enabled;
                if (!enabled) _history.Clear();
                SendHistory();
                return;
            default: SendHistory(); return;
        }
    }

    private void SendAutostart() => Send(new { type = "autostart", enabled = Autostart.IsEnabled });
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

    internal static void Diagnostic(string message)
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
                _ready = true; SendCapture(); SendChromeStatus(); SendAutostart(); SendHistory(); SendReviewMode(); SendShortcuts(); await SendStatusAsync();
                if (_pendingNotice is not null) { Notify(_pendingNotice); _pendingNotice = null; }
                return;
            }
            if (action == "cancel") { _operation?.Cancel(); return; }
            if (action == "usage") { Process.Start(new ProcessStartInfo(ChatGptReviewService.ManageUsageUrl) { UseShellExecute = true }); return; }
            if (action is "historyList" or "historyRestore" or "historyDelete" or "historyClear" or "historyEnabled")
            {
                HandleHistory(action, root);
                return;
            }
            if (action == "models") { await SendModelsAsync(); return; }
            if (action == "shortcutRecording" && _hotkeys is not null)
            {
                if (root.TryGetProperty("active", out var active) && active.ValueKind == JsonValueKind.True) _hotkeys.Suspend();
                else _hotkeys.Resume();
                return;
            }
            if (action == "shortcut" && _hotkeys is not null && _settings is not null)
            {
                var task = root.TryGetProperty("task", out var taskElement) && taskElement.ValueKind == JsonValueKind.String ? taskElement.GetString() : null;
                if (task is null || !Shortcuts.Tasks.Contains(task)) { _hotkeys.Resume(); return; }
                var keys = root.TryGetProperty("keys", out var keysElement) && keysElement.ValueKind == JsonValueKind.String ? keysElement.GetString() : null;
                // An empty string removes an optional shortcut.
                var problem = string.IsNullOrEmpty(keys) ? _hotkeys.TryChange(task, null)
                    : Shortcut.Parse(keys) is { } shortcut ? _hotkeys.TryChange(task, shortcut) : "Use a letter, a digit or F1 to F24 as the key.";
                if (problem is null)
                {
                    try { _settings.Save(); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Notify("The setting could not be saved: " + ex.Message); }
                }
                else _hotkeys.Resume();
                SendShortcuts(problem is null ? null : task, problem);
                return;
            }
            if (action == "model" && _settings is not null)
            {
                var slug = root.TryGetProperty("slug", out var slugElement) && slugElement.ValueKind == JsonValueKind.String ? slugElement.GetString() : null;
                if (slug is not null && _modelList?.Any(m => m.Slug == slug) != true) return;
                switch (root.TryGetProperty("task", out var task) ? task.GetString() : null)
                {
                    case "translate": _settings.TranslationModel = slug; _service.PreferredTranslationModel = slug; break;
                    case "factcheck": _settings.FactCheckModel = slug; _service.PreferredFactCheckModel = slug; break;
                    default: _settings.Model = slug; _service.PreferredModel = slug; break;
                }
                try { _settings.Save(); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Notify("The setting could not be saved: " + ex.Message); }
                await SendModelsAsync();
                return;
            }
            if (action == "typography" && _settings is not null)
            {
                if (root.TryGetProperty("style", out var style) && style.ValueKind == JsonValueKind.Object) _settings.SetPunctuationStyle(style);
                _service.PunctuationStyle = new Dictionary<string, string>(_settings.PunctuationStyle);
                try { _settings.Save(); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Notify("The setting could not be saved: " + ex.Message); }
                SendReviewMode();
                return;
            }
            if (action == "writing" && _settings is not null)
            {
                if (root.TryGetProperty("email", out var email) && email.ValueKind == JsonValueKind.String && WritingPreferences.IsEmail(email.GetString())) _settings.EmailSpelling = email.GetString()!;
                if (root.TryGetProperty("address", out var address) && address.ValueKind == JsonValueKind.String && WritingPreferences.IsAddress(address.GetString())) _settings.AddressForm = address.GetString()!;
                if (root.TryGetProperty("reviewDu", out var reviewDu) && reviewDu.ValueKind == JsonValueKind.String && WritingPreferences.IsReviewDu(reviewDu.GetString())) _settings.ReviewDu = reviewDu.GetString()!;
                _service.EmailSpelling = _settings.EmailSpelling;
                _service.AddressForm = _settings.AddressForm;
                _service.ReviewDu = _settings.ReviewDu;
                try { _settings.Save(); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Notify("The setting could not be saved: " + ex.Message); }
                SendReviewMode();
                return;
            }
            if (action == "reviewMode" && _settings is not null)
            {
                var mode = root.GetProperty("mode").GetString();
                if (mode is AppSettings.SingleReview or AppSettings.FocusedReview)
                {
                    _settings.ReviewMode = mode;
                    try { _settings.Save(); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Notify("The setting could not be saved: " + ex.Message); }
                }
                SendReviewMode();
                return;
            }
            if (action == "autostart")
            {
                try { Autostart.Set(root.GetProperty("enabled").GetBoolean()); }
                finally { SendAutostart(); }
                return;
            }
            if (Busy) return;
            if (action == "chromeStatus") { SendChromeStatus(); SendAutostart(); return; }
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
            if (action == "openSource")
            {
                // Only exact https URLs cited by the current fact check; the WebView itself never navigates.
                var url = root.TryGetProperty("url", out var urlElement) && urlElement.ValueKind == JsonValueKind.String ? urlElement.GetString() : null;
                if (url is null || !_factSources.Contains(url) || FactCheckParser.ValidUrl(url) is null) return;
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
                return;
            }
            if (action is not ("signin" or "signout" or "check" or "copy" or "apply" or "translate" or "reviseTranslation" or "copyTranslation" or "applyTranslation" or "factCheck" or "write" or "copyResult" or "applyRewrite")) return;
            _replyCaptureId = root.TryGetProperty("captureId", out var requestCapture) && requestCapture.ValueKind == JsonValueKind.Number && requestCapture.TryGetInt32(out var id) ? id : _captureId;
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
                        if (_settings?.ReviewMode == AppSettings.FocusedReview)
                        {
                            await CheckByTabAsync(original, _operation.Token);
                            break;
                        }
                        var result = await _service.CheckAsync(original, _operation.Token);
                        Send(new { type = "result", captureId = _replyCaptureId, original, result, formattingSuggestions = _capture?.Html?.FindFormatting(original) ?? [], canReplace = CanReplace, readOnly = ReadOnlyCapture, canPreserveFormatting = _capture?.Html is not null, model = _service.ActiveModel, mode = _chromeCapture is null ? "clipboard" : "chrome" });
                        break;
                    case "translate":
                        var source = ReadText(root);
                        if (_capture is not null && _capture.Text != source) _capture = null;
                        var translation = await _service.TranslateAsync(source, _operation.Token);
                        Send(new { type = "translation", captureId = _replyCaptureId, original = source, translation, canReplace = _capture is not null && !ReadOnlyCapture, readOnly = ReadOnlyCapture, model = _service.ActiveModel });
                        break;
                    case "reviseTranslation":
                        var revisedFrom = ReadText(root);
                        var current = root.TryGetProperty("translation", out var currentElement) ? currentElement.GetString() ?? "" : "";
                        var wish = root.TryGetProperty("request", out var wishElement) ? wishElement.GetString() ?? "" : "";
                        if (string.IsNullOrWhiteSpace(current)) throw new InvalidOperationException("Translate the text first.");
                        var revisedTranslation = await _service.ReviseTranslationAsync(revisedFrom, current, wish, _operation.Token);
                        Send(new { type = "translation", captureId = _replyCaptureId, original = revisedFrom, translation = revisedTranslation, revised = true, canReplace = _capture is not null && _capture.Text == revisedFrom && !ReadOnlyCapture, readOnly = ReadOnlyCapture, model = _service.ActiveModel });
                        break;
                    case "factCheck":
                        var checkedText = ReadText(root);
                        if (_capture is not null && _capture.Text != checkedText) _capture = null;
                        if (_chromeCapture is not null && _chromeCapture.Text != checkedText) _chromeCapture = null;
                        var facts = await _service.FactCheckAsync(checkedText, _operation.Token);
                        if (_replyCaptureId == _captureId)
                        {
                            _factSources.Clear();
                            foreach (var cited in facts.Claims.SelectMany(c => c.Sources)) _factSources.Add(cited.Url);
                        }
                        Send(new { type = "factCheck", captureId = _replyCaptureId, original = checkedText, result = facts, canReplace = CanReplace, readOnly = ReadOnlyCapture, canPreserveFormatting = _capture?.Html is not null, mode = _chromeCapture is null ? "clipboard" : "chrome" });
                        break;
                    case "write":
                        // Rewrite, summary or explanation. A rewrite request applies to "text", which is the capture or the latest rewrite.
                        var kind = root.TryGetProperty("kind", out var kindElement) && kindElement.ValueKind == JsonValueKind.String ? kindElement.GetString() : null;
                        if (kind is not (ReviewPrompt.Rewrite or ReviewPrompt.Summary or ReviewPrompt.Explain)) break;
                        var writeFrom = ReadText(root);
                        var writeOriginal = root.TryGetProperty("original", out var writeOriginalElement) && writeOriginalElement.ValueKind == JsonValueKind.String ? writeOriginalElement.GetString() ?? writeFrom : writeFrom;
                        var writeRequest = root.TryGetProperty("request", out var writeRequestElement) && writeRequestElement.ValueKind == JsonValueKind.String ? writeRequestElement.GetString() : null;
                        var written = await _service.WriteAsync(kind, writeFrom, writeRequest, _operation.Token);
                        Send(new { type = "writing", captureId = _replyCaptureId, kind, original = writeOriginal, result = written, request = writeRequest, canReplace = _capture is not null && _capture.Text == writeOriginal && !ReadOnlyCapture, readOnly = ReadOnlyCapture, model = _service.ActiveModel });
                        break;
                    case "copyResult":
                        TextTransfer.CopyRevision(ReadText(root));
                        var copiedKind = root.TryGetProperty("kind", out var copiedKindElement) && copiedKindElement.ValueKind == JsonValueKind.String ? copiedKindElement.GetString() : null;
                        Notify(copiedKind switch { ReviewPrompt.Summary => "Summary", ReviewPrompt.Explain => "Explanation", _ => "Rewrite" } + " copied to the clipboard.");
                        break;
                    case "copyTranslation":
                        TextTransfer.CopyRevision(ReadText(root));
                        Notify("Translation copied to the clipboard.");
                        break;
                    case "applyTranslation":
                    case "applyRewrite":
                        // Both replace the whole selection with plain text.
                        var translated = ReadText(root);
                        var what = action == "applyRewrite" ? "rewrite" : "translation";
                        if (_capture is null) throw new InvalidOperationException($"Capture a selection with a shortcut first, or copy the {what}.");
                        if (ReadOnlyCapture) throw new InvalidOperationException($"This text is read-only. Copy the {what} and paste it where you need it.");
                        if (!root.TryGetProperty("original", out var translatedFrom) || translatedFrom.GetString() != _capture.Text)
                            throw new InvalidOperationException($"The {what} no longer matches the captured selection. Capture the text again.");
                        var translateCapture = _capture;
                        bool translationConsumed;
                        Hide();
                        try { translationConsumed = await TextTransfer.ApplyAsync(translateCapture, translated); _capture = null; }
                        catch { Open(); throw; }
                        Send(new { type = "applied", clipboardRestored = translationConsumed, formatted = false });
                        if (!translationConsumed) Open();
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
                        if (_capture is null) throw new InvalidOperationException($"Capture a selection with {ShortcutLabel(Shortcuts.Open)} first, or use Copy revised text.");
                        if (ReadOnlyCapture) throw new InvalidOperationException("This text is read-only. Copy the revision and paste it where you need it.");
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
                        if (!consumed) Open();
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
