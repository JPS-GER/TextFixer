using Microsoft.Win32;

namespace TextFixer;

/// <summary>Start with Windows through the current user's Run key. Off until turned on in Settings.</summary>
internal static class Autostart
{
    internal const string Argument = "--autostart";
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    private const string ValueName = "TextFixer";

    // The published executable keeps this name; hot-swap renames only the retired copy.
    private static string Command => $"\"{Path.Combine(AppContext.BaseDirectory, "TextFixer.exe")}\" {Argument}";

    internal static bool IsEnabled
    {
        get
        {
            using var run = Registry.CurrentUser.OpenSubKey(RunKey);
            if (run?.GetValue(ValueName) is not string command
                || !string.Equals(command, Command, StringComparison.OrdinalIgnoreCase)) return false;
            // Task Manager marks a disabled startup entry with an odd first byte.
            using var approved = Registry.CurrentUser.OpenSubKey(ApprovedKey);
            return approved?.GetValue(ValueName) is not byte[] { Length: > 0 } state || state[0] % 2 == 0;
        }
    }

    internal static void Set(bool enabled)
    {
        using (var run = Registry.CurrentUser.CreateSubKey(RunKey))
        {
            if (enabled) run.SetValue(ValueName, Command);
            else run.DeleteValue(ValueName, throwOnMissingValue: false);
        }
        using var approved = Registry.CurrentUser.OpenSubKey(ApprovedKey, writable: true);
        approved?.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}
