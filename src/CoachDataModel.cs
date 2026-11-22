// FM Coach v0.7 - data layer part 3: squad model, squad profile + style-fit hints (local, deterministic), AI context, JSON files.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

public partial class CoachBehaviour
{
    internal sealed class SquadPlayer
    {
        public string name, age, positions, bestRole, caStars, paStars, condition, sharpness, morale, injury, height, wage, value;
        public bool? registered; public string clubId, loanClubId, teamId, excludedReason;
        public Dictionary<string, string> seasonStats = new(), seasonReport = new();
        public string slotPosition, slotRole, slotDuty, slotRoleIpOop; public int teamSelIdx = -1;
        public double apps, goals, assists, avgRating;
        public Dictionary<string, double> attrs = new();
        public Dictionary<string, string> raw = new();
        public bool IsGK => (positions ?? "").Contains("GK");
        public double CaMid => StarMid(caStars);
    }
    readonly List<SquadPlayer> squad = new();
    Dictionary<string, object> profileSnap = new();
    List<string> hintsSnap = new();
    string squadContextText; int squadContextTokens;

    static readonly Regex NumRx = new(@"-?\d+(\.\d+)?");
    static double StarMid(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return 0;
        var nums = NumRx.Matches(s).Select(m => double.Parse(m.Value, CultureInfo.InvariantCulture)).ToList();
        return nums.Count == 0 ? 0 : nums.Average();
    }
    static double? Num(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var v = NumRx.Matches(s).Select(x => double.Parse(x.Value, CultureInfo.InvariantCulture)).Take(2).ToList();
        return v.Count == 0 ? null : v.Average();
    }
    static string NonEmpty(string s) => string.IsNullOrWhiteSpace(s) || s == "-" || s.StartsWith("FM.UI.") ? null : s;

    // v0.7 (t86u): wage period and currency are FM PREFERENCES (weekly/monthly/yearly; currency). Never hardcode them:
    // detect from the display string FM produced (suffix p/a, p/m, p/w and common localized forms; currency symbol/code),
    // falling back to the game's CurrencyName if readable. The model is told to quote these as-is and never convert.
    internal static readonly (string unit, Regex rx)[] WageUnitRx =
    {
        ("per year",  new Regex(@"(p\s*/\s*a|p\.a\.|/\s*(yr|year|an|año|ano|jahr|anno|jaar|rok|год)|per (annum|year)|a year|pro jahr|par an|al año|por ano|all'anno|per jaar|\bpa\b|/j\b)", RegexOptions.IgnoreCase)),
        ("per month", new Regex(@"(p\s*/\s*m\b|p\.m\.|/\s*(mo|mth|month|mois|mes|monat|mese|maand|mies|мес)|per month|a month|pro monat|par mois|al mes|por mês|al mese|per maand|\bpcm\b)", RegexOptions.IgnoreCase)),
        ("per week",  new Regex(@"(p\s*/\s*w\b|p\.w\.|/\s*(wk|week|sem|semana|woche|settimana|tydz|нед)|per week|a week|pro woche|par semaine|por semana|a settimana|\bpw\b)", RegexOptions.IgnoreCase)),
    };
    static readonly Regex CurrencyRx = new(@"(R\$|A\$|C\$|NZ\$|HK\$|US\$|[$€£¥₩₹₺₽₴₪฿₦₱]|\b(USD|EUR|GBP|JPY|CHF|SEK|NOK|DKK|PLN|CZK|BRL|ARS|MXN|AUD|CAD|TRY|RUB|CNY|KRW|INR|ZAR|kr|zł|Kč|Fr)\b)");
    internal static string gameCurrencyName; // from game/club CurrencyName if the probe resolves it

    internal static string WageUnit(string v)
    {
        if (string.IsNullOrWhiteSpace(v)) return null;
        foreach (var (u, rx) in WageUnitRx) if (rx.IsMatch(v)) return u;
        return null;
    }
    internal static string CurrencyOf(string v)
    {
        if (!string.IsNullOrWhiteSpace(v)) { var m = CurrencyRx.Match(v); if (m.Success) return m.Value; }
        return gameCurrencyName;
    }
    /// Structured money for the AI context: display string exactly as FM shows it + detected unit/currency.
    internal static Dictionary<string, string> MoneyInfo(string v, bool isWage)
    {
        if (string.IsNullOrWhiteSpace(v)) return null;
        var d = new Dictionary<string, string> { ["display"] = v };
        if (isWage) d["unit"] = WageUnit(v) ?? "unknown (quote the display string as-is)";
        d["currency"] = CurrencyOf(v) ?? "as displayed (quote the display string as-is)";
        return d;
    }
    /// One-line form for text context: e.g. "£32.5M p/a [wage_unit=per year; currency=£]".
    internal static string FormatMoney(double n)
    {
        var sym = gameCurrencyName;
        string body = Math.Abs(n) >= 1e6 ? (n / 1e6).ToString("0.##", CultureInfo.InvariantCulture) + "M" : Math.Abs(n) >= 1e3 ? (n / 1e3).ToString("0.#", CultureInfo.InvariantCulture) + "K" : n.ToString("0", CultureInfo.InvariantCulture);
        return sym != null ? (sym.Length <= 2 && !char.IsLetter(sym[0]) ? sym + body : body + " " + sym) : body + " (raw number, FM currency)";
    }
    internal static string MoneyWithUnit(string v, bool isWage = true)
    {
        var d = MoneyInfo(v, isWage); if (d == null) return v;
        var cur = CurrencyOf(v);
        return v + " [" + (isWage ? "wage_unit=" + (WageUnit(v) ?? "as displayed") : "") + (cur != null ? (isWage ? "; " : "") + "currency=" + cur : "") + "]";
    }

    void FillPlayer(SquadPlayer sp)
    {
        string D(string p) => sp.raw.TryGetValue(p, out var r) ? Display(p, r) : null;
        sp.name = Clean(D("Name") ?? "");
        sp.age = D("Age"); sp.positions = D("PositionCombinedString") ?? D("NaturalPositionShortString") ?? D("BestPositionShortString");
        sp.bestRole = NonEmpty(D("BestRole")); sp.caStars = D("CurrentAbilityStarRange") ?? D("CoachCurrentAbilityStarRange"); sp.paStars = D("PotentialAbilityStarRange") ?? D("CoachPotentialAbilityStarRange");
        sp.condition = D("ConditionString") ?? D("Condition") ?? D("Fitness"); sp.sharpness = D("Sharpness"); sp.morale = D("MoraleString") ?? D("Morale");
        var inj = D("IsInjured");
        sp.injury = inj != null && inj.Equals("True", StringComparison.OrdinalIgnoreCase) ? (NonEmpty(D("CurrentInjury")) ?? "injured") : null;
        sp.height = D("Height"); sp.wage = D("ContextContractWeeklyWage"); sp.value = D("TransferValueRange");
        sp.slotPosition = NonEmpty(ValueText(D("TacticsPositionName"))) ?? NonEmpty(ValueText(D("TeamSelectedPositionStringFromPlayer")));
        sp.slotRole = NonEmpty(ValueText(D("TacticsRoleName"))); sp.slotRoleIpOop = NonEmpty(ValueText(D("TacticsRoleNameWithIpOop")));
        sp.slotDuty = NonEmpty(ValueText(D("TacticsDutyName")));
        sp.teamSelIdx = Num(D("TeamSelectionIndex")) is double tsi ? (int)tsi : -1;
        foreach (var key in ClubSeasonProps) { var v = ValueText(D(key)); if (v != null) sp.seasonStats[key] = v; }
        sp.apps = Num(D("ClubAppearancesThisSeason")) ?? 0; sp.goals = Num(D("ClubGoalsThisSeason")) ?? 0;
        sp.assists = Num(D("ClubAssistsThisSeason")) ?? 0; sp.avgRating = Num(D("ClubAverageRatingThisSeason")) ?? 0;
        foreach (var (g, attrs) in AttrGroups)
            foreach (var (abbr, prop) in attrs)
            {
                if (!sp.IsGK && g == "GOALKEEPING") continue;
                var v = Num(D(prop));
                if (v != null && v >= 1 && v <= 20) sp.attrs[prop.Replace("Attribute", "")] = v.Value;
            }
    }

    static readonly Regex NotInXiRx = new("^(SUB|S\\d|RES|Not|-)", RegexOptions.IgnoreCase);
    void BuildTacticSlotsFromPlayers(Dictionary<string, object> t = null, bool preferScreen = false)
    {
        bool own = t == null; t ??= tacticSnap;
        // a) the players' own tactic fields (position/role/duty in the current tactic)
        var cand = !own || preferScreen ? new List<SquadPlayer>() : squad.Where(p => p.slotPosition != null && !NotInXiRx.IsMatch(p.slotPosition)).ToList();
        string src = "players' TacticsPositionName/TacticsRoleName/TacticsDutyName";
        if (cand.Count > 11 && cand.Count(p => p.teamSelIdx >= 0 && p.teamSelIdx <= 10) >= 9)
        { cand = cand.Where(p => p.teamSelIdx >= 0 && p.teamSelIdx <= 10).OrderBy(p => p.teamSelIdx).ToList(); src += " + TeamSelectionIndex 0-10"; }
        var slots = cand.Take(11).Select(p => SlotRow(p.slotPosition, p.slotRole, p.slotRoleIpOop, p.slotDuty, p.name)).ToList();
        // b) fallback: the tactic's position/selection lists (person name + combined role key)
        if (slots.Count < 9 && t.TryGetValue("lists", out var lo) && lo is Dictionary<string, List<Dictionary<string, string>>> lists)
        {
            foreach (var kv in lists.Where(kv => Regex.IsMatch(kv.Key, "TacticalPositions|TeamSelections|FirstTeamPlayerSlots")).OrderByDescending(kv => CoachTacticSnapshot.PositionListPriority(kv.Key, kv.Value)))
            {
                static string Who(Dictionary<string, string> r) => r.Where(x => x.Key.EndsWith(".Name") && !x.Key.Contains("Tactics")).Select(x => x.Value).FirstOrDefault();
                var rows = kv.Value.Where(r => Who(r) != null).ToList();
                if (rows.Count < 9) continue;
                static bool IsOop(Dictionary<string, string> r) => r.TryGetValue("IsInPossession", out var ip) && ip.Equals("False", StringComparison.OrdinalIgnoreCase);
                var oopRole = rows.Where(IsOop).GroupBy(Who).ToDictionary(g => g.Key, g => g.First().TryGetValue("PositionRole", out var pr) ? DecodeRoleKey(pr, true) : null);
                var fromList = new List<Dictionary<string, string>>(); var used = new HashSet<string>();
                foreach (var r in rows.Where(r => !IsOop(r)).OrderBy(r => r.TryGetValue("Index", out var ix) && int.TryParse(ix, out var n) ? n : 99))
                {
                    var who = Who(r); if (!used.Add(who)) continue;
                    string RoleOf(bool o, params string[] keys)
                    {
                        foreach (var k in keys) if (r.TryGetValue(k, out var rv) && !string.IsNullOrWhiteSpace(rv)) return IsNum(rv) ? DecodeRoleKey(rv, o) : rv;
                        return null;
                    }
                    var role = RoleOf(false, "Person.TacticsRoleName", "IPRole", "IPRoleOrBest", "InPossessionRole", "PositionRole", "RoleName", "PositionRoleName", "TacticsRoleName", "Role");
                    var pl = squad.FirstOrDefault(q => q.name == who);
                    var pos = r.TryGetValue("Person.TacticsPositionName", out var tpn) ? tpn
                        : r.TryGetValue("Position", out var ps) ? (IsNum(ps) ? PositionFromFlags(ps) : ps) : null;
                    pos ??= r.TryGetValue("IPPosition", out var ipp) ? (IsNum(ipp) ? PositionFromFlags(ipp) : ipp) : null;
                    pos ??= pl?.slotPosition ?? pl?.positions;
                    var oop = oopRole.TryGetValue(who, out var orl) ? orl : RoleOf(true, "OOPRole", "OOPRoleOrBest", "OutOfPossessionRole");
                    r.TryGetValue("Person.TacticsDutyName", out var duty);
                    fromList.Add(SlotRow(pos, role, oop != null && role != null ? role + " / " + oop : null, duty, who));
                }
                if (fromList.Count >= 9) { slots = fromList.Take(11).ToList(); src = "tactic list " + kv.Key; break; }
            }
        }
        t["slots"] = slots;
        t["slotsSource"] = slots.Count > 0 ? src : "(not readable yet - open Tactics once)";
        if (TI("formation", t) == null && slots.Count >= 10 && FormationFromSlots(slots) is string fm)
        {
            if (t.TryGetValue("teamInstructionsByPhase", out var pho) && pho is Dictionary<string, Dictionary<string, string>> phd)
            { if (!phd.TryGetValue("general", out var g)) phd["general"] = g = new Dictionary<string, string>(); g["formation"] = fm + " (derived from the slot positions)"; }
            t["formationDerived"] = fm;
        }
    }

    // "D (R)","D (C)","D (C)","D (L)","DM","M (C)","M (C)","AM (R)","ST (C)","AM (L)" -> "4-1-2-3"
    internal static string FormationFromSlots(List<Dictionary<string, string>> slots)
    {
        var lines = new SortedDictionary<int, int>();
        foreach (var sl in slots)
        {
            var p = sl.TryGetValue("position", out var v) ? v ?? "" : "";
            int band = p.StartsWith("GK") ? -1 : p.StartsWith("D ") || p == "D" || p.StartsWith("WB") ? 0 : p.StartsWith("DM") ? 1 : p.StartsWith("M ") || p == "M" ? 2 : p.StartsWith("AM") ? 3 : p.StartsWith("ST") ? 4 : -2;
            if (band == -2) return null;
            if (band >= 0) lines[band] = lines.TryGetValue(band, out var c) ? c + 1 : 1;
        }
        // FM-style: wide AM/M players count with the forward line when there is no central AM line (e.g. 4-1-2-3 DM Wide)
        if (lines.Values.Sum() != 10) return null;
        if (lines.ContainsKey(3) && lines.ContainsKey(4) && !slots.Any(s => (s.GetValueOrDefault("position") ?? "") == "AM (C)")) { lines[4] += lines[3]; lines.Remove(3); }
        return string.Join("-", lines.Values);
    }

    static Dictionary<string, string> SlotRow(string position, string role, string roleIpOop, string duty, string player)
    {
        string ipRole = role, oopRole = null;
        if (roleIpOop != null && roleIpOop.Contains("/")) { var parts = roleIpOop.Split('/'); ipRole ??= parts[0].Trim(); oopRole = parts[1].Trim(); }
        var d = new Dictionary<string, string>
        {
            ["position"] = PositionShort(position) ?? position, ["positionRaw"] = position,
            ["role"] = ipRole, ["roleShort"] = RoleAbbr(ipRole), ["duty"] = duty, ["player"] = player,
        };
        if (oopRole != null) { d["roleOutOfPossession"] = oopRole; d["roleOutOfPossessionShort"] = RoleAbbr(oopRole, true); }
        if (roleIpOop != null) d["roleIpOopRaw"] = roleIpOop;
        return d;
    }

    static readonly (string group, string[] attrs)[] ProfileGroups =
    {
        ("pace", new[] { "Pace", "Acceleration" }),
        ("physical", new[] { "Stamina", "Strength", "NaturalFitness", "WorkRate" }),
        ("technical", new[] { "Passing", "Technique", "FirstTouch", "Dribbling" }),
        ("mental", new[] { "Decisions", "Anticipation", "Composure", "Teamwork", "Positioning" }),
        ("aerial", new[] { "Heading", "JumpingReach" }),
        ("defensive", new[] { "Tackling", "Marking" }),
    };

    List<SquadPlayer> BestXI()
    {
        var inSlots = squad.Where(p => p.slotPosition != null && !NotInXiRx.IsMatch(p.slotPosition)).Take(11).ToList();
        if (inSlots.Count >= 9) return inSlots;
        // v0.7.1: the XI from the tactic's slots (Tactics screen) when the players' own slot fields are not readable
        if (tacticSnap.TryGetValue("slots", out var so) && so is List<Dictionary<string, string>> sl && sl.Count >= 9)
        {
            var fromSlots = sl.Select(d => squad.FirstOrDefault(p => p.name == d.GetValueOrDefault("player"))).Where(p => p != null).Distinct().ToList();
            if (fromSlots.Count >= 9) return fromSlots;
        }
        var gk = squad.Where(p => p.IsGK).OrderByDescending(p => p.CaMid).ThenByDescending(p => p.apps).Take(1);
        var outf = squad.Where(p => !p.IsGK && p.injury == null).OrderByDescending(p => p.CaMid).ThenByDescending(p => p.apps).Take(10);
        return gk.Concat(outf).ToList();
    }

    static double? GroupAvg(IEnumerable<SquadPlayer> ps, string[] attrs)
    {
        var v = ps.Where(p => !p.IsGK).SelectMany(p => attrs.Where(a => p.attrs.ContainsKey(a)).Select(a => p.attrs[a])).ToList();
        return v.Count == 0 ? null : Math.Round(v.Average(), 1);
    }

    // instruction option text -> level -2..+2 (keywords of FM27's option names, e.g. LineOfEngagementHigher, PressingIntensityMuchLower)
    internal static int Level(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return 0;
        var t = Regex.Replace(s.Replace(" ", "").Replace(":", "").ToLowerInvariant(), "^(lineofengagement|defensiveline|pressingintensity|passingstyle|passingdirectness|teamwidth|attacktransition|defencetransition|crossengagement|gkdistributionpace)", "");
        if (Regex.IsMatch(t, "muchhigher|muchmore|veryattacking|muchwider|muchquicker|muchfaster")) return 2;
        if (Regex.IsMatch(t, "muchlower|muchdeeper|muchless|verydefensive|muchnarrower|muchshorter|muchslower")) return -2;
        if (Regex.IsMatch(t, "higher|more|attacking|positive|wider|direct|high|quicker|faster")) return 1;
        if (Regex.IsMatch(t, "lower|deeper|less|defensive|cautious|narrower|shorter|low|slower")) return -1;
        return 0;
    }

    string TI(string key, Dictionary<string, object> t = null)
    {
        if (!(t ?? tacticSnap).TryGetValue("teamInstructionsByPhase", out var o) || o is not Dictionary<string, Dictionary<string, string>> d) return null;
        foreach (var ph in d) if (ph.Value.TryGetValue(key, out var v)) return v;
        return null;
    }

    void ComputeProfileAndHints()
    {
        var xi = BestXI();
        var groups = new Dictionary<string, object>(); var xiAvgs = new Dictionary<string, double>();
        foreach (var (g, attrs) in ProfileGroups)
        {
            var a = GroupAvg(xi, attrs); var s = GroupAvg(squad, attrs);
            if (a != null) xiAvgs[g] = a.Value;
            groups[g] = new Dictionary<string, object> { ["bestXI"] = a, ["squad"] = s };
        }
        double overall = xiAvgs.Count > 0 ? xiAvgs.Values.Average() : 0;
        var rank = xiAvgs.OrderByDescending(kv => kv.Value).Select((kv, i) => (kv.Key, i + 1)).ToDictionary(x => x.Key, x => x.Item2);
        foreach (var g in xiAvgs.Keys)
        {
            var d = (Dictionary<string, object>)groups[g]; double v = xiAvgs[g];
            d["flag"] = v - overall >= 1.0 || v >= 14.5 ? "strength" : (overall - v >= 1.0 || v <= 10.5 ? "weakness" : "average");
            d["rankOf6"] = rank[g];
        }
        var heights = xi.Where(p => !p.IsGK).Select(p => Num(p.height)).Where(h => h != null && h > 140 && h < 215).Select(h => h.Value).ToList();
        var prof = new Dictionary<string, object>
        {
            ["bestXI"] = xi.Select(p => p.name + (p.slotPosition != null ? " (" + p.slotPosition + ")" : "")).ToList(),
            ["bestXISource"] = xi.Count > 0 && xi.All(p => p.slotPosition != null) ? "current tactic slots"
                : tacticSnap.TryGetValue("slots", out var xs) && xs is List<Dictionary<string, string>> xl && xl.Count >= 9 ? "current tactic slots (Tactics screen)" : "top current ability (tactic slots not readable)",
            ["groups"] = groups, ["xiOverallAttributeAvg"] = Math.Round(overall, 1), ["squadSize"] = squad.Count,
            ["injured"] = squad.Where(p => p.injury != null).Select(p => p.name).ToList(),
        };
        if (heights.Count > 0) prof["xiAvgHeightCm"] = Math.Round(heights.Average(), 0);
        profileSnap = prof;

        // ---- hints for the AI (heuristics, not rules) ----
        var hints = new List<string>();
        string F(string g) => groups.TryGetValue(g, out var o) && ((Dictionary<string, object>)o).TryGetValue("flag", out var f) ? (string)f : "unknown";
        double A(string g) => xiAvgs.TryGetValue(g, out var v) ? v : 0;
        var ment = TI("mentality"); var loe = TI("lineOfEngagement"); var dline = TI("defensiveLine"); var tempo = TI("tempo"); var press = TI("pressingIntensity");
        var direct = TI("passingDirectness"); var onWin = TI("onWinningBall"); var onLose = TI("onLosingBall"); var cross = TI("crossingStyle"); var crossEng = TI("crossEngagement");
        var cf = TI("creativeFreedom"); var trap = TI("pressingTrap");
        bool tacticKnown = ment != null || loe != null || dline != null || tempo != null || press != null;
        bool counterOn = onWin != null && onWin.ToLowerInvariant().Contains("counter");
        bool counterpress = onLose != null && onLose.ToLowerInvariant().Contains("counterpress");
        if (squad.Count == 0) hints.Add("Squad not readable yet: ask the manager to open the Squad screen once.");
        if (!tacticKnown) hints.Add("Team instructions not readable yet: ask the manager to open the Tactics screen once.");
        int passive = new[] { ment, loe, dline, tempo, press }.Count(x => Level(x) <= -1 && x != null);
        if (F("pace") == "strength" && tacticKnown && passive >= 2 && !counterOn)
            hints.Add($"Quick XI (pace {A("pace")} vs XI avg {overall:0.0}) in a passive set-up (mentality '{ment}', LoE '{loe}', line '{dline}', tempo '{tempo}', press '{press}'): consider 'Counter' on winning possession, higher tempo / more direct passing into space, in-possession roles that attack space.");
        if (F("pace") == "strength" && tacticKnown && Level(dline) <= -1)
            hints.Add($"Pace at the back would support a higher defensive line than '{dline}'.");
        if (F("pace") == "weakness" && Level(dline) >= 1)
            hints.Add($"Slow side (pace {A("pace")}) with a high line ('{dline}'): exposed in behind; consider a standard/deeper line.");
        var crossLow = (cross ?? "") + (crossEng ?? "");
        if (F("aerial") == "weakness" && (Level(crossEng) >= 1 || Regex.IsMatch(crossLow, "whip|float|early|high", RegexOptions.IgnoreCase)))
            hints.Add($"Weak in the air (aerial {A("aerial")}) but crossing-focused ('{cross ?? crossEng}'): prefer low crosses / work the ball into the box.");
        if (F("aerial") == "strength" && Level(direct) <= -1)
            hints.Add($"Strong in the air (aerial {A("aerial")}): a target forward or more crosses would add a Plan B to the short passing.");
        if (F("physical") == "weakness" && (Level(press) >= 1 || counterpress || Level(loe) >= 1))
            hints.Add($"Low stamina/work rate (physical {A("physical")}) with an intense press ('{press}', LoE '{loe}'{(counterpress ? ", counter-press" : "")}): may fade late; consider lower pressing or 'Regroup'.");
        if (F("technical") == "strength" && Level(direct) >= 1)
            hints.Add($"Technical XI (technical {A("technical")}) playing direct ('{direct}'): shorter passing / playing out may suit them.");
        if (F("technical") == "weakness" && Level(direct) <= -1)
            hints.Add($"Short passing ('{direct}') with limited technique (technical {A("technical")}): more direct passing may cut turnovers.");
        if (F("mental") == "weakness" && Level(cf) >= 1)
            hints.Add($"High creative freedom ('{cf}') with weak decisions/composure (mental {A("mental")}): 'Be More Disciplined' could help.");
        if (F("defensive") == "weakness" && Level(ment) >= 1)
            hints.Add($"Rule-of-thumb fit check: XI tackling/marking {A("defensive")}/20 vs its attribute-group mean {overall:0.0}/20. With '{ment}' mentality, retain defensive cover through the out-of-possession roles.");
        if (trap != null && Level(press) <= -1 && Regex.IsMatch(trap, @"\b(In|Out)$"))
            hints.Add($"Pressing trap '{trap}' with low pressing intensity '{press}': the trap needs some press to work.");
        foreach (var p in xi.Where(p => p.slotRole != null))
        {
            string r = p.slotRole.ToLowerInvariant();
            void Need(string what, params string[] ks)
            {
                var low = ks.Where(k => p.attrs.TryGetValue(k, out var v) && v <= 10).ToList();
                if (low.Count > 0) hints.Add($"{p.name} as {p.slotRole}{(p.slotDuty != null ? " (" + p.slotDuty + ")" : "")}: {what} role but {string.Join(", ", low.Select(k => k + " " + p.attrs[k]))}.");
            }
            if (Regex.IsMatch(r, "playmaker|regista|mezzala|libero|ball.playing|roaming|enganche|trequartista")) Need("creative/passing", "Passing", "Vision", "Technique");
            if (Regex.IsMatch(r, "ball.winning|pressing|box.to.box|segundo|carrilero|wing.back")) Need("high-energy", "Stamina", "WorkRate");
            if (Regex.IsMatch(r, "advanced forward|inside forward|winger|poacher|raumdeuter|channel|wide forward")) Need("pace-based", "Pace", "Acceleration");
            if (Regex.IsMatch(r, "target")) Need("aerial/hold-up", "Heading", "Strength", "JumpingReach");
            if (Regex.IsMatch(r, "anchor|defensive midfielder|half back|holding")) Need("screening", "Positioning", "Tackling", "Anticipation");
        }
        if (tacticSnap.TryGetValue("playerInstructions", out var pio) && pio is List<Dictionary<string, string>> pis)
            foreach (var row in pis)
            {
                var pl = squad.FirstOrDefault(s => row.Values.Any(v => v.Contains(s.name)));
                if (pl == null) continue;
                var txt = string.Join(" ", row.Values).ToLowerInvariant();
                void Chk(string pat, string what, params string[] ks)
                {
                    if (!Regex.IsMatch(txt, pat)) return;
                    var low = ks.Where(k => pl.attrs.TryGetValue(k, out var v) && v <= 10).ToList();
                    if (low.Count > 0) hints.Add($"{pl.name}: '{what}' instruction but {string.Join(", ", low.Select(k => k + " " + pl.attrs[k]))}.");
                }
                Chk("shoot ?more|shoot ?from ?distance|shootmore", "shoot more", "LongShots", "Finishing");
                Chk("dribble ?more|run ?with ?ball", "dribble more", "Dribbling", "Flair");
                Chk("cross ?(more|from ?byline|often)", "cross more", "Crossing");
                Chk("close ?down ?more|press ?more|tackle ?harder", "press/tackle harder", "Stamina", "WorkRate", "Aggression");
                Chk("mark ?tighter", "mark tighter", "Marking");
                Chk("through ?ball|more ?risk|risky", "riskier passes", "Vision", "Passing");
            }
        hintsSnap = hints.Distinct().Take(14).ToList();
        profileSnap["styleHints"] = hintsSnap;
    }

    // ---- AI context block (compact text, capped ~3k tokens) ----
    static readonly (string abbr, string attr)[] CtxAttrs = { ("Pac","Pace"),("Acc","Acceleration"),("Sta","Stamina"),("Str","Strength"),("Wor","WorkRate"),("Pas","Passing"),("Tec","Technique"),("Fir","FirstTouch"),("Dri","Dribbling"),("Cro","Crossing"),("Fin","Finishing"),("Dec","Decisions"),("Ant","Anticipation"),("Cmp","Composure"),("Tea","Teamwork"),("Pos","Positioning"),("Hea","Heading"),("Jum","JumpingReach"),("Tck","Tackling"),("Mar","Marking"),("Vis","Vision") };

    void BuildSquadContext()
    {
        AttachTacticSlots();
        var sb = new StringBuilder();
        sb.AppendLine("MY SQUAD AND TACTIC (read from the save at " + (squadReadAt == default ? snapshotAt == default ? DateTime.Now : snapshotAt : squadReadAt).ToString("HH:mm") + (lastGameDate != null ? ", game date " + lastGameDate : "") + "):");
        foreach (var k in new[] { "team.Name", "team.LeaguePositionString", "team.NextMatchOpponent.Name", "team.NextMatchCompetition.Name", "team.NextMatchDaysText", "team.NextMatchLocationString", "team.PlayersAtRiskShortString" })
            if (teamSnap.TryGetValue(k, out var v) && ValueText(v) is string tv) sb.AppendLine(k.Substring(5) + ": " + Trunc(tv, 90));
        sb.AppendLine("DATA COVERAGE: " + DataLoadSummary());
        sb.AppendLine("SQUAD SCOPE: registered first-team players currently at the club; loaned-out and injured players excluded. Registration is membership in at least one available club competition list.");
        if (teamSeasonStats.Count > 0) sb.AppendLine("TEAM STATS (FM report scope as displayed; do not assume a different season/competition): " + string.Join("; ", teamSeasonStats.Select(kv => kv.Key + "=" + kv.Value)));
        else sb.AppendLine("TEAM STATS: not returned yet; do not infer missing team totals by adding this filtered squad.");
        // money: FM's own display strings (currency + period as the manager set them); raw integers only get the detected symbol
        foreach (var k in new[] { "club.RemainingWageBudgetWithText", "club.CurrentWageSpendingWithText" })
            if (teamSnap.TryGetValue(k, out var v) && ValueText(v) is string mv) sb.AppendLine(k.Substring(5).Replace("WithText", "") + ": " + Trunc(MoneyWithUnit(mv, true), 120));
        // Raw budget integers are native currency/base-period amounts, not FM display strings.
        // Never label them with the user's symbol or duplicate a formatted wage budget with a raw figure.
        var transferDisplay = new[] { "club.TransferBudgetWithText", "club.TransferBudgetString", "club.AvailableTransferBudgetWithText" }
            .Select(k => teamSnap.GetValueOrDefault(k)).FirstOrDefault(v => v != null && CurrencyRx.IsMatch(v));
        sb.AppendLine("Available transfer budget: " + (transferDisplay ?? "display value not read; do not quote or convert raw budget numbers"));
        if (tacticSnap.ContainsKey("object"))
        {
            bool currentComplete = activeSlot is int currentSlot && slotStore.TryGetValue(currentSlot, out var currentRecord) &&
                CoachTacticSnapshot.CanUseDetails(currentRecord.detailsComplete, currentRecord.detailsSource);
            sb.AppendLine(currentComplete ? "CURRENT TACTIC SNAPSHOT: complete current read." : "CURRENT TACTIC SNAPSHOT: PARTIAL; observed fields only. Slot coverage below identifies missing settings.");
            sb.Append("TACTIC: ").Append(tacticSnap.TryGetValue("name", out var tn) ? tn : tacticSnap.TryGetValue("CurrentTacticSlotName", out var tn2) ? tn2 : "?")
              .Append(" | formation ").Append(TI("formation") ?? "?").Append(" | mentality ").Append(TI("mentality") ?? "?")
              .Append(activeSlot is int acs ? " | active slot " + acs + (primarySlot == acs ? " (also the PRIMARY)" : primarySlot is int ps ? " (primary is slot " + ps + ")" : "") : "").AppendLine();
            if (tacticSnap["teamInstructionsByPhase"] is Dictionary<string, Dictionary<string, string>> ph)
                foreach (var phase in new[] { "general", "inPossession", "transition", "outOfPossession" })
                    if (ph.TryGetValue(phase, out var d) && d.Count > 0) sb.AppendLine(phase + ": " + string.Join("; ", d.Where(kv => phase != "general" || (kv.Key != "formation" && kv.Key != "mentality")).Select(kv => kv.Key + "=" + Trunc(kv.Value, 40))));
            // v0.7.2: explicit list so the coach never recommends what is already set
            if (tacticSnap["teamInstructionsByPhase"] is Dictionary<string, Dictionary<string, string>> ph2)
            {
                var active = ph2.Where(p => p.Key != "general").SelectMany(p => p.Value.Values).Where(v => v != null && !v.EndsWith(": Default") && !v.StartsWith("Default")).Distinct().ToList();
                if (active.Count > 0) sb.AppendLine("OBSERVED SETTINGS in the active tactic (do not recommend these as new changes; other settings may be unread): " + string.Join(", ", active));
            }
        }
        else if (tacticSnap.TryGetValue("name", out var tnOnly) && tnOnly is string tns && tns.Length > 0)
            sb.AppendLine("TACTIC: " + tns + " (name only; formation, mentality and instructions still awaiting automatic reads)");
        else sb.AppendLine("TACTIC: automatic read pending; do not infer settings.");
        AppendTacticSlotsContext(sb);
        AppendOpponentContext(sb);
        if (tacticSnap.TryGetValue("slots", out var so) && so is List<Dictionary<string, string>> slots && slots.Count > 0)
            sb.AppendLine("XI ROLES: " + string.Join("; ", slots.Select(s => $"{s["position"]} {s["role"]}{(s.TryGetValue("roleOutOfPossession", out var oo) && oo != null ? "/" + oo : "")}{(s["duty"] != null ? "(" + s["duty"] + ")" : "")} {s["player"]}")));
        if (tacticSnap.TryGetValue("playerInstructions", out var pio) && pio is List<Dictionary<string, string>> pis && pis.Count > 0)
            sb.AppendLine((tacticSnap.GetValueOrDefault("playerInstructionsRead") is bool completePi && completePi
                ? "PLAYER INSTRUCTIONS (complete effective group settings; Standard/No/Follow Tactical Position are confirmed defaults): "
                : "OBSERVED PLAYER INSTRUCTION FIELDS (partial; other groups may be unread): ") + string.Join(" | ", pis.Select(CoachTacticSnapshot.PlayerInstructionContext).Where(text => text != null)));
        if (squad.Count > 0)
        {
            var top = squad.OrderByDescending(p => p.slotPosition != null && !NotInXiRx.IsMatch(p.slotPosition) ? 1 : 0).ThenByDescending(p => p.CaMid).ThenByDescending(p => p.apps).ToList();
            sb.AppendLine("SQUAD: " + squad.Count + " registered available players; all " + top.Count + " listed as name|age|pos|tactic slot|CA/PA stars|cond|sharp|morale|inj|" + "all visible attributes as name=value|current-season club stats|wage");
            var w0 = squad.Select(p => p.wage).FirstOrDefault(w => !string.IsNullOrWhiteSpace(w));
            if (w0 != null) sb.AppendLine("WAGES are FM display strings: wage_unit=" + (WageUnit(w0) ?? "as displayed") + ", currency=" + (CurrencyOf(w0) ?? "as displayed") + " (manager's FM preference; quote as shown, never convert).");
            foreach (var p in top)
            {
                string at = string.Join(" ", p.attrs.OrderBy(kv => kv.Key).Select(kv => kv.Key + "=" + kv.Value.ToString("0.##", CultureInfo.InvariantCulture)));
                sb.AppendLine($"{p.name}|{p.age}|{p.positions}|{(p.slotRole != null ? p.slotPosition + " " + p.slotRole + (p.slotDuty != null ? "(" + p.slotDuty + ")" : "") : "-")}|{p.caStars ?? "not read"}/{p.paStars ?? "not read"}|{p.condition}|{p.sharpness}|{p.morale}|{p.injury ?? "-"}|{at}|{string.Join(" ", ClubSeasonProps.Select(k => k.Replace("Club", "").Replace("ThisSeason", "") + "=" + (p.seasonStats.GetValueOrDefault(k) ?? "not read")))}|{p.wage ?? "-"}");
            }
            foreach (var player in top.Where(p => p.seasonReport.Count > 0))
                sb.AppendLine("PLAYER STAT REPORT " + player.name + " (use FM's supplied season/competition labels; do not mix with the explicitly current-season club totals above): " + string.Join("; ", player.seasonReport.Select(kv => kv.Key + "=" + kv.Value)));
            var rest = squad.Except(top).Select(p => p.name + " " + p.positions).ToList();
            if (rest.Count > 0) sb.AppendLine("OTHERS: " + Trunc(string.Join(", ", rest), 400));
        }
        else sb.AppendLine("SQUAD: registration/availability reads pending; do not infer player eligibility.");
        if (profileSnap.TryGetValue("groups", out var go) && go is Dictionary<string, object> gd && gd.Count > 0)
            sb.AppendLine("XI PROFILE (avg 1-20; flag vs XI average " + profileSnap["xiOverallAttributeAvg"] + "): " + string.Join("; ", gd.Select(kv =>
            { var d = (Dictionary<string, object>)kv.Value; return kv.Key + " " + d["bestXI"] + (d.TryGetValue("flag", out var f) ? " " + f : ""); }))
            + (profileSnap.TryGetValue("xiAvgHeightCm", out var h) ? "; avg height " + h + "cm" : "") + " [" + profileSnap["bestXISource"] + "]");
        if (hintsSnap.Count > 0) sb.AppendLine("FIT HINTS (local heuristics, verify before using): " + string.Join(" / ", hintsSnap));
        var text = sb.ToString();
        // Keep every registered player and attribute; no silent context truncation.
        squadContextText = text;
        squadContextTokens = (int)Math.Ceiling(text.Length / 3.6);
    }

    string SquadContextForAI()
    {
        if (squadContextText != null) return squadContextText;
        return "MY SQUAD AND TACTIC: " + (humanTv == null ? "not connected yet (save still loading or manager not detected)." : "being read now (" + snapshotStatus + ").") + " Missing startup data is retried automatically.";
    }

    string CacheDir()
    {
        string id = teamSnap.TryGetValue("club.UniqueID", out var u) && IsNum(u) ? u : (humanClubId ?? (HumanClub() ?? "unknown"));
        foreach (var c in Path.GetInvalidFileNameChars()) id = id.Replace(c, '_');
        var d = Path.Combine(CoachPlugin.OutDir, "cache", CareerCacheKey(), id);
        Directory.CreateDirectory(d);
        return d;
    }

    void WriteSnapshotFiles()
    {
        try
        {
            var opt = new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
            var squadJson = JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["club"] = HumanClub(), ["manager"] = humanManager, ["gameDate"] = lastGameDate, ["readAt"] = squadReadAt == default ? null : squadReadAt.ToString("s"), ["team"] = teamSnap, ["count"] = squad.Count,
                ["players"] = squad.Select(p => new Dictionary<string, object>
                {
                    ["registered"] = p.registered, ["clubId"] = p.clubId, ["loanClubId"] = p.loanClubId, ["teamId"] = p.teamId, ["name"] = p.name, ["age"] = p.age, ["positions"] = p.positions, ["bestRole"] = p.bestRole, ["caStars"] = p.caStars, ["paStars"] = p.paStars,
                    ["condition"] = p.condition, ["sharpness"] = p.sharpness, ["morale"] = p.morale, ["injury"] = p.injury, ["height"] = p.height,
                    ["tacticSlot"] = p.slotPosition == null ? null : new Dictionary<string, string> { ["position"] = p.slotPosition, ["role"] = p.slotRole, ["duty"] = p.slotDuty },
                    ["season"] = new Dictionary<string, object> { ["scope"] = "current season, club", ["values"] = p.seasonStats, ["reportValues"] = p.seasonReport, ["reportScope"] = "FM SeasonStats/SeasonStatsCombinedRow, season and competition as returned" },
                    ["wage_display"] = p.wage, ["wage_unit"] = WageUnit(p.wage), ["currency"] = CurrencyOf(p.wage ?? p.value), ["value"] = p.value, ["attributes"] = p.attrs,
                }).ToList(),
            }, opt);
            var tacticJson = Regex.Replace(JsonSerializer.Serialize(tacticSnap, opt), @"FM\.UI\.(\w+?)Reference\b", "$1");   // class names are internal detail
            var profileJson = JsonSerializer.Serialize(profileSnap, opt);
            foreach (var dir in new[] { CacheDir(), Path.Combine(TestDir, "snapshot") })
            {
                Directory.CreateDirectory(dir);
                WriteFullSnapshotStatus(dir);
                File.WriteAllText(Path.Combine(dir, "squad.json"), squadJson, Encoding.UTF8);
                File.WriteAllText(Path.Combine(dir, "tactic.json"), tacticJson, Encoding.UTF8);
                File.WriteAllText(Path.Combine(dir, "profile.json"), profileJson, Encoding.UTF8);
                File.WriteAllText(Path.Combine(dir, "context.txt"), squadContextText ?? "", Encoding.UTF8);
                WriteOpponentFile(dir);
            }
            CoachPlugin.L.LogInfo("Snapshot files written: " + CacheDir() + " and " + Path.Combine(TestDir, "snapshot"));
        }
        catch (Exception ex) { LogOnce("SnapshotWrite", ex); }
    }
}
