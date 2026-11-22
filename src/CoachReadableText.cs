using System;
using System.Linq;
using System.Text.RegularExpressions;

internal static class CoachReadableText
{
    internal static string GameValue(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (value.StartsWith("{") || Regex.IsMatch(value, @"\b\w+\s*\{.*")) return null;
        if (value.Contains("System.Collections") || value.Contains("SI.Core.TypedValue") ||
            Regex.IsMatch(value, @"\bFM\.UI\.\w*Reference\b")) return null;
        if (Regex.IsMatch(value, @"(?:AgAA|AwAA|BAAA|AAAA)[A-Za-z0-9+/]{16,}|[A-Za-z0-9+/]{48,}={0,2}")) return null;
        return value;
    }

    internal static string ExplicitStars(string value)
    {
        // Native MinValue/MaxValue encoding is unverified; only an actual display string is trustworthy.
        if (string.IsNullOrWhiteSpace(value) || (!value.Contains('★') && !Regex.IsMatch(value, @"\bstars?\b", RegexOptions.IgnoreCase))) return null;
        if (Regex.IsMatch(value, @"(^|[\s(])-\d")) return null;
        var numbers = Regex.Matches(value, @"\d+(?:\.\d+)?").Cast<Match>().Select(m => double.Parse(m.Value, System.Globalization.CultureInfo.InvariantCulture)).ToList();
        if (numbers.Count == 0 || numbers.Any(n => n < 0 || n > 5)) return null;
        return value;
    }
}
