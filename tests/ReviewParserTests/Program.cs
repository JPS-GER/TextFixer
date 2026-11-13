using System.Text.Json;
using System.Text.Json.Nodes;
using TextFixer.Contracts;
using TextFixer.Services;

// Offline tests for labelled corrections: prompt schema and offset resolution. No network.
// Exit code = number of failures.

var failures = 0;
void Check(bool ok, string name)
{
    Console.WriteLine((ok ? "PASS " : "FAIL ") + name);
    if (!ok) failures++;
}

string Output(string corrected, params object[] corrections) =>
    JsonSerializer.Serialize(new { corrected_text = corrected, findings = Array.Empty<object>(), corrections });
object C(string before, string after, string category) => new { before, after, category };
string Span(string text, ReviewCorrection c) => text[c.Start..c.End];

// ---- Schema ----
{
    var request = ReviewPrompt.BuildRequest("model", "text", structured: true);
    var schema = request["text"]!["format"]!["schema"]!;
    var required = schema["required"]!.AsArray().Select(x => x!.GetValue<string>()).ToArray();
    Check(required.Contains("corrections"), "schema requires corrections (strict mode needs every property required)");
    var item = schema["properties"]!["corrections"]!["items"]!;
    var categories = item["properties"]!["category"]!["enum"]!.AsArray().Select(x => x!.GetValue<string>()).ToArray();
    Check(categories.SequenceEqual(["spelling", "grammar", "punctuation", "wording"]), "category enum");
    Check(item["additionalProperties"]!.GetValue<bool>() == false && item["required"]!.AsArray().Count == 3, "correction item is strict");
    var plain = ReviewPrompt.BuildRequest("model", "text", structured: false)["instructions"]!.GetValue<string>();
    Check(plain.Contains("\"corrections\"", StringComparison.Ordinal), "JSON-only instructions describe corrections");
}

// ---- Resolution ----
{
    const string original = "Das ist ein wichtig Satz mit enem Fehler, und hier fehlt ein Komma dass ist klar.";
    const string corrected = "Das ist ein wichtiger Satz mit einem Fehler, und hier fehlt ein Komma, dass ist klar.";
    var result = ReviewResultParser.Parse(original, Output(corrected,
        C("wichtig", "wichtiger", "grammar"), C("enem", "einem", "spelling"), C("Komma", "Komma,", "punctuation")));
    Check(result.Corrections.Length == 3, "three corrections resolved");
    Check(result.Corrections.Select(c => Span(original, c)).SequenceEqual(["wichtig", "enem", "Komma"]), "offsets point at the original wording");
    Check(result.Corrections.Select(c => c.Category).SequenceEqual(["grammar", "spelling", "punctuation"]), "categories kept");
}

// ---- Repeated wording resolves in text order ----
{
    const string original = "teh cat and teh dog";
    var result = ReviewResultParser.Parse(original, Output("the cat and the dog",
        C("teh", "the", "spelling"), C("teh", "the", "spelling")));
    Check(result.Corrections.Select(c => c.Start).SequenceEqual([0, 12]), "second occurrence found after the first");
}

// ---- Rejections ----
{
    const string original = "Ein Satz mit Fehlr.";
    var result = ReviewResultParser.Parse(original, Output("Ein Satz mit Fehler.",
        C("Fehlr", "Fehler", "style"),       // unknown category
        C("Satz", "Satz", "grammar"),        // no change
        C("Wort", "Wörter", "spelling"),     // not in original
        C("mit", "ohne", "wording"),         // "ohne" is not in the corrected text
        C("", "x", "spelling"),              // empty before
        C("Fehlr", "Fehler", "Spelling")));  // valid, category case-insensitive
    Check(result.Corrections.Length == 1 && Span(original, result.Corrections[0]) == "Fehlr"
        && result.Corrections[0].Category == "spelling", "invalid entries dropped, valid one kept");
}

// ---- Overlap ----
{
    const string original = "Das ist ein wichtig Satz.";
    var result = ReviewResultParser.Parse(original, Output("Das ist ein wichtiger Satz.",
        C("ein wichtig", "ein wichtiger", "grammar"), C("wichtig", "wichtiger", "spelling")));
    Check(result.Corrections.Length == 1 && result.Corrections[0].Category == "grammar", "overlapping entry dropped");
}

// ---- Out of order, unique only ----
{
    const string original = "Erstens fasch, zweitens enem.";
    var result = ReviewResultParser.Parse(original, Output("Erstens falsch, zweitens einem.",
        C("enem", "einem", "spelling"), C("fasch", "falsch", "spelling")));
    Check(result.Corrections.Select(c => Span(original, c)).SequenceEqual(["fasch", "enem"]), "unique out-of-order entry accepted and sorted");
    const string twice = "fasch und enem und fasch";
    var ambiguous = ReviewResultParser.Parse(twice, Output("falsch und einem und falsch",
        C("enem", "einem", "spelling"), C("fasch", "falsch", "spelling"), C("fasch", "falsch", "spelling")));
    Check(ambiguous.Corrections.Select(c => c.Start).SequenceEqual([10, 19]), "after an out-of-order entry, later matches still resolve forward");
}

// ---- Whitespace and typographic variants, CRLF, Unicode ----
{
    const string original = "Die Größe – „Zitat“ 😀 ist\r\nfasch.";
    var result = ReviewResultParser.Parse(original, Output("Die Größe – „Zitat“ 😀 ist\nfalsch.",
        C("ist\nfasch", "ist\nfalsch", "spelling"), C("Größe - \"Zitat\"", "Größe – „Zitat“", "punctuation")));
    var spelling = result.Corrections.FirstOrDefault(c => c.Category == "spelling");
    Check(spelling is not null && Span(original, spelling) == "ist\r\nfasch", "LF in before matches CRLF in original");
    var punct = result.Corrections.FirstOrDefault(c => c.Category == "punctuation");
    Check(punct is not null && Span(original, punct) == "Größe – „Zitat“", "typographic variants map back to the original span");
    Check(result.Corrections.All(c => !char.IsLowSurrogate(original[c.Start]) && (c.End == original.Length || !char.IsLowSurrogate(original[c.End]))),
        "offsets never split a surrogate pair");
}

// ---- Missing or malformed corrections ----
{
    var noField = ReviewResultParser.Parse("Text", JsonSerializer.Serialize(new { corrected_text = "Text", findings = Array.Empty<object>() }));
    Check(noField.Corrections.Length == 0, "missing corrections field gives an empty list");
    var wrongType = ReviewResultParser.Parse("Text", "{\"corrected_text\":\"Text\",\"findings\":[],\"corrections\":\"x\"}");
    Check(wrongType.Corrections.Length == 0, "non-array corrections ignored");
    var serialized = JsonSerializer.Serialize(noField, new JsonSerializerOptions(JsonSerializerDefaults.Web));
    Check(JsonNode.Parse(serialized)!["corrections"] is JsonArray, "serializes as a camelCase corrections array");
}

Console.WriteLine(failures == 0 ? "RESULT all passed" : $"RESULT {failures} failed");
return failures;
