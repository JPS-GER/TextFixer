namespace TextFixer.Services;

/// <summary>A global shortcut: RegisterHotKey modifiers (MOD_ALT 1, MOD_CONTROL 2, MOD_SHIFT 4) and a virtual-key code.</summary>
internal readonly record struct Shortcut(uint Modifiers, uint Key)
{
    internal const uint Alt = 1, Control = 2, Shift = 4;

    /// <summary>Display and settings form, for example "Ctrl+Alt+K".</summary>
    public override string ToString()
    {
        var parts = new List<string>(4);
        if ((Modifiers & Control) != 0) parts.Add("Ctrl");
        if ((Modifiers & Alt) != 0) parts.Add("Alt");
        if ((Modifiers & Shift) != 0) parts.Add("Shift");
        parts.Add(KeyName(Key));
        return string.Join('+', parts);
    }

    internal static string KeyName(uint key) => key switch
    {
        >= 0x41 and <= 0x5A or >= 0x30 and <= 0x39 => ((char)key).ToString(),
        >= 0x70 and <= 0x87 => "F" + (key - 0x6F),
        _ => "?",
    };

    /// <summary>Parses "Ctrl+Alt+K"; null for anything malformed. Validity rules are in <see cref="Shortcuts.Problem"/>.</summary>
    internal static Shortcut? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > 40) return null;
        var parts = text.Split('+', StringSplitOptions.TrimEntries);
        uint modifiers = 0;
        foreach (var part in parts[..^1])
        {
            var flag = part.ToUpperInvariant() switch { "CTRL" => Control, "ALT" => Alt, "SHIFT" => Shift, _ => 0u };
            if (flag == 0 || (modifiers & flag) != 0) return null;
            modifiers |= flag;
        }

        var key = parts[^1].ToUpperInvariant();
        if (key.Length == 1 && (char.IsAsciiLetterUpper(key[0]) || char.IsAsciiDigit(key[0]))) return new Shortcut(modifiers, key[0]);
        if (key.Length is 2 or 3 && key[0] == 'F' && int.TryParse(key[1..], out var number) && number is >= 1 and <= 24 && key[1] != '0')
            return new Shortcut(modifiers, (uint)(0x6F + number));
        return null;
    }
}

/// <summary>The three capture shortcuts: tasks, defaults and the rules a new combination must meet.</summary>
internal static class Shortcuts
{
    internal const string Review = "review", Translate = "translate", FactCheck = "factcheck";
    internal static readonly string[] Tasks = [Review, Translate, FactCheck];

    internal static Shortcut Default(string task) => task switch
    {
        Translate => new(Shortcut.Alt, 0x45),
        FactCheck => new(Shortcut.Alt, 0x57),
        _ => new(Shortcut.Alt, 0x51),
    };

    internal static Dictionary<string, Shortcut> Defaults() => Tasks.ToDictionary(t => t, Default);

    internal static string TaskName(string task) => task switch { Translate => "Translate", FactCheck => "Fact check", _ => "Review" };

    /// <summary>
    /// Why <paramref name="shortcut"/> cannot be used for <paramref name="task"/>, or null when it can. Layout and
    /// registration checks (AltGr characters, other apps) happen when the shortcut is registered.
    /// </summary>
    internal static string? Problem(string task, Shortcut shortcut, IReadOnlyDictionary<string, Shortcut> current)
    {
        if (Shortcut.KeyName(shortcut.Key) == "?") return "Use a letter, a digit or F1 to F24 as the key.";
        if ((shortcut.Modifiers & (Shortcut.Control | Shortcut.Alt)) == 0) return "Hold Ctrl or Alt, or both, with the key.";
        if ((shortcut.Modifiers & ~(Shortcut.Control | Shortcut.Alt | Shortcut.Shift)) != 0) return "Use only Ctrl, Alt and Shift as modifier keys.";
        if (shortcut.Modifiers == Shortcut.Control && shortcut.Key is 0x41 or 0x43 or 0x56 or 0x58 or 0x59 or 0x5A)
            return $"{shortcut} is needed for editing text in every app. Choose another combination.";
        if (shortcut.Modifiers == Shortcut.Alt && shortcut.Key == 0x73) return "Alt+F4 closes windows. Choose another combination.";
        foreach (var (other, used) in current)
            if (other != task && used == shortcut) return $"{shortcut} is already the shortcut for {TaskName(other)}.";
        return null;
    }
}
