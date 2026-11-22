using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

internal static class CoachTeamStats
{
    internal static int CollectPerformance(Dictionary<string, string> fields, Dictionary<string, string> destination)
    {
        int count = 0;
        foreach (var label in fields.Where(pair => pair.Key.EndsWith(".StatNameLabel")))
        {
            var prefix = label.Key.Substring(0, label.Key.Length - "StatNameLabel".Length);
            if (string.IsNullOrWhiteSpace(label.Value) || !fields.TryGetValue(prefix + "SubjectStatValue", out var raw) ||
                !double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || double.IsNaN(value) || double.IsInfinity(value)) continue;
            destination["General performance: " + label.Value] = value.ToString("0.0", CultureInfo.InvariantCulture);
            count++;
        }
        var seasons = fields.Where(pair => pair.Key.EndsWith(".TimeFrame")).Select(pair => pair.Value).Where(v => !string.IsNullOrWhiteSpace(v)).Distinct().ToList();
        var competitions = fields.Where(pair => pair.Key.EndsWith(".Competition.Name")).Select(pair => pair.Value).Where(v => !string.IsNullOrWhiteSpace(v)).Distinct().ToList();
        if (seasons.Count == 1) destination["Statistics timeframe"] = seasons[0];
        if (competitions.Count == 1) destination["Statistics competition"] = competitions[0];
        return count;
    }

    internal static bool HasScopedMetrics(Dictionary<string, string> fields) =>
        fields.TryGetValue("Statistics timeframe", out var season) && !string.IsNullOrWhiteSpace(season) &&
        fields.TryGetValue("Statistics competition", out var competition) && !string.IsNullOrWhiteSpace(competition) &&
        fields.Keys.Any(key => key.StartsWith("General performance: "));
}
