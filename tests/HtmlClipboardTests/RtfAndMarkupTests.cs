using System.Text.Json;
using TextFixer;
using TextFixer.Native;
using TextFixer.Services;

/// <summary>RTF write-back and the markup guard for Markdown, BBCode and other markup typed as text.</summary>
internal static class RtfAndMarkupTests
{
    private static int _count;
    private static string Here([System.Runtime.CompilerServices.CallerFilePath] string path = "") => Path.GetDirectoryName(path)!;
    private static void Check(bool condition, string name) { if (!condition) throw new Exception("FAIL: " + name); _count++; }

    private static EditorEdit[] Edit(string original, string before, string after)
    {
        var start = original.IndexOf(before, StringComparison.Ordinal);
        if (start < 0) throw new Exception("test text missing: " + before);
        var revised = original[..start] + after + original[(start + before.Length)..];
        return EditorEdits.Validate(original, revised, JsonSerializer.SerializeToElement(new[] { new { start, end = start + before.Length, before, after } }));
    }

    private static string? Rtf(string rtf, string original, string before, string after) => RtfClipboard.TryRewrite(rtf, original, Edit(original, before, after));

    internal static int Run()
    {
        // A Word-like document: hidden tables, info and field codes, formatting runs, escapes and paragraphs.
        const string word = @"{\rtf1\ansi\ansicpg1252\deff0{\fonttbl{\f0\fswiss Calibri;}}{\colortbl;\red255\green0\blue0;}{\*\generator Riched20;}{\info{\title Titel}}"
            + @"\pard\f0\fs22 Das ist ein Tesst mit {\b fettem Wort} und H\'e4usern.\par" + "\r\n"
            + @"Zweiter Absatz mit {\field{\*\fldinst HYPERLINK ""https://example.test""}{\fldrslt Link}} und \" + "u8364? Preis.\\par}";
        const string text = "Das ist ein Tesst mit fettem Wort und Häusern.\r\nZweiter Absatz mit Link und € Preis.";
        Check(RtfClipboard.VisibleText(word).TrimEnd().Replace("\n", "\r\n") == text, "visible RTF text skips hidden groups and decodes escapes");
        Check(Rtf(word, text, "Tesst", "Test") == word.Replace("Tesst", "Test"), "a word in a plain run is replaced, everything else stays");
        Check(Rtf(word, text, "fettem", "fetten") == word.Replace("fettem", "fetten"), "a word inside a bold group keeps the group");
        Check(Rtf(word, text, "Häusern", "Hausern") == word.Replace(@"H\'e4usern", "Hausern"), "a word with an ANSI escape is replaced as a whole");
        Check(Rtf(word, text, "Tesst", "Täst")!.Contains(@"T\u228?st"), "non-ASCII corrections are written as \\uN with a replacement character");
        Check(Rtf(word, text, "Preis", "{Preis}")!.Contains(@"\{Preis\}"), "braces in a correction are escaped");
        Check(Rtf(word, text, "Link", "Verweis") == word.Replace(@"\fldrslt Link", @"\fldrslt Verweis"), "the visible result of a field is corrected, its code is not");
        Check(Rtf(word, text, "Wort und", "Satz oder") is null, "a correction across a formatting change is refused");
        Check(Rtf(word, text, "Wort und", "Wort, und") == word.Replace("fettem Wort}", "fettem Wort,}"), "a correction is narrowed to the characters it changes");
        Check(Rtf(word, text, "Häusern.\r\nZweiter".Split('\r')[0], "Häusern!") is not null, "a correction at the end of a paragraph stays inside it");
        Check(Rtf(word, "Ein ganz anderer Text.", "anderer", "neuer") is null, "an RTF that does not match the plain text is not used");
        Check(RtfClipboard.TryRewrite(null, text, []) is null && RtfClipboard.TryRewrite("not rtf", text, []) is null, "missing or invalid RTF gives null");

        // Real RTF of the Windows rich edit control (WordPad, rich text fields): font switches inside words for
        // umlauts and emoji, a bullet list whose bullet is missing from the plain text.
        var sample = File.ReadAllText(Path.Combine(Here(), "richedit-sample.rtf"));
        const string samplePlain = "Das ist ein Tesst mit fettem Wort und Häusern 🙂.\r\nZweiter Absatz mit kursivem Txt und € Preis.\r\nPunkt eins";
        foreach (var (before, after) in new[] { ("Tesst", "Test"), ("Häusern", "Hauser"), ("fettem", "fetten"), ("Txt", "Text"), ("Preis", "Preise"), ("eins", "zwei") })
            Check(RtfClipboard.TryRewrite(sample, samplePlain, Edit(samplePlain, before, after)) is { } fixedSample
                && RtfClipboard.VisibleText(fixedSample).Replace("·\t", "").TrimEnd().Replace("\n", "\r\n") == samplePlain.Replace(before, after),
                $"rich edit sample: {before} to {after}");

        // A text right after a control word without its space must not run into it.
        const string tight = @"{\rtf1\ansi {\b0\lquote}x\par}";
        Check(RtfClipboard.VisibleText(tight) == "‘x\n", "symbol control words are visible characters");
        const string digits = @"{\rtf1\ansi\b0x\par}";
        var tightResult = RtfClipboard.TryRewrite(digits, "x", Edit("x", "x", "5x"));
        Check(tightResult == @"{\rtf1\ansi\b0 5x\par}", "a correction after a control word gets the delimiting space");
        const string unicodeRun = @"{\rtf1\ansi\uc1 Gr\u252?\u223?e und Tesst\par}";
        Check(RtfClipboard.TryRewrite(unicodeRun, "Grüße und Tesst", Edit("Grüße und Tesst", "Tesst", "Test")) == unicodeRun.Replace("Tesst", "Test"), "\\uN escapes with replacement characters map to one character");
        Check(RtfClipboard.TryRewrite(unicodeRun, "Grüße und Tesst", Edit("Grüße und Tesst", "Grüße", "Gruesse")) == @"{\rtf1\ansi\uc1 Gruesse und Tesst\par}", "a word written with \\uN escapes is replaced including its replacement characters");

        // Markup typed as text: the words change, the markup does not.
        const string markdown = "# Titel\n\nDas ist **fett** und [ein Lnk](https://example.test/a_b) mit `code_x`.\n- Punkt eins\n";
        Check(MarkupGuard.FirstConflict(markdown, Edit(markdown, "fett", "fetter")) is null, "a word inside bold Markdown may change");
        Check(MarkupGuard.FirstConflict(markdown, Edit(markdown, "Lnk", "Link")) is null, "a link text may change");
        Check(MarkupGuard.FirstConflict(markdown, Edit(markdown, "**fett**", "fett")) is not null, "removing bold markers is refused");
        Check(MarkupGuard.FirstConflict(markdown, Edit(markdown, "example", "beispiel")) is not null, "a link address is protected");
        Check(MarkupGuard.FirstConflict(markdown, Edit(markdown, "code_x", "code")) is not null, "inline code is protected");
        Check(MarkupGuard.FirstConflict(markdown, Edit(markdown, "# ", "")) is not null, "a heading marker is protected");
        Check(MarkupGuard.FirstConflict(markdown, Edit(markdown, "- Punkt", "Punkt")) is not null, "a list marker is protected");
        const string bbcode = "[b]Hallo Welt[/b] siehe [url=https://example.test]hier[/url] und [quote=Max]Zitat mit Feler[/quote] #Tag";
        Check(MarkupGuard.FirstConflict(bbcode, Edit(bbcode, "Feler", "Fehler")) is null, "a word inside a BBCode quote may change");
        Check(MarkupGuard.FirstConflict(bbcode, Edit(bbcode, "[/b]", "")) is not null, "a BBCode tag is protected");
        Check(MarkupGuard.FirstConflict(bbcode, Edit(bbcode, "#Tag", "Tag")) is not null, "a hashtag is protected");
        Check(MarkupGuard.FirstConflict(bbcode, Edit(bbcode, "hier", "dort")) is null, "a BBCode link text may change");
        const string prose = "Er sagte, 5 * 3 sei 15 - und das stimmt nicht, schrieb max@example.de.";
        Check(MarkupGuard.FirstConflict(prose, Edit(prose, "sei", "ist")) is null && MarkupGuard.FirstConflict(prose, Edit(prose, " - ", ", ")) is null, "prose with a lone asterisk or dash is not markup");
        var refused = false;
        try { MarkupGuard.Check(markdown, Edit(markdown, "**fett**", "fett")); } catch (InvalidOperationException ex) { refused = ex.Message.Contains("**fett**"); }
        Check(refused, "the refusal names the correction");
        return _count;
    }
}
