// FM Coach v0.7 - data layer part 2: sources, triggers and the time-sliced snapshot job (squad + tactic).
// READ-ONLY. Property names come from FM27's own property registry and are tried defensively; results are probed/logged.
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using UnityEngine;
using SI.Bindable;
using SI.Core;
using DateTime = System.DateTime;

public partial class CoachBehaviour
{
    TypedValue humanTv, gameTv; string humanSrcPath, gameSrcPath;

    // Global 'Human' (PersonReference), 'GameStateCache/Manager' and 'game' (GameReference) nodes in the binding tree.
    void FindDataSources(Bindings b)
    {
        try
        {
            var rk = Bindings.RootKey;
            var root = b.GetNode(ref rk)?.TryCast<Bindings.Node>();
            if (root?.FirstChild == null) { AcceptDataSources(null, null, null, null); return; }
            int budget = 30000, bestH = 99, bestG = 99; TypedValue h = null, g = null; string hp = null, gp = null;
            void Walk(Bindings.Node n, int depth, string path)
            {
                for (var c = n; c != null && budget > 0; c = c.NextSibling)
                {
                    budget--;
                    string nm = null; try { nm = c.Name; } catch { }
                    if (nm == "Human" || nm == "game" || nm == "Manager")
                    {
                        TypedValue v = null; try { v = c.Value; } catch { }
                        string cls = v != null && !v.IsNull ? ClassOf(v) : null;
                        if (cls != null)
                        {
                            bool person = cls.EndsWith("PersonReference") && (nm == "Human" || path.Contains("GameStateCache"));
                            if (person && depth < bestH) { h = Copy(v); bestH = depth; hp = path + "/" + nm; }
                            if (nm == "game" && cls.EndsWith("GameReference") && depth < bestG) { g = Copy(v); bestG = depth; gp = path + "/" + nm; }
                        }
                    }
                    if (depth < 5 && c.FirstChild != null) Walk(c.FirstChild, depth + 1, path + "/" + nm);
                }
            }
            Walk(root.FirstChild, 0, "");
            AcceptDataSources(h, g, hp, gp);
        }
        catch (Exception ex) { LogOnce("FindDataSources", ex); }
    }

    static TypedValue Copy(TypedValue v) { try { return TypedValue.CreateCopyFrom(v); } catch { return v; } }

    readonly Dictionary<string, Bindings.Key> screenTacticScopes = new();

    // Tactic-ish references currently bound on screen (Tactics / pre-match screens).
    List<(string path, string cls, TypedValue tv)> FindScreenTacticNodes(Bindings b, int max)
    {
        var res = new List<(string path, string cls, TypedValue tv)>();
        screenTacticScopes.Clear();
        try
        {
            var rk = Bindings.RootKey;
            var root = b.GetNode(ref rk)?.TryCast<Bindings.Node>();
            if (root?.FirstChild == null) return res;
            int budget = 30000; var seen = new HashSet<string>();
            void Walk(Bindings.Node n, int depth, string path)
            {
                for (var c = n; c != null && budget > 0 && res.Count < 76; c = c.NextSibling)
                {
                    budget--;
                    string nm = null; try { nm = c.Name; } catch { }
                    if (depth == 0 && (nm ?? "").StartsWith("OSR-", StringComparison.Ordinal)) continue; // one-shot request roots are our own queries, not screen data
                    TypedValue v = null; try { v = c.Value; } catch { }
                    if (v != null && !v.IsNull)
                    {
                        string s = null; try { s = v.AsString(); } catch { }
                        if (s != null && s.StartsWith("FM.UI.") && seen.Add(s + "|" + path + "/" + nm))
                        {
                            bool isTac = s.Contains("TeamTactic") || s.Contains("MatchTactic");
                            bool isPos = s.Contains("TacticsPosition") || s.Contains("TacticsTeamSelection");
                            if (isTac && res.Count(x => x.Item2.Contains("Tactic") && !x.Item2.Contains("Tactics")) < 16) { var p = path + "/" + nm; res.Add((p, s, Copy(v))); screenTacticScopes[p] = c.Key; }
                            else if (isPos && res.Count(x => x.Item2.Contains("Tactics")) < 60) { var p = path + "/" + nm; res.Add((p, s, Copy(v))); screenTacticScopes[p] = c.Key; }
                        }
                    }
                    if (depth < 7 && c.FirstChild != null) Walk(c.FirstChild, depth + 1, path + "/" + nm);
                }
            }
            Walk(root.FirstChild, 0, "");
        }
        catch (Exception ex) { LogOnce("FindScreenTacticNodes", ex); }
        int Score((string path, string cls, TypedValue tv) node)
        {
            var scope = screenTacticScopes.GetValueOrDefault(node.path);
            int score = node.path.EndsWith("/Tactic") ? 20 : 0;
            if (scope.m_key != 0)
            {
                if (ValueText(Read(b, scope, "MentalityString")) is string m && !m.StartsWith("FM.UI.")) score += 100;
                if (int.TryParse(ValueText(Read(b, scope, "CurrentTacticSlot")), out _)) score += 50;
            }
            if (node.cls.Contains("MatchTactic")) score += 10;
            return score;
        }
        return res.Where(n => !n.cls.Contains("Tactics")).OrderByDescending(Score).Take(max)
            .Concat(res.Where(n => n.cls.Contains("Tactics")).Take(60)).ToList();
    }

    // ---- candidate property names (FM27 registry; v0.7.1: aligned with the catalogues in probe_20261119-005812) ----
    static readonly string[] TeamPathCands = { "Team", "Club.MainTeam" };
    static readonly string[] ClubPathCands = { "Club", "Team.Club", "Team.TeamContainer" };
    static readonly string[] TeamListCands = { "Players", "PlayersNoVirtuals", "AvailableFirstTeamPlayers" };
    static readonly string[] TacticCands = { "Tactic", "BestXITacticDefault", "TeamReportLastTactic" };
    static readonly string[] TeamValueCands = { "Name", "UniqueId", "LeaguePosition", "LeaguePositionString", "TeamAverageAbility", "BestXITeamAbility", "TacticalStyle", "MostLikelyTacticalStyle",
        "NextMatchOpponent.Name", "NextMatchCompetition.Name", "NextMatchDaysText", "NextMatchLocationString", "PlayersAtRiskShortString" };
    static readonly string[] ClubValueCands = { "Name", "UniqueId", "UniqueID", "TransferBudget", "TransferBudgetWithText", "TransferBudgetString", "AvailableTransferBudgetWithText", "WageBudget", "RemainingWageBudget", "RemainingWageBudgetWithText", "CurrentWageSpendingWithText", "CurrentWageCurrrentlySpendingWithText" };
    static readonly string[] GameValueCands = { "CurrentGameDateString", "CurrentGameDate" };

    // team instructions: canonical key, phase, candidate property names (TacticReference; values are TacticTeamInstructionOptions ids)
    static readonly (string key, string phase, string[] props)[] TeamInstrCands =
    {
        ("mentality", "general", new[] { "MentalityString", "Mentality" }),
        ("formation", "general", new[] { "FormationName", "FormationNameNoWords", "Formation" }),
        ("tacticalStyle", "general", new[] { "TacticalStyle", "TacticCreatorTacticalStyle" }),
        ("passingDirectness", "inPossession", new[] { "PassingDirectness" }),
        ("tempo", "inPossession", new[] { "Tempo" }),
        ("width", "inPossession", new[] { "TacticWidth" }),
        ("approachPlay", "inPossession", new[] { "BuildUpStrategy" }),
        ("focusPlay", "inPossession", new[] { "ProgressThrough" }),
        ("passReception", "inPossession", new[] { "PassReception" }),
        ("finalThird", "inPossession", new[] { "Patience" }),
        ("shooting", "inPossession", new[] { "ShotsFromDistance" }),
        ("crossingStyle", "inPossession", new[] { "CrossingStyle" }),
        ("supportingRuns", "inPossession", new[] { "SupportingRuns" }),
        ("creativeFreedom", "inPossession", new[] { "CreativeFreedom" }),
        ("dribbling", "inPossession", new[] { "Dribbling" }),
        ("timeWasting", "inPossession", new[] { "TimeWasting" }),
        ("onWinningBall", "transition", new[] { "AttackingTransition" }),
        ("onLosingBall", "transition", new[] { "DefensiveTransition" }),
        ("goalKicks", "transition", new[] { "GoalKicks" }),
        ("gkDistributionTarget", "transition", new[] { "GoalkeeperDistributionTarget" }),
        ("gkDistributionSpeed", "transition", new[] { "GoalkeeperDistributionSpeed" }),
        ("lineOfEngagement", "outOfPossession", new[] { "LineOfEngagement" }),
        ("defensiveLine", "outOfPossession", new[] { "DefensiveLine" }),
        ("defensiveLineBehaviour", "outOfPossession", new[] { "DefensiveLineBehaviour" }),
        ("pressingIntensity", "outOfPossession", new[] { "PressingTrigger", "PressingIntensity" }),
        ("pressingTrap", "outOfPossession", new[] { "PressingTrap" }),
        ("crossEngagement", "outOfPossession", new[] { "CrossEngagement" }),
        ("tackling", "outOfPossession", new[] { "Tackling" }),
        ("preventShortGKDistribution", "outOfPossession", new[] { "PreventGKShort", "PreventShortGKDistribution" }),
    };
    static readonly string[] TacticExtraValues = { "Name", "CurrentTacticSlotName", "CurrentTacticSlot", "TacticSlot1Name", "TacticSlot2Name", "TacticSlot3Name", "FormationNameNoWords",
        "TacticalStyleIntensityString", "TacticalStyleComplexityString", "TacticIconName", "SupportingRunsSecondary", "HasTactic", "AllTacticPositionsFilled", "IsTeamSelectionValid", "TotalPlayersSelectedString", "GoalkeeperUpForSetPieces" };
    static readonly string[] TacticListCands = { "TacticalPositions", "TacticalPositionsCombined", "TeamSelections", "FirstTeamPlayerSlots", "InPossessionInstructionList", "OutOfPossessionInstructionList",
        "TacticActiveTweaks", "StoredTeamInstructions" };

    static readonly string[] PlayerExtraProps = { "Name", "UniqueId", "Age", "PositionCombinedString", "NaturalPositionShortString", "BestPositionShortString", "BestRole",
        "CurrentAbilityStarRange", "PotentialAbilityStarRange", "CoachCurrentAbilityStarRange", "CoachPotentialAbilityStarRange",
        "ConditionString", "Condition", "Sharpness", "MoraleString", "Morale", "IsInjured", "CurrentInjury", "Height",
        "Appearances", "Goals", "Assists", "AverageRating", "ClubAppearancesThisSeason", "ClubGoalsThisSeason", "ClubAssistsThisSeason", "ClubAverageRatingThisSeason", "SeasonStatsCombinedRow", "SeasonStats", "Club.UniqueId", "LoanClub.UniqueId", "Team.UniqueId", "ContextContractWeeklyWage", "TransferValueRange",
        "TacticsPositionName", "TacticsRoleName", "TacticsRoleNameWithIpOop", "TacticsDutyName", "TeamSelectionIndex", "TeamSelectedPositionStringFromPlayer", "PlayerUsesPersonalisedInstructions" };
    static IEnumerable<string> PlayerProps() => PlayerExtraProps.Concat(AttrGroups.SelectMany(g => g.attrs.Select(a => a.prop))).Distinct();

    // ---- snapshot state ----
    readonly Dictionary<string, object> tacticSnap = new();
    readonly Dictionary<string, string> teamSnap = new();
    string snapshotStatus = "not read yet"; DateTime snapshotAt; string lastGameDate, gameDateKey; readonly HashSet<string> deadPlayerProps = new();
    IEnumerator snapJob; string snapReason; bool snapForTest, snapTestRunning;
    float nextDataSourceSearch, nextDateCheck, nextScheduledSnap, nextTacticScreenCheck;
    Req slotPollReq; string lastMergedActiveSlotRaw;
    bool probeWritten, tacticsProbeWritten; string probeFile; readonly HashSet<string> tacticScreensSeen = new();
    readonly StringBuilder probe = new();
    Req dateReq;

    // ---- driver, called every frame from Update() ----
    float lastDateSnap = -999f;
    void DataTick()
    {
        if (!DataEnabled) return;
        var bs = BS();
        if (bs == null) { AcceptDataSources(null, null, null, null); return; }
        dataB = bs.Cast<Bindings>();
        float now = Time.unscaledTime;
        if (now >= nextDataSourceSearch) { nextDataSourceSearch = now + 3f; FindDataSources(dataB); }
        PumpRequests();
        PollSessionIdentity();
        if (humanTv == null) return;

        try
        {
            var trig = Path.Combine(TestDir, "snapshot.txt");
            if (Time.frameCount % 60 == 0 && File.Exists(trig)) { File.Delete(trig); StartSnapshot("test hook snapshot.txt", true); }
        }
        catch (Exception ex) { LogOnce("SnapshotHook", ex); }

        if (snapJob == null && nextScheduledSnap == 0) { nextScheduledSnap = now + RefreshMinutes * 60f; StartSnapshot("save loaded", false); }
        if (snapJob == null && now >= nextScheduledSnap) { nextScheduledSnap = now + RefreshMinutes * 60f; StartSnapshot("every " + RefreshMinutes + " min", false); }

        if (snapJob == null && gameTv != null && now >= nextDateCheck && dateReq == null) { nextDateCheck = now + 20f; dateReq = Ask(gameTv, "CurrentGameDate", 5f); }
        if (dateReq != null && dateReq.Done)
        {
            var d = dateReq.Got ? Clean(dateReq.Str) : null; dateReq = null;
            if (d != null && gameDateKey != null && d != gameDateKey && snapJob == null && now - lastDateSnap < 120f) { /* throttle while the manager clicks Continue repeatedly; re-checked in 20 s */ }
            else if (d != null && gameDateKey != null && d != gameDateKey && snapJob == null)
            { lastDateSnap = now; CoachPlugin.L.LogInfo("Game date changed " + gameDateKey + " -> " + d + "; refreshing squad/tactic"); gameDateKey = d; StartSnapshot("game date changed", false); }
            else if (d != null) gameDateKey = d;
        }

        if (now >= nextTacticScreenCheck)
        {
            nextTacticScreenCheck = now + 3f;
            var nodes = FindScreenTacticNodes(dataB, 4);
            var key = string.Join(",", nodes.Where(n => !n.cls.Contains("Tactics")).Select(n => n.cls).Distinct().OrderBy(x => x));
            if (nodes.Any(n => n.cls.Contains("TeamTactic") && n.path.IndexOf("Message", StringComparison.OrdinalIgnoreCase) < 0)) lastTacticScreenSeen = now;   // v0.7.3 screen key
            if (key.Length > 0 && tacticScreensSeen.Contains(key))
            {   // v0.7.2: same screen, but the manager may have switched tactic slot -> re-merge
                if (slotPollReq == null) { var pn = nodes.FirstOrDefault(n => n.cls.Contains("TeamTactic")); if (pn.tv != null) slotPollReq = Ask(pn.tv, "CurrentTacticSlot", 4f); }
                else if (slotPollReq.Done)
                {
                    var cur = slotPollReq.Got ? ValueText(slotPollReq.Str) : null; slotPollReq = null;
                    bool slotChanged = cur != null && lastMergedActiveSlotRaw != null && cur != lastMergedActiveSlotRaw;
                    if (slotChanged || now - lastTacticMergeAt >= 15f)
                    {
                        if (slotChanged) InvalidateCurrentTactic(cur, "selected tactic changed; refreshing");
                        CoachPlugin.L.LogInfo(slotChanged
                            ? "Tactic slot switched on the Tactics screen (" + lastMergedActiveSlotRaw + " -> " + cur + "); re-reading"
                            : "Tactics screen: refreshing current instructions");
                        tacticScreensSeen.Remove(key);
                    }
                }
            }
            if (snapJob == null && key.Length > 0 && tacticScreensSeen.Add(key))
            {
                CoachPlugin.L.LogInfo("Tactic data on screen (" + key + "); merging into the tactic snapshot");
                snapReason = "tactics screen"; snapJob = TacticScreenJob(nodes);
            }
            if (key.Length == 0) tacticScreensSeen.Clear();
        }

        OpponentTick();
        if (snapJob != null)
        {
            try { if (!snapJob.MoveNext()) { snapJob = null; if (snapForTest && !snapTestRunning) StartSnapshot("test hook snapshot.txt (queued)", true); } }
            catch (Exception ex) { snapJob = null; refreshingMissing = false; LogOnce("SnapshotJob", ex); fullSnapshotFinished = true; loadingStage = "Read failed; retrying automatically"; snapshotStatus = loadingStage; nextFullRetry = now + 30f; }
        }
    }

    void StartSnapshot(string reason, bool forTest)
    {
        if (snapJob != null) { if (forTest) snapForTest = true; return; }
        snapReason = reason; snapForTest = forTest; snapTestRunning = forTest; refreshingMissing = false;
        CoachPlugin.L.LogInfo("Squad/tactic snapshot started (" + reason + ")");
        snapJob = SnapshotJob();
    }

    // Nested IEnumerators are flattened so 'yield return Sub()' waits for the sub-routine.
    static IEnumerator Flatten(IEnumerator e) => CoachCoroutine.Flatten(e);

    IEnumerator SnapshotJob() => Flatten(SnapshotSteps());

    IEnumerator SnapshotSteps()
    {
        var t0 = Time.realtimeSinceStartup; int issued0 = reqIssued;
        fullSnapshotFinished = false;
        SetLoadingStage("Connecting to your club");
        bool P = ProbeEnabled && !probeWritten;
        if (P) { probe.Clear(); probe.AppendLine("# FM Coach v0.7.3 probe " + DateTime.Now.ToString("s")); probe.AppendLine("human source: " + humanSrcPath + "  game source: " + gameSrcPath); }

        // 1) team + club from the human manager. Right after a save loads FM may not answer yet: retry (v0.7.1).
        List<Req> teamReqs = null, clubReqs = null; Req nameReq = null, teamR = null, clubR = null;
        for (int attempt = 1; attempt <= 4; attempt++)
        {
            teamReqs = TeamPathCands.Select(p => Ask(humanTv, p, 8f, true, "human")).ToList();
            clubReqs = ClubPathCands.Select(p => Ask(humanTv, p, 8f, true, "human")).ToList();
            nameReq = Ask(humanTv, "Name", 8f);
            yield return WaitAll(teamReqs.Concat(clubReqs).Append(nameReq), 9f);
            teamR = teamReqs.FirstOrDefault(r => r.Tv != null && (ClassOf(r.Tv) ?? "").EndsWith("TeamReference"));
            clubR = clubReqs.FirstOrDefault(r => r.Tv != null && (ClassOf(r.Tv) ?? "").EndsWith("ClubReference"));
            if (teamR != null) break;
            CoachPlugin.L.LogInfo($"Snapshot: team not answered yet (attempt {attempt}/4, {ReqStats()}); retrying in 6 s");
            snapshotStatus = "waiting for the save (attempt " + attempt + ")";
            float until = Time.unscaledTime + 6f; while (Time.unscaledTime < until) yield return null;
        }
        if (nameReq?.Got == true && string.IsNullOrWhiteSpace(humanManager)) humanManager = Clean(nameReq.Str);
        if (P)
        {
            probe.AppendLine("\n## human -> team/club candidates");
            foreach (var r in teamReqs.Concat(clubReqs)) probe.AppendLine($"  Human.{r.Path} = {ReqText(r)}{(r.Got ? "  [" + ClassOf(r.Tv) + "]" : "")}");
        }
        if (teamR == null)
        {
            SetLoadingStage("Team unavailable; retrying automatically");
            fullSnapshotFinished = true;
            CoachPlugin.L.LogWarning("Snapshot: could not resolve the manager's team from " + humanSrcPath + " (tried " + string.Join(", ", TeamPathCands) + "; " + ReqStats() + ")");
            FinishProbe(); FinishSnapshot(t0, issued0); yield break;
        }
        var teamTv = teamR.Tv; var clubTv = clubR?.Tv; fullTeamTv = teamTv; snapTeamTv = teamTv;
        CoachPlugin.L.LogInfo("Snapshot: team via Human." + teamR.Path + " (" + teamR.Str + "), club via " + (clubR != null ? "Human." + clubR.Path : "(none)"));

        // 2) team / club / game values, squad list and tactic candidates
        var newTeam = new Dictionary<string, string>();
        var tv = TeamValueCands.Select(p => Ask(teamTv, p, 8f, false, "team")).ToList();
        var cv = clubTv != null ? ClubValueCands.Select(p => Ask(clubTv, p, 8f, false, "club")).ToList() : new List<Req>();
        var gv = gameTv != null ? GameValueCands.Select(p => Ask(gameTv, p, 8f, false, "game")).ToList() : new List<Req>();
        var listReqs = TeamListCands.Select(p => Ask(teamTv, p, 10f, true, "team")).ToList();
        var tacReqs = TacticCands.Select(p => Ask(teamTv, p, 35f, true, "team")).ToList();
        yield return WaitAll(tv.Concat(cv).Concat(gv).Concat(listReqs), 10f);
        foreach (var r in tv.Concat(cv).Concat(gv).Where(r => r.Got)) { var v = ValueText(r.Str); if (v != null) newTeam[r.Owner + "." + r.Path] = v; }
        teamSnap.Clear(); foreach (var kv in newTeam) teamSnap[kv.Key] = kv.Value;
        if (teamSnap.TryGetValue("club.UniqueId", out var clubId)) humanClubId = clubId;
        if (teamSnap.TryGetValue("game.CurrentGameDateString", out var gd)) lastGameDate = gd;
        if (gv.FirstOrDefault(r => r.Path == "CurrentGameDate" && r.Got) is Req gdr) gameDateKey = Clean(gdr.Str);
        foreach (var k in new[] { "club.RemainingWageBudgetWithText", "club.CurrentWageSpendingWithText", "club.CurrentWageCurrrentlySpendingWithText" })
            if (teamSnap.TryGetValue(k, out var mv) && CurrencyOf(mv) is string cur && cur != gameCurrencyName) { gameCurrencyName = cur; break; }
        if (P)
        {
            probe.AppendLine("\n## team/club/game values (team class " + ClassOf(teamTv) + ")");
            foreach (var r in tv.Concat(cv).Concat(gv)) probe.AppendLine($"  {r.Owner}.{r.Path} = {ReqText(r)}");
            probe.AppendLine("\n## team list candidates");
            foreach (var r in listReqs) probe.AppendLine($"  team.{r.Path} = {ReqText(r)}{(r.Got ? " count=" + ListCount(r.Tv) : "")}");
            probe.AppendLine("\n## tactic candidates");
            foreach (var r in tacReqs) probe.AppendLine($"  team.{r.Path} = {ReqText(r)}{(r.Got ? " [" + ClassOf(r.Tv) + "]" : "")}");
            foreach (var cls in new[] { ClassOf(teamTv), clubTv != null ? ClassOf(clubTv) : null, "FM.UI.PersonReference", "FM.UI.TacticReference", "FM.UI.TeamTacticReference", "FM.UI.MatchTacticReference",
                "FM.UI.TacticsPositionReference", "FM.UI.TacticsRoleReference", "FM.UI.TacticsTeamSelectionReference", "FM.UI.GameReference" }.Where(c => c != null).Distinct())
            {
                var ps = PropsOf(cls);
                probe.AppendLine($"\n## properties of {cls} ({ps.Count})");
                foreach (var p in ps.OrderBy(p => p.Name)) probe.AppendLine("  " + p.Name + "  : " + p.Kind);
            }
        }


        // 4) squad (requests are queued and rate-limited by the engine: <=120 issued per frame, <=700 live)
        var listR = listReqs.FirstOrDefault(r => r.Tv != null && ListCount(r.Tv) > 0) ?? listReqs.FirstOrDefault(r => r.Tv != null && ListCount(r.Tv) == 0);
        squadListRead = listR != null;
        yield return ReadRegisteredPlayers(teamTv);
        yield return ReadTeamSeasonStats(teamTv);
        excludedInjured = excludedLoaned = excludedUnregistered = excludedUnknown = excludedNotFirstTeam = 0;
        var newSquad = new List<SquadPlayer>();
        if (listR != null)
        {
            var items = ListItems(listR.Tv, Math.Max(0, ListCount(listR.Tv)));
            squadCandidates = items.Count;
            SetLoadingStage("Checking availability and first-team assignment for " + items.Count + " candidates");
            var identityProps = new[] { "Name", "UniqueId", "IsInjured", "Club.UniqueId", "LoanClub.UniqueId", "Team.UniqueId" };
            var candidates = items.Select(item => (source: item, player: new SquadPlayer(), requests:
                identityProps.Select(p => Ask(item, p, 6f, false, "squad eligibility")).ToList())).ToList();
            yield return WaitAll(candidates.SelectMany(c => c.requests), 8f);
            var selected = new List<(TypedValue source, SquadPlayer player)>();
            foreach (var candidate in candidates)
            {
                foreach (var request in candidate.requests.Where(r => r.Got)) candidate.player.raw[request.Path] = request.Str;
                FillPlayer(candidate.player);
                if (string.IsNullOrWhiteSpace(candidate.player.name) || candidate.player.name.StartsWith("FM.UI.")) excludedUnknown++;
                else if (IncludeSnapshotPlayer(candidate.player)) selected.Add((candidate.source, candidate.player));
            }
            SetLoadingStage("Reading attributes and current-season stats for " + selected.Count + " registered, available first-team players");
            var props = PlayerProps().Except(identityProps).ToArray();
            CoachPlugin.L.LogInfo("Snapshot: candidate list team." + listR.Path + " -> " + items.Count + " candidates, " + selected.Count + " eligible first-team players; " + props.Length + " fields each");
            var all = new List<(SquadPlayer sp, List<Req> rs)>();
            for (int i = 0; i < selected.Count; i++)
            {
                all.Add((selected[i].player, props.Select(p => Ask(selected[i].source, p, 10f, p is "SeasonStatsCombinedRow" or "SeasonStats", "player")).ToList()));
                if (i % 4 == 3) yield return null;
            }
            yield return WaitAll(all.SelectMany(a => a.rs), 12f);
            foreach (var (sp, rs) in all)
            {
                foreach (var r in rs.Where(r => r.Got)) sp.raw[r.Path] = r.Str;
                FillPlayer(sp); newSquad.Add(sp);
                var stats = rs.FirstOrDefault(r => r.Path == "SeasonStatsCombinedRow" && r.Tv != null) ?? rs.FirstOrDefault(r => r.Path == "SeasonStats" && r.Tv != null);
                if (stats != null) yield return ReadStatBlock(stats.Tv, stats.Path, sp.seasonReport, 0);
            }
            if (P && all.Count > 0)
            {
                probe.AppendLine("\n## first player raw values (" + (all[0].sp.name ?? "?") + ")");
                foreach (var r in all[0].rs) probe.AppendLine($"  {r.Path} = {ReqText(r)}");
                probe.AppendLine($"  -> players with a name: {newSquad.Count}/{all.Count}; with >=8 attributes: {newSquad.Count(s => s.attrs.Count >= 8)}; with a tactic slot: {newSquad.Count(s => s.slotPosition != null)}");
                foreach (var sp in newSquad.Take(40)) probe.AppendLine($"  slot? {sp.name}: TacticsPositionName={Raw(sp, "TacticsPositionName")} TacticsRoleName={Raw(sp, "TacticsRoleName")} WithIpOop={Raw(sp, "TacticsRoleNameWithIpOop")} Duty={Raw(sp, "TacticsDutyName")} TSI={Raw(sp, "TeamSelectionIndex")} SelPos={Raw(sp, "TeamSelectedPositionStringFromPlayer")}");
            }
        }
        else CoachPlugin.L.LogWarning("Snapshot: no squad list on the team (tried " + string.Join(", ", listReqs.Select(r => r.Path + "=" + ReqText(r))) + ")");
        squad.Clear(); squad.AddRange(newSquad); squadReadAt = DateTime.Now;
        foreach (var sp in squad) if (gameCurrencyName == null && (CurrencyOf(sp.wage) ?? CurrencyOf(sp.value)) is string c2) gameCurrencyName = c2;

        // 3) tactic (v0.7.1: evaluated after the squad - FM answers Team.Tactic late or only once the tactic is loaded)
        if (!tacReqs.Any(r => r.Tv != null && (ClassOf(r.Tv) ?? "").Contains("Tactic"))) yield return WaitAll(tacReqs, 15f);
        var tacR = tacReqs.FirstOrDefault(r => r.Tv != null && (ClassOf(r.Tv) ?? "").Contains("Tactic"));
        var prevTactic = tacticSnap.ContainsKey("screenMerged") ? new Dictionary<string, object>(tacticSnap) : null;
        tacticSnap.Clear();
        tacticSnap["source"] = tacR != null ? "team." + tacR.Path + " [" + ClassOf(tacR.Tv) + "]" : "(none)";
        if (tacR != null) yield return CrawlTactic(tacR.Tv, "team." + tacR.Path, P);
        else
        {
            CoachPlugin.L.LogWarning("Snapshot: no tactic object on the team (tried " + string.Join(", ", TacticCands) + "; " + string.Join(", ", tacReqs.Select(r => r.Path + "=" + ReqText(r))) + ")");
            if (prevTactic != null)
            {   // keep what the Tactics screen gave us instead of wiping it
                foreach (var kv in prevTactic) tacticSnap[kv.Key] = kv.Value;
                CoachPlugin.L.LogInfo("Snapshot: keeping the tactic read from the Tactics screen at " + prevTactic["screenMerged"]);
            }
            else if (tacReqs.FirstOrDefault(r => r.Got && ValueText(r.Str) != null) is Req nameR)
            {   // e.g. BestXITacticDefault = {PropertyValue=0; DisplayValue=AK-MAIN}: at least the tactic's name
                tacticSnap["name"] = ValueText(nameR.Str); tacticSnap["nameSource"] = "team." + nameR.Path;
                tacticSnap["source"] = "team." + nameR.Path + " (name only; open the Tactics screen once for the details)";
            }
        }
        if (P)
        {
            probe.AppendLine("\n## tactic candidates (after the squad)");
            foreach (var r in tacReqs) probe.AppendLine($"  team.{r.Path} = {ReqText(r)}{(r.Got ? " [" + ClassOf(r.Tv) + "]" : "")}");
        }

        // 4b) next opponent (v0.7.3)
        yield return OpponentSteps(false);

        yield return ReadAllSavedTactics(teamTv);

        // 5) derived data + files
        snapshotAt = DateTime.Now;
        BuildTacticSlotsFromPlayers(preferScreen: tacticSnap.ContainsKey("screenMerged"));
        ComputeProfileAndHints();
        fullSnapshotFinished = true; loadingStage = "Startup snapshot complete";
        BuildSquadContext();
        WriteSnapshotFiles();
        FinishProbe();
        FinishSnapshot(t0, issued0);
    }

    static string Raw(SquadPlayer sp, string k) => sp.raw.TryGetValue(k, out var v) ? Trunc(Clean(v), 60) : "-";
    static string ReqText(Req r) => r.Got ? Trunc(Clean(r.Str), 160) : "(no value: " + (r.Empty ? "empty" : r.Err ?? "pending") + ")";

    // FM value -> text for files/context: drops unresolved reference echoes and raw structs (e.g. "GameDate { Data1 = ... }").
    static readonly Regex StructRx = new(@"^\w+ \{ .*\}$");
    static readonly Regex DisplayValueRx = new(@"[{;]\s*DisplayValue=([^;}]+)");
    internal static string ValueText(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var display = raw.StartsWith("{") ? DisplayValueRx.Match(raw) : null;
        var v = Clean(display is { Success: true } ? display.Groups[1].Value.Trim() : raw);
        if (string.IsNullOrWhiteSpace(v) || (v.StartsWith("FM.UI.") && v.EndsWith("Reference")) || StructRx.IsMatch(v) || v.StartsWith("System.Collections")) return null;
        return CoachReadableText.GameValue(v);
    }

    static readonly Regex ListNameRx = new("Instruction|Position|Selection|Slot|Role|Player|Formation|Tweak", RegexOptions.IgnoreCase);
    static readonly Regex PosListRx = new("TacticalPositions|FirstTeamPlayerSlots|TeamSelections", RegexOptions.IgnoreCase);
    static readonly string[] SlotItemProps = { "Index", "Position", "PositionRole", "IsInPossession", "IsGoalkeeper", "TeamSelectionIndex", "PlayerIndex", "Name",
        "Person.Name", "Player.Name", "MatchPlayer.Name", "Person.TacticsPositionName", "Person.TacticsRoleName", "Person.TacticsDutyName", "Role", "Duty", "TacticsRoleName",
        "IPRole", "OOPRole", "IPRoleOrBest", "OOPRoleOrBest", "InPossessionRole", "OutOfPossessionRole", "IPPosition", "OOPPosition", "RoleName", "PositionRoleName" };
    static readonly Regex RefFollowRx = new("^(Player|Person|MatchPlayer)$", RegexOptions.IgnoreCase);

    // Curated value properties of the tactic object + its position/selection/instruction lists one level deep.
    IEnumerator CrawlTactic(TypedValue tac, string srcLabel, bool P, StringBuilder sink = null, Dictionary<string, object> dst = null, Dictionary<string, TypedValue> context = null, Bindings.Key scope = default)
    {
        dst ??= tacticSnap;
        context = context == null ? new Dictionary<string, TypedValue>() : new Dictionary<string, TypedValue>(context);
        if (scope.m_key == 0 && !context.ContainsKey("InPossession")) context["InPossession"] = TypedValue.Create(true);
        var pr = sink ?? probe; if (sink != null) P = true;
        var cls = ClassOf(tac);
        var props = PropsOf(tac);
        var names = TeamInstrCands.SelectMany(c => c.props).Concat(TacticExtraValues).Distinct().ToList();
        var reqs = names.Select(n => Ask(tac, n, 10f, false, "tactic", context, scope)).ToList();
        var listNames = TacticListCands.Concat(props.Where(p => p.IsList && ListNameRx.IsMatch(p.Name) && !p.Name.StartsWith("Debug")).Select(p => p.Name)).Distinct().ToList();
        var lreqs = listNames.Select(n => Ask(tac, n, 10f, true, "tactic", context, scope)).ToList();
        if (sink != null) pr.AppendLine($"\n## crawl {cls} via {srcLabel}: lists asked: {string.Join(", ", listNames)}");
        yield return WaitAll(reqs.Concat(lreqs), 12f);
        var rawVals = new SortedDictionary<string, string>();
        foreach (var r in reqs.Where(r => r.Got)) { var v = ValueText(r.Str); if (v != null) rawVals[r.Path] = Trunc(v, 200); }
        var phases = new Dictionary<string, Dictionary<string, string>>();
        foreach (var (key, phase, cands) in TeamInstrCands)
            foreach (var c in cands)
                if (rawVals.TryGetValue(c, out var v))
                {
                    if (!phases.TryGetValue(phase, out var d)) phases[phase] = d = new Dictionary<string, string>();
                    var label = key == "supportingRuns" ? CoachTacticSnapshot.SupportingRunsLabel(v, rawVals.GetValueOrDefault("SupportingRunsSecondary")) : InstructionLabel(key, v);
                    if (!d.ContainsKey(key) && label != null) d[key] = label;
                }
        var lists = new Dictionary<string, List<Dictionary<string, string>>>();
        foreach (var lr in lreqs.Where(r => r.Tv != null))
        {
            var items = ListItems(lr.Tv, 40);
            if (items.Count == 0) continue;
            var icls = ClassOf(items[0]);
            var iprops = PropsOf(items[0]).Where(p => !p.Name.StartsWith("Debug") && !p.Name.StartsWith("#")).ToList();
            var ipaths = iprops.Where(p => IsValueKind(p) || Regex.IsMatch(p.Name, "Instruction|Tweak", RegexOptions.IgnoreCase)).Select(p => p.Name)
                .Concat(iprops.Where(p => !p.IsList && !IsValueKind(p) && RefFollowRx.IsMatch(p.Name)).Select(p => p.Name + ".Name")).ToList();
            // v0.7.1: items wrapped as SI.Bindable.DynamicReference have no catalogue: ask the slot fields by name
            if (ipaths.Count == 0) ipaths = PosListRx.IsMatch(lr.Path) ? new List<string>(SlotItemProps) : new List<string> { "Name" };
            if (P) for (int i = 0; i < Math.Min(items.Count, 12); i++) { string s0 = null; try { s0 = items[i].AsString(); } catch { } pr.AppendLine($"  item {lr.Path}[{i}] = {Trunc(Clean(s0) ?? "?", 160)} type={RawType(items[i])}"); }
            var per = new List<List<Req>>();
            for (int i = 0; i < items.Count; i++)
            {
                var itemProperties = PropsOf(items[i]);
                var itemPaths = itemProperties.Count == 0 ? ipaths : itemProperties
                    .Where(p => !p.Name.StartsWith("Debug") && !p.Name.StartsWith("#") && (IsValueKind(p) || Regex.IsMatch(p.Name, "Instruction|Tweak", RegexOptions.IgnoreCase)))
                    .Select(p => p.Name).Concat(itemProperties.Where(p => !p.IsList && RefFollowRx.IsMatch(p.Name)).Select(p => p.Name + ".Name")).Distinct().ToList();
                per.Add(itemPaths.Select(p => Ask(items[i], p, 10f, Regex.IsMatch(p, "Instruction|Tweak", RegexOptions.IgnoreCase), "tactic." + lr.Path, context)).ToList());
                if (i % 6 == 5) yield return null;
            }
            yield return WaitAll(per.SelectMany(x => x), 10f);
            var rows = new List<Dictionary<string, string>>();
            foreach (var rs in per)
            {
                var row = new Dictionary<string, string>();
                foreach (var r in rs.Where(r => r.Got)) { var v = ValueText(r.Str); if (v != null) row[r.Path] = Trunc(v, 120); }
                yield return ExpandPlayerInstructionRows(rs, row);
                if (row.Count > 0) rows.Add(row);
                else if (P) pr.AppendLine($"  row {lr.Path}: " + string.Join("; ", rs.Take(8).Select(r => r.Path + "=" + ReqText(r))));
            }
            if (rows.Count > 0) lists[lr.Path + " [" + icls + "]"] = rows;
            if (P) pr.AppendLine($"\n## tactic list {lr.Path}: {items.Count} items of {icls}; item props: {string.Join(", ", iprops.Select(p => p.Name + ":" + p.Kind))}");
        }
        if (P) foreach (var lr in lreqs.Where(r => r.Tv == null || ListCount(r.Tv) <= 0)) pr.AppendLine($"  list {lr.Path} = {ReqText(lr)}{(lr.Tv != null ? " type=" + RawType(lr.Tv) : "")}");
        yield return ReadPositionInstructionOverrides(tac, lists, context, scope);
        var endIdentity = new[] { "CurrentTacticSlot", "CurrentTacticSlotName", "Name" }
            .Select(property => Ask(tac, property, 5f, false, "tactic read validation", context, scope, true)).ToList();
        yield return WaitAll(endIdentity, 6f);
        string End(string path) => endIdentity.FirstOrDefault(request => request.Path == path && request.Got) is Req answer ? ValueText(answer.Str) : null;
        var startName = rawVals.GetValueOrDefault("CurrentTacticSlotName") ?? rawVals.GetValueOrDefault("Name");
        var endName = End("CurrentTacticSlotName") ?? End("Name");
        if (rawVals.TryGetValue("CurrentTacticSlot", out var startIndex) &&
            !CoachTacticSnapshot.SameTacticRead(startIndex, startName, End("CurrentTacticSlot"), endName))
        {
            CoachPlugin.L.LogInfo("Discarded tactic read: selection changed or could not be revalidated (" + startIndex + ":" + startName + " -> " + End("CurrentTacticSlot") + ":" + endName + ")");
            dst.Clear();
            InvalidateCurrentTactic(End("CurrentTacticSlot"), "tactic changed during read; retrying", true);
            tacticScreensSeen.Clear(); nextTacticScreenCheck = Time.unscaledTime + 0.5f;
            yield break;
        }
        dst["expectedInstructionKeys"] = TeamInstrCands.Where(c => props.Count == 0 || c.props.Any(n => props.Any(p => p.Name == n))).Select(c => c.key).ToList();
        dst["playerInstructionsRead"] = CoachTacticSnapshot.PlayerInstructionRowsComplete(lists);
        dst["object"] = cls;
        if (rawVals.TryGetValue("Name", out var tn)) dst["name"] = tn;
        if (rawVals.TryGetValue("CurrentTacticSlotName", out var sn)) { dst["CurrentTacticSlotName"] = sn; dst["name"] = sn; }
        dst["teamInstructionsByPhase"] = phases;
        dst["allValues"] = rawVals;
        dst["lists"] = lists;
        dst["playerInstructions"] = ExtractPlayerInstructions(lists);
        CoachPlugin.L.LogInfo($"Snapshot: tactic {cls} via {srcLabel}: {rawVals.Count}/{names.Count} values, phases=[{string.Join(", ", phases.Select(kv => kv.Key + ":" + kv.Value.Count))}], lists={lists.Count} ({string.Join(", ", lists.Select(kv => kv.Key.Split(' ')[0] + "=" + kv.Value.Count))})");
        if (P)
        {
            pr.AppendLine($"\n## tactic {cls} via {srcLabel}: values ({rawVals.Count}/{names.Count} resolved)");
            foreach (var r in reqs) pr.AppendLine("  " + r.Path + " = " + ReqText(r) + (rawVals.ContainsKey(r.Path) && IsNum(rawVals[r.Path]) ? "  -> " + InstructionLabel("", rawVals[r.Path]) : ""));
            foreach (var kv in lists) { pr.AppendLine($"\n## tactic list rows {kv.Key}"); foreach (var row in kv.Value.Take(14)) pr.AppendLine("  " + string.Join("; ", row.Select(x => x.Key + "=" + x.Value))); }
        }
    }

    static readonly Regex PiListRx = new("Player|Position|Selection", RegexOptions.IgnoreCase);
    static readonly Regex PiKeyRx = new("Instruction|Shout|Option", RegexOptions.IgnoreCase);
    static readonly Regex PiIdRx = new("Name|Position|Role|Duty|Player", RegexOptions.IgnoreCase);
    static List<Dictionary<string, string>> ExtractPlayerInstructions(Dictionary<string, List<Dictionary<string, string>>> lists)
    {
        var res = new List<Dictionary<string, string>>();
        foreach (var kv in lists.Where(kv => CoachTacticSnapshot.IsOwnInstructionList(kv.Key)))
            foreach (var row in kv.Value)
            {
                var pi = row.Where(x => PiKeyRx.IsMatch(x.Key) && CoachTacticSnapshot.IsPlayerInstructionData(x.Key)).ToList();
                if (pi.Count == 0) continue;
                var d = new Dictionary<string, string>();
                foreach (var x in row.Where(x => PiIdRx.IsMatch(x.Key) && !PiKeyRx.IsMatch(x.Key)).Take(4)) d[x.Key] = x.Value;
                foreach (var x in pi) d[x.Key] = x.Value;
                res.Add(d);
            }
        return res;
    }

    IEnumerator TacticScreenJob(List<(string path, string cls, TypedValue tv)> nodes) => Flatten(TacticScreenSteps(nodes));

    IEnumerator TacticScreenSteps(List<(string path, string cls, TypedValue tv)> nodes, bool finish = true)
    {
        var t0 = Time.realtimeSinceStartup; int issued0 = reqIssued;
        var screenTactic = new Dictionary<string, object>();
        var expectedInstructionKeys = new HashSet<string>();
        T Get<T>(string k) where T : class, new() => screenTactic.TryGetValue(k, out var o) && o is T t ? t : new T();
        var vals = new SortedDictionary<string, string>(Get<SortedDictionary<string, string>>("allValues"));
        var lists = new Dictionary<string, List<Dictionary<string, string>>>(Get<Dictionary<string, List<Dictionary<string, string>>>>("lists"));
        var phases = Get<Dictionary<string, Dictionary<string, string>>>("teamInstructionsByPhase");
        object obj = null;
        string name = null;
        bool P = ProbeEnabled && !tacticsProbeWritten; var tp = new StringBuilder();
        if (P) { tp.AppendLine("# FM Coach v0.7.3 tactics-screen probe " + DateTime.Now.ToString("s")); foreach (var n in nodes) tp.AppendLine("  node " + n.path + " = " + n.cls); }
        int added = 0, crawled = 0;
        var tacNodes = nodes.Where(n => !n.cls.Contains("Tactics")).ToList();
        // v0.7.2: only our own team's tactic (a scout/opposition report can show another team's TeamTacticReference)
        var myTeam = teamSnap.TryGetValue("team.Name", out var mt) ? mt : humanTeamName;
        if (myTeam != null && tacNodes.Count > 0)
        {
            var tq = tacNodes.Select(n => Ask(n.tv, "Team.Name", 5f, false, "screen", null, screenTacticScopes.GetValueOrDefault(n.path))).ToList();
            yield return WaitAll(tq, 6f);
            var keep = new List<(string path, string cls, TypedValue tv)>();
            for (int i = 0; i < tacNodes.Count; i++)
            {
                var tn = tq[i].Got ? ValueText(tq[i].Str) : null;
                if (tn != null && tn != myTeam) { CoachPlugin.L.LogInfo("Tactics merge: skipping " + tacNodes[i].cls + " of " + tn + " (not " + myTeam + ")"); continue; }
                keep.Add(tacNodes[i]);
            }
            tacNodes = keep;
            if (tacNodes.Count == 0) { snapshotStatus = "tactic on screen is not ours"; if (finish) FinishSnapshot(t0, issued0); yield break; }
        }
        var posNodes = nodes.Where(n => n.cls.Contains("Tactics")).ToList();
        var doneCls = new HashSet<string>();
        foreach (var n in tacNodes.Take(4))
        {
            if (doneCls.Contains(n.cls)) continue;   // the same tactic is usually bound twice on the screen
            yield return CrawlTactic(n.tv, "screen " + n.path, false, P ? tp : null, screenTactic, null, screenTacticScopes.GetValueOrDefault(n.path));
            crawled++;
            if (screenTactic.GetValueOrDefault("expectedInstructionKeys") is List<string> expected) expectedInstructionKeys.UnionWith(expected);
            CoachPlugin.L.LogInfo("Screen tactic source: " + n.path + "; scope=" + screenTacticScopes.GetValueOrDefault(n.path).m_key.ToString("x") + "; keys=" + ReferenceKeys(n.tv));
            var av = Get<SortedDictionary<string, string>>("allValues");
            if (av.Count >= 20) doneCls.Add(n.cls);
            if (obj == null && screenTactic.TryGetValue("object", out var o2)) obj = o2;
            if (screenTactic.TryGetValue("name", out var nn) && nn is string ns && ns.Length > 0) name = ns;
            foreach (var kv in av) if (!vals.ContainsKey(kv.Key)) { vals[kv.Key] = kv.Value; added++; }
            foreach (var kv in Get<Dictionary<string, List<Dictionary<string, string>>>>("lists")) if (!lists.ContainsKey(kv.Key)) { lists[kv.Key] = kv.Value; added++; }
            foreach (var ph in Get<Dictionary<string, Dictionary<string, string>>>("teamInstructionsByPhase"))
            {
                if (!phases.TryGetValue(ph.Key, out var d)) phases[ph.Key] = d = new Dictionary<string, string>();
                foreach (var kv in ph.Value) if (!d.ContainsKey(kv.Key)) { d[kv.Key] = kv.Value; added++; }
            }
        }
        if (phases.Values.Sum(d => d.Count) < 3) { CoachPlugin.L.LogInfo("Tactics merge skipped: no resolved instruction data (request/source echo only)"); yield break; }
        // the pitch: one TacticsPositionReference / TacticsTeamSelectionReference per slot (v0.7.1)
        if (posNodes.Count > 0)
        {
            var rowProps = SlotItemProps;
            var per = new List<(string cls, List<Req> rs)>();
            foreach (var n in posNodes.Take(60))
            {
                var ps = PropsOf(n.cls);
                per.Add((n.cls, rowProps.Where(p => ps.Count == 0 || ps.Any(x => x.Name == p.Split('.')[0])).Select(p => Ask(n.tv, p, 8f, false, "pitch", null, screenTacticScopes.GetValueOrDefault(n.path))).ToList()));
                if (per.Count % 8 == 0) yield return null;
            }
            yield return WaitAll(per.SelectMany(x => x.rs), 10f);
            foreach (var g in per.GroupBy(x => x.cls))
            {
                var rows = new List<Dictionary<string, string>>(); var seenRow = new HashSet<string>();
                foreach (var (_, rs) in g)
                {
                    var row = new Dictionary<string, string>();
                    foreach (var r in rs.Where(r => r.Got)) { var v = ValueText(r.Str); if (v != null) row[r.Path] = Trunc(v, 120); }
                    if (row.Count > 0 && seenRow.Add(string.Join(";", row.OrderBy(x => x.Key).Select(x => x.Key + "=" + x.Value)))) rows.Add(row);
                }
                if (rows.Count > 0) { lists["TacticalPositions(screen) [" + g.Key + "]"] = rows; added += rows.Count; }
                if (P) { tp.AppendLine($"\n## pitch nodes {g.Key}: {g.Count()} nodes, {rows.Count} distinct rows"); foreach (var (_, rs) in g.Take(24)) tp.AppendLine("  " + string.Join("; ", rs.Select(r => r.Path + "=" + ReqText(r)))); }
            }
        }
        CoachTacticSnapshot.Replace(tacticSnap, vals, lists, phases,
            obj ?? string.Join(", ", tacNodes.Select(x => x.cls).Distinct()), name, expectedInstructionKeys, CoachTacticSnapshot.PlayerInstructionRowsComplete(lists));
        tacticSnap["playerInstructions"] = ExtractPlayerInstructions(lists);
        var srcNow = "tactics screen " + string.Join(", ", tacNodes.Select(x => x.cls).Distinct()) + " at " + DateTime.Now.ToString("HH:mm");
        if (!tacticSnap.TryGetValue("source", out var so) || (so as string) == "(none)" || ((so as string) ?? "").StartsWith("tactics screen") || ((so as string) ?? "").Contains("(name only")) tacticSnap["source"] = srcNow;
        snapshotAt = DateTime.Now;
        tacticSnap["screenMerged"] = DateTime.Now.ToString("s") + " from " + string.Join(", ", nodes.Select(x => x.cls).Distinct());
        CoachPlugin.L.LogInfo("Tactics screen merge: +" + added + " fields from " + crawled + " tactic object(s) and " + posNodes.Count + " pitch node(s)");
        BuildTacticSlotsFromPlayers(preferScreen: true);
        // slots only from the saved team tactic, never from a match tactic (in-match changes are not the saved slot)
        var teamTac = tacNodes.FirstOrDefault(n => n.cls.Contains("TeamTactic"));
        if (teamTac.tv != null) yield return ReadTacticSlots(teamTac.tv, P ? tp : null, screenTacticScopes.GetValueOrDefault(teamTac.path));
        if (!tacticSnap.ContainsKey("object"))
        {
            tacticScreensSeen.Clear(); nextTacticScreenCheck = Time.unscaledTime + 0.5f;
            BuildSquadContext(); WriteSnapshotFiles(); yield break;
        }
        lastMergedActiveSlotRaw = activeSlotRaw;
        lastTacticMergeAt = Time.unscaledTime;
        ComputeProfileAndHints(); BuildSquadContext(); WriteSnapshotFiles();
        if (P)
        {
            tacticsProbeWritten = true;
            try
            {
                tp.AppendLine("\n## slots: " + (tacticSnap.TryGetValue("slotsSource", out var ss) ? ss : "?"));
                tp.AppendLine("\n## request stats: " + ReqStats());
                var f = Path.Combine(CoachPlugin.OutDir, "probe_tactics_" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".txt");
                File.WriteAllText(f, tp.ToString(), Encoding.UTF8);
                CoachPlugin.L.LogInfo("Tactics probe written: " + f + " (" + tp.Length + " chars)");
            }
            catch (Exception ex) { LogOnce("TacticsProbeWrite", ex); }
        }
        if (finish) FinishSnapshot(t0, issued0);   // also answers a snapshot.txt test request that arrived during the merge
    }

    void FinishProbe()
    {
        if (!ProbeEnabled || probeWritten || probe.Length == 0) return;
        probeWritten = true;
        try
        {
            probe.AppendLine("\n## request stats: " + ReqStats());
            probeFile = Path.Combine(CoachPlugin.OutDir, "probe_" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".txt");
            File.WriteAllText(probeFile, probe.ToString(), Encoding.UTF8);
            CoachPlugin.L.LogInfo("Probe written: " + probeFile + " (" + probe.Length + " chars)");
        }
        catch (Exception ex) { LogOnce("ProbeWrite", ex); }
    }

    void FinishSnapshot(float t0, int issued0, bool updateSnapshotTime = true)
    {
        if (updateSnapshotTime) snapshotAt = DateTime.Now;
        int ti = tacticSnap.TryGetValue("teamInstructionsByPhase", out var ph) && ph is Dictionary<string, Dictionary<string, string>> pd ? pd.Sum(kv => kv.Value.Count) : 0;
        snapshotStatus = $"{squad.Count} players, tactic {(tacticSnap.ContainsKey("object") ? "read (" + ti + " instructions)" : "NOT read")}, at {snapshotAt:HH:mm}";
        snapshotStatus = DataLoadSummary();
        nextFullRetry = Time.unscaledTime + 90f;
        CoachPlugin.L.LogInfo($"Squad/tactic snapshot done ({snapReason}): {snapshotStatus}; {reqIssued - issued0} requests in {(Time.realtimeSinceStartup - t0):0.0}s; AI context ≈{squadContextTokens} tokens");
        dirty = true;
        if (snapForTest)
        {
            try { File.WriteAllText(Path.Combine(TestDir, "snapshot_done.txt"), $"time: {DateTime.Now:s}\nreason: {snapReason}\nstatus: {snapshotStatus}\nplayers: {squad.Count}\ncontext_tokens_est: {squadContextTokens}\ntactic_slots: {TacticSlotsDoneLine()}\nquick_chips: {QuickChipsLine()}\nopponent: {(oppSnap.GetValueOrDefault("forOpponent") as string ?? "none")} missing=[{string.Join(",", (oppSnap.GetValueOrDefault("missing") as List<string>) ?? new List<string>())}]\nprimary_slot: {(primarySlot?.ToString() ?? "unknown")}\nactive_slot: {(activeSlot?.ToString() ?? "unknown")}\nfiles: {Path.Combine(TestDir, "snapshot")}\nprobe: {probeFile}\n", Encoding.UTF8); }
            catch (Exception ex) { LogOnce("SnapshotDone", ex); }
            snapForTest = false;
        }
        snapTestRunning = false;
    }
}
