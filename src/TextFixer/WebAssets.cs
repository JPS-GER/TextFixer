namespace TextFixer;

internal static class WebAssets
{
    private static readonly Dictionary<string, string> Names = typeof(WebAssets).Assembly.GetManifestResourceNames()
        .Where(name => name.StartsWith("web/", StringComparison.Ordinal))
        .ToDictionary(name => name[4..].Replace('\\', '/'), name => name, StringComparer.Ordinal);

    internal static (byte[] Data, string Type)? Find(string urlPath)
    {
        var path = Uri.UnescapeDataString(urlPath).TrimStart('/');
        if (path.Length == 0) path = "index.html";
        if (!Names.TryGetValue(path, out var name)) return null;
        using var input = typeof(WebAssets).Assembly.GetManifestResourceStream(name);
        if (input is null) return null;
        using var output = new MemoryStream();
        input.CopyTo(output);
        var type = Path.GetExtension(path) switch
        {
            ".html" => "text/html; charset=utf-8",
            ".css" => "text/css; charset=utf-8",
            ".js" => "text/javascript; charset=utf-8",
            ".svg" => "image/svg+xml; charset=utf-8",
            _ => "application/octet-stream"
        };
        return (output.ToArray(), type);
    }
}
