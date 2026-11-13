using System.Text.Json;
using TextFixer;

var count = 0;
void Test(string name, string original, string revised, object edits, bool valid)
{
    var passed = false;
    try { EditorEdits.Validate(original, revised, JsonSerializer.SerializeToElement(edits)); passed = true; }
    catch (InvalidOperationException) { }
    if (passed != valid) throw new Exception("FAIL " + name);
    count++;
}
object Edit(int start, int end, string before, string after) => new { start, end, before, after };
Test("valid typo", "A tesst.", "A test.", new[] { Edit(2, 7, "tesst", "test") }, true);
Test("Unicode and empty paragraph", "🙂\r\n\r\ntesst", "🙂\r\n\r\ntest", new[] { Edit(6, 11, "tesst", "test") }, true);
Test("wrong revised text", "tesst", "other", new[] { Edit(0, 5, "tesst", "test") }, false);
Test("stale before", "tesst", "test", new[] { Edit(0, 5, "wrong", "test") }, false);
Test("newline deletion", "a\n\nb", "a\nb", new[] { Edit(1, 2, "\n", "") }, false);
Test("newline insertion", "ab", "a\nb", new[] { Edit(1, 1, "", "\n") }, false);
Test("surrogate split", "🙂", "x", new[] { Edit(0, 1, "\uD83D", "x") }, false);
Test("overlap", "tesst", "tt", new[] { Edit(0, 3, "tes", "t"), Edit(2, 5, "sst", "t") }, false);
Test("negative offset", "text", "text", new[] { Edit(-1, 0, "", "") }, false);
Test("outside selection", "text", "text", new[] { Edit(0, 10, "text", "text") }, false);
Test("no-op", "text", "text", Array.Empty<object>(), true);
Test("duplicate insertion offset", "ab", "aXYb", new[] { Edit(1, 1, "", "X"), Edit(1, 1, "", "Y") }, false);
Test("too many edits", "text", "text", Enumerable.Repeat(Edit(0, 0, "", ""), 201).ToArray(), false);
Console.WriteLine($"PASS: {count} editor range validation cases.");
