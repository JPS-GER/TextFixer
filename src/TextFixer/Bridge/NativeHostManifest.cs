using System.Text.Json;
using Microsoft.Win32;

namespace TextFixer.Bridge;

/// <summary>
/// Checks a caller origin against the locally registered native messaging host manifest:
/// HKCU, then HKLM, Software\Google\Chrome\NativeMessagingHosts\com.textfixer.desktop (default value =
/// manifest path). The manifest must name this host, point to this executable and list the exact origin.
/// </summary>
internal static class NativeHostManifest
{
    private const string RegistryPath = @"Software\Google\Chrome\NativeMessagingHosts\" + BridgeProtocol.HostName;

    public static bool IsOriginAllowed(string origin, string executablePath)
    {
        if (!BridgeProtocol.IsExtensionOrigin(origin))
        {
            return false;
        }

        foreach (var hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            string? manifestPath;
            try
            {
                using var key = hive.OpenSubKey(RegistryPath);
                manifestPath = key?.GetValue(null) as string;
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(manifestPath))
            {
                continue;
            }

            // Chrome uses the first registered manifest it finds (HKCU before HKLM); so does this check.
            return IsOriginAllowedByManifest(manifestPath, origin, executablePath);
        }

        return false;
    }

    internal static bool IsOriginAllowedByManifest(string manifestPath, string origin, string executablePath)
    {
        if (!BridgeProtocol.IsExtensionOrigin(origin))
        {
            return false;
        }

        try
        {
            var bytes = File.ReadAllBytes(manifestPath);
            if (bytes.Length > 64 * 1024)
            {
                return false;
            }

            using var document = JsonDocument.Parse(bytes);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("name", out var name) || name.GetString() != BridgeProtocol.HostName
                || !root.TryGetProperty("path", out var path) || path.ValueKind != JsonValueKind.String
                || !root.TryGetProperty("allowed_origins", out var origins) || origins.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            var manifestDirectory = Path.GetDirectoryName(Path.GetFullPath(manifestPath))!;
            var hostPath = Path.GetFullPath(path.GetString()!, manifestDirectory);
            if (!string.Equals(hostPath, Path.GetFullPath(executablePath), StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return origins.EnumerateArray().Any(o => o.ValueKind == JsonValueKind.String && o.GetString() == origin);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException or InvalidOperationException)
        {
            return false;
        }
    }
}
