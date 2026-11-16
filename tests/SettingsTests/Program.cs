using TextFixer.Services;

// Offline tests for settings, shortcuts and writing preferences. Uses a temporary folder; exit code = number of failures.

var failures = 0;
void Check(bool ok, string name)
{
    Console.WriteLine((ok ? "PASS " : "FAIL ") + name);
    if (!ok) failures++;
}

var folder = Path.Combine(Path.GetTempPath(), "TextFixerSettingsTests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(folder);
try
{
    var settingsPath = Path.Combine(folder, "settings.json");
    Check(AppSettings.Load(settingsPath).ReviewMode == AppSettings.SingleReview, "one request for all tabs by default");
    var settings = AppSettings.Load(settingsPath);
    settings.ReviewMode = AppSettings.FocusedReview;
    settings.Save();
    Check(AppSettings.Load(settingsPath).ReviewMode == AppSettings.FocusedReview, "setting round-trips");
    File.WriteAllText(settingsPath, "{ not json");
    Check(AppSettings.Load(settingsPath).ReviewMode == AppSettings.SingleReview, "damaged settings fall back to defaults");
    settings = AppSettings.Load(settingsPath);

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
