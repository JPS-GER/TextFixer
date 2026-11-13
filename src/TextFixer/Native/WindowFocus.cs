using System.Runtime.InteropServices;

namespace TextFixer.Native;

internal static class WindowFocus
{
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint window);
    [DllImport("user32.dll")] private static extern bool BringWindowToTop(nint window);
    [DllImport("user32.dll")] private static extern bool IsIconic(nint window);
    [DllImport("user32.dll")] private static extern bool ShowWindow(nint window, int command);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint processId);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint source, uint target, bool attach);

    // Called in response to a user hotkey, tray action or extension capture, on our UI thread.
    // Do not synthesize Alt presses or leave the window permanently topmost.
    internal static bool BringOwnWindowForward(nint window)
    {
        GetWindowThreadProcessId(window, out var owner);
        if (owner != Environment.ProcessId) return false;
        ShowWindow(window, IsIconic(window) ? 9 : 5);
        if (SetForegroundWindow(window) && GetForegroundWindow() == window) return true;
        var foreground = GetForegroundWindow();
        var foregroundThread = GetWindowThreadProcessId(foreground, out _);
        var currentThread = GetCurrentThreadId();
        var attached = foregroundThread != 0 && foregroundThread != currentThread && AttachThreadInput(currentThread, foregroundThread, true);
        try
        {
            BringWindowToTop(window);
            SetForegroundWindow(window);
            return GetForegroundWindow() == window;
        }
        finally { if (attached) AttachThreadInput(currentThread, foregroundThread, false); }
    }
}
