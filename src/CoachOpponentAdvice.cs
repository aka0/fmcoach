using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

internal static class CoachOpponentAdvice
{
    internal const string EvidencePolicy = "OPPOSITION ANALYSIS BASIS: partial scouting is normal. Build a useful provisional plan from available fixture facts, scoped statistics, observed tendencies and our known squad/tactics. Opponent exact roles, player instructions and match-day strategy are not prerequisites. Do not ask for them or require another screen before advising. Distinguish observed facts from conditional tactical hypotheses; do not invent hidden strategy or treat a usual formation as a confirmed starting shape.";

    internal static void AppendLimits(StringBuilder text, IEnumerable<string> missing)
    {
        var fields = (missing ?? Enumerable.Empty<string>()).Select(name => Regex.Replace(name, "(?<=[a-z])(?=[A-Z])", " ").ToLowerInvariant()).ToList();
        if (fields.Count > 0) text.AppendLine("  SCOUTING LIMITS (unknown, not blockers): " + string.Join(", ", fields) + ". Use the evidence available; do not turn this list into requests for exact opposition details.");
    }
}
