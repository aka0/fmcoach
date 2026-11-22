using System;
using System.Linq;

internal static class CoachQualityProfile
{
    // Eccentricity, rushing out and punching are tendencies, not a higher-is-better scale.
    static readonly string[] GoalkeepingSkills = { "AerialReach", "CommandOfArea", "Communication", "Handling", "Kicking", "OneOnOnes", "Reflexes", "Throwing" };
    internal static double? GoalkeepingAverage(Func<string, double?> read)
    {
        var skills = GoalkeepingSkills.Select(read).Where(value => value.HasValue && value >= 1 && value <= 20).Select(value => value.Value).ToList();
        return skills.Count == 0 ? null : Math.Round(skills.Average(), 1);
    }
}
