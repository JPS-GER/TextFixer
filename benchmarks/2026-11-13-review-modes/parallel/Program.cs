using System.Diagnostics;
using System.Text.Json;
using TextFixer.Services;
using TextFixer.Contracts;

// Benchmark: single request vs five per-tab requests sent in parallel. Writes results.json. Never prints credentials.
var texts = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "texts.json"))).RootElement;
var service = new ChatGptReviewService();
var status = await service.GetStatusAsync();
Console.WriteLine("connected: " + status.Connected);
if (!status.Connected) return 1;
var focuses = new[] { "spelling", "grammar", "punctuation", "wording", "worth" };
var output = new List<object>();
object Shape(string text, ReviewResult r) => new { corrected = r.CorrectedText, corrections = r.Corrections.Select(c => new { before = text[c.Start..c.End], after = c.After, c.Category }), findings = r.Findings.Select(f => new { f.Kind, f.Explanation, f.SuggestedRewrite }) };
foreach (var item in texts.EnumerateArray())
{
    var id = item.GetProperty("id").GetString()!;
    var text = item.GetProperty("text").GetString()!;
    var sw = Stopwatch.StartNew();
    object single;
    try { var r = await service.CheckAsync(text, null, CancellationToken.None); single = new { ms = sw.ElapsedMilliseconds, result = Shape(text, r) }; }
    catch (Exception ex) { single = new { ms = sw.ElapsedMilliseconds, error = ex.Message }; }
    Console.WriteLine($"{id} single {sw.ElapsedMilliseconds} ms");

    var wall = Stopwatch.StartNew();
    var tasks = focuses.Select(async focus =>
    {
        try { var r = await service.CheckAsync(text, focus, CancellationToken.None); return (object)new { focus, ms = wall.ElapsedMilliseconds, result = Shape(text, r) }; }
        catch (Exception ex) { return new { focus, ms = wall.ElapsedMilliseconds, error = ex.Message }; }
    }).ToArray();
    var tabs = await Task.WhenAll(tasks);
    var wallMs = wall.ElapsedMilliseconds;
    Console.WriteLine($"{id} parallel {wallMs} ms");
    output.Add(new { id, model = service.ActiveModel, single, wallMs, tabs });
    File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "results.json"), JsonSerializer.Serialize(output, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
}
Console.WriteLine("done");
return 0;
