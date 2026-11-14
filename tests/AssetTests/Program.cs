using System.Reflection;
using System.Text;

var path = Path.GetFullPath(args.Length > 0 ? args[0] : "artifacts/TextFixer/TextFixer.dll");
var assembly = Assembly.LoadFile(path);
var method = assembly.GetType("TextFixer.WebAssets", true)!.GetMethod("Find", BindingFlags.Static | BindingFlags.NonPublic)!;
foreach (var asset in new[] { "/index.html", "/style.css", "/app.js", "/diff.js", "/review-model.js", "/markup.js", "/logo.svg", "/" })
{
    var result = method.Invoke(null, [asset]);
    if (result is not ValueTuple<byte[], string> found || found.Item1.Length == 0 || !found.Item2.Contains("charset=utf-8"))
        throw new Exception("Missing embedded asset: " + asset);
    if (asset == "/index.html" && !Encoding.UTF8.GetString(found.Item1).Contains("id=\"task-choices\""))
        throw new Exception("Published embedded interface is stale.");
}
foreach (var icon in new[] { "brand/TextFixer.ico", "brand/tray-dark.ico", "brand/tray-light.ico" })
{
    using var stream = assembly.GetManifestResourceStream(icon);
    if (stream is null || stream.Length < 100) throw new Exception("Missing embedded icon: " + icon);
}
foreach (var missing in new[] { "/missing.js", "/../Program.cs", "/%2e%2e/Program.cs" })
    if (method.Invoke(null, [missing]) is not null) throw new Exception("Unexpected embedded asset: " + missing);
Console.WriteLine("PASS: actual installed assembly serves eight embedded asset paths, embeds app/tray icons, and rejects three unknown/traversal paths.");
