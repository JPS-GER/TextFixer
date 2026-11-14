using System.Text.Json;

namespace TextFixer.Services;

/// <summary>Small user preferences in %LOCALAPPDATA%\TextFixer\settings.json. Unknown or damaged files fall back to defaults.</summary>
internal sealed class AppSettings
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly string _path;

    public bool ClipboardHistory { get; set; } = true;

    /// <summary>"single": one request for all tabs. "focused": one request per tab, shown as each arrives.</summary>
    public string ReviewMode { get; set; } = SingleReview;
    internal const string SingleReview = "single", FocusedReview = "focused";

    /// <summary>ChatGPT model slug chosen in Settings; null uses the first model the account lists.</summary>
    public string? Model { get; set; }

    /// <summary>Model slug for Alt+E translations; null uses the review model.</summary>
    public string? TranslationModel { get; set; }

    /// <summary>Model slug for Alt+W fact checks; null uses the review model.</summary>
    public string? FactCheckModel { get; set; }

    /// <summary>Mode per <see cref="Punctuation.Rules"/> key: keep, plain or typographic.</summary>
    public Dictionary<string, string> PunctuationStyle { get; } = Punctuation.Defaults();

    /// <summary>Global shortcut per task (<see cref="Shortcuts.Tasks"/>), stored as text such as "Alt+Q".</summary>
    public Dictionary<string, Shortcut> Shortcuts { get; } = Services.Shortcuts.Defaults();

    private AppSettings(string path) => _path = path;

    internal static AppSettings Load(string? path = null)
    {
        path ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TextFixer", "settings.json");
        var settings = new AppSettings(path);
        try
        {
            if (!File.Exists(path)) return settings;
            using var document = JsonDocument.Parse(File.ReadAllBytes(path));
            if (document.RootElement.TryGetProperty("clipboardHistory", out var history) && history.ValueKind is JsonValueKind.True or JsonValueKind.False)
                settings.ClipboardHistory = history.GetBoolean();
            if (document.RootElement.TryGetProperty("reviewMode", out var mode) && mode.ValueKind == JsonValueKind.String && mode.GetString() is SingleReview or FocusedReview)
                settings.ReviewMode = mode.GetString()!;
            if (document.RootElement.TryGetProperty("model", out var model) && model.ValueKind == JsonValueKind.String && model.GetString() is { Length: > 0 and <= 200 } slug)
                settings.Model = slug;
            if (document.RootElement.TryGetProperty("translationModel", out var translationModel) && translationModel.ValueKind == JsonValueKind.String && translationModel.GetString() is { Length: > 0 and <= 200 } translationSlug)
                settings.TranslationModel = translationSlug;
            if (document.RootElement.TryGetProperty("factCheckModel", out var factCheckModel) && factCheckModel.ValueKind == JsonValueKind.String && factCheckModel.GetString() is { Length: > 0 and <= 200 } factCheckSlug)
                settings.FactCheckModel = factCheckSlug;
            if (document.RootElement.TryGetProperty("punctuationStyle", out var style) && style.ValueKind == JsonValueKind.Object)
                settings.SetPunctuationStyle(style);
            if (document.RootElement.TryGetProperty("shortcuts", out var shortcuts) && shortcuts.ValueKind == JsonValueKind.Object)
                settings.SetShortcuts(shortcuts);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
        return settings;
    }

    /// <summary>Takes the known keys with a valid mode; anything else is ignored.</summary>
    internal void SetPunctuationStyle(JsonElement style)
    {
        foreach (var rule in Punctuation.Rules)
            if (style.TryGetProperty(rule.Key, out var mode) && mode.ValueKind == JsonValueKind.String && Punctuation.IsMode(mode.GetString()))
                PunctuationStyle[rule.Key] = mode.GetString()!;
    }

    /// <summary>
    /// Takes the saved set as a whole, so swapped shortcuts load. An unreadable or invalid entry keeps its default;
    /// if the result has duplicates, every task falls back to its default.
    /// </summary>
    internal void SetShortcuts(JsonElement shortcuts)
    {
        var candidate = Services.Shortcuts.Defaults();
        foreach (var task in Services.Shortcuts.Tasks)
            if (shortcuts.TryGetProperty(task, out var value) && value.ValueKind == JsonValueKind.String && Shortcut.Parse(value.GetString()) is { } shortcut)
                candidate[task] = shortcut;
        foreach (var task in Services.Shortcuts.Tasks)
            if (Services.Shortcuts.Problem(task, candidate[task], new Dictionary<string, Shortcut>()) is not null) candidate[task] = Services.Shortcuts.Default(task);
        if (candidate.Values.Distinct().Count() != candidate.Count) return;
        foreach (var (task, shortcut) in candidate) Shortcuts[task] = shortcut;
    }

    internal void Save()
    {
        var temp = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllBytes(temp, JsonSerializer.SerializeToUtf8Bytes(new { clipboardHistory = ClipboardHistory, reviewMode = ReviewMode, punctuationStyle = PunctuationStyle, model = Model, translationModel = TranslationModel, factCheckModel = FactCheckModel, shortcuts = Shortcuts.ToDictionary(s => s.Key, s => s.Value.ToString()) }, Options));
        File.Move(temp, _path, overwrite: true);
    }
}
