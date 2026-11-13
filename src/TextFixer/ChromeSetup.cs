using System.Text.Json;
using Microsoft.Win32;

namespace TextFixer;

internal static class ChromeSetup
{
    internal const string ExtensionId = "hpmehbmdgfpgoggpemlefhfmbjeedpop";
    private const string RegistryPath = @"Software\Google\Chrome\NativeMessagingHosts\com.textfixer.desktop";
    internal static string? ExtensionFolder
    {
        get
        {
            var folder = new DirectoryInfo(AppContext.BaseDirectory);
            for (var i = 0; i < 5 && folder is not null; i++, folder = folder.Parent)
            {
                var candidate = Path.Combine(folder.FullName, "chrome-extension");
                if (File.Exists(Path.Combine(candidate, "manifest.json"))) return candidate;
            }
            return null;
        }
    }

    internal static bool IsRegistered()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RegistryPath);
            if (key?.GetValue("") is not string path || !File.Exists(path)) return false;
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;
            return root.GetProperty("name").GetString() == "com.textfixer.desktop"
                && string.Equals(Path.GetFullPath(root.GetProperty("path").GetString()!), Environment.ProcessPath, StringComparison.OrdinalIgnoreCase)
                && root.GetProperty("allowed_origins").EnumerateArray().Any(origin => origin.GetString() == $"chrome-extension://{ExtensionId}/");
        }
        catch { return false; }
    }

    // Runs only when the user presses the explicitly labelled setup action in the desktop app.
    internal static void Register()
    {
        var exe = Environment.ProcessPath ?? throw new InvalidOperationException("The executable path is unknown.");
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TextFixer", "Chrome");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "chrome-native-host.json");
        var manifest = new { name = "com.textfixer.desktop", description = "TextFixer desktop connection", path = exe, type = "stdio", allowed_origins = new[] { $"chrome-extension://{ExtensionId}/" } };
        File.WriteAllText(path, JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
        using var key = Registry.CurrentUser.CreateSubKey(RegistryPath);
        key.SetValue("", path);
    }
}
