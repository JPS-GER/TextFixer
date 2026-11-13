using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using TextFixer.Bridge;

// Offline tests for the Chrome bridge transport: framing, manifest validation, host <-> server relay.
// No registry writes, no Chrome, no tray app. Exit code = number of failures.

var failures = 0;
void Check(bool ok, string name)
{
    Console.WriteLine((ok ? "PASS " : "FAIL ") + name);
    if (!ok) failures++;
}

async Task<bool> Throws<T>(Func<Task> action) where T : Exception
{
    try { await action(); return false; }
    catch (T) { return true; }
}

byte[] Frame(byte[] payload)
{
    var frame = new byte[4 + payload.Length];
    BinaryPrimitives.WriteUInt32LittleEndian(frame, (uint)payload.Length);
    payload.CopyTo(frame, 4);
    return frame;
}

byte[] Json(object value) => JsonSerializer.SerializeToUtf8Bytes(value);
const string Origin = "chrome-extension://abcdefghijklmnopabcdefghijklmnop/";

// ---- Framing ----
{
    var stream = new MemoryStream();
    await BridgeFraming.WriteFrameAsync(stream, Json(new { kind = "capture", text = "Grüße 😀" }), default);
    stream.Position = 0;
    var header = new byte[4];
    stream.ReadExactly(header);
    Check(BinaryPrimitives.ReadUInt32LittleEndian(header) == stream.Length - 4, "length prefix is little-endian payload size");
    stream.Position = 0;
    var message = await BridgeFraming.ReadMessageAsync(stream, default);
    Check(message?.Message.GetProperty("text").GetString() == "Grüße 😀", "UTF-8 round trip");
    Check(await BridgeFraming.ReadFrameAsync(stream, default) is null, "clean EOF returns null");

    Check(await Throws<BridgeProtocolException>(() => BridgeFraming.ReadFrameAsync(new MemoryStream([1, 0]), default)), "truncated header rejected");
    Check(await Throws<BridgeProtocolException>(() => BridgeFraming.ReadFrameAsync(new MemoryStream([5, 0, 0, 0, (byte)'{']), default)), "truncated payload rejected");
    Check(await Throws<BridgeProtocolException>(() => BridgeFraming.ReadFrameAsync(new MemoryStream([0, 0, 0, 0]), default)), "zero length rejected");
    var oversize = new byte[4];
    BinaryPrimitives.WriteUInt32LittleEndian(oversize, BridgeProtocol.MaxMessageBytes + 1);
    Check(await Throws<BridgeProtocolException>(() => BridgeFraming.ReadFrameAsync(new MemoryStream(oversize), default)), "oversize length rejected before allocation");
    Check(await Throws<BridgeProtocolException>(() => BridgeFraming.ReadMessageAsync(new MemoryStream(Frame("[1,2]"u8.ToArray())), default)), "non-object JSON rejected");
    Check(await Throws<BridgeProtocolException>(() => BridgeFraming.ReadMessageAsync(new MemoryStream(Frame([0x7B, 0xFF, 0x7D])), default)), "invalid UTF-8 rejected");
    Check(await Throws<BridgeProtocolException>(() => BridgeFraming.WriteFrameAsync(new MemoryStream(), new byte[BridgeProtocol.MaxMessageBytes + 1], default)), "oversize write rejected");
    var exact = new byte[BridgeProtocol.MaxMessageBytes];
    exact.AsSpan().Fill((byte)' ');
    exact[0] = (byte)'{';
    exact[^1] = (byte)'}';
    var exactStream = new MemoryStream();
    await BridgeFraming.WriteFrameAsync(exactStream, exact, default);
    exactStream.Position = 0;
    Check((await BridgeFraming.ReadMessageAsync(exactStream, default)) is not null, "exactly 1 MB accepted");
}

// ---- Origin and manifest ----
{
    Check(BridgeProtocol.IsExtensionOrigin(Origin), "valid origin format");
    Check(!BridgeProtocol.IsExtensionOrigin("chrome-extension://abcdefghijklmnopabcdefghijklmnop"), "origin without slash rejected");
    Check(!BridgeProtocol.IsExtensionOrigin("chrome-extension://ABCDEFGHIJKLMNOPABCDEFGHIJKLMNOP/"), "uppercase id rejected");
    Check(!BridgeProtocol.IsExtensionOrigin("https://example.com/"), "web origin rejected");
    Check(NativeMessagingHost.IsNativeInvocation([Origin, "--parent-window=0"]), "native invocation detected");
    Check(!NativeMessagingHost.IsNativeInvocation([]) && !NativeMessagingHost.IsNativeInvocation(["--x"]), "normal start not detected as native");

    var dir = Directory.CreateTempSubdirectory("tf-manifest-").FullName;
    var exe = Path.Combine(dir, "TextFixer.exe");
    var manifest = Path.Combine(dir, "com.textfixer.desktop.json");
    void WriteManifest(string name, string path, string[] origins) =>
        File.WriteAllText(manifest, JsonSerializer.Serialize(new { name, description = "TextFixer", path, type = "stdio", allowed_origins = origins }));

    WriteManifest(BridgeProtocol.HostName, "TextFixer.exe", [Origin]);
    Check(NativeHostManifest.IsOriginAllowedByManifest(manifest, Origin, exe), "relative manifest path + listed origin allowed");
    Check(!NativeHostManifest.IsOriginAllowedByManifest(manifest, "chrome-extension://pponmlkjihgfedcbapponmlkjihgfedcba/", exe), "unlisted origin rejected");
    Check(!NativeHostManifest.IsOriginAllowedByManifest(manifest, Origin, Path.Combine(dir, "Other.exe")), "manifest for another exe rejected");
    WriteManifest("com.other.host", exe, [Origin]);
    Check(!NativeHostManifest.IsOriginAllowedByManifest(manifest, Origin, exe), "wrong host name rejected");
    File.WriteAllText(manifest, "{broken");
    Check(!NativeHostManifest.IsOriginAllowedByManifest(manifest, Origin, exe), "malformed manifest rejected");
    Check(!NativeHostManifest.IsOriginAllowedByManifest(Path.Combine(dir, "missing.json"), Origin, exe), "missing manifest rejected");
    Directory.Delete(dir, true);
}

// ---- Host <-> server relay over a real named pipe ----
var pipeName = "TextFixer.BridgeTest." + Guid.NewGuid().ToString("N");
using var server = new ChromeBridgeServer(pipeName, verifyClientExecutable: false);
var received = new TaskCompletionSource<BridgeMessageEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
var connected = new TaskCompletionSource<BridgeSessionEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
var disconnected = new TaskCompletionSource<BridgeSessionEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
server.MessageReceived += (_, e) => received.TrySetResult(e);
server.SessionConnected += (_, e) => connected.TrySetResult(e);
server.SessionDisconnected += (_, e) => disconnected.TrySetResult(e);
server.Start();

{
    using var second = new ChromeBridgeServer(pipeName, false);
    var failed = false;
    try { second.Start(); }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { failed = true; }
    Check(failed, "second server on the same pipe name fails (FirstPipeInstance)");
}

{
    // stdin/stdout of the host are simulated with anonymous pipes.
    using var stdinWriter = new AnonymousPipeServerStream(PipeDirection.Out);
    using var stdinReader = new AnonymousPipeClientStream(PipeDirection.In, stdinWriter.ClientSafePipeHandle);
    using var stdoutReader = new AnonymousPipeServerStream(PipeDirection.In);
    using var stdoutWriter = new AnonymousPipeClientStream(PipeDirection.Out, stdoutReader.ClientSafePipeHandle);
    var launches = 0;
    var options = new NativeMessagingHost.HostOptions(pipeName, o => o == Origin, () => launches++, TimeSpan.FromSeconds(5));
    var host = NativeMessagingHost.RunAsync([Origin, "--parent-window=0"], stdinReader, stdoutWriter, options, default);

    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    var ready = await BridgeFraming.ReadMessageAsync(stdoutReader, timeout.Token);
    Check(ready is not null && BridgeFraming.Kind(ready.Value.Message) == BridgeProtocol.ReadyKind, "extension receives bridgeReady");
    var session = await connected.Task.WaitAsync(timeout.Token);
    Check(session.Origin == Origin, "server session carries validated origin");
    Check(launches == 0, "no app launch when server is running");

    await BridgeFraming.WriteFrameAsync(stdinWriter, Json(new { kind = BridgeProtocol.HelloKind, protocol = 1, origin = "chrome-extension://pponmlkjihgfedcbapponmlkjihgfedcba/" }), timeout.Token);
    await BridgeFraming.WriteFrameAsync(stdinWriter, Json(new { kind = "capture", captureId = "c1", text = "Tset", hasFormatting = true }), timeout.Token);
    var message = await received.Task.WaitAsync(timeout.Token);
    Check(BridgeFraming.Kind(message.Message) == "capture" && message.Message.GetProperty("text").GetString() == "Tset", "extension -> app message delivered");
    Check(message.SessionId == session.SessionId, "message bound to session");

    var sent = await server.SendAsync(session.SessionId, new { kind = "apply", captureId = "c1", original = "Tset", edits = new[] { new { start = 0, end = 4, before = "Tset", after = "Test" } } }, timeout.Token);
    var apply = await BridgeFraming.ReadMessageAsync(stdoutReader, timeout.Token);
    Check(sent && apply is not null && apply.Value.Message.GetProperty("edits")[0].GetProperty("after").GetString() == "Test", "app -> extension targeted send delivered");
    Check(!await server.SendAsync("unknown", new { kind = "apply" }), "send to unknown session returns false");
    Check(await Throws<BridgeProtocolException>(() => server.SendAsync(session.SessionId, new[] { 1 })), "non-object send rejected");

    stdinWriter.Dispose(); // Chrome closes the port.
    var exitCode = await host.WaitAsync(timeout.Token);
    Check(exitCode == 0, "host exits cleanly on stdin EOF");
    var gone = await disconnected.Task.WaitAsync(timeout.Token);
    Check(gone.SessionId == session.SessionId && !server.SessionIds.Contains(session.SessionId), "server reports disconnect");
}

{
    // Rejected origin: error frame, no connection.
    var output = new MemoryStream();
    var options = new NativeMessagingHost.HostOptions(pipeName, _ => false, () => { }, TimeSpan.FromSeconds(1));
    var code = await NativeMessagingHost.RunAsync([Origin], new MemoryStream(), output, options, default);
    output.Position = 0;
    var error = await BridgeFraming.ReadMessageAsync(output, default);
    Check(code == 2 && error?.Message.GetProperty("code").GetString() == "origin_not_allowed", "unregistered origin refused with bridgeError");
}

{
    // No app running: launch once, then app_unavailable.
    var output = new MemoryStream();
    var launches = 0;
    var options = new NativeMessagingHost.HostOptions("TextFixer.BridgeTest.none." + Guid.NewGuid().ToString("N"), _ => true, () => launches++, TimeSpan.FromSeconds(1.5));
    var code = await NativeMessagingHost.RunAsync([Origin], new MemoryStream(), output, options, default);
    output.Position = 0;
    var error = await BridgeFraming.ReadMessageAsync(output, default);
    Check(code == 3 && launches == 1 && error?.Message.GetProperty("code").GetString() == "app_unavailable", "missing app launched once, then app_unavailable");
}

{
    // A raw client with a wrong hello is dropped without a session.
    var before = server.SessionIds.Count;
    using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
    await client.ConnectAsync(2000);
    await BridgeFraming.WriteFrameAsync(client, Json(new { kind = BridgeProtocol.HelloKind, protocol = 99, origin = Origin }), default);
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    var reply = await BridgeFraming.ReadFrameAsync(client, timeout.Token);
    Check(reply is null && server.SessionIds.Count == before, "wrong protocol version dropped");
}

{
    // Garbage instead of a frame is dropped.
    using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
    await client.ConnectAsync(2000);
    await client.WriteAsync(new byte[] { 0xFF, 0xFF, 0xFF, 0x7F });
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    Check(await BridgeFraming.ReadFrameAsync(client, timeout.Token) is null, "oversize frame header on pipe dropped");
}

{
    // Executable verification on: a client from this same executable is accepted.
    var name = "TextFixer.BridgeTest.verify." + Guid.NewGuid().ToString("N");
    using var verifying = new ChromeBridgeServer(name, verifyClientExecutable: true);
    verifying.Start();
    using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
    await client.ConnectAsync(2000);
    await BridgeFraming.WriteFrameAsync(client, Json(new { kind = BridgeProtocol.HelloKind, protocol = 1, origin = Origin }), default);
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    var reply = await BridgeFraming.ReadMessageAsync(client, timeout.Token);
    Check(reply is not null && BridgeFraming.Kind(reply.Value.Message) == BridgeProtocol.ReadyKind, "same-executable client accepted with verification on");
}

server.Stop();
Console.WriteLine(failures == 0 ? "ALL PASSED" : $"{failures} FAILED");
return failures;
