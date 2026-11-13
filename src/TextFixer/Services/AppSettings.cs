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

    /// <summary>Keep straight quotation marks and apostrophes instead of typographic ones.</summary>
    public bool StraightQuotes { get; set; }

    /// <summary>Keep the plain hyphen instead of en or em dashes.</summary>
    public bool PlainDashes { get; set; }

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
            if (document.RootElement.TryGetProperty("straightQuotes", out var quotes) && quotes.ValueKind is JsonValueKind.True or JsonValueKind.False)
                settings.StraightQuotes = quotes.GetBoolean();
            if (document.RootElement.TryGetProperty("plainDashes", out var dashes) && dashes.ValueKind is JsonValueKind.True or JsonValueKind.False)
                settings.PlainDashes = dashes.GetBoolean();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
        return settings;
    }

    internal void Save()
    {
        var temp = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllBytes(temp, JsonSerializer.SerializeToUtf8Bytes(new { clipboardHistory = ClipboardHistory, reviewMode = ReviewMode, straightQuotes = StraightQuotes, plainDashes = PlainDashes }, Options));
        File.Move(temp, _path, overwrite: true);
    }
}
