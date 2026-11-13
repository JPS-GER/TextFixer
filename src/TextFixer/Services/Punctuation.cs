namespace TextFixer.Services;

/// <summary>
/// Punctuation style per character group. "keep": answers add none, characters the user typed stay.
/// "plain": every such character becomes its plain form, including the user's own. "typographic": no rule.
/// The window applies the same groups in review-model.js (reviewStyleMaps).
/// </summary>
public static class Punctuation
{
    public const string Keep = "keep", Plain = "plain", Typographic = "typographic";

    public sealed record Rule(string Key, string Name, string Examples, string PlainForm);

    public static readonly IReadOnlyList<Rule> Rules =
    [
        new("quotes", "typographic quotation marks and apostrophes", "„ “ ” ‚ ‘ ’ « »", "straight quotation marks (\") and apostrophes (')"),
        new("dashes", "en dashes and em dashes", "– —", "the plain hyphen (-)"),
        new("ellipsis", "the ellipsis character", "…", "three periods (...)"),
        new("spaces", "non-breaking, narrow and other special spaces", "U+00A0, U+202F, U+2009", "normal spaces"),
        new("multiply", "the multiplication sign", "×", "the letter x"),
        new("arrows", "arrow characters", "→ ← ↔ ⇒", "-> <- <-> =>"),
        new("primes", "prime marks for feet, inches, minutes and seconds", "′ ″", "' and \""),
    ];

    public static Dictionary<string, string> Defaults() => Rules.ToDictionary(r => r.Key, _ => Keep);

    public static bool IsMode(string? value) => value is Keep or Plain or Typographic;

    /// <summary>Prompt instructions for the chosen style; empty when every group allows typographic characters.</summary>
    public static string PromptRules(IReadOnlyDictionary<string, string> style) => string.Concat(Rules.Select(r =>
        style.TryGetValue(r.Key, out var mode) ? mode switch
        {
            Keep => $"\n\nDo not introduce {r.Name} ({r.Examples}). Write {r.PlainForm} wherever you add or change text, and leave the ones already in the text as they are.",
            Plain => $"\n\nWrite {r.PlainForm} instead of {r.Name} ({r.Examples}), and also replace the ones already in the text.",
            _ => "",
        } : ""));
}
