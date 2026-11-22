using System.Text;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

// The session code runs with deterministic binding and clock substitutes, without loading FM or Unity.
namespace SI.Core
{
    public class NativeReference { public IntPtr Pointer; }
    public class TypedValue
    {
        readonly NativeReference value;
        public TypedValue(int address) { value = new NativeReference { Pointer = new IntPtr(address) }; }
        public NativeReference Get() => value;
    }
}
namespace SI.Bindable
{
    public class Bindings { public struct Key { } public delegate void ValueChangedCallback(); }
    public static class BindingUtils
    {
        public static int Unbound;
        public static void UnbindIfValid(ref Bindings.Key key, Bindings.ValueChangedCallback cb) { Unbound++; }
    }
}
namespace UnityEngine { public static class Time { public static float unscaledTime; } }
public static class CoachPlugin
{
    public static readonly Logger L = new();
    public sealed class Logger { public void LogInfo(string text) { } }
}

public partial class CoachBehaviour
{
    sealed class Req
    {
        public bool Done, Got, Empty; public string Str;
        public SI.Bindable.Bindings.Key Key;
        public SI.Bindable.Bindings.ValueChangedCallback Cb;
    }
    SI.Core.TypedValue humanTv, gameTv;
    string humanSrcPath, gameSrcPath;
    IEnumerator snapJob;
    bool snapForTest, snapTestRunning;
    readonly List<Req> reqLive = new(), reqToUnbind = new();
    readonly Queue<Req> reqQueue = new();
    Req dateReq, slotPollReq;
    readonly List<(SI.Bindable.Bindings.Key, SI.Bindable.Bindings.ValueChangedCallback)> humanBinds = new();
    readonly HashSet<ulong> humanScopesBound = new(), humanProbed = new();
    readonly List<object> keepAlive = new(), squad = new();
    ulong humanKey;
    string humanClub, humanClubId, humanTeamName, humanManager, humanClubSource;
    readonly Dictionary<string, string> teamSnap = new();
    readonly Dictionary<string, object> tacticSnap = new(), profileSnap = new();
    readonly List<string> hintsSnap = new();
    string squadContextText, gameCurrencyName;
    int squadContextTokens;
    readonly Dictionary<int, object> slotStore = new();
    bool slotStoreLoaded, tacticInventoryRead;
    int? primarySlot, activeSlot;
    string primarySlotRaw, activeSlotRaw, slotMetaReadAt, lastMergedActiveSlotRaw;
    readonly HashSet<string> deadPlayerProps = new(), tacticScreensSeen = new();
    readonly System.Text.StringBuilder probe = new();
    bool probeWritten, tacticsProbeWritten;
    string probeFile, lastGameDate, gameDateKey, snapshotStatus;
    DateTime snapshotAt;
    float nextScheduledSnap, nextDateCheck, nextTacticScreenCheck, nextTreeHumanSearch, lastDateSnap;
    bool autoDumped, chatDirty, wasBusy, dirty;
    string lastCandidatesLog;
    static CoachChat Chat;
    readonly List<object> chatLines = new();
    readonly Dictionary<int, object> chatChips = new();
    readonly System.Text.StringBuilder inputText = new();
    int caret;
    readonly Queue<char> pendingChars = new();
    sealed class Status { public string text; }
    Status chatStatus;
    // Additional v0.7.3 state; the same fixtures also compile against v0.7.2.
    SI.Core.TypedValue snapTeamTv;
    readonly Dictionary<string, object> oppSnap = new();
    string oppForName, screenKey, screenVia, quickShownKey, lastTopRootName;
    bool oppComplete;
    float nextOppRetry, lastTacticScreenSeen, nextScreenCheck;
    readonly List<string> rootNamesSample = new();
    readonly Dictionary<string, string> answers = new();
    Req Ask(SI.Core.TypedValue source, string path, float timeout, bool keep, string owner)
        => new() { Done = true, Got = answers.ContainsKey(path) && answers[path] != null, Empty = answers.ContainsKey(path) && answers[path] == null, Str = answers.GetValueOrDefault(path) };
    static string ValueText(string text) => text;
    void SetHumanClub(string club, string source) { humanClub = club; humanClubSource = source; }
    void ClearTarget() { }
    void ChatDeactivate(string reason) { }
    void SetThinking(bool on) { }
    void ResetFullSnapshot() { }

    void Contaminate()
    {
        squad.Add("old player"); teamSnap["team.Name"] = "old club";
        tacticSnap["name"] = "old tactic"; slotStore[1] = "old slot"; slotStoreLoaded = true;
        squadContextText = "old context"; nextScheduledSnap = 900;
        chatLines.Add("old conversation"); chatChips[0] = "old player";
        oppSnap["opponent"] = "old opposition"; snapTeamTv = humanTv;
    }
    void AssertCleared()
    {
        Program.Check(squad.Count == 0 && teamSnap.Count == 0 && tacticSnap.Count == 0, "old snapshot removed");
        Program.Check(slotStore.Count == 0 && !slotStoreLoaded && nextScheduledSnap == 0, "slot cache and refresh reset");
        Program.Check(squadContextText == null && chatLines.Count == 0 && chatChips.Count == 0, "context and UI history reset");
    }
    void CompleteIdentityPoll()
    {
        UnityEngine.Time.unscaledTime += 10;
        PollSessionIdentity(); PollSessionIdentity();
    }
    public static void RunSessionTests(bool latest)
    {
        var b = new CoachBehaviour();
        b.AcceptDataSources(new(1), new(2), "human", "game"); b.Contaminate();
        b.AcceptDataSources(new(1), new(2), "other binding path", "game");
        Program.Check(b.squad.Count == 1, "same native sources preserve the session");
        var request = new Req { Cb = () => { } }; b.reqLive.Add(request);
        b.AcceptDataSources(null, null, null, null); b.AssertCleared();
        Program.Check(b.humanTv == null && b.gameTv == null && request.Done && SI.Bindable.BindingUtils.Unbound > 0,
            "source loss clears references and cancels bindings");
        if (latest) Program.Check(b.oppSnap.Count == 0 && b.snapTeamTv == null, "opposition data reset");

        b.AcceptDataSources(new(3), new(4), "human", "game");
        b.answers["UniqueId"] = "10"; b.answers["Club.UniqueId"] = "20";
        b.answers["Name"] = "Manager"; b.answers["Club.Name"] = "Club A";
        b.answers["DateCreated"] = "Career A"; b.CompleteIdentityPoll();
        var careerA = b.CareerCacheKey(); b.Contaminate();
        b.answers["Club.UniqueId"] = "21"; b.answers["Club.Name"] = "Club B"; b.CompleteIdentityPoll(); b.AssertCleared();
        Program.Check(b.humanClub == "Club B" && b.humanClubId == "21", "club transfer installs new identity");
        b.Contaminate(); b.answers["DateCreated"] = "Career B"; b.CompleteIdentityPoll(); b.AssertCleared();
        Program.Check(careerA != b.CareerCacheKey(), "careers with identical managers and clubs use separate caches");
        b.Contaminate(); b.answers["UniqueId"] = "11"; b.CompleteIdentityPoll(); b.AssertCleared();
        b.Contaminate(); b.answers["Club.UniqueId"] = null; b.answers["Club.Name"] = null; b.CompleteIdentityPoll(); b.AssertCleared();
        Program.Check(b.humanClub == null && b.sessionClubId == null, "leaving a club clears the previous team");
        b.Contaminate(); b.AcceptDataSources(new(5), new(6), "human", "game"); b.AssertCleared();
        Program.Check(b.sessionCareer == null, "source replacement requires fresh career identity");
    }
}

public static class Program
{
    static int checks;
    public static void Check(bool condition, string description)
    {
        if (!condition) throw new Exception(description);
        checks++;
    }
    static void SquadScope()
    {
        Check(CoachSquadPolicy.Exclusion(true, false, "club", null, "club") == null, "registered fit player included");
        Check(CoachSquadPolicy.Exclusion(true, false, "parent", "club", "club") == null, "registered incoming loanee at club included");
        Check(CoachSquadPolicy.Exclusion(true, false, "club", "elsewhere", "club") == "loaned out", "outgoing loanee excluded");
        Check(CoachSquadPolicy.Exclusion(true, true, "club", null, "club") == "injured", "injured player excluded");
        Check(CoachSquadPolicy.Exclusion(false, false, "club", null, "club") == "not registered", "unregistered first-team player excluded");
        Check(CoachSquadPolicy.Exclusion(null, false, "club", null, "club") == "registration not confirmed", "unknown registration never treated as confirmed");
        Check(CoachSquadPolicy.Exclusion(true, null, "club", null, "club") == "injury status unavailable", "unknown availability remains incomplete");
        Check(CoachSquadPolicy.Exclusion(true, false, "elsewhere", null, "club") == "not at this club", "departed player excluded");
        Check(CoachSquadPolicy.Exclusion(true, false, "club", "0", "club") == null, "empty loan-club sentinel is not an outgoing loan");
    }
    static IEnumerator EmptyStep() { yield break; }
    static IEnumerator CachedSteps(Action step)
    {
        for (int i = 0; i < 200; i++) { step(); yield return EmptyStep(); }
    }
    static void CoroutineScheduling()
    {
        int steps = 0, frames = 0;
        var job = CoachCoroutine.Flatten(CachedSteps(() => steps++), 8);
        Check(job.MoveNext() && steps < 200, "cached nested work yields before traversing the entire graph");
        while (job.MoveNext()) { if (++frames > 1000) throw new Exception("coroutine failed to finish"); }
        Check(steps == 200 && frames > 1, "bounded iterator preserves all work across frames");
        Check(CoachCoroutine.BatchWaitSeconds(8, 600, 700) >= 30, "small request batches get a bounded grace period");
        Check(CoachCoroutine.BatchWaitSeconds(10, 1400, 700) == 30, "batch deadline accounts for rate-limited request waves");
        Check(CoachSquadPolicy.Exclusion(true, false, "club", null, "club", "reserves", "senior") == "not in the first team", "known reserve assignment excluded");
        Check(CoachSquadPolicy.Exclusion(true, false, "club", null, "club", "senior", "senior") == null, "known senior assignment included");
    }
    static void NativeInputText()
    {
        Check(CoachInputText.Normalize("Sandro Tonali — Matías Soulé", 500) == "Sandro Tonali — Matías Soulé", "Unicode player names and punctuation preserved");
        Check(CoachInputText.Normalize("one\r\ntwo\tthree", 500) == "one two three", "multiline paste becomes a single question");
        Check(CoachInputText.Normalize("ab\0cd", 500) == "abcd", "control characters excluded");
        Check(CoachInputText.Normalize("abcdef", 3) == "abc", "question length enforced");
        Check(CoachInputText.Normalize("a\U0001F600b", 2) == "a", "length limit does not retain half an emoji surrogate pair");
        Check(CoachInputText.Normalize(null, 500) == "", "empty native values supported");
    }
    static void ContextProvenance()
    {
        var limitedScouting = new StringBuilder();
        CoachOpponentAdvice.AppendLimits(limitedScouting, new[] { "expectedStyle", "recentForm", "strengths", "weaknesses" });
        Check(limitedScouting.ToString().Contains("unknown, not blockers") && limitedScouting.ToString().Contains("expected style"), "limited scouting remains explicit uncertainty without becoming a prerequisite");
        Check(!limitedScouting.ToString().Contains("tell the manager to") && !limitedScouting.ToString().Contains("open the"), "opposition context does not direct screen-chasing for missing intelligence");
        var completeScouting = new StringBuilder(); CoachOpponentAdvice.AppendLimits(completeScouting, new string[0]);
        Check(completeScouting.Length == 0, "no missing-data checklist emitted for available scouting");
        Check(CoachTacticSnapshot.FreshnessLabel(true, "automatic club snapshot") == "current session", "automatic source has explicit current-session provenance");
        Check(CoachTacticSnapshot.FreshnessLabel(true, "Tactics screen (cached)") == "cached", "complete cached values are still labelled cached");
        Check(CoachTacticSnapshot.FreshnessLabel(false, null) == "unread", "unread slot cannot look freshly loaded");
        Check(CoachTacticSnapshot.HasFreshDetails(true, "Tactics screen"), "fresh observed fields remain usable even when other tactic fields are missing");
        Check(!CoachTacticSnapshot.HasFreshDetails(true, "Tactics screen (cached)"), "cached partial fields stay excluded");
        Check(!CoachTacticSnapshot.CanUseDetails(false, "Tactics screen"), "incomplete slot facts are not emitted");
        Check(!CoachTacticSnapshot.CanUseDetails(true, "Tactics screen (cached)"), "cached complete slot facts are not current facts");
        Check(!CoachTacticSnapshot.CanUseDetails(true, "CACHED"), "cache provenance is case insensitive");
        Check(CoachTacticSnapshot.CanUseDetails(true, "automatic slot snapshot"), "verified complete fresh slot facts are usable");
        Check(CoachReadableText.GameValue("{FormationsFaced={InPossessionFormations=System.Collections.Generic.List`1[SI.Core.TypedValue]}}") == null, "serialized formation objects rejected");
        Check(CoachReadableText.GameValue("2028 - Won against LiverpoolAgAAACAxMTEBIgMgMDExHAAAAGRpdUeSWkIAZmVyZJcCABlZY09PAQMAFA") == null, "embedded encoded match markup rejected");
        Check(CoachReadableText.GameValue("5-2-2-1 DM AM") == "5-2-2-1 DM AM", "readable formation text preserved");
        Check(CoachReadableText.GameValue("This should be a close contest.") != null, "plain staff assessment preserved");
        Check(CoachReadableText.ExplicitStars("9★") == null, "impossible nine-star display rejected");
        Check(CoachReadableText.ExplicitStars("18") == null, "unknown numeric star encoding not guessed");
        Check(CoachReadableText.ExplicitStars("{MaxValue=18;MinValue=18}") == null, "raw star ranges not converted using an assumed divisor");
        Check(CoachReadableText.ExplicitStars("4.5★") == "4.5★", "explicit five-star display accepted");
        Check(CoachReadableText.ExplicitStars("-1★") == null, "negative star sentinel rejected");
    }
    static void LoadCoverage()
    {
        var coverage = new CoachSnapshotCoverage
        {
            Finished = true, FirstTeamRead = true, RegistrationRead = true, TeamStatsRead = true, TacticInventoryRead = true,
            Players = 30, AttributesComplete = 30, SeasonStatsComplete = 30, TacticsExpected = 3, TacticsComplete = 3,
        };
        Check(coverage.Ready && coverage.Missing().Count == 0, "ready only with all requested categories loaded");
        Check(coverage.SquadComplete, "finished squad data can be retained during missing-category retries");
        coverage.TacticInventoryRead = false;
        Check(!coverage.Ready, "cached or partial slot inventory blocks ready");
        coverage.TacticInventoryRead = true;
        coverage.RegistrationRead = false;
        Check(!coverage.Ready && coverage.Missing().Contains("competition registration lists"), "unknown registration blocks ready");
        coverage.RegistrationRead = true; coverage.UnknownPlayers = 1;
        Check(!coverage.Ready, "unresolved player availability blocks ready");
        coverage.UnknownPlayers = 0; coverage.TacticsComplete = 2;
        Check(!coverage.Ready && coverage.Summary("").Contains("tactics 2/3"), "partial tactic read remains visible");
        Check(coverage.SquadComplete, "missing tactic details do not invalidate completed player data");
        coverage.TacticsComplete = 3; coverage.AttributesComplete = 29;
        Check(!coverage.Ready, "missing player attributes block ready");
        Check(!coverage.SquadComplete, "incomplete player data still requests a full squad refresh");
        coverage.AttributesComplete = 30; coverage.SeasonStatsComplete = 29;
        Check(!coverage.Ready, "missing explicit season stats block ready");
        coverage.SeasonStatsComplete = 30; coverage.TeamStatsRead = false;
        Check(!coverage.Ready, "missing team metrics block ready");
        coverage.TeamStatsRead = true; coverage.FirstTeamRead = false;
        Check(!coverage.Ready, "unread first-team list cannot appear as an empty complete squad");
        coverage.FirstTeamRead = true; coverage.Finished = false;
        Check(!coverage.Ready && coverage.Summary("Reading registration").Contains("Reading registration"), "active loading stage is visible");
        coverage.Finished = true; coverage.TacticsExpected = coverage.TacticsComplete = 0;
        Check(!coverage.Ready, "unknown tactic count cannot appear as complete zero of zero");
    }
    static void TacticSwitch()
    {
        Check(CoachTacticSnapshot.IsSlotSelectorName("TacticSlotIndex"), "registered tactic slot selector is eligible for a read-only probe");
        Check(!CoachTacticSnapshot.IsSlotSelectorName("PlayerTacticIndex"), "player-specific indices are not team tactic selectors");
        Check(CoachTacticSnapshot.MatchesSlotProbe("728", 1, "AK-MAIN", "728", "0", "AK-MAIN"), "exact team/slot/name evidence validates a selector response");
        Check(!CoachTacticSnapshot.MatchesSlotProbe("728", 1, "AK-MAIN", "728", "1", "AK-PRESS"), "ignored selector cannot relabel active settings as inactive");
        Check(!CoachTacticSnapshot.MatchesSlotProbe("728", 1, "AK-MAIN", "999", "0", "AK-MAIN"), "same named tactic belonging to another team is rejected");
        var gkValues = new Dictionary<string, double> { ["Handling"] = 12, ["Reflexes"] = 16, ["Eccentricity"] = 20, ["RushingOut"] = 20, ["TendencyToPunch"] = 20 };
        Check(CoachQualityProfile.GoalkeepingAverage(name => gkValues.TryGetValue(name, out var value) ? value : null) == 14, "goalkeeping skill average excludes high behavioural tendencies");
        gkValues["Eccentricity"] = gkValues["RushingOut"] = gkValues["TendencyToPunch"] = 1;
        Check(CoachQualityProfile.GoalkeepingAverage(name => gkValues.TryGetValue(name, out var value) ? value : null) == 14, "changing tendencies does not change a quality score");
        Check(CoachQualityProfile.GoalkeepingAverage(name => null) == null, "missing goalkeeper skills do not become zero quality");
        Check(CoachTacticSnapshot.SameTacticRead("1", "AK-PRESS", "1", "AK-PRESS"), "stable selected tactic can be committed");
        Check(!CoachTacticSnapshot.SameTacticRead("1", "AK-PRESS", "0", "AK-MAIN"), "in-flight previous-slot payload cannot be relabelled as the newly selected slot");
        Check(!CoachTacticSnapshot.SameTacticRead("1", "AK-PRESS", null, null), "unverifiable end identity cannot commit old settings");
        Check(!CoachTacticSnapshot.SameTacticRead("1", "AK-PRESS", "1", "Renamed"), "mid-read tactic rename rejects mixed snapshot");
        Check(CoachTacticSnapshot.SupportingRunsLabel("1128871265", "1484157235") == "Balanced", "live verified supporting-runs pair is Balanced, not Overlap Right");
        Check(CoachTacticSnapshot.SupportingRunsLabel("1128871265", "unknown") == null, "unverified supporting-runs pairs stay unknown");
        Check(CoachTacticSnapshot.PositionListPriority("TacticalPositionsCombined", new() { new() { ["OOPRole"] = "2" } }) > CoachTacticSnapshot.PositionListPriority("TacticalPositions(screen)", new() { new() { ["PositionRole"] = "4096" } }), "combined possession-phase roles outrank IP-only rows");
        var metrics = new Dictionary<string, string>();
        Check(CoachTeamStats.CollectPerformance(new() { ["plot[0].StatNameLabel"] = "Pas %", ["plot[0].SubjectStatValue"] = "90.4931564331055", ["plot.SpiderChartPointColour"] = "RGBA(1,2,3,255)", ["plot.TimeFrame"] = "This Season" }, metrics) == 1 && metrics["General performance: Pas %"] == "90.5", "chart metrics require a paired label and numeric value");
        Check(!CoachTeamStats.HasScopedMetrics(metrics), "unconfirmed competition cannot count as complete team stats");
        metrics["Statistics competition"] = "Premier League";
        Check(CoachTeamStats.HasScopedMetrics(metrics), "named metrics with native season and competition are scoped");
        var piContext = CoachTacticSnapshot.PlayerInstructionContext(new() { ["Player"] = "Tonali", ["Instructions[IP.PassingRisk]"] = "Take More Risks", ["InstructionCoverage"] = "incomplete" });
        Check(piContext == "Tonali: in possession passing risk = Take More Risks", "AI instruction context uses readable game labels without raw dictionary keys");
        Check(CoachTacticSnapshot.PlayerInstructionLabel("ForwardRuns", "PI_HOLD_POSITION") == "Make Fewer Runs", "FM27 forward runs label matches verified player card");
        Check(CoachTacticSnapshot.PlayerInstructionLabel("AttackingWidth", "PI_NONE") == "Standard", "verified Tonali attacking-width default is Standard");
        Check(CoachTacticSnapshot.PlayerInstructionLabel("FreedomOfMovement", "PI_NONE") == "Follow Tactical Position", "default freedom is tactical position, not no instructions");
        Check(CoachTacticSnapshot.PlayerInstructionLabel("HoldUpBall", "PI_NONE") == "No", "hold-up default follows card semantics");
        Check(CoachTacticSnapshot.PlayerInstructionLabel("Shooting", "PI_NONE") == "Standard", "default shooting is standard, not missing");
        Check(CoachTacticSnapshot.PlayerInstructionLabel("Tackling", "PI_GET_STUCK_IN") == "Tackle Harder", "verified tackle override label");
        for (int count = 1; count <= 3; count++)
            Check(CoachTacticSnapshot.InventoryComplete(new[] { "A", "B", "C" }, Enumerable.Range(0, 3).Select(i => (bool?)(i < count)).ToArray(), count), "verified inventory with " + count + " saved tactics");
        Check(!CoachTacticSnapshot.InventoryComplete(new[] { "A", "B", "C" }, new bool?[] { true, true, null }, 2), "unknown third slot blocks inventory");
        Check(!CoachTacticSnapshot.InventoryComplete(new[] { "A", "B", "C" }, new bool?[] { true, false, false }, 2), "native tactic count mismatch blocks inventory");
        Check(CoachTacticSnapshot.RequiredDetails(null, null, new()).Contains("formation"), "missing formation explicitly blocks tactic completion");
        Check(CoachTacticSnapshot.RequiredDetails("4-3-3", null, new()).Contains("mentality"), "missing mentality explicitly blocks tactic completion");
        Check(CoachTacticSnapshot.IsOwnInstructionList("TacticalPositionsInstructionOverrides"), "own position instructions included");
        Check(!CoachTacticSnapshot.IsOwnInstructionList("AllOppositionInstructionSuggestions"), "opposition suggestions cannot masquerade as our player instructions");
        Check(!CoachTacticSnapshot.IsPlayerInstructionData("InstructionCoverage"), "coverage metadata is not a player instruction");
        Check(CoachTacticSnapshot.IsPlayerInstructionData("PersonalisedInstructions"), "actual player instructions included");
        Check(CoachTacticSnapshot.IsPlayerInstructionData("TacticActiveTweaks"), "active tweaks included");
        Check(!CoachTacticSnapshot.IsPlayerInstructionData("PlayerInstructionGroupOptions"), "available instruction options are not treated as settings");
        Check(!CoachTacticSnapshot.IsPlayerInstructionData("SelectedPlayerInstruction"), "UI selection is not treated as a full instruction set");
        var snapshot = new Dictionary<string, object>();
        void Read(string name, string tempo, string removed = null)
        {
            var values = new SortedDictionary<string, string> { ["Tempo"] = tempo };
            var instructions = new Dictionary<string, string> { ["tempo"] = tempo };
            if (removed != null) instructions["pressing"] = removed;
            CoachTacticSnapshot.Replace(snapshot, values, new(),
                new() { ["inPossession"] = instructions }, "TeamTactic", name);
        }
        Read("Slot 1", "Low", "High"); snapshot["formationDerived"] = "4-3-3";
        Read("Slot 2", "High");
        var phases = (Dictionary<string, Dictionary<string, string>>)snapshot["teamInstructionsByPhase"];
        Check((string)snapshot["name"] == "Slot 2" && phases["inPossession"]["tempo"] == "High", "slot switch replaces settings");
        Check(!phases["inPossession"].ContainsKey("pressing") && !snapshot.ContainsKey("formationDerived"), "absent instructions and derived metadata removed");
        Read("Slot 2", "Normal");
        phases = (Dictionary<string, Dictionary<string, string>>)snapshot["teamInstructionsByPhase"];
        Check(phases["inPossession"]["tempo"] == "Normal", "same-slot edits replace settings");
        CoachTacticSnapshot.Replace(snapshot, new(), new(), new(), "TeamTactic", "Fresh",
            new[] { "tempo", "mentality" }, true);
        Check(((List<string>)snapshot["expectedInstructionKeys"]).Contains("tempo") && (bool)snapshot["playerInstructionsRead"],
            "screen snapshot retains fresh instruction coverage metadata");
        var positionRows = Enumerable.Range(0, 11).Select(i => new Dictionary<string, string> { ["Instructions[PersonalisedInstructions]"] = "none", ["InstructionCoverage"] = "complete" }).ToList();
        Check(CoachTacticSnapshot.PlayerInstructionRowsComplete(new() { ["TacticalPositions"] = positionRows }), "complete position instruction list recognized");
        positionRows[5]["InstructionCoverage"] = "incomplete";
        Check(!CoachTacticSnapshot.PlayerInstructionRowsComplete(new() { ["TacticalPositions"] = positionRows }), "one unresolved player instruction source blocks all-eleven completeness");
        Check(!CoachTacticSnapshot.PlayerInstructionRowsComplete(new() { ["TacticalPositions"] = new() { new() { ["PlayerInstructionGroupOptions"] = "choices" } } }), "UI option rows cannot establish complete player instructions");
    }
    sealed class FakeRouter : HttpMessageHandler
    {
        public readonly TaskCompletionSource<bool> Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource<bool> Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string LastBody;
        int calls;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            LastBody = await request.Content.ReadAsStringAsync();
            if (Interlocked.Increment(ref calls) == 1) { Started.SetResult(true); await Release.Task; }
            return new(HttpStatusCode.OK) { Content = new StringContent("{\"choices\":[{\"message\":{\"content\":\"A reply [mood:pleased]\"}}]}") };
        }
    }
    static async Task Until(Func<bool> predicate)
    {
        for (int i = 0; i < 500; i++) { if (predicate()) return; await Task.Delay(10); }
        throw new Exception("timed out waiting for test request");
    }
    static async Task LateReply()
    {
        var key = Path.GetTempFileName();
        using var router = new FakeRouter(); using var client = new HttpClient(router);
        typeof(CoachChat).GetField("http", BindingFlags.Static | BindingFlags.NonPublic).SetValue(null, client);
        try
        {
            File.WriteAllText(key, "KEY=fake-regression-token");
            var chat = new CoachChat { KeyFile = key };
            Check(chat.Ask("OLD CAREER question", "system", "{}", false), "request started");
            await router.Started.Task;
            chat.ClearHistory(); router.Release.SetResult(true);
            await Until(() => !chat.Busy);
            Check(chat.Done.IsEmpty, "late old-session reply discarded");
            Check(chat.Ask("new question", "system", "{}", false), "new session request accepted");
            await Until(() => !chat.Busy);
            Check(!router.LastBody.Contains("OLD CAREER"), "late reply cannot restore old conversation history");
            Check(chat.Done.TryDequeue(out var result) && chat.IsCurrent(result), "current reply delivered");
            chat.ClearHistory(); Check(!chat.IsCurrent(result), "already queued result rejected after reset");
        }
        finally { File.Delete(key); }
    }
    static void FaceTransparency(string sheetPath)
    {
        var original = File.ReadAllBytes(sheetPath);
        var normalized = (byte[])original.Clone();
        Check(CoachFacePixels.NormalizeBackground(normalized) > 800, "opaque background aliases removed from reaction frames");
        const int width = 32, height = 40, frames = 11, sheetWidth = width * frames;
        for (int frame = 0; frame < frames; frame++)
        {
            int transparent = 0; bool rgbPreserved = true, subjectAlphaPreserved = true;
            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                {
                    int i = (y * sheetWidth + frame * width + x) * 4;
                    rgbPreserved &= original[i] == normalized[i] && original[i + 1] == normalized[i + 1] && original[i + 2] == normalized[i + 2];
                    if (normalized[i + 3] == 0) transparent++;
                    // Skin and the darker blue jacket are outside the background palette tolerance.
                    bool background = Math.Abs(original[i] - 26) <= 2 && Math.Abs(original[i + 1] - 29) <= 2 && Math.Abs(original[i + 2] - 36) <= 2;
                    if (!background) subjectAlphaPreserved &= original[i + 3] == normalized[i + 3];
                }
            Check(rgbPreserved, "portrait RGB pixels preserved");
            Check(subjectAlphaPreserved, "skin and clothing alpha preserved");
            Check(transparent >= 200, $"frame {frame} has a transparent background");
        }
        Check(CoachFacePixels.NormalizeBackground(normalized) == 0, "normalization is idempotent");
        var opaque = new byte[] { 26, 28, 35, 255 };
        Check(CoachFacePixels.NormalizeBackground(opaque) == 0 && opaque[3] == 255, "no background guessed without a transparent key");
    }
    public static async Task Main(string[] args)
    {
        ContextProvenance(); NativeInputText(); CoroutineScheduling(); SquadScope(); LoadCoverage(); TacticSwitch(); CoachBehaviour.RunSessionTests(args.Contains("latest")); await LateReply();
        FaceTransparency(Path.Combine(args[1], "coach_faces_32x40.rgba"));
        Console.WriteLine($"PASS: {checks} regression checks");
    }
}
