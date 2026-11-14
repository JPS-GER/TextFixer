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

    File.WriteAllBytes(path, [1, 2, 3, 4]);
    using (var damaged = new ClipboardHistory(path))
        Check(damaged.Entries.Count == 0, "damaged file gives an empty history");

    var settingsPath = Path.Combine(folder, "settings.json");
    Check(AppSettings.Load(settingsPath).ClipboardHistory, "history is on by default");
    var settings = AppSettings.Load(settingsPath);
    settings.ClipboardHistory = false;
    settings.Save();
    Check(!AppSettings.Load(settingsPath).ClipboardHistory, "setting round-trips");
    File.WriteAllText(settingsPath, "{ not json");
    Check(AppSettings.Load(settingsPath).ClipboardHistory, "damaged settings fall back to defaults");

    // Shortcuts: parsing, rules, round-trip, swaps and fallbacks.
    Check(Shortcut.Parse("Ctrl+Alt+K") == new Shortcut(Shortcut.Control | Shortcut.Alt, 0x4B) && Shortcut.Parse("alt+f12") == new Shortcut(Shortcut.Alt, 0x7B)
        && Shortcut.Parse("Shift+Alt+7")?.ToString() == "Alt+Shift+7", "shortcut parse and display order");
    Check(Shortcut.Parse("Alt+Space") is null && Shortcut.Parse("Alt+Alt+Q") is null && Shortcut.Parse("Alt+F0") is null && Shortcut.Parse("Alt+F25") is null && Shortcut.Parse("Win+Q") is null && Shortcut.Parse("") is null,
        "malformed shortcuts are rejected");
    var defaults = Shortcuts.Defaults();
    Check(Shortcuts.Problem("review", Shortcut.Parse("Shift+Q")!.Value, defaults) is not null, "a shortcut needs Ctrl or Alt");
    Check(Shortcuts.Problem("review", Shortcut.Parse("Ctrl+C")!.Value, defaults)?.Contains("editing") == true && Shortcuts.Problem("review", Shortcut.Parse("Ctrl+Shift+C")!.Value, defaults) is null,
        "editing shortcuts are reserved, variants with Shift are allowed");
    Check(Shortcuts.Problem("review", Shortcut.Parse("Alt+F4")!.Value, defaults) is not null, "Alt+F4 is reserved");
    Check(Shortcuts.Problem("translate", Shortcut.Parse("Alt+E")!.Value, defaults) == "Alt+E is already the shortcut for Open.", "duplicates name the other task");
    Check(Shortcuts.Problem("review", Shortcut.Parse("Alt+Q")!.Value, defaults) is null, "keeping the own shortcut is fine");
    settings = AppSettings.Load(settingsPath);
    Check(settings.Shortcuts["open"]?.ToString() == "Alt+E" && settings.Shortcuts["review"]?.ToString() == "Alt+Q"
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
    Check(clash["open"]?.ToString() == "Alt+E" && clash["translate"] is null && clash["factcheck"] is null, "a set with duplicates falls back to the defaults");
    File.WriteAllText(settingsPath, """{"shortcuts":{"open":null,"review":"Ctrl+V","translate":"nonsense","explain":"Ctrl+Alt+X"}}""");
    var mixed = AppSettings.Load(settingsPath);
    Check(mixed.Shortcuts["open"]?.ToString() == "Alt+E" && mixed.Shortcuts["review"]?.ToString() == "Alt+Q" && mixed.Shortcuts["translate"] is null && mixed.Shortcuts["explain"]?.ToString() == "Ctrl+Alt+X",
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
