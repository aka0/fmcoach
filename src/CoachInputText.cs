using System;
using System.Text;

internal static class CoachInputText
{
    internal static string Normalize(string value, int maxCharacters)
    {
        if (maxCharacters < 0) throw new ArgumentOutOfRangeException(nameof(maxCharacters));
        var result = new StringBuilder();
        foreach (var character in (value ?? "").Replace("\r\n", " ").Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' '))
        {
            if (character < 0x20 || character == 0x7f) continue;
            if (result.Length == maxCharacters) break;
            result.Append(character);
        }
        if (result.Length > 0 && char.IsHighSurrogate(result[result.Length - 1])) result.Length--;
        return result.ToString();
    }
}
