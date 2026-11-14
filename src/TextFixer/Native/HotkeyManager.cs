using System.Runtime.InteropServices;
using TextFixer.Services;
using Shortcut = TextFixer.Services.Shortcut;

namespace TextFixer.Native;

/// <summary>
/// Registers the three capture shortcuts as global hotkeys. While the user records a new shortcut in Settings, all
/// hotkeys are suspended so the pressed combination reaches the window; they return after the change, on cancel, or
/// after <see cref="SuspendLimit"/> at the latest.
/// </summary>
internal sealed class HotkeyManager : NativeWindow, IDisposable
{
    [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterHotKey(nint window, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(nint window, int id);
    [DllImport("user32.dll")] private static extern nint GetKeyboardLayout(uint thread);
    [DllImport("user32.dll")] private static extern uint MapVirtualKeyEx(uint code, uint mapType, nint layout);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int ToUnicodeEx(uint key, uint scan, byte[] state, [Out] char[] buffer, int size, uint flags, nint layout);

    private const uint NoRepeat = 0x4000;
    private static readonly TimeSpan SuspendLimit = TimeSpan.FromSeconds(30);
    private readonly AppSettings _settings;
    private readonly Dictionary<string, bool> _registered = [];
    private readonly System.Windows.Forms.Timer _resume = new() { Interval = (int)SuspendLimit.TotalMilliseconds };
    private bool _suspended;

    /// <summary>Raised with the task of the pressed shortcut: review, translate or factcheck.</summary>
    internal event EventHandler<string>? Pressed;

    /// <summary>Raised after registration changes, so the window and tray can show the current shortcuts.</summary>
    internal event EventHandler? Changed;

    public HotkeyManager(AppSettings settings)
    {
        _settings = settings;
        CreateHandle(new CreateParams());
        _resume.Tick += (_, _) => Resume();
    }

    /// <summary>False when another app or Windows holds the task's shortcut.</summary>
    internal bool IsRegistered(string task) => _registered.GetValueOrDefault(task);

    internal string Label(string task) => _settings.Shortcuts.TryGetValue(task, out var shortcut) ? shortcut.ToString() : Shortcuts.Default(task).ToString();

    /// <summary>Registers every task's shortcut from the settings.</summary>
    internal void RegisterAll()
    {
        _resume.Stop();
        _suspended = false;
        UnregisterAll();
        foreach (var task in Shortcuts.Tasks) _registered[task] = Register(task, _settings.Shortcuts[task]);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    internal void Suspend()
    {
        if (_suspended) return;
        _suspended = true;
        UnregisterAll();
        _resume.Stop();
        _resume.Start();
    }

    internal void Resume() { if (_suspended) RegisterAll(); }

    /// <summary>
    /// Validates and registers <paramref name="shortcut"/> for <paramref name="task"/> and stores it in the settings
    /// (the caller saves). Returns the reason when it cannot be used; the previous shortcut then stays.
    /// All shortcuts are registered again afterwards.
    /// </summary>
    internal string? TryChange(string task, Shortcut shortcut)
    {
        var problem = Shortcuts.Problem(task, shortcut, _settings.Shortcuts) ?? TypesCharacter(shortcut);
        if (problem is null)
        {
            _resume.Stop();
            _suspended = false;
            UnregisterAll();
            if (Register(task, shortcut))
            {
                UnregisterHotKey(Handle, Id(task));
                _settings.Shortcuts[task] = shortcut;
            }
            else problem = $"{shortcut} is already used by another app or by Windows. Choose another combination.";
        }

        RegisterAll();
        return problem;
    }

    /// <summary>Ctrl+Alt is AltGr on many layouts; a combination that types a character would block typing it.</summary>
    private static string? TypesCharacter(Shortcut shortcut)
    {
        if ((shortcut.Modifiers & (Shortcut.Control | Shortcut.Alt)) != (Shortcut.Control | Shortcut.Alt)) return null;
        var layout = GetKeyboardLayout(0);
        var state = new byte[256];
        state[0x11] = state[0x12] = state[0xA2] = state[0xA5] = 0x80;
        if ((shortcut.Modifiers & Shortcut.Shift) != 0) state[0x10] = state[0xA0] = 0x80;
        var buffer = new char[8];
        // Flag 4: leave the keyboard state (dead keys) unchanged.
        var count = ToUnicodeEx(shortcut.Key, MapVirtualKeyEx(shortcut.Key, 0, layout), state, buffer, buffer.Length, 4, layout);
        if (count == 0) return null;
        var typed = count > 0 ? new string(buffer, 0, count) : "a character";
        if (count > 0 && typed.All(char.IsControl)) return null;
        return $"{shortcut} types {typed} with your keyboard layout. Choose another combination.";
    }

    private static int Id(string task) => task switch { Shortcuts.Translate => 47, Shortcuts.FactCheck => 48, _ => 46 };

    private bool Register(string task, Shortcut shortcut) => RegisterHotKey(Handle, Id(task), shortcut.Modifiers | NoRepeat, shortcut.Key);

    private void UnregisterAll() { foreach (var task in Shortcuts.Tasks) UnregisterHotKey(Handle, Id(task)); }

    protected override void WndProc(ref Message m)
    {
        var id = (int)m.WParam;
        if (m.Msg == 0x0312 && Shortcuts.Tasks.FirstOrDefault(t => Id(t) == id) is { } task)
        {
            try { TextTransfer.MaskAltMenu(); }
            catch { return; }
            Pressed?.Invoke(this, task);
        }

        base.WndProc(ref m);
    }

    public void Dispose()
    {
        _resume.Dispose();
        UnregisterAll();
        DestroyHandle();
    }
}
