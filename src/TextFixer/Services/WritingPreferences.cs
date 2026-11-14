namespace TextFixer.Services;

/// <summary>
/// Spelling of "E-Mail" (all tasks; "keep" adds no rule) and the form of address for translations into German
/// ("auto" lets the translation choose what fits the text), and the capitalization of du forms in Review ("keep"
/// keeps the capitalization the text uses). Review never switches between du and Sie.
/// </summary>
internal static class WritingPreferences
{
    internal const string Keep = "keep", Auto = "auto";
    internal static readonly string[] EmailModes = [Keep, "E-Mail", "E-mail", "Email"];
    internal static readonly string[] AddressModes = [Auto, "du", "Du", "Sie"];
    internal static readonly string[] ReviewDuModes = [Keep, "du", "Du"];

    internal static bool IsEmail(string? mode) => mode is not null && EmailModes.Contains(mode);
    internal static bool IsAddress(string? mode) => mode is not null && AddressModes.Contains(mode);
    internal static bool IsReviewDu(string? mode) => mode is not null && ReviewDuModes.Contains(mode);

    private const string Protect = " Never change URLs, email addresses, file names, code, names of products or companies, or quoted text.";

    /// <summary>The E-Mail spelling rule, for every task.</summary>
    internal static string EmailRule(string email) => email == Keep ? "" :
        $"\n\nSpell the word for electronic mail as \"{email}\", also in compounds (for example \"{email}-Adresse\" in German or \"{email} address\" in English), "
        + "and change other spellings such as Email, E-mail, E-Mail, eMail or e-mail to it." + Protect;

    /// <summary>Capitalization of du forms for review and fact-check corrections; a spelling choice, never a change of address.</summary>
    internal static string ReviewDuRule(string mode) => mode switch
    {
        "du" => "\n\nWhere German text addresses the reader informally, write the du forms in lowercase (du, dich, dir, dein, euch, euer); keep a capital letter at the start of a sentence."
            + DuLimits,
        "Du" => "\n\nWhere German text addresses the reader informally, capitalize the du forms (Du, Dich, Dir, Dein, Euch, Euer)."
            + DuLimits,
        _ => "\n\nKeep the capitalization of informal German address forms (du, dich, dir, dein, ihr, euch, euer) exactly as the text writes them: never change du to Du or Du to du."
            + " Never switch between informal and formal address.",
    };

    private const string DuLimits = " Report each change as a spelling correction. Never switch between informal and formal address, and never change the formal Sie, Ihnen or Ihr."
        + " Change ihr and euch only where they address several readers informally." + Protect;

    /// <summary>The form of address for translations into German.</summary>
    internal static string TranslationAddressRule(string address) => address switch
    {
        "du" => "\n\nWhen the translation is German and addresses the reader, use the informal du with lowercase forms (du, dich, dir, dein, ihr, euch, euer).",
        "Du" => "\n\nWhen the translation is German and addresses the reader, use the informal Du with capitalized forms (Du, Dich, Dir, Dein, Ihr, Euch, Euer).",
        "Sie" => "\n\nWhen the translation is German and addresses the reader, use the formal Sie (Sie, Ihnen, Ihr).",
        _ => "",
    };
}
