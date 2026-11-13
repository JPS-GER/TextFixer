using Microsoft.Win32;

namespace TextFixer;

internal static class Branding
{
    internal static Icon CreateWindowIcon() => Load("TextFixer.ico", 32);
    internal static Icon CreateTrayIcon() => Load(UsesLightTaskbar() ? "tray-dark.ico" : "tray-light.ico", Math.Clamp(SystemInformation.SmallIconSize.Width, 16, 48));

    private static Icon Load(string name, int size)
    {
        using var stream = typeof(Branding).Assembly.GetManifestResourceStream("brand/" + name)
            ?? throw new InvalidOperationException("Missing embedded app icon: " + name);
        using var icon = new Icon(stream, size, size);
        return (Icon)icon.Clone();
    }

    private static bool UsesLightTaskbar()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            if (key?.GetValue("SystemUsesLightTheme") is int value) return value != 0;
        }
        catch { }
        return SystemColors.Control.GetBrightness() > .5f;
    }
}
