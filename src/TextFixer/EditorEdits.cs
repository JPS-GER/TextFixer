using System.Text;
using System.Text.Json;

namespace TextFixer;

internal sealed record EditorEdit(int Start, int End, string Before, string After);

internal static class EditorEdits
{
    internal static EditorEdit[] Validate(string original, string revised, JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("Invalid correction batch. Check the text again.");
        if (value.GetArrayLength() > 200)
            throw new InvalidOperationException("Apply at most 200 corrections at a time.");
        var edits = new List<EditorEdit>();
        var result = new StringBuilder();
        var position = 0;
        var previousStart = -1;
        foreach (var item in value.EnumerateArray())
        {
            if (!item.TryGetProperty("start", out var startValue) || !startValue.TryGetInt32(out var start)
                || !item.TryGetProperty("end", out var endValue) || !endValue.TryGetInt32(out var end)
                || !item.TryGetProperty("before", out var beforeValue) || beforeValue.ValueKind != JsonValueKind.String
                || !item.TryGetProperty("after", out var afterValue) || afterValue.ValueKind != JsonValueKind.String)
                throw new InvalidOperationException("Invalid correction range.");
            var before = beforeValue.GetString()!;
            var after = afterValue.GetString()!;
            if (start <= previousStart || start < position || end < start || end > original.Length
                || original[start..end] != before || before.IndexOfAny(['\r', '\n']) >= 0 || after.IndexOfAny(['\r', '\n']) >= 0
                || SplitsCharacter(original, start) || SplitsCharacter(original, end))
                throw new InvalidOperationException("A correction overlaps, changes line breaks, or no longer matches the original.");
            result.Append(original, position, start - position).Append(after);
            position = end;
            previousStart = start;
            edits.Add(new EditorEdit(start, end, before, after));
        }
        result.Append(original, position, original.Length - position);
        if (result.ToString() != revised) throw new InvalidOperationException("The selected corrections do not match the revised text.");
        return edits.ToArray();
    }

    private static bool SplitsCharacter(string text, int offset) =>
        offset > 0 && offset < text.Length && char.IsSurrogatePair(text, offset - 1);
}
