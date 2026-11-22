using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

// A screen read replaces the previously selected tactic, including fields that disappeared.
internal static class CoachTacticSnapshot
{
    internal static bool HasFreshDetails(bool read, string source) => read && !string.IsNullOrWhiteSpace(source) &&
        source.IndexOf("cached", System.StringComparison.OrdinalIgnoreCase) < 0;
    internal static bool CanUseDetails(bool complete, string source) => HasFreshDetails(complete, source);

    internal static string FreshnessLabel(bool read, string source) =>
        source?.IndexOf("cached", System.StringComparison.OrdinalIgnoreCase) >= 0 ? "cached" :
        HasFreshDetails(read, source) ? "current session" : "unread";

    // Current FM27 display semantics verified against the instruction cards.
    internal static string PlayerInstructionLabel(string group, string nativeName)
    {
        if (nativeName == null) return null;
        if (nativeName == "PI_NONE") return group switch
        {
            "FreedomOfMovement" => "Follow Tactical Position",
            "HoldUpBall" => "No",
            _ => "Standard",
        };
        if (group == "ForwardRuns" && nativeName == "PI_HOLD_POSITION") return "Make Fewer Runs";
        return nativeName switch
        {
            "PI_SHOOT_LESS" => "Shoot Less Often", "PI_TAKE_MORE_RISKS" => "Take More Risks",
            "PI_PASS_MORE_DIRECT" => "More Direct", "PI_GET_STUCK_IN" => "Tackle Harder",
            "PI_LOWER_PRESSING_INTENSITY" => "Less Often",
            _ => nativeName.Replace("PI_", "").Replace('_', ' ').ToLowerInvariant(),
        };
    }

    internal static string PlayerInstructionContext(Dictionary<string, string> row)
    {
        var name = row.GetValueOrDefault("Player") ?? row.GetValueOrDefault("Person.Name") ?? row.GetValueOrDefault("Player.Name") ?? "Position";
        var settings = new List<string>();
        foreach (var pair in row)
        {
            var match = Regex.Match(pair.Key, @"^Instructions\[(IP|OOP)\.([^\]]+)\](\.target)?$");
            if (!match.Success) continue;
            var group = Regex.Replace(match.Groups[2].Value, "(?<=[a-z])(?=[A-Z])", " ").ToLowerInvariant();
            settings.Add((match.Groups[1].Value == "IP" ? "in possession " : "out of possession ") + group +
                (match.Groups[3].Success ? " target" : "") + " = " + pair.Value);
        }
        return settings.Count == 0 ? null : name + ": " + string.Join("; ", settings);
    }

    internal static bool IsSlotSelectorName(string name) => !string.IsNullOrWhiteSpace(name) &&
        (name == "Index" || Regex.IsMatch(name, "Tactic", RegexOptions.IgnoreCase) && Regex.IsMatch(name, "Slot|Index", RegexOptions.IgnoreCase) &&
        !Regex.IsMatch(name, "Player|Position|Role|Instruction|Formation|Opponent|Opposition|Match", RegexOptions.IgnoreCase));

    internal static bool MatchesSlotProbe(string expectedTeam, int expectedSlot, string expectedName,
        string actualTeam, string actualSlot, string actualName) =>
        !string.IsNullOrWhiteSpace(expectedTeam) && expectedTeam == actualTeam &&
        int.TryParse(actualSlot, out var slot) && slot == expectedSlot - 1 &&
        !string.IsNullOrWhiteSpace(expectedName) && expectedName == actualName;

    internal static bool SameTacticRead(string startSlot, string startName, string endSlot, string endName) =>
        int.TryParse(startSlot, out var start) && int.TryParse(endSlot, out var end) && start == end &&
        (string.IsNullOrWhiteSpace(startName) || string.IsNullOrWhiteSpace(endName) || startName == endName);

    internal static int PositionListPriority(string name, List<Dictionary<string, string>> rows) =>
        rows.Count(row => row.ContainsKey("OOPRole") || row.GetValueOrDefault("IsInPossession") == "False") * 100 +
        (name.Contains("Combined") ? 10 : 0) + (name.Contains("(screen)") ? 1 : 0);

    // This exact primary/secondary pair was verified as Balanced in both live slots.
    // The secondary native value is not an independently active overlap instruction.
    internal static string SupportingRunsLabel(string primary, string secondary) =>
        primary == "1128871265" && secondary == "1484157235" ? "Balanced" : null;

    internal static bool IsOwnInstructionList(string name) =>
        Regex.IsMatch(name ?? "", "Player|Position|Selection", RegexOptions.IgnoreCase) &&
        !Regex.IsMatch(name ?? "", "Opposition|Opponent|Suggestion|Advice", RegexOptions.IgnoreCase);

    internal static bool IsPlayerInstructionData(string name) =>
        Regex.IsMatch(name ?? "", "Instruction|Tweak|Shout", RegexOptions.IgnoreCase) &&
        !Regex.IsMatch(name ?? "", "Coverage|Options|Dropdown|Selected|Advice|Suggestion|Revert|Group|Count|Uses|Locked|Tweaked", RegexOptions.IgnoreCase);

    internal static bool InventoryComplete(string[] names, bool?[] valid, int? count)
    {
        if (names.Length != 3 || valid.Length != 3 || valid.Any(v => !v.HasValue)) return false;
        int saved = valid.Count(v => v == true);
        return saved > 0 && (!count.HasValue || count == saved) &&
            Enumerable.Range(0, 3).All(i => valid[i] != true || !string.IsNullOrWhiteSpace(names[i]));
    }

    internal static bool PlayerInstructionRowsComplete(Dictionary<string, List<Dictionary<string, string>>> lists) =>
        lists.Where(pair => Regex.IsMatch(pair.Key, "TacticalPositions|TeamSelections|FirstTeamPlayerSlots", RegexOptions.IgnoreCase))
            .Any(pair => pair.Value.Count == 11 && pair.Value.All(row =>
                row.TryGetValue("InstructionCoverage", out var status) && status == "complete"));

    internal static List<string> RequiredDetails(string formation, string mentality,
        List<Dictionary<string, string>> positions)
    {
        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(formation)) missing.Add("formation");
        if (string.IsNullOrWhiteSpace(mentality)) missing.Add("mentality");
        if (positions.Count != 11) missing.Add("roles/positions: " + positions.Count + "/11");
        else foreach (var row in positions)
        {
            var position = row.GetValueOrDefault("position");
            if (string.IsNullOrWhiteSpace(position)) missing.Add("position identity");
            if (string.IsNullOrWhiteSpace(row.GetValueOrDefault("role"))) missing.Add((position ?? "position") + " in-possession role");
            if (string.IsNullOrWhiteSpace(row.GetValueOrDefault("roleOutOfPossession"))) missing.Add((position ?? "position") + " out-of-possession role");
        }
        return missing;
    }

    internal static void Replace(Dictionary<string, object> snapshot,
        SortedDictionary<string, string> values,
        Dictionary<string, List<Dictionary<string, string>>> lists,
        Dictionary<string, Dictionary<string, string>> phases, object source, string name,
        IEnumerable<string> expectedInstructionKeys = null, bool playerInstructionsRead = false)
    {
        snapshot.Clear();
        snapshot["allValues"] = values;
        snapshot["lists"] = lists;
        snapshot["teamInstructionsByPhase"] = phases;
        snapshot["object"] = source;
        snapshot["expectedInstructionKeys"] = expectedInstructionKeys?.Distinct().ToList() ?? new List<string>();
        snapshot["playerInstructionsRead"] = playerInstructionsRead;
        if (name != null) snapshot["name"] = name;
    }
}
