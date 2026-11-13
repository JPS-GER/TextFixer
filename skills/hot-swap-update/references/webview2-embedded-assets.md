# Embedding a WebView2 page in a .NET exe

Serve the page from embedded resources instead of `SetVirtualHostNameToFolderMapping` to a `web\` folder, so the install folder holds only the exe.

## Project file

```xml
<!-- web\ at the repo root, embedded as resources named "web/<path>" -->
<ItemGroup>
  <EmbeddedResource Include="..\..\web\**\*" LogicalName="web/%(RecursiveDir)%(Filename)%(Extension)" />
</ItemGroup>
```

Remove any `<Content Include="..\web\**" CopyToOutputDirectory=…>` item that copied the folder next to the exe. `%(RecursiveDir)` uses the build machine's path separator, so the lookup below normalizes `\` to `/`.

## WebAssets.cs

```csharp
static class WebAssets
{
    static readonly Dictionary<string, string> Types = new(StringComparer.OrdinalIgnoreCase)
    {
        [".html"] = "text/html; charset=utf-8", [".css"] = "text/css; charset=utf-8",
        [".js"] = "text/javascript; charset=utf-8", [".json"] = "application/json",
        [".svg"] = "image/svg+xml", [".png"] = "image/png", [".ico"] = "image/x-icon", [".woff2"] = "font/woff2",
    };

    static readonly Dictionary<string, string> Names = typeof(WebAssets).Assembly.GetManifestResourceNames()
        .Where(n => n.StartsWith("web/") || n.StartsWith("web\\"))
        .ToDictionary(n => n[4..].Replace('\\', '/'), n => n, StringComparer.OrdinalIgnoreCase);

    public static (byte[] Data, string Type)? Find(string urlPath)
    {
        string path = Uri.UnescapeDataString(urlPath).TrimStart('/');
        if (path == "") path = "index.html";
        if (!Names.TryGetValue(path, out var name)) return null;
        using var s = typeof(WebAssets).Assembly.GetManifestResourceStream(name);
        if (s == null) return null;
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return (ms.ToArray(), Types.GetValueOrDefault(Path.GetExtension(path), "application/octet-stream"));
    }
}
```

## Serving it

After `EnsureCoreWebView2Async`, before `Navigate`:

```csharp
const string Host = "myapp.app";   // any made-up host; https gives a secure context
core.AddWebResourceRequestedFilter($"https://{Host}/*", CoreWebView2WebResourceContext.All);
core.WebResourceRequested += (_, e) =>
{
    var file = WebAssets.Find(new Uri(e.Request.Uri).AbsolutePath);
    e.Response = file == null
        ? core.Environment.CreateWebResourceResponse(null, 404, "Not Found", "")
        : core.Environment.CreateWebResourceResponse(new MemoryStream(file.Value.Data), 200, "OK",
            $"Content-Type: {file.Value.Type}\r\nCache-Control: no-store");
};
core.Navigate($"https://{Host}/index.html");
```

- The filter only catches that exact host, so other virtual-host folder mappings (e.g. `videos.myapp.app` → a user folder) keep working alongside it.
- Keep a `NavigationStarting` guard that sends any other URL to the system browser.
- Page code changes now need a rebuild (the files are compiled in), which matches how the exe is shipped anyway.

## Verify

With the UI's remote debugging port enabled, evaluate in the page:

```js
[(await fetch('/styles.css')).status, (await fetch('/nope.js')).status]   // → [200, 404]
```
