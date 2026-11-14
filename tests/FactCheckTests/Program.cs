using System.Text;
using System.Text.Json;
using TextFixer.Contracts;
using TextFixer.Services;

// Offline tests for the fact-check stream reader (citations, search calls) and FactCheckParser (provenance rules).
var failures = 0;
void Check(bool ok, string name)
{
    Console.WriteLine($"{(ok ? "PASS" : "FAIL")}: {name}");
    if (!ok) failures++;
}

static string Sse(params object[] events) =>
    string.Concat(events.Select(e => "event: x\ndata: " + JsonSerializer.Serialize(e) + "\n\n"));

static Task<ResponseResult> Read(string sse) =>
    ChatGptResponsesClient.ReadEventStreamAsync(new HttpResponseMessage { Content = new StringContent(sse, Encoding.UTF8) }, CancellationToken.None);

static object Delta(string text, int item = 1, int content = 0) => new { type = "response.output_text.delta", output_index = item, content_index = content, delta = text };
static object Annotation(int start, int end, string url, int index, int item = 1, int content = 0) =>
    new { type = "response.output_text.annotation.added", output_index = item, content_index = content, annotation_index = index, annotation = new { type = "url_citation", start_index = start, end_index = end, url, title = "T " + index } };
static object Search(string id, string status, int item = 0) => new { type = "response.output_item.done", output_index = item, item = new { type = "web_search_call", id, status } };
static object Completed(object[]? output = null) => new { type = "response.completed", response = new { output = output ?? [] } };

// Citation range = the marker text inside the JSON output, as the API reports it.
static (int Start, int End) Range(string output, string marker)
{
    var start = output.IndexOf(marker, StringComparison.Ordinal);
    if (start < 0 || output.IndexOf(marker, start + 1, StringComparison.Ordinal) >= 0) throw new InvalidOperationException("marker not unique: " + marker);
    return (start, start + marker.Length);
}

const string Original = "Der Eiffelturm steht in Rom. Er wurde 1889 fertig. Berlin hat 4 Millionen Einwohner.";

// 1. Deltas in two chunks, citations as stream events, duplicates, and a search call; offsets rebased onto the full output.
{
    var output = """{"claims":[{"quote":"Der Eiffelturm steht in Rom","verdict":"wrong","explanation":"Er steht in Paris (cite A).","correction":"Der Eiffelturm steht in Paris"},{"quote":"Er wurde 1889 fertig","verdict":"supported","explanation":"Fertig 1889 (cite B).","correction":null}]}""";
    var a = Range(output, "(cite A)");
    var b = Range(output, "(cite B)");
    var half = output.Length / 2;
    var sse = Sse(
        Search("ws_1", "completed"),
        Delta(output[..half]), Delta(output[half..]),
        Annotation(a.Start, a.End, "https://example.org/paris", 0),
        Annotation(a.Start, a.End, "https://example.org/paris", 0),
        Annotation(b.Start, b.End, "https://www.history.example/eiffel?utm_source=x", 1),
        Completed([new { type = "web_search_call", id = "ws_1", status = "completed" }]));
    var response = await Read(sse);
    Check(response.Text == output, "deltas concatenate");
    Check(response.Citations.Length == 2, "duplicate annotation events are counted once");
    Check(response.SearchCalls.SequenceEqual(["completed"]), "one completed search");
    var claims = FactCheckParser.Parse(Original, response);
    Check(claims.Length == 2 && claims[0].Verdict == "wrong" && claims[0].Correction == "Der Eiffelturm steht in Paris", "wrong claim keeps its correction");
    Check(claims[0].Sources.Single().Url == "https://example.org/paris" && claims[1].Sources.Single().Url == "https://www.history.example/eiffel?utm_source=x",
        "each citation belongs to the claim whose JSON contains it; URL kept as cited");
    Check(claims[1].Sources[0].Domain == "history.example" && claims[1].Sources[0].Title == "T 1", "domain and title");
    Check(claims[0].Explanation == "Er steht in Paris." && claims[1].Explanation == "Fertig 1889.", "citation text is removed from the explanation");
    Check(claims[1].Correction is null, "a supported claim has no correction");
}

// 2. Citations only in the final response (no annotation events), text from deltas; offsets per content part.
{
    var output = """{"claims":[{"quote":"Berlin hat 4 Millionen Einwohner","verdict":"wrong","explanation":"Etwa 3,8 Millionen [Quelle](https://stat.example/berlin).","correction":"Berlin hat knapp 4 Millionen Einwohner"}]}""";
    var r = Range(output, "[Quelle](https://stat.example/berlin)");
    var message = new { type = "message", content = new object[] { new { type = "output_text", text = output, annotations = new object[] { new { type = "url_citation", start_index = r.Start, end_index = r.End, url = "https://stat.example/berlin", title = "Statistik" } } } } };
    var response = await Read(Sse(Delta(output), Completed([new { type = "web_search_call", id = "ws_9", status = "completed" }, message])));
    Check(response.Citations.Length == 1 && response.Citations[0].Start == r.Start, "final-only citations are collected after deltas");
    Check(response.Text == output, "final text is not appended twice");
    var claim = FactCheckParser.Parse(Original, response).Single();
    Check(claim.Sources.Single().Title == "Statistik" && claim.Explanation == "Etwa 3,8 Millionen.", "markdown citation stripped");
}

// 3. Final text only (no deltas): text and citations from response.completed.
{
    var output = """{"claims":[{"quote":"Er wurde 1889 fertig","verdict":"supported","explanation":"Ja (x).","correction":null}]}""";
    var r = Range(output, "(x)");
    var message = new { type = "message", content = new object[] { new { type = "output_text", text = output, annotations = new object[] { new { type = "url_citation", start_index = r.Start, end_index = r.End, url = "https://a.example/", title = "" } } } } };
    var response = await Read(Sse(Completed([new { type = "web_search_call", id = "ws", status = "completed" }, message])));
    var claim = FactCheckParser.Parse(Original, response).Single();
    Check(claim.Verdict == "supported" && claim.Sources[0].Title == "a.example", "final-only text; empty title falls back to the domain");
}

// 4. No search invoked: verdicts without citations become unverifiable and no search is counted.
{
    var output = """{"claims":[{"quote":"Der Eiffelturm steht in Rom","verdict":"wrong","explanation":"Paris.","correction":"Der Eiffelturm steht in Paris"},{"quote":"Er wurde 1889 fertig","verdict":"supported","explanation":"Ja.","correction":null}]}""";
    var response = await Read(Sse(Delta(output), Completed()));
    Check(response.SearchCalls.Length == 0, "no search call recorded");
    var claims = FactCheckParser.Parse(Original, response);
    Check(claims.All(c => c.Verdict == "unverifiable" && c.NoSource && c.Correction is null), "verdicts without a citation become unverifiable, without corrections");
}

// 5. A failed search followed by fluent prose with a citation-looking link: not counted as a completed search.
{
    var output = """{"claims":[{"quote":"Er wurde 1889 fertig","verdict":"supported","explanation":"Laut [Wiki](https://de.wikipedia.org/wiki/Eiffelturm) korrekt.","correction":null}]}""";
    var response = await Read(Sse(Search("ws_f", "failed"), Delta(output), Completed([new { type = "web_search_call", id = "ws_f", status = "failed" }])));
    Check(response.SearchCalls.SequenceEqual(["failed"]) && !response.SearchCalls.Contains("completed"), "failed search is not completed");
    var claim = FactCheckParser.Parse(Original, response).Single();
    Check(claim.Verdict == "unverifiable" && claim.Sources.Length == 0 && claim.NoSource, "a URL written into the JSON is not a source");
    Check(claim.Explanation == "Laut Wiki korrekt.", "markdown link reduced to its text");
}

// 6. Fabricated or unsafe cited URLs are dropped.
{
    var output = """{"claims":[{"quote":"Er wurde 1889 fertig","verdict":"supported","explanation":"(1)(2)(3)(4)(5)","correction":null}]}""";
    string[] bad = ["http://plain.example/", "https://user:pw@evil.example/", "javascript:alert(1)", "https://a.example/x y", "file:///C:/x"];
    var events = new List<object> { Search("ws", "completed"), Delta(output) };
    for (var i = 0; i < bad.Length; i++) { var r = Range(output, $"({i + 1})"); events.Add(Annotation(r.Start, r.End, bad[i], i)); }
    events.Add(Completed());
    var claim = FactCheckParser.Parse(Original, await Read(Sse([.. events]))).Single();
    Check(claim.Sources.Length == 0 && claim.Verdict == "unverifiable", "non-https, userinfo, scripts, whitespace and files are rejected");
}

// 7. Quotes: not in the text are dropped; repeated quotes are kept (the window decides applicability); bad verdicts become unverifiable.
{
    const string repeated = "Rom und Rom.";
    var output = """{"claims":[{"quote":"Paris","verdict":"wrong","explanation":"(a)","correction":"x"},{"quote":"Rom","verdict":"wrong","explanation":"(b)","correction":"Paris"},{"quote":"Rom und","verdict":"maybe","explanation":"(c)","correction":null}]}""";
    var events = new List<object> { Search("ws", "completed"), Delta(output) };
    foreach (var (m, i) in new[] { "(a)", "(b)", "(c)" }.Select((m, i) => (m, i))) { var r = Range(output, m); events.Add(Annotation(r.Start, r.End, $"https://s{i}.example/", i)); }
    events.Add(Completed());
    var claims = FactCheckParser.Parse(repeated, await Read(Sse([.. events])));
    Check(claims.Length == 2 && claims[0].Quote == "Rom" && claims[0].Sources[0].Url == "https://s1.example/", "a quote missing from the text is dropped; sources stay aligned");
    Check(claims[1].Verdict == "unverifiable", "an unknown verdict becomes unverifiable");
    Check(claims[0].Quote == "Rom" && claims[1].Quote.StartsWith("Rom"), "overlapping and repeated quotes are both returned");
}

// 8. Non-ASCII text before a citation keeps UTF-16 offsets aligned (ClaimSpans converts UTF-8 byte positions).
{
    var output = "Hier: " + """{"claims":[{"quote":"Er wurde 1889 fertig","verdict":"supported","explanation":"Grüße 😀 (q).","correction":null},{"quote":"Der Eiffelturm steht in Rom","verdict":"wrong","explanation":"Paris (p).","correction":"Der Eiffelturm steht in Paris"}]}""";
    var q = Range(output, "(q)");
    var p = Range(output, "(p)");
    var claims = FactCheckParser.Parse(Original, await Read(Sse(Search("ws", "completed"), Delta(output), Annotation(q.Start, q.End, "https://q.example/", 0), Annotation(p.Start, p.End, "https://p.example/", 1), Completed())));
    Check(claims[0].Sources.Single().Url == "https://q.example/" && claims[1].Sources.Single().Url == "https://p.example/", "emoji and umlauts before citations keep claim spans aligned");
}

// 9. More than five sources per claim, tracking duplicates collapse.
{
    var output = """{"claims":[{"quote":"Er wurde 1889 fertig","verdict":"supported","explanation":"(0)(1)(2)(3)(4)(5)(6)","correction":null}]}""";
    var events = new List<object> { Search("ws", "completed"), Delta(output) };
    for (var i = 0; i < 7; i++)
    {
        var r = Range(output, $"({i})");
        events.Add(Annotation(r.Start, r.End, i == 1 ? "https://s0.example/a?utm_source=x" : $"https://s{i}.example/a", i));
    }
    events.Add(Completed());
    var claim = FactCheckParser.Parse(Original, await Read(Sse([.. events]))).Single();
    Check(claim.Sources.Length == 5 && claim.Sources.Select(s => s.Url).Distinct().Count() == 5 && claim.Sources[1].Url == "https://s2.example/a", "at most five sources; tracking variants count once");
}

// 10. Unreadable output and stream errors.
{
    var threw = false;
    try { FactCheckParser.Parse(Original, new ResponseResult("Ich kann das nicht prüfen.", [], [])); }
    catch (ChatGptServiceException ex) { threw = ex.Code == "invalid_fact_check"; }
    Check(threw, "prose without JSON is an error, not an empty result");
    var empty = FactCheckParser.Parse(Original, new ResponseResult("""{"claims":[]}""", [], []));
    Check(empty.Length == 0, "empty claims list is valid");
    var interrupted = false;
    try { await Read(Sse(Delta("{\"claims\":["))); }
    catch (ChatGptServiceException ex) { interrupted = ex.Code == "stream_interrupted"; }
    Check(interrupted, "a stream without response.completed fails");
}

// 11. Review regressions (S67 #0007): non-object array entries and duplicate "claims" must not shift provenance.
{
    var output = """{"claims":[null,{"quote":"Der Eiffelturm steht in Rom","verdict":"wrong","explanation":"(a)","correction":"Der Eiffelturm steht in Paris"},{"quote":"Er wurde 1889 fertig","verdict":"supported","explanation":"(b)","correction":null}]}""";
    var a = Range(output, "(a)"); var b = Range(output, "(b)");
    var claims = FactCheckParser.Parse(Original, await Read(Sse(Search("ws", "completed"), Delta(output), Annotation(a.Start, a.End, "https://a.example/", 0), Annotation(b.Start, b.End, "https://b.example/", 1), Completed())));
    Check(claims.Length == 2 && claims[0].Sources.Single().Url == "https://a.example/" && claims[1].Verdict == "supported" && claims[1].Sources.Single().Url == "https://b.example/",
        "a null array entry is skipped without shifting sources");

    var duplicate = """{"claims":[{"quote":"Der Eiffelturm steht in Rom","verdict":"wrong","explanation":"(a)","correction":null}],"claims":[{"quote":"Er wurde 1889 fertig","verdict":"supported","explanation":"(b)","correction":null}]}""";
    a = Range(duplicate, "(a)"); b = Range(duplicate, "(b)");
    var threw = false;
    try { FactCheckParser.Parse(Original, await Read(Sse(Search("ws", "completed"), Delta(duplicate), Annotation(a.Start, a.End, "https://a.example/", 0), Annotation(b.Start, b.End, "https://b.example/", 1), Completed()))); }
    catch (ChatGptServiceException ex) { threw = ex.Code == "invalid_fact_check"; }
    Check(threw, "duplicate claims properties are rejected");
}

// 12. An annotation that arrives before its part's first delta is rebased once all parts are known.
{
    var prefix = "Ich prüfe die Aussagen jetzt mit der Websuche und melde mich gleich mit Ergebnissen.";
    var output = """{"claims":[{"quote":"Er wurde 1889 fertig","verdict":"supported","explanation":"Ja (s).","correction":null}]}""";
    var s = Range(output, "(s)");
    var response = await Read(Sse(Search("ws", "completed"), Delta(prefix, item: 0), Annotation(s.Start, s.End, "https://s.example/", 0, item: 2), Delta(output, item: 2), Completed()));
    Check(response.Citations.Single().Start == prefix.Length + s.Start, "early annotation offset is relative to its own part");
    var claim = FactCheckParser.Parse(Original, response).Single();
    Check(claim.Sources.Single().Url == "https://s.example/" && claim.Verdict == "supported", "early annotation stays with its claim");
}

// 13. Mixed parts: one part streamed, another only in the final response; final output is authoritative and ordered.
{
    var first = "Vorbemerkung. ";
    var output = """{"claims":[{"quote":"Er wurde 1889 fertig","verdict":"supported","explanation":"Ja (m).","correction":null}]}""";
    var m = Range(output, "(m)");
    var final = new object[]
    {
        new { type = "web_search_call", id = "ws", status = "completed" },
        new { type = "message", content = new object[] { new { type = "output_text", text = first, annotations = Array.Empty<object>() } } },
        new { type = "message", content = new object[] { new { type = "output_text", text = output, annotations = new object[] { new { type = "url_citation", start_index = m.Start, end_index = m.End, url = "https://m.example/", title = "M" } } } } },
    };
    var response = await Read(Sse(Delta(output, item: 2), Completed(final)));
    Check(response.Text == first + output, "a final-only part is included in output order");
    Check(response.Citations.Single().Start == first.Length + m.Start, "final citation rebased after the final-only part");
}

// 14. Zero-length and out-of-range citations are ignored instead of failing the check.
{
    var output = """{"claims":[{"quote":"Er wurde 1889 fertig","verdict":"supported","explanation":"Ja (z).","correction":null}]}""";
    var z = Range(output, "(z)");
    var response = await Read(Sse(Search("ws", "completed"), Delta(output), Annotation(z.Start, z.Start, "https://zero.example/", 0), Annotation(z.Start, output.Length + 50, "https://far.example/", 1), Annotation(z.Start, z.End, "https://ok.example/", 2), Completed()));
    var claim = FactCheckParser.Parse(Original, response).Single();
    Check(claim.Sources.Single().Url == "https://ok.example/", "zero-length and out-of-range citations are dropped");
    claim = FactCheckParser.Parse(Original, new ResponseResult(output, [new ResponseCitation(z.Start, z.Start, "https://zero.example/", null), new ResponseCitation(z.Start, int.MaxValue, "https://far.example/", null)], ["completed"])).Single();
    Check(claim.Verdict == "unverifiable" && claim.Sources.Length == 0, "the parser ignores invalid citation ranges");
}

// 15. Final-only text obeys the output size limit.
{
    var huge = new string('x', 4 * 1024 * 1024 + 1);
    var message = new { type = "message", content = new object[] { new { type = "output_text", text = huge, annotations = Array.Empty<object>() } } };
    var tooLarge = false;
    try { await Read(Sse(Completed([message]))); }
    catch (ChatGptServiceException ex) { tooLarge = ex.Code == "response_too_large"; }
    Check(tooLarge, "final-only text is size-limited");
}

{
    // A needs_context claim keeps its question and needs no source; other verdicts lose their question.
    var output = "{\"claims\":[{\"quote\":\"Die Studie\",\"verdict\":\"needs_context\",\"explanation\":\"Unklar, welche Studie.\",\"correction\":null,\"question\":\"Welche Studie ist gemeint?\"},"
        + "{\"quote\":\"Rom\",\"verdict\":\"unverifiable\",\"explanation\":\"x\",\"correction\":null,\"question\":\"Warum?\"}]}";
    var claims = FactCheckParser.Parse("Die Studie zeigt, dass Rom gross ist.", new ResponseResult(output, [], []));
    Check(claims.Length == 2 && claims[0].Verdict == "needs_context" && claims[0].Question == "Welche Studie ist gemeint?" && !claims[0].NoSource, "needs_context claim keeps its question without a source");
    Check(claims[1].Question is null, "only needs_context claims carry a question");
    var request = ReviewPrompt.BuildFactCheckRequest("m", "Die Studie zeigt.", structured: true, context: "Artikel: https://example.org/a", clarifications: [new FactClarification("Die Studie", "Welche?", "Die PISA-Studie 2022")], only: ["Die Studie"]);
    var content = System.Text.Json.Nodes.JsonNode.Parse(request["input"]![0]!["content"]!.GetValue<string>())!;
    Check(content["text"]!.GetValue<string>() == "Die Studie zeigt." && content["context"]!.GetValue<string>().Contains("example.org") && content["clarifications"]![0]!["answer"]!.GetValue<string>() == "Die PISA-Studie 2022"
        && content["only"]![0]!.GetValue<string>() == "Die Studie", "fact-check request carries context, clarifications and only");
    var schema = request["text"]!["format"]!["schema"]!["properties"]!["claims"]!["items"]!;
    Check(schema["required"]!.AsArray().Any(x => x!.GetValue<string>() == "question") && schema["properties"]!["verdict"]!["enum"]!.AsArray().Any(x => x!.GetValue<string>() == "needs_context"), "schema has needs_context and question");
}

{
    // Dialog answers: citation markers go, line breaks stay, sources only from annotations.
    var output = "Die Aussage stimmt nicht. ([who.int](https://www.who.int/x))\n- Erster Punkt ([bund.de](https://www.bund.de/y))\n- Zweiter Punkt, siehe [Quelle](https://evil.example/z)";
    var m1 = "([who.int](https://www.who.int/x))";
    var r1 = Range(output, m1);
    var m2 = "([bund.de](https://www.bund.de/y))";
    var r2 = Range(output, m2);
    var response = new ResponseResult(output, [new ResponseCitation(r1.Start, r1.End, "https://www.who.int/x", "WHO"), new ResponseCitation(r2.Start, r2.End, "https://www.bund.de/y", null), new ResponseCitation(r2.Start, r2.End, "http://insecure.example/", "x")], ["completed"]);
    var (answer, sources) = FactCheckParser.CitedAnswer(response);
    Check(answer == "Die Aussage stimmt nicht.\n- Erster Punkt\n- Zweiter Punkt, siehe Quelle", "dialog answer drops citation markers and keeps line breaks");
    Check(sources.Length == 2 && sources[0].Title == "WHO" && sources[1].Title == "bund.de" && sources.All(x => x.Url.StartsWith("https://")), "dialog sources come from https annotations only");
    var request = ReviewPrompt.BuildDialogRequest("m", ReviewPrompt.Explain, "API", [new DialogTurn("assistant", "Eine Schnittstelle."), new DialogTurn("user", "Stimmt das?")]);
    var input = request["input"]!.AsArray();
    Check(input.Count == 3 && input[0]!["role"]!.GetValue<string>() == "user" && input[0]!["content"]!.GetValue<string>().Contains("\"task\":\"explanation\"")
        && input[1]!["role"]!.GetValue<string>() == "assistant" && input[2]!["content"]!.GetValue<string>() == "Stimmt das?", "dialog request carries the text and the conversation");
    Check(request["tools"]![0]!["type"]!.GetValue<string>() == "web_search" && request["store"]!.GetValue<bool>() == false, "dialog request uses web search and is not stored");
}

Console.WriteLine(failures == 0 ? "All fact-check tests passed." : $"{failures} fact-check test(s) failed.");
return failures == 0 ? 0 : 1;
