// FM Coach v0.7.1 - FM27 tactic enums (from Cpp2IL enum dump of FM.UI: TacticTeamInstructionOptions, In/OutOfPossessionRole).
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

public partial class CoachBehaviour
{
    static readonly Dictionary<uint, string> TeamInstructionOptionNames = new()
    {
        [1985571685u] = "PassingStyleMuchShorter",
        [1146434657u] = "PassingStyleShorter",
        [1483944022u] = "PassingDirectnessNone",
        [2019653698u] = "PassingStyleMoreDirect",
        [1867543624u] = "PassingStyleMuchMoreDirect",
        [1498756423u] = "MuchLowerTempo",
        [1311996258u] = "LowerTempo",
        [1496610426u] = "TempoNone",
        [1345339954u] = "HigherTempo",
        [543778113u] = "MuchHigherTempo",
        [1315850041u] = "WasteTimeLessOften",
        [1329030726u] = "WasteTimeStandard",
        [1733973351u] = "WasteTimeMoreOften",
        [1734436950u] = "AttackTransitionCounter",
        [846284410u] = "AttackingTransitionNone",
        [1450726497u] = "AttackTransitionHoldShape",
        [1716541542u] = "SetupMuchNarrowerInPossession",
        [1111652208u] = "SetupNarrowerInPossession",
        [1951095402u] = "TeamWidthNone",
        [1244744824u] = "SetupWiderInPossession",
        [1347573861u] = "SetupMuchWiderInPossession",
        [896626297u] = "PlayForSetPieces",
        [1869829494u] = "PlayForSetPiecesNone",
        [1968654134u] = "GoalkeeperUpForAttackingSetPieces",
        [1466265977u] = "GoalkeeperUpForAttackingSetPiecesNone",
        [1297429358u] = "BeMoreExpressive",
        [1934574368u] = "CreativeFreedomNone",
        [1752052821u] = "BeMoreDisciplined",
        [1365857100u] = "PlayThroughPress",
        [1732331848u] = "BuildUpStrategyNone",
        [1702379875u] = "BypassPress",
        [1730167140u] = "GoalKicksShort",
        [1248478788u] = "GoalKicksNone",
        [1749839698u] = "GoalKicksLong",
        [1936095309u] = "GKDistributionPaceSlow",
        [842232368u] = "GKDistributionPaceNone",
        [1380137303u] = "GKDistributionPaceQuick",
        [1398290509u] = "GKDistributionTargetNone",
        [1097811032u] = "GKDistributionTargetCentrebacks",
        [1732531317u] = "GKDistributionTargetFullbacks",
        [2001361485u] = "GKDistributionTargetFlanks",
        [543316536u] = "GKDistributionTargetPlaymaker",
        [1362654530u] = "GKDistributionTargetTargetman",
        [1664119088u] = "GKDistributionTargetClearOppositionDefence",
        [1246451814u] = "LookForUnderlapLeft",
        [894725200u] = "LookForUnderlapRight",
        [1848850767u] = "LookForUnderlapBoth",
        [1128871265u] = "LookForUnderlapNone",
        [809924212u] = "LookForOverlapBoth",
        [1496789296u] = "LookForOverlapLeft",
        [1484157235u] = "LookForOverlapRight",
        [1700947790u] = "LookForOverlapNone",
        [1313559094u] = "RunAtDefence",
        [1480870768u] = "DribblingNone",
        [946684999u] = "DribbleLess",
        [2001548153u] = "FocusPlayDownTheLeftFlank",
        [1299723626u] = "FocusPlayDownTheRightFlank",
        [1465200750u] = "FocusPlayDownTheMiddle",
        [1849981002u] = "FocusPlayDownBothFlanks",
        [1800754547u] = "ProgressThroughNone",
        [1987666283u] = "PassingReceptionIntoSpace",
        [1196126273u] = "PassReceptionNone",
        [1817268034u] = "PassingReceptionIntoFeet",
        [1196903216u] = "WorkBallIntoBox",
        [1364668513u] = "PatienceNone",
        [844190326u] = "HitEarlyCrosses",
        [1799907952u] = "ShotsFromDistanceShootOnSight",
        [1951290700u] = "ShotsFromDistanceNone",
        [2021157752u] = "ShotsFromDistanceStayPatient",
        [1817334343u] = "CrossingNone",
        [542589235u] = "LowCrosses",
        [1649964405u] = "FloatCrosses",
        [1850439286u] = "WhippedCrosses",
        [2033537859u] = "LineOfEngagementHigh",
        [1196979288u] = "LineOfEngagementMid",
        [927550517u] = "LineOfEngagementLow",
        [1735747406u] = "DefensiveLineMuchHigher",
        [1329018200u] = "DefensiveLineHigherDefensiveLine",
        [894978671u] = "DefensiveLineStandard",
        [807423041u] = "DefensiveLineDeeper",
        [2003456321u] = "DefensiveLineMuchDeeper",
        [825575765u] = "HoldLine",
        [1397124971u] = "DefensiveLineSecondaryStandard",
        [1466132292u] = "DropOff",
        [1500664391u] = "PressingIntensityMuchLower",
        [1985168495u] = "PressingIntensityLower",
        [1313689463u] = "PressingIntensityNone",
        [2000905329u] = "PressingIntensityHigher",
        [1450268022u] = "PressingIntensityMuchHigher",
        [928473934u] = "DefenceTransitionCounterpress",
        [1396068403u] = "DefensiveTransitionNone",
        [2001753209u] = "DefenceTransitionRegroup",
        [1498428227u] = "GetStuckIn",
        [1297249398u] = "TacklingNone",
        [896480825u] = "StayOnFeet",
        [1748587383u] = "PreventShortGKDistribution",
        [1667524405u] = "PreventShortGKDistributionNone",
        [1870092139u] = "PressingTrapIn",
        [844782149u] = "PressingTrapNone",
        [1381658469u] = "PressingTrapOut",
        [1684615797u] = "CrossStop",
        [1097682775u] = "CrossEngagementNone",
        [1280010857u] = "CrossInvite",
    };
    static readonly (string name, ulong bit, string abbr, bool duty)[] IpRoles =
    {
        ("Goalkeeper", 1ul, "GK", false),
        ("Ball Playing Goalkeeper", 4096ul, "BGK", false),
        ("No Nonsense Goalkeeper", 9007199254740992ul, "NGK", false),
        ("Full Back", 4ul, "FB", false),
        ("Wing Back", 8ul, "WB", false),
        ("No Nonsense Full Back", 68719476736ul, "NFB", false),
        ("Advanced Wing Back", 274877906944ul, "AWB", false),
        ("Inside Wing Back", 17592186044416ul, "IWB", false),
        ("Inside Full Back", 4503599627370496ul, "IFB", false),
        ("Playmaking Wing Back", 36028797018963968ul, "PWB", false),
        ("Centre Back", 2ul, "CB", false),
        ("Advanced Centre Back", 16384ul, "ACB", false),
        ("Ball Playing Centre Back", 16777216ul, "BCB", false),
        ("No Nonsense Centre Back", 536870912ul, "NCB", false),
        ("Wide Centre Back", 2251799813685248ul, "WCB", false),
        ("Overlapping Centre Back", 18014398509481984ul, "OCB", false),
        ("Midfield Playmaker", 144115188075855872ul, "MPM", false),
        ("Defensive Midfielder", 16ul, "DM", false),
        ("Central Midfielder", 32ul, "CM", false),
        ("Deep Lying Playmaker", 32768ul, "DLP", false),
        ("Box To Box Midfielder", 65536ul, "BBM", false),
        ("Ball Winning Midfielder", 268435456ul, "BWM", false),
        ("Anchor", 8589934592ul, "A", false),
        ("Half Back", 34359738368ul, "HB", false),
        ("Enganche", 137438953472ul, "EG", false),
        ("Regista", 549755813888ul, "RGA", false),
        ("Box To Box Playmaker", 70368744177664ul, "BBP", false),
        ("Mezzala", 140737488355328ul, "MEZ", false),
        ("Segundo Volante", 1125899906842624ul, "SV", false),
        ("Wide Midfielder", 64ul, "WM", false),
        ("Winger", 128ul, "W", false),
        ("Attacking Midfielder", 512ul, "AM", false),
        ("Channel Midfielder", 72057594037927936ul, "CHM", false),
        ("Advanced Playmaker", 131072ul, "AP", false),
        ("Inside Forward", 134217728ul, "IF", false),
        ("Inside Winger", 562949953421312ul, "IW", false),
        ("Defensive Winger", 1073741824ul, "DW", false),
        ("Free Role", 4294967296ul, "FR", false),
        ("Wide Target Forward", 4398046511104ul, "WTF", false),
        ("Playmaking Winger", 8796093022208ul, "PW", false),
        ("Wide Forward", 35184372088832ul, "WFD", false),
        ("Wide Central Midfielder", 281474976710656ul, "WCM", false),
        ("Deep Lying Forward", 1024ul, "DLF", false),
        ("Centre Forward", 2048ul, "CFD", false),
        ("Target Forward", 262144ul, "TF", false),
        ("Poacher", 524288ul, "P", false),
        ("Complete Forward", 1048576ul, "CF", false),
        ("Channel Forward", 2147483648ul, "CHF", false),
        ("False Nine", 1099511627776ul, "F9", false),
        ("Second Striker", 2199023255552ul, "SS", false),
        ("Defend Duty", 2097152ul, "", true),
        ("Defend Stopper Duty", 33554432ul, "", true),
        ("Defend Covering Duty", 67108864ul, "", true),
        ("Support Duty", 4194304ul, "", true),
        ("Attack Duty", 8388608ul, "", true),
        ("Float Duty", 17179869184ul, "", true),
    };
    static readonly (string name, ulong bit, string abbr, bool duty)[] OopRoles =
    {
        ("Defend Duty", 2097152ul, "", true),
        ("Support Duty", 4194304ul, "", true),
        ("Attack Duty", 8388608ul, "", true),
        ("Defend Stopper Duty", 33554432ul, "", true),
        ("Defend Covering Duty", 67108864ul, "", true),
        ("Float Duty", 17179869184ul, "", true),
        ("Goalkeeper", 1ul, "GK", false),
        ("Sweeper Keeper", 2ul, "SK", false),
        ("Line Keeper", 4ul, "LK", false),
        ("Centre Back", 8ul, "CB", false),
        ("Stopping Centre Back", 16ul, "SCB", false),
        ("Covering Centre Back", 32ul, "CCB", false),
        ("Wide Centre Back", 64ul, "WCB", false),
        ("Stopping Wide Centre Back", 128ul, "SWCB", false),
        ("Covering Wide Centre Back", 256ul, "CWCB", false),
        ("Full Back", 512ul, "FB", false),
        ("Pressing Full Back", 1024ul, "PFB", false),
        ("Holding Full Back", 2048ul, "HFB", false),
        ("Wing Back", 4096ul, "WB", false),
        ("Pressing Wing Back", 8192ul, "PWB", false),
        ("Holding Wing Back", 16384ul, "HWB", false),
        ("Defensive Midfielder", 32768ul, "DM", false),
        ("Dropping Defensive Midfielder", 65536ul, "DDM", false),
        ("Pressing Defensive Midfielder", 131072ul, "PDM", false),
        ("Screening Defensive Midfielder", 262144ul, "SDM", false),
        ("Wide Cover Defensive Midfielder", 524288ul, "WDM", false),
        ("Central Midfielder", 1048576ul, "CM", false),
        ("Pressing Central Midfielder", 134217728ul, "PCM", false),
        ("Screening Central Midfielder", 268435456ul, "SCM", false),
        ("Wide Cover Central Midfielder", 536870912ul, "WCM", false),
        ("Attacking Midfielder", 1073741824ul, "AM", false),
        ("Tracking Attacking Midfielder", 2147483648ul, "TAM", false),
        ("Central Outlet Attacking Midfielder", 4294967296ul, "COAM", false),
        ("Splitting Outlet Attacking Midfielder", 8589934592ul, "SOAM", false),
        ("Wide Midfielder", 34359738368ul, "WM", false),
        ("Tracking Wide Midfielder", 68719476736ul, "TWM", false),
        ("Wide Outlet Wide Midfielder", 137438953472ul, "WOWM", false),
        ("Winger", 274877906944ul, "W", false),
        ("Tracking Winger", 549755813888ul, "TW", false),
        ("Inside Outlet Winger", 1099511627776ul, "IOW", false),
        ("Wide Outlet Winger", 2199023255552ul, "WOW", false),
        ("Centre Forward", 4398046511104ul, "CFD", false),
        ("Tracking Centre Forward", 8796093022208ul, "TCF", false),
        ("Central Outlet Centre Forward", 17592186044416ul, "COCF", false),
        ("Splitting Outlet Centre Forward", 35184372088832ul, "SOCF", false),
    };

    // Role display name -> FM abbreviation (both possession phases), e.g. "Ball-Playing Goalkeeper" -> BGK.
    static string RoleKey(string s) => Regex.Replace(Regex.Replace(s ?? "", @"\(.*?\)", ""), "[^A-Za-z0-9]", "").ToLowerInvariant();
    static readonly Dictionary<string, string> RoleAbbrByKey = IpRoles.Where(r => !r.duty && r.abbr != "").GroupBy(r => RoleKey(r.name)).ToDictionary(g => g.Key, g => g.First().abbr);
    static readonly Dictionary<string, string> OopAbbrByKey = OopRoles.Where(r => !r.duty && r.abbr != "").GroupBy(r => RoleKey(r.name)).ToDictionary(g => g.Key, g => g.First().abbr);
    internal static string RoleAbbr(string roleName, bool oop = false)
    {
        var k = RoleKey(roleName); if (k == "") return null;
        if (!oop && RoleAbbrByKey.TryGetValue(k, out var a)) return a;
        if (OopAbbrByKey.TryGetValue(k, out var b)) return b;
        if (oop && RoleAbbrByKey.TryGetValue(k, out var c)) return c;
        return null;
    }

    // Instruction option id (TacticTeamInstructionOptions) -> readable label, e.g. 1196979288 -> "Line Of Engagement Mid".
    internal static string InstructionLabel(string key, string v)
    {
        // These legacy enum labels contradict FM27's Supporting Runs control.
        // Keep raw IDs in diagnostics until the current display mapping is verified.
        if (key is "supportingRuns" or "supportingRunsSecondary") return null;
        if (string.IsNullOrWhiteSpace(v)) return v;
        if (uint.TryParse(v.Trim(), out var id))
        {
            if (id == 0) return "Default (0)";
            if (TeamInstructionOptionNames.TryGetValue(id, out var n))
                return n.EndsWith("None") ? Hum(n.Substring(0, n.Length - 4)) + ": Default" : Hum(n);
            if (key == "mentality" && id <= 7) return new[] { "None", "Very Defensive", "Defensive", "Cautious", "Balanced", "Positive", "Attacking", "Very Attacking" }[id];
        }
        return v;
    }
    static string Hum(string n) => Regex.Replace(n, "(?<=[a-z0-9])(?=[A-Z])", " ");

    // Combined role key (u64: role bit | duty bit), e.g. from TacticsPositionReference.PositionRole.
    internal static string DecodeRoleKey(string v, bool oop)
    {
        if (!ulong.TryParse((v ?? "").Trim(), out var x) || x == 0) return null;
        var table = oop ? OopRoles : IpRoles;
        var role = table.Where(r => !r.duty && r.bit != 0 && (x & r.bit) == r.bit).OrderByDescending(r => r.bit).FirstOrDefault();
        var duty = table.Where(r => r.duty && (x & r.bit) == r.bit).OrderByDescending(r => r.bit).FirstOrDefault();
        if (role.name == null) return null;
        return role.name + (role.abbr != "" ? " [" + role.abbr + "]" : "") + (duty.name != null ? " (" + duty.name.Replace(" Duty", "") + ")" : "");
    }

    // TacticsPositionReference.Position bit flags (low 16 bits; the high bits mark left/right-of-centre duplicates).
    // Confirmed against the user's Tactics screen: 1 GK, 4 D (R), 8 D (L), 16 D (C), 128 DM, 1024 M (C), 2048 AM (R), 4096 AM (L), 16384 ST (C).
    // Interpolated from FM's usual order (unconfirmed): 2 SW, 32 WB (R), 64 WB (L), 256 M (R), 512 M (L), 8192 AM (C).
    static readonly (ulong bit, string name)[] PositionBits =
    {
        (1, "GK"), (2, "D (C)"), (4, "D (R)"), (8, "D (L)"), (16, "D (C)"), (32, "WB (R)"), (64, "WB (L)"), (128, "DM"),
        (256, "M (R)"), (512, "M (L)"), (1024, "M (C)"), (2048, "AM (R)"), (4096, "AM (L)"), (8192, "AM (C)"), (16384, "ST (C)"),
    };
    internal static string PositionFromFlags(string v)
    {
        if (!ulong.TryParse((v ?? "").Trim(), out var x) || x == 0) return null;
        var low = x & 0xFFFF;
        foreach (var (bit, name) in PositionBits) if (low == bit) return name;
        return null;
    }

    // Normalise FM position strings to the Tactics-screen short form, e.g. "DR"/"Right Back" -> "D (R)", "STC" -> "ST (C)".
    internal static string PositionShort(string p)
    {
        if (string.IsNullOrWhiteSpace(p)) return null;
        var s = p.Trim();
        if (Regex.IsMatch(s, @"^(GK|D|WB|DM|M|AM|ST)( \([RLC]+\))?$")) return s;
        var k = Regex.Replace(s, "[^A-Za-z]", "").ToUpperInvariant();
        string side(string t) => t.EndsWith("R") ? "R" : t.EndsWith("L") ? "L" : "C";
        switch (k)
        {
            case "GK": case "GOALKEEPER": return "GK";
            case "DR": case "RB": case "RIGHTBACK": case "RIGHTFULLBACK": return "D (R)";
            case "DL": case "LB": case "LEFTBACK": case "LEFTFULLBACK": return "D (L)";
            case "DC": case "DCR": case "DCL": case "CB": case "CENTREBACK": case "CENTERBACK": case "CENTRALDEFENDER": return "D (C)";
            case "WBR": case "RWB": case "RIGHTWINGBACK": return "WB (R)";
            case "WBL": case "LWB": case "LEFTWINGBACK": return "WB (L)";
            case "DM": case "DMC": case "DMCR": case "DMCL": case "DEFENSIVEMIDFIELDER": return "DM";
            case "MR": case "RM": case "RIGHTMIDFIELDER": return "M (R)";
            case "ML": case "LM": case "LEFTMIDFIELDER": return "M (L)";
            case "MC": case "MCR": case "MCL": case "CM": case "CENTRALMIDFIELDER": return "M (C)";
            case "AMR": case "RW": case "RIGHTWINGER": case "RIGHTATTACKINGMIDFIELDER": return "AM (R)";
            case "AML": case "LW": case "LEFTWINGER": case "LEFTATTACKINGMIDFIELDER": return "AM (L)";
            case "AMC": case "AMCR": case "AMCL": case "CAM": case "ATTACKINGMIDFIELDER": return "AM (C)";
            case "ST": case "STC": case "STCR": case "STCL": case "CF": case "STRIKER": case "CENTREFORWARD": return "ST (C)";
        }
        return s;
    }
}
