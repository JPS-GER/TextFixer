using System.Diagnostics;
using System.Security.Principal;
using System.Text.RegularExpressions;

namespace TextFixer.Bridge;

/// <summary>Constants shared by the native messaging host and the tray-side bridge server.</summary>
public static class BridgeProtocol
{
    /// <summary>Native messaging host name registered for Chrome.</summary>
    public const string HostName = "com.textfixer.desktop";

    public const int Version = 1;

    /// <summary>Maximum frame payload in both directions (Chrome's host-to-browser limit is 1 MB).</summary>
    public const int MaxMessageBytes = 1024 * 1024;

    /// <summary>Single-instance mutex of the tray app. Program must use this name.</summary>
    public const string AppMutexName = "Local\\TextFixer.Desktop";

    /// <summary>Serializes "launch the tray app" across concurrently started hosts.</summary>
    internal const string LaunchMutexName = "Local\\TextFixer.BridgeLaunch";

    /// <summary>First frame from host to server: {"kind":"bridgeHello","protocol":1,"origin":"chrome-extension://.../"}.</summary>
    public const string HelloKind = "bridgeHello";

    /// <summary>Server's answer to a valid hello; the host forwards it, so the extension knows the app is connected.</summary>
    public const string ReadyKind = "bridgeReady";

    /// <summary>Error frame written by the host to the extension before it exits: {"kind":"bridgeError","code":"...","message":"..."}.</summary>
    public const string ErrorKind = "bridgeError";

    private static readonly Regex ExtensionOrigin = new("^chrome-extension://[a-p]{32}/$", RegexOptions.CultureInvariant);

    public static bool IsExtensionOrigin(string? value) => value is not null && ExtensionOrigin.IsMatch(value);

    /// <summary>Per user and session pipe name. Pipe names are machine-wide, so both parts are included.</summary>
    public static string DefaultPipeName()
    {
        using var identity = WindowsIdentity.GetCurrent();
        using var process = Process.GetCurrentProcess();
        return $"TextFixer.ChromeBridge.{identity.User!.Value}.{process.SessionId}";
    }
}
