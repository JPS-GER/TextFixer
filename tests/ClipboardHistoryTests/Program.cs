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
}
finally { Directory.Delete(folder, recursive: true); }

Console.WriteLine(failures == 0 ? "RESULT all passed" : $"RESULT {failures} failed");
return failures;
