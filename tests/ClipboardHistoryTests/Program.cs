using System.Text;
using TextFixer.Services;

// Offline tests for the clipboard history store. Uses a temporary folder; exit code = number of failures.

var failures = 0;
void Check(bool ok, string name)
{
    Console.WriteLine((ok ? "PASS " : "FAIL ") + name);
    if (!ok) failures++;
}

var folder = Path.Combine(Path.GetTempPath(), "TextFixerHistoryTests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(folder);
try
{
    var path = Path.Combine(folder, "clipboard-history.bin");
    var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    using (var history = new ClipboardHistory(path))
    {
        Check(history.Entries.Count == 0, "missing file gives an empty history");
        var changes = 0;
        history.Changed += (_, _) => changes++;
        history.Add("first", null, null, t0);
        history.Add("second", "<b>second</b>", "{\rtf1 second}", t0.AddMinutes(1));
        history.Add("   ", null, null, t0);
        Check(history.Entries.Select(e => e.Text).SequenceEqual(["second", "first"]), "newest first, whitespace ignored");
        Check(history.Entries[0].HasFormatting && !history.Entries[1].HasFormatting, "formatting flag");
        var firstId = history.Entries[1].Id;
        history.Add("first", "<i>first</i>", null, t0.AddMinutes(2));
        Check(history.Entries.Count == 2 && history.Entries[0].Id == firstId && history.Entries[0].Html == "<i>first</i>",
            "same text moves to the top, keeps its id, takes the new formatting");
        history.MoveToTop(history.Entries[1].Id, t0.AddMinutes(3));
        Check(history.Entries[0].Text == "second" && history.Entries[0].CopiedAt == t0.AddMinutes(3), "restore moves to the top");
        for (var i = 0; i < 30; i++) history.Add("entry " + i, null, null, t0.AddHours(i));
        Check(history.Entries.Count == ClipboardHistory.Capacity && history.Entries[0].Text == "entry 29"
            && history.Entries[^1].Text == "entry 10", "capped at 20, oldest dropped");
        history.Delete(history.Entries[0].Id);
        Check(history.Entries[0].Text == "entry 28", "delete");
        Check(changes > 0, "changes are reported");
        history.Add("Grüße 😀\r\nzweite Zeile", "<p>Grüße</p>", null, t0.AddDays(2));
    }

    var raw = File.ReadAllBytes(path);
    Check(!Encoding.UTF8.GetString(raw).Contains("entry 28") && !Encoding.Unicode.GetString(raw).Contains("entry 28"), "file is encrypted");
    using (var reloaded = new ClipboardHistory(path))
    {
        Check(reloaded.Entries.Count == 20 && reloaded.Entries[0].Text == "Grüße 😀\r\nzweite Zeile"
            && reloaded.Entries[0].Html == "<p>Grüße</p>" && reloaded.Entries[0].CopiedAt == t0.AddDays(2), "dispose flushes; reload round-trips text, formatting and time");
        reloaded.Clear();
        Check(reloaded.Entries.Count == 0 && !File.Exists(path), "clear empties the list and deletes the file");
    }

    // Images: a separate encrypted file per entry, deduplicated, deleted with the entry; files keep only their paths.
    byte[] png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, 0x49, 0x48, 0x44, 0x52, 0, 0, 0, 3, 0, 0, 0, 2, 8, 6, 0, 0, 0, 1, 2, 3];
    var images = Path.Combine(folder, "clipboard-images");
    using (var history = new ClipboardHistory(path))
    {
        history.AddImage(png, "image/png", 3, 2, t0);
        history.Add("text", null, null, t0.AddMinutes(1));
        history.AddImage(png, "image/png", 3, 2, t0.AddMinutes(2));
        var image = history.Entries[0];
        Check(history.Entries.Count == 2 && image.IsImage && image.Text == "" && image.ImageWidth == 3 && image.ImageBytes == png.Length,
            "the same image moves to the top instead of a second entry");
        Check(history.ReadImage(image.Id) is { } back && back.SequenceEqual(png), "image bytes round-trip");
        var stored = File.ReadAllBytes(Path.Combine(images, image.Id + ".bin"));
        Check(!stored.AsSpan().SequenceEqual(png) && stored.AsSpan().IndexOf(png.AsSpan(0, 8)) < 0, "image file is encrypted");
        history.AddFiles([@"C:\a\Film.mp4", @"C:\b\notes.txt"], t0.AddMinutes(3));
        history.AddFiles([@"c:\A\film.mp4", @"C:\b\NOTES.txt"], t0.AddMinutes(4));
        Check(history.Entries.Count == 3 && history.Entries[0].IsFiles && history.Entries[0].Text == "c:\\A\\film.mp4\nC:\\b\\NOTES.txt",
            "the same file list moves to the top; the text lists the paths");
        Check(history.Find("missing") is null && history.ReadImage(history.Entries[0].Id) is null, "only image entries have image bytes");
    }
    File.WriteAllBytes(Path.Combine(images, "orphan.bin"), [1]);
    using (var reloaded = new ClipboardHistory(path))
    {
        var image = reloaded.Entries.First(e => e.IsImage);
        Check(reloaded.Entries.Count == 3 && reloaded.Entries[0].Files!.Length == 2 && image.ImageType == "image/png", "images and files reload");
        Check(!File.Exists(Path.Combine(images, "orphan.bin")), "image files without an entry are removed");
        reloaded.Delete(image.Id);
        Check(!File.Exists(Path.Combine(images, image.Id + ".bin")), "deleting an image entry deletes its file");
        reloaded.AddImage(png, "image/png", 3, 2, t0);
        reloaded.Clear();
        Check(!Directory.Exists(images), "clear deletes the image files");
    }
    using (var full = new ClipboardHistory(path))
    {
        var big = new byte[ClipboardHistory.MaxImageTotal / 3 + 1];
        for (var i = 0; i < 3; i++) { big[0] = (byte)i; full.AddImage(big, "image/bmp", 1, 1, t0.AddMinutes(i)); }
        Check(full.Entries.Count(e => e.IsImage) == 2 && full.Entries[0].ImageBytes == big.Length, "images over the total size drop the oldest");
        full.Clear();
    }

    // Clipboard bitmaps become BMP files with a file header; byte ranges for video seeking.
    var dib = new byte[40 + 2 * 2 * 4];
    BitConverter.GetBytes(40).CopyTo(dib, 0); BitConverter.GetBytes(2).CopyTo(dib, 4); BitConverter.GetBytes(-2).CopyTo(dib, 8);
    BitConverter.GetBytes((short)1).CopyTo(dib, 12); BitConverter.GetBytes((short)32).CopyTo(dib, 14);
    var bmp = TextFixer.Native.ClipboardImage.BmpFromDib(dib)!;
    Check(bmp[0] == 'B' && bmp[1] == 'M' && BitConverter.ToInt32(bmp, 2) == dib.Length + 14 && BitConverter.ToInt32(bmp, 10) == 54
        && TextFixer.Native.ClipboardImage.DibSize(dib) == (2, 2), "DIB to BMP: header, pixel offset and size");
    BitConverter.GetBytes(3).CopyTo(dib, 16);
    Check(BitConverter.ToInt32(TextFixer.Native.ClipboardImage.BmpFromDib(dib)!, 10) == 66, "bit field masks move the pixel offset");
    Check(TextFixer.Native.ClipboardImage.BmpFromDib([1, 2, 3]) is null && TextFixer.Native.ClipboardImage.PngSize(png) == (3, 2), "damaged bitmaps are refused, PNG size is read");
    Check(HistoryMedia.ParseRange("bytes=0-", 100) == (0, 99) && HistoryMedia.ParseRange("bytes=10-19", 100) == (10, 19)
        && HistoryMedia.ParseRange("bytes=-30", 100) == (70, 99) && HistoryMedia.ParseRange("bytes=90-500", 100) == (90, 99), "byte ranges");
    Check(HistoryMedia.ParseRange("bytes=100-", 100) is null && HistoryMedia.ParseRange("bytes=5-1", 100) is null && HistoryMedia.ParseRange("bytes=0-1,4-5", 100) is null
        && HistoryMedia.ParseRange("items=0-1", 100) is null, "invalid ranges are refused");
    Check(HistoryMedia.ContentType(@"C:\x\a.MP4") == "video/mp4" && HistoryMedia.ContentType("a.png") == "image/png" && HistoryMedia.ContentType("a.exe") is null,
        "only image and video files are served");
    Check(HistoryMedia.YouTubeId("https://www.youtube.com/watch?v=dQw4w9WgXcQ&t=5") == "dQw4w9WgXcQ" && HistoryMedia.YouTubeId("https://youtu.be/dQw4w9WgXcQ") == "dQw4w9WgXcQ"
        && HistoryMedia.YouTubeId("https://m.youtube.com/shorts/dQw4w9WgXcQ") == "dQw4w9WgXcQ" && HistoryMedia.YouTubeId("https://www.youtube.com/@channel") is null
        && HistoryMedia.YouTubeId("https://evil.example/watch?v=dQw4w9WgXcQ") is null && HistoryMedia.YouTubeId("javascript:alert(1)") is null, "YouTube video ids");
    Check(HistoryMedia.IsOpenable(@"C:\a\Brief.PDF") && HistoryMedia.IsOpenable("clip.mp4") && !HistoryMedia.IsOpenable("tool.exe") && !HistoryMedia.IsOpenable("run.bat")
        && !HistoryMedia.IsOpenable("app.lnk") && !HistoryMedia.IsOpenable("x.ps1") && !HistoryMedia.IsOpenable("noext"), "only media and listed documents open in their app");
    Check(HistoryMedia.ContentType("page.HTML") == "text/html" && HistoryMedia.ContentType("a.htm") == "text/html" && HistoryMedia.ContentType("a.svg") is null,
        "HTML files are served for the page preview, SVG files are not");
    Check(HistoryMedia.WebUrl("https://example.com/a?b=1") is not null && HistoryMedia.WebUrl("file:///C:/x") is null && HistoryMedia.WebUrl("javascript:alert(1)") is null
        && HistoryMedia.WebUrl("https://a.com b") is null, "only http and https links open in the browser");
    Check(HistoryMedia.MailAddress("max@example.de") == "max@example.de" && HistoryMedia.MailAddress("mailto:max@example.de") == "max@example.de"
        && HistoryMedia.MailAddress("write max@example.de") is null, "e-mail addresses");

    File.WriteAllBytes(path, [1, 2, 3, 4]);
    using (var damaged = new ClipboardHistory(path))
        Check(damaged.Entries.Count == 0, "damaged file gives an empty history");

    var settingsPath = Path.Combine(folder, "settings.json");
    Check(AppSettings.Load(settingsPath).ClipboardHistory, "history is on by default");
    var settings = AppSettings.Load(settingsPath);
    settings.ClipboardHistory = false;
    settings.Save();
    Check(!AppSettings.Load(settingsPath).ClipboardHistory, "setting round-trips");
    Check(AppSettings.Load(settingsPath).ClipboardImages, "images are recorded by default");
    settings.ClipboardImages = false;
    settings.Save();
    Check(!AppSettings.Load(settingsPath).ClipboardImages, "image setting round-trips");
    File.WriteAllText(settingsPath, "{ not json");
    Check(AppSettings.Load(settingsPath).ClipboardHistory, "damaged settings fall back to defaults");

    // Shortcuts: parsing, rules, round-trip, swaps and fallbacks.
    Check(Shortcut.Parse("Ctrl+Alt+K") == new Shortcut(Shortcut.Control | Shortcut.Alt, 0x4B) && Shortcut.Parse("alt+f12") == new Shortcut(Shortcut.Alt, 0x7B)
        && Shortcut.Parse("Shift+Alt+7")?.ToString() == "Alt+Shift+7", "shortcut parse and display order");
    Check(Shortcut.Parse("Ctrl+Alt+.")?.Key == Shortcut.PeriodKey && Shortcut.Parse("Ctrl+Alt+,")?.ToString() == "Ctrl+Alt+," && Shortcut.Parse("Ctrl+Alt+-")?.Key == Shortcut.MinusKey,
        "period, comma and minus parse and print by their label");
    Check(Shortcut.Parse("Alt+Space") is null && Shortcut.Parse("Alt+Alt+Q") is null && Shortcut.Parse("Alt+F0") is null && Shortcut.Parse("Alt+F25") is null && Shortcut.Parse("Win+Q") is null && Shortcut.Parse("") is null,
        "malformed shortcuts are rejected");
    var defaults = Shortcuts.Defaults();
    Check(Shortcuts.Problem("review", Shortcut.Parse("Shift+Q")!.Value, defaults) is not null, "a shortcut needs Ctrl or Alt");
    Check(Shortcuts.Problem("review", Shortcut.Parse("Ctrl+C")!.Value, defaults)?.Contains("editing") == true && Shortcuts.Problem("review", Shortcut.Parse("Ctrl+Shift+C")!.Value, defaults) is null,
        "editing shortcuts are reserved, variants with Shift are allowed");
    Check(Shortcuts.Problem("review", Shortcut.Parse("Alt+F4")!.Value, defaults) is not null, "Alt+F4 is reserved");
    Check(Shortcuts.Problem("translate", Shortcut.Parse("Ctrl+Alt+.")!.Value, defaults) == "Ctrl+Alt+. is already the shortcut for Open.", "duplicates name the other task");
    Check(Shortcuts.Problem("review", Shortcut.Parse("Ctrl+Alt+-")!.Value, defaults) is null, "keeping the own shortcut is fine");
    settings = AppSettings.Load(settingsPath);
    Check(settings.Shortcuts["open"]?.ToString() == "Ctrl+Alt+." && settings.Shortcuts["review"]?.ToString() == "Ctrl+Alt+-"
        && new[] { "translate", "factcheck", "rewrite", "summary", "explain" }.All(t => settings.Shortcuts[t] is null), "defaults: Open and Review set, the other tasks unset");
    settings.Shortcuts["summary"] = Shortcut.Parse("Ctrl+Alt+S");
    settings.Shortcuts["review"] = null;
    settings.Save();
    var roundTrip = AppSettings.Load(settingsPath);
    Check(roundTrip.Shortcuts["summary"]?.ToString() == "Ctrl+Alt+S" && roundTrip.Shortcuts["review"] is null, "set and removed shortcuts round-trip");
    File.WriteAllText(settingsPath, """{"shortcuts":{"open":"Alt+Q","review":"Alt+E"}}""");
    var swapped = AppSettings.Load(settingsPath).Shortcuts;
    Check(swapped["open"]?.ToString() == "Alt+Q" && swapped["review"]?.ToString() == "Alt+E", "swapped shortcuts load");
    File.WriteAllText(settingsPath, """{"shortcuts":{"open":"Alt+E","translate":"Alt+E","factcheck":"Alt+W"}}""");
    var clash = AppSettings.Load(settingsPath).Shortcuts;
    Check(clash["open"]?.ToString() == "Ctrl+Alt+." && clash["translate"] is null && clash["factcheck"] is null, "a set with duplicates falls back to the defaults");
    File.WriteAllText(settingsPath, """{"shortcuts":{"open":null,"review":"Ctrl+V","translate":"nonsense","explain":"Ctrl+Alt+X"}}""");
    var mixed = AppSettings.Load(settingsPath);
    Check(mixed.Shortcuts["open"]?.ToString() == "Ctrl+Alt+." && mixed.Shortcuts["review"]?.ToString() == "Ctrl+Alt+-" && mixed.Shortcuts["translate"] is null && mixed.Shortcuts["explain"]?.ToString() == "Ctrl+Alt+X",
        "Open cannot be removed; invalid entries fall back to their defaults");
    File.WriteAllText(settingsPath, """{"emailSpelling":"E-Mail","addressForm":"Du"}""");
    var writing = AppSettings.Load(settingsPath);
    Check(writing.EmailSpelling == "E-Mail" && writing.AddressForm == "Du", "writing preferences load");
    File.WriteAllText(settingsPath, """{"emailSpelling":"mail","addressForm":"Ihr"}""");
    writing = AppSettings.Load(settingsPath);
    Check(writing.EmailSpelling == "keep" && writing.AddressForm == "auto", "unknown writing preferences fall back to keep and automatic");
    Check(WritingPreferences.EmailRule("keep") == "" && WritingPreferences.TranslationAddressRule("auto") == "", "keep and automatic add no rule");
    Check(WritingPreferences.TranslationAddressRule("Sie").Contains("formal Sie") && WritingPreferences.TranslationAddressRule("Du").Contains("capitalized"), "translation address rules");
    Check(WritingPreferences.EmailRule("Email").Contains("Never change URLs"), "E-Mail rule protects URLs and code");
    Check(WritingPreferences.ReviewDuRule("keep").Contains("never change du to Du or Du to du") && WritingPreferences.ReviewDuRule("Du").Contains("capitalize") && WritingPreferences.ReviewDuRule("du").Contains("Never switch between informal and formal"),
        "review du rule: keep protects the written form, du/Du only change capitalization");
    File.WriteAllText(settingsPath, """{"reviewDu":"Du"}""");
    Check(AppSettings.Load(settingsPath).ReviewDu == "Du", "review du setting loads");
    File.WriteAllText(settingsPath, """{"reviewDu":"Sie"}""");
    Check(AppSettings.Load(settingsPath).ReviewDu == "keep", "Sie is not a review option");
}
finally { Directory.Delete(folder, recursive: true); }

Console.WriteLine(failures == 0 ? "RESULT all passed" : $"RESULT {failures} failed");
return failures;
