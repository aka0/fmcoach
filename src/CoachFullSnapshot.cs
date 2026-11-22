// Automatic, read-only startup reads. Never switches a screen, tactic slot or game setting.
using System;
using DateTime = System.DateTime;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using FM.UI;
using SI.Core;
using SI.Bindable;
using SI.Interop;
using SI.Bindable.Reference.Core;
using UnityEngine;
using UnityEngine.UIElements;

public partial class CoachBehaviour
{
    readonly HashSet<string> registeredPlayerIds = new(), registeredPlayerNames = new();
    readonly Dictionary<string, string> teamSeasonStats = new();
    readonly List<string> registrationSources = new();
    bool registrationRead, registrationComplete, fullSnapshotFinished, squadListRead, refreshingMissing;
    TypedValue fullTeamTv;
    DateTime squadReadAt;
    int squadCandidates, excludedInjured, excludedLoaned, excludedUnregistered, excludedUnknown, excludedNotFirstTeam;
    string loadingStage = "Waiting for your save";
    float nextFullRetry, nextLoadingHeartbeat, nextLoadingLog;
    DateTime stageStartedUtc = DateTime.UtcNow;
    Label dataLoadLabel;
    static readonly string[] ClubSeasonProps = { "ClubAppearancesThisSeason", "ClubGoalsThisSeason", "ClubAssistsThisSeason", "ClubAverageRatingThisSeason" };
    static readonly Regex StatName = new("Name|Label|Title|Header|Value|Display|Data|Rows|Items|Observation|Visual|TimeFrame|Stat|Season|Competition|Match|Played|Won|Draw|Lost|Point|Goal|Against|Conced|Assist|Shot|Possession|Pass|Chance|Expected|XG|Xg|Rating|Minute|Form|Performance|CleanSheet|Tackl|Intercept|Save|Cross|Dribbl|Press", RegexOptions.IgnoreCase);

    void SetLoadingStage(string stage)
    {
        loadingStage = stage; stageStartedUtc = DateTime.UtcNow; snapshotStatus = stage; dirty = true;
        nextLoadingHeartbeat = 0;
        CoachPlugin.L.LogInfo("Coach loading: " + stage);
    }

    CoachSnapshotCoverage SnapshotCoverage()
    {
        var slots = slotStore.Values.Where(r => r.valid != false && !string.IsNullOrWhiteSpace(r.name)).ToList();
        return new CoachSnapshotCoverage
        {
            Finished = fullSnapshotFinished, FirstTeamRead = squadListRead, RegistrationRead = registrationRead && registrationComplete,
            Players = squad.Count, AttributesComplete = squad.Count(PlayerAttributesComplete),
            SeasonStatsComplete = squad.Count(p => ClubSeasonProps.All(k => p.seasonStats.ContainsKey(k))),
            TacticInventoryRead = tacticInventoryRead, TacticsExpected = slots.Count, TacticsComplete = slots.Count(r => r.detailsComplete && r.detailsSource?.Contains("cached") != true),
            UnknownPlayers = excludedUnknown,
            TeamStatsRead = CoachTeamStats.HasScopedMetrics(teamSeasonStats),
        };
    }

    string DataLoadSummary() => !DataEnabled ? "Coach data loading disabled" : SnapshotCoverage().Summary(loadingStage) + (refreshingMissing ? " · refreshing missing data" : "");

    bool PlayerAttributesComplete(SquadPlayer player) => AttrGroups
        .Where(g => player.IsGK || g.group != "GOALKEEPING")
        .SelectMany(g => g.attrs).All(a => player.attrs.ContainsKey(a.prop.Replace("Attribute", "")));

    void FullSnapshotTick()
    {
        if (Time.unscaledTime >= nextLoadingHeartbeat)
        {
            nextLoadingHeartbeat = Time.unscaledTime + 5f;
            try { var directory = Path.Combine(TestDir, "snapshot"); Directory.CreateDirectory(directory); WriteFullSnapshotStatus(directory); }
            catch (Exception ex) { LogOnce("LoadingHeartbeat", ex); }
        }
        if (humanTv != null && !fullSnapshotFinished && Time.unscaledTime >= nextLoadingLog)
        {
            nextLoadingLog = Time.unscaledTime + 15f;
            CoachPlugin.L.LogInfo("Coach loading heartbeat: " + loadingStage + "; " + ReqStats() + "; queued=" + reqQueue.Count + " live=" + reqLive.Count + " frame=" + Time.frameCount);
        }
        if (dataLoadLabel != null)
        {
            dataLoadLabel.text = DataLoadSummary();
            dataLoadLabel.style.display = new StyleEnum<DisplayStyle>(collapsed ? DisplayStyle.None : DisplayStyle.Flex);
        }
        if (DataEnabled && humanTv != null && fullSnapshotFinished && snapJob == null &&
            Time.unscaledTime >= nextFullRetry && !SnapshotCoverage().Ready)
        {
            nextFullRetry = Time.unscaledTime + 90f;
            var coverage = SnapshotCoverage();
            if (coverage.SquadComplete && fullTeamTv != null)
            {
                refreshingMissing = true; snapReason = "retry missing tactic/team data"; snapJob = Flatten(MissingDataSteps());
            }
            else StartSnapshot("retry missing startup data", false);
        }
    }

    IEnumerator MissingDataSteps()
    {
        var started = Time.realtimeSinceStartup; int issued = reqIssued;
        var coverage = SnapshotCoverage();
        if (!coverage.TacticInventoryRead || coverage.TacticsExpected == 0 || coverage.TacticsComplete != coverage.TacticsExpected)
        {
            var nodes = FindScreenTacticNodes(dataB, 4);
            if (nodes.Count > 0) yield return TacticScreenSteps(nodes, false);
            else yield return ReadAllSavedTactics(fullTeamTv);
        }
        if (!coverage.TeamStatsRead) yield return ReadTeamSeasonStats(fullTeamTv);
        refreshingMissing = false; loadingStage = "Missing-category refresh complete";
        ComputeProfileAndHints(); BuildSquadContext(); WriteSnapshotFiles(); FinishSnapshot(started, issued, false);
    }

    void ResetFullSnapshot()
    {
        registeredPlayerIds.Clear(); registeredPlayerNames.Clear(); registrationSources.Clear(); teamSeasonStats.Clear();
        registrationRead = registrationComplete = fullSnapshotFinished = squadListRead = refreshingMissing = false; fullTeamTv = null; squadReadAt = default; squadCandidates = excludedInjured = excludedLoaned = excludedUnregistered = excludedUnknown = excludedNotFirstTeam = 0;
        loadingStage = "Waiting for your save"; nextFullRetry = nextLoadingHeartbeat = nextLoadingLog = 0;
    }

    IEnumerator ReadRegisteredPlayers(TypedValue team)
    {
        SetLoadingStage("Reading first-team registration");
        registeredPlayerIds.Clear(); registeredPlayerNames.Clear(); registrationSources.Clear(); registrationRead = registrationComplete = false;
        var roots = new[] { "SquadRegistration", "SpecificSquadRegistration", "SquadRegistrationComps", "TeamSquad" }
            .Select(p => Ask(team, p, 6f, true, "registration")).ToList();
        yield return WaitAll(roots, 7f);
        var queue = new Queue<(TypedValue value, string path, int depth)>();
        foreach (var r in roots.Where(r => r.Tv != null))
        {
            var items = ListItems(r.Tv, 16);
            if (items.Count > 0) foreach (var item in items) queue.Enqueue((item, r.Path, 0));
            else queue.Enqueue((r.Tv, r.Path, 0));
        }
        var seen = new HashSet<string>(); int visited = 0;
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (queue.Count > 0 && visited++ < 32 && DateTime.UtcNow < deadline)
        {
            var (source, path, depth) = queue.Dequeue();
            var cls = ClassOf(source); var key = ReferenceIdentity(source);
            if (!seen.Add(key)) continue;
            var cat = PropsOf(cls);
            var names = new[] { "RegisteredPlayers", "SquadRegistrationForComp", "SquadRegistrationSquad", "SquadRegistration", "Players" }
                .Where(n => n != "Players" || (cls ?? "").Contains("Registration"))
                .Where(n => cat.Count == 0 || cat.Any(p => p.Name == n)).ToArray();
            var requests = names.Select(n => Ask(source, n, 6f, true, "registration")).ToList();
            yield return WaitAll(requests, 7f);
            foreach (var r in requests.Where(r => r.Got && r.Tv != null))
            {
                int count = ListCount(r.Tv);
                if (r.Path == "RegisteredPlayers" && count >= 0)
                {
                    registrationRead = true; registrationSources.Add(path + ".RegisteredPlayers");
                    var people = ListItems(r.Tv, count).Select(item =>
                        new[] { "UniqueId", "Name", "Person.UniqueId", "Person.Name", "Player.UniqueId", "Player.Name" }
                            .Select(p => Ask(item, p, 5f, false, "registered player")).ToList()).ToList();
                    yield return WaitAll(people.SelectMany(p => p), 6f);
                    foreach (var who in people)
                        foreach (var q in who.Where(q => q.Got))
                        {
                            var v = ValueText(q.Str); if (v == null) continue;
                            if (q.Path.EndsWith("UniqueId") && IsNum(v)) registeredPlayerIds.Add(v);
                            else if (q.Path.EndsWith("Name")) registeredPlayerNames.Add(v);
                        }
                }
                else if (depth < 2)
                {
                    if (count > 0) foreach (var item in ListItems(r.Tv, 16)) queue.Enqueue((item, path + "." + r.Path, depth + 1));
                    else if (count < 0) queue.Enqueue((r.Tv, path + "." + r.Path, depth + 1));
                }
            }
        }
        registrationComplete = queue.Count == 0;
        CoachPlugin.L.LogInfo("Registration read: " + registeredPlayerIds.Count + " player IDs; sources=" + registrationSources.Count + "; traversalComplete=" + registrationComplete);
    }

    static string ReferenceIdentity(TypedValue value)
    {
        try { var r = value?.Get()?.TryCast<InteropReference>(); if (r != null) { var id = r.GetPersistentIdentity(); if (!string.IsNullOrWhiteSpace(id)) return r.GetIl2CppType().FullName + ":" + id; } } catch { }
        return SourcePointer(value).ToString();
    }

    bool IncludeSnapshotPlayer(SquadPlayer player)
    {
        string R(string key) => player.raw.TryGetValue(key, out var value) ? ValueText(Display(key, value)) : null;
        var id = R("UniqueId");
        bool member = registeredPlayerIds.Contains(id ?? "") || registeredPlayerNames.Contains(player.name);
        player.registered = member ? true : registrationRead && registrationComplete ? false : (bool?)null;
        player.clubId = R("Club.UniqueId"); player.loanClubId = R("LoanClub.UniqueId"); player.teamId = R("Team.UniqueId");
        player.excludedReason = CoachSquadPolicy.Exclusion(player.registered, CoachSquadPolicy.Boolean(R("IsInjured")), player.clubId, player.loanClubId, humanClubId, player.teamId, teamSnap.GetValueOrDefault("team.UniqueId"));
        if (player.excludedReason == "injured") excludedInjured++;
        else if (player.excludedReason == "loaned out" || player.excludedReason == "not at this club") excludedLoaned++;
        else if (player.excludedReason == "not in the first team") excludedNotFirstTeam++;
        else if (player.excludedReason == "not registered") excludedUnregistered++;
        else if (player.excludedReason != null) excludedUnknown++;
        return player.excludedReason == null;
    }

    IEnumerator ReadTeamSeasonStats(TypedValue team)
    {
        SetLoadingStage("Reading team season statistics");
        teamSeasonStats.Clear();
        // GeneralPerformanceVisual is the verified season/competition-scoped source.
        // ReportStatsData needs an unrelated report Type and timed out on every load.
        yield return ReadGeneralPerformanceStats(team, teamSeasonStats);
    }

    IEnumerator ReadGeneralPerformanceStats(TypedValue team, Dictionary<string, string> destination)
    {
        LogBindingContexts(team, "GeneralPerformanceVisual");
        var visual = Ask(team, "GeneralPerformanceVisual", 8f, true, "team general performance");
        yield return WaitAll(new[] { visual }, 9f);
        var fields = new Dictionary<string, string>();
        if (visual.Got && visual.Tv != null)
            yield return ReadStatBlock(visual.Tv, "GeneralPerformanceVisual", fields, 0);
        int metrics = CoachTeamStats.CollectPerformance(fields, destination);
        CoachPlugin.L.LogInfo("Team general performance: " + metrics + " named numeric metrics (" + fields.Count + " source fields), scoped=" + CoachTeamStats.HasScopedMetrics(destination) + "; source=" + ClassOf(visual.Tv));
    }

    IEnumerator ReadStatBlock(TypedValue source, string prefix, Dictionary<string, string> destination, int depth, int[] remaining = null)
    {
        remaining ??= new[] { 160 };
        if (depth > 5 || remaining[0]-- <= 0) yield break;
        // Some report providers return a list directly, others expose named child blocks.
        if (depth <= 5 && ListCount(source) >= 0)
        {
            var reportItems = ListItems(source, 80);
            for (int i = 0; i < reportItems.Count; i++)
                yield return ReadStatBlock(reportItems[i], prefix + "[" + i + "]", destination, depth + 1, remaining);
            yield break;
        }
        var cat = PropsOf(source);
        var names = cat.Where(p => !p.Name.StartsWith("Debug") && !p.Name.StartsWith("#") && StatName.IsMatch(p.Name))
            .Select(p => p.Name).Take(80).ToList();
        var requests = names.Select(n => Ask(source, n, 6f, true, "season stats")).ToList();
        yield return WaitAll(requests, 7f);
        foreach (var r in requests.Where(r => r.Got))
        {
            var value = ValueText(r.Str); if (value != null) destination[prefix + "." + r.Path] = value;
            if (r.Path == "Competition" && r.Tv != null)
            {
                var name = Ask(r.Tv, "Name", 5f, false, "statistics competition");
                yield return WaitAll(new[] { name }, 6f);
                if (name.Got && ValueText(name.Str) is string competition && !string.IsNullOrWhiteSpace(competition))
                    destination[prefix + ".Competition.Name"] = competition;
            }
            if (depth >= 5 || r.Tv == null) continue;
            var items = ListItems(r.Tv, 40);
            if (items.Count > 0)
                for (int i = 0; i < items.Count; i++) yield return ReadStatBlock(items[i], prefix + "." + r.Path + "[" + i + "]", destination, depth + 1, remaining);
            else if (value == null && ((ClassOf(r.Tv) ?? "").Contains("DynamicReference") ||
                Regex.IsMatch(ClassOf(r.Tv) ?? "", "Report.*Reference|Stat.*Reference|Observation.*Reference|Visual.*Reference")))
                yield return ReadStatBlock(r.Tv, prefix + "." + r.Path, destination, depth + 1, remaining);
        }
    }

    TypedValue MakeTeamReference(TypedValue team, Func<Il2CppSystem.Collections.Generic.List<InteropReference.Pair>, InteropReference> factory, InteropReference prototype)
    {
        try
        {
            var reference = team?.Get()?.TryCast<InteropReference>();
            if (reference?.m_id == null || prototype?.m_id == null) return null;
            // Reference.Identifier is a name hash; UIRecordID is a different native ID.
            // Preserve the destination constructor's actual record class and defaults.
            var pairs = new Dictionary<uint, InteropReference.Pair>(); bool classRead = false;
            for (int i = 0; i < prototype.m_id.Count; i++)
            {
                var pair = prototype.m_id[i]; pairs[pair.ID] = pair;
                try { if (PropertyIdentifierSet.Instance.GetInfo(pair.ID)?.Name == "UIRecordID") classRead = true; } catch { }
            }
            CoachPlugin.L.LogInfo("Automatic tactic prototype keys: " + ReferenceKeys(TypedValue.Create(prototype)));
            if (!classRead) { CoachPlugin.L.LogInfo("Automatic tactic prototype has no native class ID; skipping construction"); return null; }
            for (int i = 0; i < reference.m_id.Count; i++)
            {
                var pair = reference.m_id[i]; string name = null;
                try { name = PropertyIdentifierSet.Instance.GetInfo(pair.ID)?.Name; } catch { }
                if (name != "UIRecordID") pairs[pair.ID] = pair;
            }
            var ids = new Il2CppSystem.Collections.Generic.List<InteropReference.Pair>();
            foreach (var pair in pairs.Values) ids.Add(pair);
            return TypedValue.Create(factory(ids));
        }
        catch (Exception ex) { LogOnce("AutomaticTacticSource", ex); return null; }
    }

    string ReferenceKeys(TypedValue value)
    {
        try
        {
            var reference = value?.Get()?.TryCast<InteropReference>(); if (reference?.m_id == null) return "no reference IDs";
            var keys = new List<string>();
            for (int i = 0; i < reference.m_id.Count; i++)
            {
                var pair = reference.m_id[i];
                string name = null;
                try { name = PropertyIdentifierSet.Instance.GetInfo(pair.ID)?.Name; } catch { }
                if (name == null) try { name = ContextIdentifierSet.Instance.GetInfo(pair.ID)?.Name; } catch { }
                keys.Add((name ?? pair.ID.ToString("x")) + "=" + pair.Value);
            }
            return string.Join(", ", keys);
        }
        catch (Exception ex) { return ex.GetType().Name; }
    }

    IEnumerator ReadAllSavedTactics(TypedValue team)
    {
        SetLoadingStage("Reading saved tactics and instructions");
        LogBindingContexts(team, "Tactic");
        LogBindingContexts(team, "TacticDropdown");
        LogBindingContexts(team, "SquadPlannerTacticDropdown");
        var source = MakeTeamReference(team, ids => new TeamTacticReference(ids), new TeamTacticReference()); if (source == null) yield break;
        CoachPlugin.L.LogInfo("Automatic tactic source keys: " + ReferenceKeys(source));
        var identity = Ask(source, "Team.UniqueId", 6f, false, "automatic tactic ownership");
        yield return WaitAll(new[] { identity }, 7f);
        if (!identity.Got || !teamSnap.TryGetValue("team.UniqueId", out var ourTeamId) || ValueText(identity.Str) != ourTeamId)
        { CoachPlugin.L.LogInfo("Automatic tactic ownership unavailable: " + ReqText(identity)); TacticSlotContexts(source); yield break; }
        var current = new Dictionary<string, object>();
        yield return CrawlTactic(source, "automatic team reference", false, null, current);
        if (current.TryGetValue("teamInstructionsByPhase", out var all) && all is Dictionary<string, Dictionary<string, string>> phases && phases.Values.Sum(d => d.Count) >= 3)
        {
            tacticSnap.Clear(); foreach (var pair in current) tacticSnap[pair.Key] = pair.Value;
            tacticSnap["source"] = "automatic club snapshot";
            BuildTacticSlotsFromPlayers(preferScreen: true);
        }
        yield return ReadTacticSlots(source, null, readSource: "automatic club snapshot");
        if (!SlotProviderProbes)
        {
            if (slotStore.Values.Any(record => record.valid == true && !CoachTacticSnapshot.CanUseDetails(record.detailsComplete, record.detailsSource)))
                CoachPlugin.L.LogInfo("Inactive saved tactics await a current-session read; diagnostic provider probes are disabled");
            yield break;
        }
        var slotValues = new Dictionary<int, TypedValue>();
        yield return ProbeSavedTacticDropdowns(team, slotValues);
        var candidates = new List<(string label, TypedValue value)>();
        var factoryRequests = new[] { true, false }.Select(full => (full,
            request: Ask(team, "Tactic", 4f, true, "native team tactic factory", new Dictionary<string, TypedValue> { ["Boolean"] = TypedValue.Create(full) }))).ToList();
        yield return WaitAll(factoryRequests.Select(item => item.request), 5f);
        foreach (var item in factoryRequests)
        {
            var value = item.request.Tv;
            CoachPlugin.L.LogInfo("Native team tactic factory Boolean=" + item.full + ": " + ReqText(item.request) + "; keys=" + ReferenceKeys(value));
            if (value != null && (ClassOf(value) ?? "").EndsWith(".TeamTacticReference")) candidates.Add(("team.Tactic Boolean=" + item.full, value));
        }
        candidates.Add(("constructed team tactic", source));
        // Persistent database identity may omit the native Boolean mode; test each explicit factory mode.
        var contextNames = TacticSlotContexts(source);
        foreach (var rec in slotStore.Values.Where(r => r.valid == true && !CoachTacticSnapshot.CanUseDetails(r.detailsComplete, r.detailsSource)).OrderBy(r => r.slot).ToList())
        {
            bool completed = false; float probeDeadline = Time.realtimeSinceStartup + 20f;
            var variants = new List<(string label, TypedValue value)> {
                ("FM dropdown", slotValues.GetValueOrDefault(rec.slot) ?? TypedValue.Create(rec.slot - 1)),
                ("Int32", TypedValue.Create(rec.slot - 1)), ("UInt32", TypedValue.Create((uint)(rec.slot - 1))) };
            foreach (var name in contextNames.OrderBy(value => value == "TacticIndex" ? 0 : 1))
            {
                foreach (var variant in variants)
                {
                    if (Time.realtimeSinceStartup >= probeDeadline) break;
                    var context = new Dictionary<string, TypedValue> { [name] = variant.value, ["InPossession"] = TypedValue.Create(true) };
                    // A selector may be consumed when the reference is created, rather
                    // than when a field is read. Test both declared Boolean modes there.
                    var creation = new[] { true, false }.Select(full =>
                    {
                        var creationContext = new Dictionary<string, TypedValue>(context) { ["Boolean"] = TypedValue.Create(full) };
                        return (full, request: Ask(team, "Tactic", 3f, true, "selected native tactic factory", creationContext));
                    }).ToList();
                    yield return WaitAll(creation.Select(item => item.request), 4f);
                    var sources = new List<(string label, TypedValue value)>();
                    foreach (var created in creation)
                    {
                        var value = created.request.Tv;
                        CoachPlugin.L.LogInfo("Selected tactic factory " + name + "=" + (rec.slot - 1) + " " + variant.label + " Boolean=" + created.full + ": " + ReqText(created.request) + "; keys=" + ReferenceKeys(value));
                        if (value != null && (ClassOf(value) ?? "").EndsWith(".TeamTacticReference")) sources.Add(("selected factory Boolean=" + created.full, value));
                    }
                    sources.AddRange(candidates);
                    foreach (var candidate in sources)
                    {
                        if (Time.realtimeSinceStartup >= probeDeadline) break;
                        var check = new[] { "Team.UniqueId", "CurrentTacticSlot", "CurrentTacticSlotName", "Name" }
                            .Select(property => Ask(candidate.value, property, 3f, false, "saved slot selector preflight", context, default, true)).ToList();
                        yield return WaitAll(check, 4f);
                        string C(string path) => check.FirstOrDefault(request => request.Path == path && request.Got) is Req response ? ValueText(response.Str) : null;
                        var returned = C("CurrentTacticSlotName") ?? C("Name");
                        bool matches = CoachTacticSnapshot.MatchesSlotProbe(ourTeamId, rec.slot, rec.name, C("Team.UniqueId"), C("CurrentTacticSlot"), returned);
                        CoachPlugin.L.LogInfo("Saved tactic selector probe " + candidate.label + " " + name + "=" + (rec.slot - 1) + " " + variant.label + " (" + RawType(variant.value) + "): returned " + C("Team.UniqueId") + "/" + C("CurrentTacticSlot") + "/currentName=" + C("CurrentTacticSlotName") + "/name=" + C("Name") + "; matched=" + matches);
                        if (!matches) continue;
                        var data = new Dictionary<string, object>();
                        yield return CrawlTactic(candidate.value, "automatic slot " + rec.slot, false, null, data, context);
                        var returnedName = data.GetValueOrDefault("CurrentTacticSlotName") as string ?? data.GetValueOrDefault("name") as string;
                        if (returnedName != rec.name) continue;
                        var raw = data.GetValueOrDefault("allValues") as SortedDictionary<string, string>;
                        if (raw == null || !int.TryParse(raw.GetValueOrDefault("CurrentTacticSlot"), out var index) || index != rec.slot - 1) continue;
                        BuildTacticSlotsFromPlayers(data);
                        var updated = RecFrom(data, rec.slot, rec.name, "automatic slot snapshot via " + candidate.label + " / " + name);
                        updated.valid = rec.valid; updated.isActive = rec.isActive; updated.isPrimary = rec.isPrimary;
                        if (updated.detailsRead && (!slotStore.TryGetValue(rec.slot, out var previous) || !CoachTacticSnapshot.HasFreshDetails(previous.detailsRead, previous.detailsSource) || updated.missing.Count < previous.missing.Count)) slotStore[rec.slot] = updated;
                        if (updated.detailsComplete) { completed = true; break; }
                    }
                    if (completed) break;
                }
                if (completed || Time.realtimeSinceStartup >= probeDeadline) break;
            }
            if (!completed) CoachPlugin.L.LogInfo("Saved tactic slot " + rec.slot + " remains unverified after bounded read-only provider checks");
        }
        SaveSlotStore();
    }

    IEnumerator ProbeSavedTacticDropdowns(TypedValue team, Dictionary<int, TypedValue> slotValues)
    {
        var roots = (ProbeEnabled ? new[] { "TacticDropdown", "SquadPlannerTacticDropdown" } : new[] { "SquadPlannerTacticDropdown" })
            .Select(property => Ask(team, property, 4f, true, "saved tactic dropdown discovery")).ToList();
        yield return WaitAll(roots, 5f);
        foreach (var root in roots)
        {
            CoachPlugin.L.LogInfo("Saved tactic dropdown " + root.Path + ": " + ReqText(root) + "; class=" + ClassOf(root.Tv));
            if (root.Tv == null) continue;
            var items = ListItems(root.Tv, 6);
            foreach (var item in items)
            {
                var properties = PropsOf(item);
                CoachPlugin.L.LogInfo("Saved tactic dropdown item " + root.Path + ": " + ClassOf(item) + "; keys=" + ReferenceKeys(item) + "; properties=" + string.Join(", ", properties.Select(property => property.Name + ":" + property.Kind)));
                var fields = properties.Where(property => IsValueKind(property)).Select(property => Ask(item, property.Name, 3f, true, "saved tactic dropdown item")).ToList();
                yield return WaitAll(fields, 4f);
                CoachPlugin.L.LogInfo("Saved tactic dropdown values: " + string.Join("; ", fields.Select(field => field.Path + "=" + ReqText(field))));
                if (root.Path == "SquadPlannerTacticDropdown")
                {
                    var value = fields.FirstOrDefault(field => field.Path == "PropertyValue" && field.Got && field.Tv != null);
                    var display = fields.FirstOrDefault(field => field.Path == "DisplayValue" && field.Got);
                    if (value != null && int.TryParse(ValueText(value.Str), out var nativeIndex) && nativeIndex >= 0 && nativeIndex <= 2 &&
                        slotStore.TryGetValue(nativeIndex + 1, out var record) && display != null && ValueText(display.Str) == record.name)
                    {
                        slotValues[nativeIndex + 1] = value.Tv;
                        CoachPlugin.L.LogInfo("Native saved slot value " + (nativeIndex + 1) + ": " + RawType(value.Tv));
                    }
                }
            }
        }
    }

    readonly HashSet<string> loggedBindingContexts = new();
    void LogBindingContexts(TypedValue source, string property)
    {
        var cls = ClassOf(source);
        if (cls == null || !loggedBindingContexts.Add(cls + "." + property)) return;
        try
        {
            var info = PropsOf(cls).FirstOrDefault(p => p.Name == property);
            if (info.Name == null) return;
            Type type = null;
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies()) { type = assembly.GetType(cls, false); if (type != null) break; }
            var description = type?.GetMethod("GetPropertyDescriptionInternal", BindingFlags.Public | BindingFlags.Static)?.Invoke(null, new object[] { info.Id });
            if (description != null) CoachPlugin.L.LogInfo("Binding description " + cls + "." + property + ": " + description);
            if (property == "ReportStatsData")
                foreach (var reportClass in new[] { "FM.UI.TeamReportStatsReference", "FM.UI.TeamReportStatReference" })
                    CoachPlugin.L.LogInfo("Report property catalogue " + reportClass + ": " + string.Join(", ", PropsOf(reportClass).Select(p => p.Name + ":" + p.Kind)));
            var get = type?.GetMethod("GetContextsInternal", BindingFlags.Public | BindingFlags.Static);
            var contexts = new Il2CppSystem.Collections.Generic.List<ContextID>();
            get?.Invoke(null, new object[] { info.Id, contexts });
            var names = new List<string>();
            for (int i = 0; i < contexts.Count; i++) names.Add(ContextIdentifierSet.Instance.GetInfo(contexts[i].id)?.Name ?? contexts[i].id.ToString());
            CoachPlugin.L.LogInfo("Binding context requirements " + cls + "." + property + ": " + string.Join(", ", names));
        }
        catch (Exception ex) { LogOnce("BindingContexts " + property, ex); }
    }

    List<string> TacticSlotContexts(TypedValue source)
    {
        var names = new HashSet<string>(); var required = new HashSet<string>();
        try
        {
            var cls = ClassOf(source); Type type = null;
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies()) { type = assembly.GetType(cls, false); if (type != null) break; }
            var get = type?.GetMethod("GetContextsInternal", BindingFlags.Public | BindingFlags.Static);
            if (get == null) return names.ToList();
            foreach (var property in PropsOf(cls).Where(p => p.Name is "Tempo" or "CurrentTacticSlotName" or "TacticsSlotValue" or "TeamSelections" or "TacticalPositions"))
            {
                var contexts = new Il2CppSystem.Collections.Generic.List<ContextID>();
                get.Invoke(null, new object[] { property.Id, contexts });
                for (int i = 0; i < contexts.Count; i++)
                {
                    var name = ContextIdentifierSet.Instance.GetInfo(contexts[i].id)?.Name;
                    if (name != null) required.Add(name);
                    if (name != null && Regex.IsMatch(name, "^(TacticSlot|TacticSlotIndex|TacticIndex|TacticsSlotValue)$", RegexOptions.IgnoreCase)) names.Add(name);
                }
            }
            CoachPlugin.L.LogInfo("Required tactic contexts: " + (required.Count > 0 ? string.Join(", ", required) : "none declared"));
            CoachPlugin.L.LogInfo("Automatic tactic slot contexts: " + (names.Count > 0 ? string.Join(", ", names) : "not exposed by this provider"));
        }
        catch (Exception ex) { LogOnce("TacticSlotContexts", ex); }
        try
        {
            var entries = ContextIdentifierSet.Instance.m_idToInfo.GetEnumerator();
            var registered = new List<string>(); int examined = 0;
            while (entries.MoveNext() && examined++ < 5000)
            {
                var name = entries.Current.Value?.Name;
                if (CoachTacticSnapshot.IsSlotSelectorName(name)) registered.Add(name);
            }
            foreach (var name in registered.Distinct().OrderBy(name => name).Take(8)) names.Add(name);
            CoachPlugin.L.LogInfo("Registered tactic selector probe names (support unverified): " + string.Join(", ", names));
        }
        catch (Exception ex) { LogOnce("RegisteredTacticContexts", ex); }
        return names.Take(8).ToList();
    }

    // The native instruction editor selects each effective option by group, TSI and phase.
    // Enumerations and context names come from FM27's own API, not UI automation.
    IEnumerator ReadPositionInstructionOverrides(TypedValue tactic,
        Dictionary<string, List<Dictionary<string, string>>> lists,
        Dictionary<string, TypedValue> baseContext, Bindings.Key scope)
    {
        var positions = lists.Where(pair => pair.Key.StartsWith("TacticalPositionsCombined"))
            .SelectMany(pair => pair.Value).Where(row => row.ContainsKey("TeamSelectionIndex")).ToList();
        if (positions.Count != 11) yield break;
        foreach (var property in new[] { "SelectedPlayerInstruction", "PlayerInstructionGroupIsLocked", "SelectedPlayerInstructionExtraValue", "PlayerUsesPersonalisedInstructions" })
            LogBindingContexts(tactic, property);
        var pending = new List<(Dictionary<string, string> position, bool ip, FM.UI.PlayerInstructionGrouping group, Req selected, Req locked, Req extra)>();
        foreach (var position in positions)
        {
            if (!int.TryParse(position["TeamSelectionIndex"], out var index)) continue;
            int batchStart = pending.Count;
            foreach (bool ip in new[] { true, false })
            foreach (FM.UI.PlayerInstructionGrouping group in Enum.GetValues(typeof(FM.UI.PlayerInstructionGrouping)))
            {
                if ((int)group < 0) continue;
                // Groups are defined by FM's editor: ball instructions and box movement
                // in possession; pressing, marking and tackling out of possession.
                bool oopGroup = group is FM.UI.PlayerInstructionGrouping.TriggerPress or FM.UI.PlayerInstructionGrouping.Marking or
                    FM.UI.PlayerInstructionGrouping.Tackling or FM.UI.PlayerInstructionGrouping.MarkingTarget;
                if (ip == oopGroup) continue;
                var context = baseContext == null ? new Dictionary<string, TypedValue>() : new Dictionary<string, TypedValue>(baseContext);
                context["TeamSelectionIndex"] = TypedValue.Create(index);
                context["InPossession"] = TypedValue.Create(ip);
                context["PlayerInstructionGroupID"] = TypedValue.Create((int)group);
                var selected = Ask(tactic, "SelectedPlayerInstruction", 6f, false, "effective player instruction", context, scope);
                var locked = Ask(tactic, "PlayerInstructionGroupIsLocked", 6f, false, "effective player instruction", context, scope);
                pending.Add((position, ip, group, selected, locked, null));
            }
            // Keep each native contextual batch below the issue/unbind budgets. Large
            // batches answered only the first120 groups in live FM, dropping later XI.
            var playerBatch = pending.Skip(batchStart).SelectMany(item => new[] { item.selected, item.locked }).ToList();
            yield return WaitAll(playerBatch, 8f);
            CoachPlugin.L.LogInfo("Player instruction group batch TSI=" + index + ": " + playerBatch.Count(r => r.Got) + "/" + playerBatch.Count + " values");
        }
        var output = new List<Dictionary<string, string>>();
        foreach (var position in positions)
        {
            var row = new Dictionary<string, string> { ["TeamSelectionIndex"] = position["TeamSelectionIndex"],
                ["Player"] = position.GetValueOrDefault("Person.Name") ?? position.GetValueOrDefault("Player.Name") ?? "unknown" };
            bool complete = true;
            foreach (var query in pending.Where(item => ReferenceEquals(item.position, position)))
            {
                string raw = query.selected.Got ? ValueText(query.selected.Str) : null;
                string instruction = raw;
                if (int.TryParse(raw, out var id))
                {
                    instruction = CoachTacticSnapshot.PlayerInstructionLabel(query.group.ToString(), Enum.GetName(typeof(FM.UI.PlayerInstruction), id));
                }
                var locked = query.locked.Got ? CoachSquadPolicy.Boolean(ValueText(query.locked.Str)) : null;
                if (instruction == null || locked == null) { complete = false; continue; }
                string key = "Instructions[" + (query.ip ? "IP." : "OOP.") + query.group + "]";
                row[key] = instruction + (locked == true ? " (role locked)" : "");
                if (query.group == FM.UI.PlayerInstructionGrouping.MarkingTarget &&
                    instruction.IndexOf("specific", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    var targetContext = baseContext == null ? new Dictionary<string, TypedValue>() : new Dictionary<string, TypedValue>(baseContext);
                    targetContext["TeamSelectionIndex"] = TypedValue.Create(int.Parse(position["TeamSelectionIndex"]));
                    targetContext["InPossession"] = TypedValue.Create(query.ip);
                    targetContext["PlayerInstructionGroupID"] = TypedValue.Create((int)query.group);
                    var extra = Ask(tactic, "SelectedPlayerInstructionExtraValue", 6f, false, "player marking target", targetContext, scope);
                    yield return WaitAll(new[] { extra }, 7f);
                    var target = extra.Got ? ValueText(extra.Str) : null;
                    if (target == null) complete = false;
                    else row[key + ".target"] = target;
                }
            }
            if (!pending.Any(item => ReferenceEquals(item.position, position))) complete = false;
            row["InstructionCoverage"] = complete ? "complete" : "incomplete";
            output.Add(row);
        }
        lists["TacticalPositionsPlayerInstructions"] = output;
        CoachPlugin.L.LogInfo("Effective player instructions: " + output.Count(row => row["InstructionCoverage"] == "complete") + "/11 positions; " +
            output.Sum(row => row.Keys.Count(key => key.StartsWith("Instructions["))) + " resolved group fields");
    }

    IEnumerator ExpandPlayerInstructionRows(List<Req> requests, Dictionary<string, string> row)
    {
        var sources = requests.Where(r => CoachTacticSnapshot.IsPlayerInstructionData(r.Path)).ToList();
        bool complete = sources.Count > 0;
        foreach (var request in sources)
        {
            if (!request.Got || request.Tv == null) { complete = false; continue; }
            int count = ListCount(request.Tv);
            if (count == 0) { row["Instructions[" + request.Path + "]"] = "none"; continue; }
            if (count < 0) { complete = false; continue; }
            var items = ListItems(request.Tv, count);
            if (items.Count != count) complete = false;
            var values = new List<(int index, List<Req> requests)>();
            for (int i = 0; i < items.Count; i++)
            {
                var names = PropsOf(items[i]).Where(p => !p.Name.StartsWith("Debug") && !p.Name.StartsWith("#") && !p.IsList &&
                    (IsValueKind(p) || Regex.IsMatch(p.Name, "Instruction|Name|Value|Label|Text"))).Select(p => p.Name);
                values.Add((i, names.Select(n => Ask(items[i], n, 6f, false, "player instruction")).ToList()));
            }
            yield return WaitAll(values.SelectMany(v => v.requests), 7f);
            foreach (var item in values)
                if (item.requests.Count == 0 || item.requests.Any(r => !r.Got || ValueText(r.Str) == null)) complete = false;
            foreach (var item in values)
                foreach (var r in item.requests.Where(r => r.Got))
                    if (ValueText(r.Str) is string value) row["Instructions[" + request.Path + "][" + item.index + "]." + r.Path] = InstructionLabel(r.Path, value);
        }
        row["InstructionCoverage"] = complete ? "complete" : "incomplete";
    }

    void WriteFullSnapshotStatus(string directory)
    {
        var status = new Dictionary<string, object>
        {
            ["tacticInventoryRead"] = tacticInventoryRead,
            ["tactics"] = slotStore.Values.OrderBy(r => r.slot).Select(r => new {
                r.slot, r.name, r.valid,
                freshness = CoachTacticSnapshot.FreshnessLabel(r.detailsRead, r.detailsSource), r.detailsSource, r.readAt,
                detailsComplete = CoachTacticSnapshot.CanUseDetails(r.detailsComplete, r.detailsSource),
                playerInstructionsRead = r.playerInstructionsRead && CoachTacticSnapshot.HasFreshDetails(r.detailsRead, r.detailsSource),
                storedDetailsComplete = r.detailsComplete, storedPlayerInstructionsRead = r.playerInstructionsRead,
                missing = CoachTacticSnapshot.HasFreshDetails(r.detailsRead, r.detailsSource) ? r.missing : new List<string> { "current-session tactic read" },
            }).ToList(),
            ["summary"] = DataLoadSummary(), ["ready"] = SnapshotCoverage().Ready, ["missing"] = SnapshotCoverage().Missing(), ["stage"] = loadingStage, ["finished"] = fullSnapshotFinished,
            ["squadListRead"] = squadListRead, ["registrationRead"] = registrationRead, ["registrationComplete"] = registrationComplete, ["registrationSources"] = registrationSources,
            ["registeredAvailablePlayers"] = squad.Count, ["candidatePlayers"] = squadCandidates,
            ["excluded"] = new { injured = excludedInjured, loanedOutOrAway = excludedLoaned, unregistered = excludedUnregistered, notFirstTeam = excludedNotFirstTeam, unknown = excludedUnknown },
            ["teamSeasonStats"] = teamSeasonStats, ["readAt"] = snapshotAt.ToString("s"), ["squadReadAt"] = squadReadAt == default ? null : squadReadAt.ToString("s"),
            ["updatedUtc"] = DateTime.UtcNow.ToString("o"), ["stageSeconds"] = (DateTime.UtcNow - stageStartedUtc).TotalSeconds,
            ["frame"] = Time.frameCount, ["requests"] = new { issued = reqIssued, resolved = reqResolved, timedOut = reqTimedOut, queued = reqQueue.Count, live = reqLive.Count },
        };
        File.WriteAllText(Path.Combine(directory, "loading.json"), JsonSerializer.Serialize(status, new JsonSerializerOptions { WriteIndented = true }));
    }
}
