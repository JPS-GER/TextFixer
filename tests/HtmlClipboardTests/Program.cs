using System.Text;
using System.Text.Json;
using TextFixer;
using TextFixer.Native;

var count = 0;
void Check(bool condition, string name) { if (!condition) throw new Exception("FAIL: " + name); count++; }
HtmlClipboard Document(string fragment) => new("<html><head><style>p { margin-bottom: 1em; }</style></head><body><!--StartFragment-->", fragment, "<!--EndFragment--></body></html>");
EditorEdit[] Edits(string original, string revised, params (int Start, int End, string Before, string After)[] values) =>
    EditorEdits.Validate(original, revised, JsonSerializer.SerializeToElement(values.Select(v => new { start = v.Start, end = v.End, before = v.Before, after = v.After })));
string Rewrite(HtmlClipboard document, string original, string revised, string before, string after)
{
    var start = original.IndexOf(before, StringComparison.Ordinal);
    return HtmlClipboard.Parse(document.Rewrite(original, Edits(original, revised, (start, start + before.Length, before, after))))!.Fragment;
}

const string paragraphHtml = "<p style=\"margin:0 0 1em\">Ich kann dann Sessions öffnen.</p><p>Innerhalb der Terminalfenster habe ich Zusatzfunktionen.</p>";
const string paragraphText = "Ich kann dann Sessions öffnen.\r\n\r\nInnerhalb der Terminalfenster habe ich Zusatzfunktionen.";
var paragraphResult = Rewrite(Document(paragraphHtml), paragraphText, paragraphText.Replace("Sessions", "Sitzungen"), "Sessions", "Sitzungen");
Check(paragraphResult == paragraphHtml.Replace("Sessions", "Sitzungen"), "paragraph gap markup and margin style survive exact text patch");
Check(Rewrite(Document("<div>A tesst.</div><div><br></div><div>B.</div>"), "A tesst.\r\n\r\nB.", "A test.\r\n\r\nB.", "tesst", "test") == "<div>A test.</div><div><br></div><div>B.</div>", "empty paragraph element retained");
Check(Rewrite(Document("<p><b>A tesst</b> and <i>other text</i>.</p>"), "A tesst and other text.", "A test and other text.", "tesst", "test") == "<p><b>A test</b> and <i>other text</i>.</p>", "untouched inline formatting retained");
Check(Rewrite(Document("<p>rec<b>ie</b>ve</p>"), "recieve", "receive", "ie", "ei") == "<p>rec<b>ei</b>ve</p>", "minimal patch preserves unchanged characters outside formatting boundary");
var unicode = Document("<p>Grüße 🙂 &amp; a tesst.</p>");
var parsed = HtmlClipboard.Parse(unicode.Encode());
Check(parsed == unicode, "CF_HTML UTF-8 byte-offset round trip");
Check(Rewrite(unicode, "Grüße 🙂 & a tesst.", "Grüße 🙂 & a test.", "tesst", "test") == "<p>Grüße 🙂 &amp; a test.</p>", "Unicode and entity spelling retained");
Check(Rewrite(Document("<p>A &lt; B.</p>"), "A < B.", "A > B.", "<", ">") == "<p>A &gt; B.</p>", "replacement encoded as text, not markup");
Check(Rewrite(Document("<p>A tesst&nbsp;here.</p>"), "A tesst here.", "A test here.", "tesst", "test") == "<p>A test&nbsp;here.</p>", "unmodified NBSP retained");
Check(Rewrite(Document("<p>A &#x1F642; tesst.</p>"), "A 🙂 tesst.", "A 🙂 test.", "tesst", "test") == "<p>A &#x1F642; test.</p>", "surrogate entity retains raw encoding");
Check(Rewrite(Document("<p>A &amp; B.</p>"), "A & B.", "A and B.", "&", "and") == "<p>A and B.</p>", "entity replacement maps exact source range");
Check(Rewrite(Document("<p>A  tesst.</p>"), "A  tesst.", "A tesst.", "  ", " ") == "<p>A tesst.</p>", "literal within-node whitespace correction");
Check(Rewrite(Document("<p>A  tesst.</p>"), "A tesst.", "A test.", "tesst", "test") == "<p>A  test.</p>", "collapsed source whitespace does not block unrelated correction or change HTML spaces");
var punctuation = Document("<p><b>Hello</b> world</p>");
var insertions = Edits("Hello world", "Hello, world!", (5, 5, "", ","), (11, 11, "", "!"));
Check(HtmlClipboard.Parse(punctuation.Rewrite("Hello world", insertions))!.Fragment == "<p><b>Hello,</b> world!</p>", "insertions retain surrounding tags");
var attributes = Document("<p title=\"1 > 0\">A tesst.</p><!-- comment --><p>B.</p>");
Check(Rewrite(attributes, "A tesst.\r\nB.", "A test.\r\nB.", "tesst", "test") == "<p title=\"1 > 0\">A test.</p><!-- comment --><p>B.</p>", "quoted greater-than attributes and comments preserved");
var reject = false;
try { Rewrite(Document("<p>te<b>ss</b>t</p>"), "tesst", "test", "tesst", "test"); }
catch (InvalidOperationException) { reject = true; }
Check(reject, "cross-format edit refuses before writing a rich payload");
reject = false;
try { Rewrite(Document("<p>Different text</p>"), "A tesst.", "A test.", "tesst", "test"); }
catch (InvalidOperationException) { reject = true; }
Check(reject, "HTML/plain-text mismatch rejected");
Check(HtmlClipboard.Parse("not a CF_HTML payload") is null, "malformed header rejected");
Check(HtmlClipboard.Parse(unicode.Encode().Replace("StartFragment:", "WrongFragment:")) is null, "missing byte offset rejected");
Check(HtmlClipboard.Parse(unicode.Encode()[..^5]) is null, "truncated UTF-8 payload rejected");
Check(HtmlClipboard.Parse(unicode.Rewrite("irrelevant", [])) == unicode, "no changes preserve full rich fragment");
var headerBytes = Encoding.UTF8.GetBytes(unicode.Encode());
Check(Encoding.UTF8.GetString(headerBytes).Contains("Grüße 🙂"), "clipboard payload remains UTF-8");
var withUrl = unicode with { SourceUrl = "https://example.test/folder/Größe/" };
Check(HtmlClipboard.Parse(withUrl.Encode()) == withUrl, "source URL and UTF-8 offsets survive round trip");
var rewrittenWithUrl = HtmlClipboard.Parse(withUrl.Rewrite("Grüße 🙂 & a tesst.", Edits("Grüße 🙂 & a tesst.", "Grüße 🙂 & a test.", (13, 18, "tesst", "test"))));
Check(rewrittenWithUrl?.SourceUrl == withUrl.SourceUrl, "source URL remains available after text patch");

HtmlClipboard Simple(string fragment) => new("<html><body><!--StartFragment-->", fragment, "<!--EndFragment--></body></html>");
var formatted = Simple("<p><u>word </u>next</p>");
var fixes = formatted.FindFormatting("word next");
Check(fixes.Length == 1 && fixes[0].Start == 4, "trailing underline space detected");
Check(fixes[0].Property == "underline" && fixes[0].RunStart == 0 && fixes[0].RunEnd == 5, "preview carries exact decoration and run boundaries");
Check(formatted.FixFormatting("word next", [fixes[0].Id], []).Fragment == "<p><u>word</u> next</p>", "only edge space moved out of underline");
var leading = Simple("<p>first<u> word</u></p>");
Check(leading.FixFormatting("first word", [leading.FindFormatting("first word")[0].Id], []).Fragment == "<p>first <u>word</u></p>", "leading space moved outside underline");
foreach (var fragment in new[] { "<p><u>word  </u>next</p>", "<p><u> </u>next</p>", "<p><u>word </u></p>", "<p><u>two words</u></p>", "<p><b>word </b>next</p>", "<p class='x'><u>word </u>next</p>", "<p><u><u>word </u>next</u></p>" })
    Check(Simple(fragment).FindFormatting("word next").Length == 0, "ambiguous or intentional formatting skipped: " + fragment);
var linked = Simple("<p><a href='https://example.test'>word </a>next</p>");
Check(linked.FixFormatting("word next", [linked.FindFormatting("word next")[0].Id], []).Fragment == "<p><a href='https://example.test'>word</a> next</p>", "link preserved, space unlinked");
var nbsp = Simple("<p><s>word&nbsp;</s>next</p>");
Check(nbsp.FixFormatting("word\u00a0next", [nbsp.FindFormatting("word\u00a0next")[0].Id], []).Fragment == "<p><s>word</s>&nbsp;next</p>", "NBSP encoding retained");
reject = false;
try { formatted.FixFormatting("word next", [fixes[0].Id], [new EditorEdit(3, 4, "d", "D")]); } catch (InvalidOperationException) { reject = true; }
Check(reject, "abutting text correction rejected");
Check(formatted.FixFormatting("word next", [], []).Fragment == formatted.Fragment, "no acceptance means no formatting change");
Console.WriteLine($"PASS: {count} rich clipboard paragraph, formatting, Unicode, entity, byte-offset and refusal checks.");
