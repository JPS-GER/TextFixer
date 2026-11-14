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
    Check(Shortcuts.Problem("review", Shortcut.Parse("Alt+E")!.Value, defaults) == "Alt+E is already the shortcut for Translate.", "duplicates name the other task");
    Check(Shortcuts.Problem("translate", Shortcut.Parse("Alt+E")!.Value, defaults) is null, "keeping the own shortcut is fine");
    settings = AppSettings.Load(settingsPath);
    Check(settings.Shortcuts["review"].ToString() == "Alt+Q" && settings.Shortcuts["factcheck"].ToString() == "Alt+W", "default shortcuts");
    settings.Shortcuts["translate"] = Shortcut.Parse("Ctrl+Alt+T")!.Value;
    settings.Save();
    Check(AppSettings.Load(settingsPath).Shortcuts["translate"].ToString() == "Ctrl+Alt+T", "shortcuts round-trip as text");
    File.WriteAllText(settingsPath, """{"shortcuts":{"review":"Alt+E","translate":"Alt+Q"}}""");
    var swapped = AppSettings.Load(settingsPath).Shortcuts;
    Check(swapped["review"].ToString() == "Alt+E" && swapped["translate"].ToString() == "Alt+Q" && swapped["factcheck"].ToString() == "Alt+W", "swapped shortcuts load");
    File.WriteAllText(settingsPath, """{"shortcuts":{"review":"Ctrl+V","translate":"nonsense","factcheck":"Ctrl+Alt+F"}}""");
    var mixed = AppSettings.Load(settingsPath).Shortcuts;
    Check(mixed["review"].ToString() == "Alt+Q" && mixed["translate"].ToString() == "Alt+E" && mixed["factcheck"].ToString() == "Ctrl+Alt+F", "invalid entries keep their defaults");
    File.WriteAllText(settingsPath, """{"shortcuts":{"review":"Alt+W","translate":"Alt+W"}}""");
    var clash = AppSettings.Load(settingsPath).Shortcuts;
    Check(clash["review"].ToString() == "Alt+Q" && clash["translate"].ToString() == "Alt+E" && clash["factcheck"].ToString() == "Alt+W", "duplicate saved shortcuts fall back to defaults");
}
finally { Directory.Delete(folder, recursive: true); }

Console.WriteLine(failures == 0 ? "RESULT all passed" : $"RESULT {failures} failed");
return failures;
