// FM Coach v0.7.3 - next opponent: fixture, FM's opposition/scout report fields, and our own form/fitness, read-only via bindings.
// Sources (FM27 TeamReference catalogue): team.NextMatchOpponent (a TeamReference) and, on it, MostUsedFormation, ExpectedFormations,
// ExpectedTacticalStyle, TeamReportExpectedTactic, RecentFormValues, RecentPerformance, ScoutReportStrengths/Weaknesses, KeyPlayer,
// TopGoalscorerPlayer, HighestAverageRatingPlayer, CurrentInjuryListCount, HasTeamReport, NoTeamReportSupportingString,
// LastFiveMatchAnalysisPro/ConCount, LeaguePositionString; on our team NextOppositionThreatLevelAssessment, RecentFormValues, etc.
// Several of these only answer once FM has built the report (opposition report / next match screen open): missing fields are listed
// as ordinary scouting uncertainty. Advice uses the evidence available. Retried every 60 s while incomplete; nothing is guessed.
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using UnityEngine;
using SI.Core;
using DateTime = System.DateTime;

public partial class CoachBehaviour
{
    TypedValue snapTeamTv;
    readonly Dictionary<string, object> oppSnap = new();
    float nextOppRetry; string oppForName; bool oppComplete;

    static readonly (string key, string prop)[] OppProps =
    {
        ("name", "Name"), ("leaguePosition", "LeaguePositionString"), ("mostUsedFormation", "MostUsedFormation"),
        ("expectedFormations", "ExpectedFormations"), ("expectedStyle", "ExpectedTacticalStyle"),
        ("expectedFormationName", "TeamReportExpectedTactic.FormationName"), ("expectedMentality", "TeamReportExpectedTactic.MentalityString"),
        ("recentForm", "RecentFormValues"), ("recentFormAllComps", "RecentFormValuesAllComps"), ("recentPerformance", "RecentPerformance"),
        ("generalPerformance", "TeamReportGeneralPerformance"),
        ("strengths", "ScoutReportStrengths"), ("weaknesses", "ScoutReportWeaknesses"),
        ("keyPlayer", "KeyPlayer.Name"), ("topScorer", "TopGoalscorerPlayer.Name"), ("bestRated", "HighestAverageRatingPlayer.Name"),
        ("injuriesCount", "CurrentInjuryListCount"), ("hasTeamReport", "HasTeamReport"), ("noReportReason", "NoTeamReportSupportingString"),
        ("last5Positives", "LastFiveMatchAnalysisProCount"), ("last5Negatives", "LastFiveMatchAnalysisConCount"),
    };
    static readonly (string key, string prop)[] OurProps =
    {
        ("recentForm", "RecentFormValues"), ("recentFormAllComps", "RecentFormValuesAllComps"), ("recentPerformance", "RecentPerformance"),
        ("threatAssessment", "NextOppositionThreatLevelAssessment"), ("injuriesCount", "CurrentInjuryListCount"),
        ("injuryReturnsNextFixture", "InjuryExpectedReturnsNextFixtureString"), ("fitnessTest", "PreMatchFitnessTestString"),
        ("last5Positives", "LastFiveMatchAnalysisProCount"), ("last5Negatives", "LastFiveMatchAnalysisConCount"),
        ("leaguePosition", "LeaguePositionString"),
    };
    static readonly string[] OppEssential = { "mostUsedFormation", "expectedStyle", "recentForm", "strengths", "weaknesses", "keyPlayer" };

    // value or list -> text; reference items are named via a Name request
    IEnumerator ReadTexts(List<Req> rs, Dictionary<string, object> dst, (string key, string prop)[] map)
    {
        var follow = new List<(string key, List<Req> names)>();
        foreach (var r in rs.Where(r => r.Got))
        {
            var key = map.First(m => m.prop == r.Path).key;
            int n = r.Tv != null ? ListCount(r.Tv) : -1;
            if (n > 0)
            {
                var items = ListItems(r.Tv, 8); var texts = new List<string>(); var refs = new List<TypedValue>();
                foreach (var it in items) { string s0 = null; try { s0 = Clean(it.AsString()); } catch { } var v = ValueText(s0); if (v != null) texts.Add(Trunc(v, 120)); else refs.Add(it); }
                if (texts.Count > 0) dst[key] = texts;
                else if (refs.Count > 0) follow.Add((key, refs.Select(t => Ask(t, "Name", 5f, false, "opp")).ToList()));
            }
            else if (ValueText(r.Str) is string v) dst[key] = Trunc(v, 200);
        }
        if (follow.Count > 0)
        {
            yield return WaitAll(follow.SelectMany(f => f.names), 6f);
            foreach (var (key, names) in follow) { var t = names.Where(x => x.Got).Select(x => ValueText(x.Str)).Where(x => x != null).ToList(); if (t.Count > 0) dst[key] = t; }
        }
    }

    IEnumerator OpponentJob() => Flatten(OpponentSteps(true));

    // standalone=true: retry run outside a snapshot (writes files + context itself)
    IEnumerator OpponentSteps(bool standalone)
    {
        var team = snapTeamTv; if (team == null) yield break;
        var now = DateTime.Now.ToString("s");
        var oppR = Ask(team, "NextMatchOpponent", 8f, true, "team");
        var fx = new[] { "NextMatchOpponent.Name", "NextMatchCompetition.Name", "NextMatchLocationString", "NextMatchDaysText" }.Select(p => Ask(team, p, 8f, false, "team")).ToList();
        // the fixture date: whatever date-like string FixtureReference exposes (catalogue-driven, no guessing of names)
        var fxDateProps = PropsOf("FM.UI.FixtureReference").Where(p => p.Kind == "Value:System.String" && p.Name.IndexOf("Date", StringComparison.OrdinalIgnoreCase) >= 0).Select(p => "NextFixture." + p.Name).Take(4).ToList();
        var fxd = fxDateProps.Select(p => Ask(team, p, 8f, false, "team")).ToList();
        var ours = OurProps.Select(m => Ask(team, m.prop, 8f, true, "ours")).ToList();
        yield return WaitAll(fx.Concat(fxd).Concat(ours).Append(oppR), 9f);

        string F(int i) => fx[i].Got ? ValueText(fx[i].Str) : null;
        var fixture = new Dictionary<string, object> { ["opponent"] = F(0), ["competition"] = F(1), ["location"] = F(2), ["when"] = F(3),
            ["date"] = fxd.Where(r => r.Got).Select(r => ValueText(r.Str)).FirstOrDefault(v => v != null), ["source"] = "team.NextMatch* / team.NextFixture", ["readAt"] = now };
        var oursD = new Dictionary<string, object>();
        yield return ReadTexts(ours, oursD, OurProps);
        var tired = squad.Where(p => p.injury != null || (p.condition != null && !System.Text.RegularExpressions.Regex.IsMatch(p.condition, "Peak|Excellent|^(9\\d|100)"))).Take(8)
            .Select(p => p.name + " (" + (p.injury != null ? "injured: " + p.injury : "condition " + p.condition) + ")").ToList();
        if (squad.Count > 0) oursD["notFullyFit"] = tired;
        oursD["primaryTactic"] = primarySlot is int ps && slotStore.TryGetValue(ps, out var pr) ? pr.name : null;
        oursD["source"] = "team (" + string.Join(", ", OurProps.Select(m => m.prop)) + ") + squad snapshot"; oursD["readAt"] = now;

        var oppD = new Dictionary<string, object>();
        var opponentStats = new Dictionary<string, string>();
        if (oppR.Tv != null && (ClassOf(oppR.Tv) ?? "").EndsWith("TeamReference"))
        {
            var cat = PropsOf(ClassOf(oppR.Tv));
            var rs = OppProps.Where(m => cat.Count == 0 || cat.Any(c => c.Name == m.prop.Split('.')[0])).Select(m => Ask(oppR.Tv, m.prop, 10f, true, "opp")).ToList();
            yield return WaitAll(rs, 11f);
            yield return ReadTexts(rs, oppD, OppProps);
            yield return ReadGeneralPerformanceStats(oppR.Tv, opponentStats);
            CoachPlugin.L.LogInfo("Opponent team statistics: " + opponentStats.Count + " readable report fields; source=GeneralPerformanceVisual");
            oppD["source"] = "team.NextMatchOpponent [" + ClassOf(oppR.Tv) + "]"; oppD["readAt"] = now;
        }
        else oppD["source"] = "team.NextMatchOpponent not answered (" + ReqText(oppR) + ")";
        var missing = OppEssential.Where(k => !oppD.ContainsKey(k)).ToList();

        var oppName = fixture["opponent"] as string ?? oppD.GetValueOrDefault("name") as string;
        if (oppName != oppForName) { oppForName = oppName; oppComplete = false; }
        // keep fields read earlier for the same opponent (e.g. while the report screen was open)
        if (oppSnap.TryGetValue("opponent", out var prevO) && prevO is Dictionary<string, object> po && (oppSnap.GetValueOrDefault("forOpponent") as string) == oppName)
            foreach (var kv in po) if (!oppD.ContainsKey(kv.Key)) oppD[kv.Key] = kv.Value;
        missing = OppEssential.Where(k => !oppD.ContainsKey(k)).ToList();
        if (!CoachTeamStats.HasScopedMetrics(opponentStats)) missing.Add("team statistics with confirmed season/competition scope");
        oppComplete = missing.Count == 0;

        oppSnap.Clear();
        oppSnap["forOpponent"] = oppName; oppSnap["readAt"] = now;
        oppSnap["fixture"] = fixture; oppSnap["opponent"] = oppD; oppSnap["ours"] = oursD;
        oppSnap["teamStatistics"] = new Dictionary<string, object>
        {
            ["forOpponent"] = oppName, ["source"] = "team.NextMatchOpponent.GeneralPerformanceVisual", ["readAt"] = now,
            ["scope"] = "Use season/competition labels supplied in report fields; absent labels mean unknown scope. Never assume current season or next-fixture competition.",
            ["fields"] = opponentStats, ["available"] = opponentStats.Keys.Any(key => key.StartsWith("General performance: ")), ["scopeVerified"] = CoachTeamStats.HasScopedMetrics(opponentStats),
        };
        oppSnap["missing"] = missing;
        oppSnap["screenHint"] = null;
        oppSnap["analysisBasis"] = CoachOpponentAdvice.EvidencePolicy;
        oppSnap["notReadByThisVersion"] = new[] { "suspensions (no reliable current-ban binding found)" };
        CoachPlugin.L.LogInfo($"Opponent: {oppName ?? "?"} ({fixture["location"] ?? "?"}, {fixture["competition"] ?? "?"}); read {oppD.Count(kv => kv.Key != "source" && kv.Key != "readAt")} fields; missing [{string.Join(", ", missing)}]");
        nextOppRetry = Time.unscaledTime + 60f;
        if (standalone) { BuildSquadContext(); WriteSnapshotFiles(); }
    }

    void OpponentTick()
    {   // retry while FM has not built the report yet (cheap: ~35 requests); only between other jobs
        if (snapJob != null || snapTeamTv == null || oppComplete || Time.unscaledTime < nextOppRetry) return;
        nextOppRetry = Time.unscaledTime + 60f;   // also nudged to ~3 s when the manager changes screen (UpdateScreenKey)
        snapReason = "opponent retry"; snapForTest = false; snapJob = OpponentJob();
    }

    void WriteOpponentFile(string dir)
    {
        try { File.WriteAllText(Path.Combine(dir, "opponent.json"), JsonSerializer.Serialize(oppSnap, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }), Encoding.UTF8); }
        catch (Exception ex) { LogOnce("OpponentWrite", ex); }
    }

    static string J(object o) => o is IEnumerable<string> l ? string.Join("; ", l) : o?.ToString();

    void AppendOpponentContext(StringBuilder sb)
    {
        if (oppSnap.Count == 0) { sb.AppendLine("OPPONENT: next fixture not read yet."); return; }
        var fx = oppSnap["fixture"] as Dictionary<string, object>; var o = oppSnap["opponent"] as Dictionary<string, object>; var u = oppSnap["ours"] as Dictionary<string, object>;
        string G(Dictionary<string, object> d, string k) => d != null && d.TryGetValue(k, out var v) ? CoachReadableText.GameValue(J(v)) : null;
        sb.Append("OPPONENT: ").Append(G(fx, "opponent") ?? "?").Append(" | ").Append(G(fx, "location") ?? "?").Append(" | ").Append(G(fx, "competition") ?? "?")
          .Append(" | ").Append(G(fx, "date") ?? G(fx, "when") ?? "date ?").AppendLine();
        sb.AppendLine(CoachOpponentAdvice.EvidencePolicy);
        var parts = new List<string>();
        void P(string label, string k) { var v = G(o, k); if (!string.IsNullOrWhiteSpace(v)) parts.Add(label + " " + Trunc(v, 160)); }
        P("league pos", "leaguePosition"); P("usual formation", "mostUsedFormation"); P("expected formation", "expectedFormationName"); P("expected formations", "expectedFormations");
        P("expected mentality", "expectedMentality"); P("style", "expectedStyle"); P("form", "recentForm"); P("performance", "recentPerformance");
        P("STRENGTHS", "strengths"); P("WEAKNESSES", "weaknesses"); P("key player", "keyPlayer"); P("top scorer", "topScorer"); P("best rated", "bestRated"); P("injured", "injuriesCount");
        if (parts.Count > 0) sb.AppendLine("  them: " + string.Join("; ", parts));
        var us = new List<string>();
        void U(string label, string k) { var v = G(u, k); if (!string.IsNullOrWhiteSpace(v)) us.Add(label + " " + Trunc(v, 160)); }
        U("form", "recentForm"); U("performance", "recentPerformance"); U("staff threat view", "threatAssessment"); U("injured", "injuriesCount"); U("returns", "injuryReturnsNextFixture"); U("not fully fit", "notFullyFit");
        if (us.Count > 0) sb.AppendLine("  us: " + string.Join("; ", us));
        if (oppSnap["missing"] is List<string> miss) CoachOpponentAdvice.AppendLimits(sb, miss);
        if (oppSnap.TryGetValue("teamStatistics", out var statsValue) && statsValue is Dictionary<string, object> stats &&
            stats.GetValueOrDefault("fields") is Dictionary<string, string> fields && fields.Count > 0)
        {
            sb.AppendLine("OPPONENT TEAM STATISTICS (" + G(stats, "forOpponent") + "; source " + G(stats, "source") + "; read " + G(stats, "readAt") + "):");
            sb.AppendLine(G(stats, "scope"));
            foreach (var field in fields)
                if (CoachReadableText.GameValue(field.Value) is string value) sb.AppendLine("  " + field.Key + "=" + value);
            sb.AppendLine("Use these as evidence for tactical suggestions only when metric, season and competition scope are clear; distinguish observed statistics from suggested explanations. Compare with our loaded tactics and attributes; do not recommend instructions already set.");
        }
        else sb.AppendLine("OPPONENT TEAM STATISTICS: unavailable; do not invent goals, xG, possession, passing or shooting figures.");
        sb.AppendLine("  Suspensions are not read by this version.");
    }
}
