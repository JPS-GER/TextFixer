using System.Diagnostics;
using System.IO.Pipes;
using System.Text.Json;

namespace TextFixer.Bridge;

/// <summary>
/// Native messaging host mode of TextFixer.exe. Chrome starts the exe with the caller origin as first
/// argument (plus --parent-window=... on Windows). The host validates the origin against the registered
/// manifest, connects to the tray app's pipe (starting the app once if needed) and relays frames.
/// Writes nothing to stdout except protocol frames.
/// </summary>
public static class NativeMessagingHost
{
    internal static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan ConnectAttempt = TimeSpan.FromMilliseconds(250);

    public static bool IsNativeInvocation(string[] args) =>
        args.Length > 0 && args[0].StartsWith("chrome-extension://", StringComparison.Ordinal);

    /// <returns>Process exit code.</returns>
    public static async Task<int> RunAsync(string[] args, CancellationToken cancellationToken = default)
    {
        await using var input = Console.OpenStandardInput();
        await using var output = Console.OpenStandardOutput();
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("Executable path unknown.");
        return await RunAsync(
            args, input, output,
            new HostOptions(
                BridgeProtocol.DefaultPipeName(),
                origin => NativeHostManifest.IsOriginAllowed(origin, executable),
                LaunchTrayApp,
                ConnectTimeout),
            cancellationToken).ConfigureAwait(false);
    }

    internal sealed record HostOptions(string PipeName, Func<string, bool> IsOriginAllowed, Action LaunchApp, TimeSpan ConnectTimeout);

    internal static async Task<int> RunAsync(string[] args, Stream input, Stream output, HostOptions options, CancellationToken cancellationToken)
    {
        var origin = args.Length > 0 ? args[0] : string.Empty;
        if (!BridgeProtocol.IsExtensionOrigin(origin) || !options.IsOriginAllowed(origin))
        {
            await TryWriteErrorAsync(output, "origin_not_allowed", "This extension is not registered for TextFixer.", cancellationToken).ConfigureAwait(false);
            return 2;
        }

        NamedPipeClientStream? pipe;
        try
        {
            pipe = await ConnectAsync(options, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            pipe = null;
        }

        if (pipe is null)
        {
            await TryWriteErrorAsync(output, "app_unavailable", "TextFixer is not running and could not be started.", cancellationToken).ConfigureAwait(false);
            return 3;
        }

        await using (pipe)
        {
            try
            {
                var hello = JsonSerializer.SerializeToUtf8Bytes(new { kind = BridgeProtocol.HelloKind, protocol = BridgeProtocol.Version, origin });
                await BridgeFraming.WriteFrameAsync(pipe, hello, cancellationToken).ConfigureAwait(false);
            }
            catch (IOException)
            {
                await TryWriteErrorAsync(output, "app_unavailable", "TextFixer closed the connection.", cancellationToken).ConfigureAwait(false);
                return 3;
            }

            using var relay = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var toApp = RelayAsync(input, pipe, relay.Token);
            var toExtension = RelayAsync(pipe, output, relay.Token);
            var finished = await Task.WhenAny(toApp, toExtension).ConfigureAwait(false);
            relay.Cancel();
            var appSideFailed = finished == toExtension && !await finished.ConfigureAwait(false);
            try
            {
                await Task.WhenAll(toApp, toExtension).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }

            if (appSideFailed)
            {
                await TryWriteErrorAsync(output, "protocol_error", "TextFixer sent an invalid message.", CancellationToken.None).ConfigureAwait(false);
                return 4;
            }

            return 0;
        }
    }

    /// <returns>True on a clean end of stream, false on a protocol violation or I/O failure.</returns>
    private static async Task<bool> RelayAsync(Stream source, Stream destination, CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                var frame = await BridgeFraming.ReadMessageAsync(source, cancellationToken).ConfigureAwait(false);
                if (frame is null)
                {
                    return true;
                }

                // Only the server may produce bridge control frames towards the extension; the
                // extension may not forge them towards the app (the server ignores them anyway).
                await BridgeFraming.WriteFrameAsync(destination, frame.Value.Payload, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            return true;
        }
        catch (Exception ex) when (ex is IOException or BridgeProtocolException or ObjectDisposedException)
        {
            return false;
        }
    }

    private static async Task<NamedPipeClientStream?> ConnectAsync(HostOptions options, CancellationToken cancellationToken)
    {
        var deadline = Stopwatch.StartNew();
        var launched = false;
        while (deadline.Elapsed < options.ConnectTimeout)
        {
            var pipe = new NamedPipeClientStream(".", options.PipeName, PipeDirection.InOut,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            try
            {
                await pipe.ConnectAsync((int)ConnectAttempt.TotalMilliseconds, cancellationToken).ConfigureAwait(false);
                return pipe;
            }
            catch (TimeoutException)
            {
                await pipe.DisposeAsync().ConfigureAwait(false);
            }
            catch (IOException)
            {
                // All instances busy, or the server is restarting.
                await pipe.DisposeAsync().ConfigureAwait(false);
                await Task.Delay(ConnectAttempt, cancellationToken).ConfigureAwait(false);
            }

            if (!launched)
            {
                launched = true;
                options.LaunchApp();
            }
        }

        return null;
    }

    /// <summary>
    /// Starts the tray app once. A launch mutex plus the app's own mutex prevent two hosts from
    /// starting two instances. ShellExecute keeps Chrome's stdio pipes out of the new process.
    /// </summary>
    private static void LaunchTrayApp()
    {
        using var launchMutex = new Mutex(false, BridgeProtocol.LaunchMutexName);
        var owned = false;
        try
        {
            try
            {
                owned = launchMutex.WaitOne(TimeSpan.FromSeconds(10));
            }
            catch (AbandonedMutexException)
            {
                owned = true;
            }

            if (!owned || IsAppRunning())
            {
                return;
            }

            Process.Start(new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true })?.Dispose();

            // Hold the launch mutex until the new instance owns the app mutex.
            var wait = Stopwatch.StartNew();
            while (!IsAppRunning() && wait.Elapsed < TimeSpan.FromSeconds(5))
            {
                Thread.Sleep(50);
            }
        }
        finally
        {
            if (owned)
            {
                launchMutex.ReleaseMutex();
            }
        }
    }

    private static bool IsAppRunning()
    {
        if (Mutex.TryOpenExisting(BridgeProtocol.AppMutexName, out var mutex))
        {
            mutex.Dispose();
            return true;
        }

        return false;
    }

    private static async Task TryWriteErrorAsync(Stream output, string code, string message, CancellationToken cancellationToken)
    {
        try
        {
            var payload = JsonSerializer.SerializeToUtf8Bytes(new { kind = BridgeProtocol.ErrorKind, code, message });
            await BridgeFraming.WriteFrameAsync(output, payload, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
        {
        }
    }
}
