// FM Coach - proof of concept v0.3. READ-ONLY: only reads/subscribes to values via the game's UI data bindings; never sets data or sends actions.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.Injection;
using UnityEngine;
using UnityEngine.UIElements;
using UnityEngine.InputSystem;
using FM.UI;
using SI.Bindable;
using SI.Bindable.Reference.Core;
using SI.Core;

[BepInPlugin("local.fmcoach.poc", "FM Coach (PoC)", "0.7.30")]
public class CoachPlugin : BasePlugin
{
    internal static ManualLogSource L;
    internal static string OutDir;
    public override void Load()
    {
        L = Log;
        OutDir = Path.Combine(Paths.BepInExRootPath, "fmcoach");
        Directory.CreateDirectory(OutDir);
        // ---- AI chat config (BepInEx\config\local.fmcoach.poc.cfg). The key itself is only ever read from KeyFile at request time.
        var docs = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "fmcoach");
        var keyFile = Config.Bind("AI", "KeyFile", Path.Combine(docs, ".key"), "File holding the OpenRouter key as KEY=sk-or-... (never logged or copied)").Value;
        var model = Config.Bind("AI", "Model", "anthropic/claude-haiku-5.5", "OpenRouter model id").Value;
        var maxTok = Config.Bind("AI", "MaxTokens", 600, "max_tokens per reply").Value;
        var timeout = Config.Bind("AI", "TimeoutSeconds", 60, "HTTP timeout").Value;
        CoachBehaviour.PromptFile = Config.Bind("AI", "PromptFile", Path.Combine(docs, "coach_system_prompt.txt"), "Editable coach system prompt (re-read when it changes); the copy embedded in the DLL is used if this file is missing or empty").Value;
        CoachBehaviour.TestDir = Config.Bind("AI", "TestDir", Path.Combine(docs, "test"), "Folder watched for ask.txt (test hook); answer.txt is written here").Value;
        try { Directory.CreateDirectory(CoachBehaviour.TestDir); } catch { }
        // ---- v0.7 data layer + UI state
        CoachBehaviour.ProbeEnabled = Config.Bind("Debug", "Probe", true, "Write BepInEx\\fmcoach\\probe_*.txt (which team/club/tactic properties resolve) after each save load").Value;
        CoachBehaviour.SlotProviderProbes = Config.Bind("Debug", "SlotProviderProbes", false, "Diagnostic read-only inactive-slot selector probes; the tested FM27 providers ignore these selectors").Value;
        CoachBehaviour.DataEnabled = Config.Bind("Data", "Enabled", true, "Read the manager's squad and tactic (read-only) for the coach").Value;
        CoachBehaviour.RefreshMinutes = Math.Max(1f, Config.Bind("Data", "RefreshMinutes", 15f, "Re-read squad/tactic every N real minutes (also on game-date change and Tactics screen visits)").Value);
        var uiTab = Config.Bind("UI", "Tab", "Chat", "Last panel tab (Chat|Player|Insights|Tactic|Debug)");
        var uiMax = Config.Bind("UI", "Maximized", false, "Panel maximized (wider/taller)");
        var uiMin = Config.Bind("UI", "Minimized", false, "Panel minimized to the title bar");
        CoachBehaviour.tab = Array.IndexOf(CoachBehaviour.Tabs, uiTab.Value) >= 0 ? uiTab.Value : "Chat";
        CoachBehaviour.maximized = uiMax.Value;
        CoachBehaviour.startMinimized = uiMin.Value;
        CoachBehaviour.SaveUiState = (t, mx, mn) => { try { uiTab.Value = t; uiMax.Value = mx; uiMin.Value = mn; } catch { } };
        L.LogInfo($"Data: enabled={CoachBehaviour.DataEnabled} probe={CoachBehaviour.ProbeEnabled} refresh={CoachBehaviour.RefreshMinutes}min; UI: tab={CoachBehaviour.tab} maximized={uiMax.Value} minimized={uiMin.Value}");
        CoachBehaviour.Chat = new CoachChat { KeyFile = keyFile, Model = model, MaxTokens = maxTok, TimeoutSeconds = timeout };
        L.LogInfo("AI chat: model=" + model + " max_tokens=" + maxTok + " timeout=" + timeout + "s keyfile=" + (File.Exists(keyFile) ? "found" : "MISSING") + " testdir=" + CoachBehaviour.TestDir);
        ClassInjector.RegisterTypeInIl2Cpp<CoachBehaviour>();
        var go = new GameObject("FMCoachPoC");
        UnityEngine.Object.DontDestroyOnLoad(go);
        go.hideFlags = HideFlags.HideAndDontSave;
        go.AddComponent<CoachBehaviour>();
        L.LogInfo("FM Coach v" + typeof(CoachPlugin).Assembly.GetName().Version.ToString(3) + " loaded (read-only game access; AI chat via OpenRouter). F10 or Ctrl+Shift+` = show/hide Assistant Coach; F11 or Ctrl+Shift+\\ = debug dump; F9 = cycle coach face (debug).");
    }
}

public partial class CoachBehaviour : MonoBehaviour
{
    public CoachBehaviour(IntPtr p) : base(p) { }

    // ---------- fields to read (property names from the game's own registry dump) ----------
    static readonly (string label, string prop)[] InfoFields =
    {
        ("Age", "Age"), ("Position", "PositionCombinedString"), ("Pos", "NaturalPositionShortString"),
        ("Nation", "NationalityText"), ("Club", "Club.Name"),
        ("Value", "TransferValueRange"), ("Wage", "ContextContractWeeklyWage"), ("Club id", "Club.UniqueID"),
        ("PA (perceived)", "PerceivedPotentialAbility"),
        ("Ability", "CurrentAbilityStarRange"), ("Potential", "PotentialAbilityStarRange"),
        ("Ability (coach)", "CoachCurrentAbilityStarRange"), ("Potential (coach)", "CoachPotentialAbilityStarRange"),
        ("Ability (scout)", "ScoutCurrentAbilityStarRange"), ("Potential (scout)", "ScoutPotentialAbilityStarRange"),
        ("Role stars", "CurrentRoleStarRange"), ("Reputation", "PlayerWorldReputationStarRating"),
        ("Condition", "ConditionString"), ("Fitness", "Fitness"), ("Morale", "MoraleString"), ("Sharpness", "Sharpness"), ("Fatigue", "Fatigue"),
        ("Injured", "IsInjured"), ("Personality", "Personality"), ("Height", "Height"), ("Best role", "BestRole"),
        ("Apps", "Appearances"), ("Human club (ctx)", "ContextHumanClub.Name"), ("Contract club", "ContractClub.Name"), ("Human team", "TeamFromHumanTeam"), ("Is human", "IsCurrentHuman"), ("Goals", "Goals"), ("Assists", "Assists"), ("Avg rating", "AverageRating"),
    };
    static readonly (string group, (string abbr, string prop)[] attrs)[] AttrGroups =
    {
        ("TECHNICAL", new[] { ("Cor","AttributeCorners"),("Cro","AttributeCrossing"),("Dri","AttributeDribbling"),("Fin","AttributeFinishing"),("Fir","AttributeFirstTouch"),("Fre","AttributeFreeKicks"),("Hea","AttributeHeading"),("Lon","AttributeLongShots"),("L Th","AttributeLongThrows"),("Mar","AttributeMarking"),("Pas","AttributePassing"),("Pen","AttributePenaltyTaking"),("Tck","AttributeTackling"),("Tec","AttributeTechnique") }),
        ("MENTAL", new[] { ("Agg","AttributeAggression"),("Ant","AttributeAnticipation"),("Bra","AttributeBravery"),("Cmp","AttributeComposure"),("Cnt","AttributeConcentration"),("Dec","AttributeDecisions"),("Det","AttributeDetermination"),("Fla","AttributeFlair"),("Ldr","AttributeLeadership"),("OtB","AttributeMovement"),("Pos","AttributePositioning"),("Tea","AttributeTeamwork"),("Vis","AttributeVision"),("Wor","AttributeWorkRate") }),
        ("PHYSICAL", new[] { ("Acc","AttributeAcceleration"),("Agi","AttributeAgility"),("Bal","AttributeBalance"),("Jum","AttributeJumpingReach"),("Nat","AttributeNaturalFitness"),("Pac","AttributePace"),("Sta","AttributeStamina"),("Str","AttributeStrength") }),
        ("GOALKEEPING", new[] { ("Aer","AttributeAerialReach"),("Cmd","AttributeCommandOfArea"),("Com","AttributeCommunication"),("Ecc","AttributeEccentricity"),("Han","AttributeHandling"),("Kic","AttributeKicking"),("1v1","AttributeOneOnOnes"),("Ref","AttributeReflexes"),("TRO","AttributeRushingOut"),("Thr","AttributeThrowing"),("Pun","AttributeTendencyToPunch") }),
    };

    static readonly string[] Scopes = { "person", "player", "playerinfo" };
    static readonly HashSet<string> SkipSections = new() { "Tooltip", "Overlay" };
    static readonly HashSet<string> NonPlayerSections = new() { "MenuTitle", "Menu" };
    static readonly string[] SectionPriority = { "Card", "CardBody", "Report", "ReportBody", "MenuBody", "FullscreenMatchSimulation" };

    bool show = false, collapsed = startMinimized; // hidden until summoned with Ctrl+Shift+D
    float nextScan, nextBuild;
    bool dirty = true, autoDumped;
    string status = "Scanning... (v0.7.8)";
    readonly HashSet<string> loggedOnce = new();

    // current target
    ulong targetKey; string targetScope, targetSection, targetName, targetRaw;
    Bindings.Key targetK;
    readonly Dictionary<string, string> values = new();
    readonly List<(Bindings.Key key, Bindings.ValueChangedCallback cb)> binds = new();
    readonly List<object> keepAlive = new();
    string lastCandidatesLog = "";
    // human (user) club: read from the title-bar manager button's person scope (Club.Name), else ContextHumanClub on the viewed player, else fallback
    float nextClubDebug;
    ulong humanKey; string humanClub, humanClubSource, humanManager;
    readonly List<(Bindings.Key key, Bindings.ValueChangedCallback cb)> humanBinds = new();


    // UI
    VisualElement panelRoot, body, header; Label dumpBtn, titleLbl;
    float posLeft = -1, posTop = -1;
    bool dragging; Vector2 dragStart; float dragL, dragT;

    static readonly Color CardBg = new(0.106f, 0.118f, 0.153f, 0.98f);    // navy-charcoal
    static readonly Color HeaderBg = new(0.135f, 0.149f, 0.192f, 1f);
    static readonly Color Border = new(1f, 1f, 1f, 0.12f);
    static readonly Color Divider = new(1f, 1f, 1f, 0.10f);
    static readonly Color TextMain = new(0.95f, 0.95f, 0.96f, 1f);
    static readonly Color TextDim = new(0.62f, 0.65f, 0.70f, 1f);
    static readonly Color Accent = new(0.40f, 0.85f, 0.45f, 1f);

    void LogOnce(string where, Exception ex) { if (loggedOnce.Add(where)) CoachPlugin.L.LogError(where + " (logged once): " + ex); }

    static BindingSubsystem BS()
    {
        BindingSubsystem bs = null;
        try { bs = EmbeddedDataHandler.s_bindingSubsystem; } catch { }
        if (bs == null) { try { bs = NewsActionHandlerModule.m_bindingSubsystem; } catch { } }
        return bs;
    }

    // FM embedded text: \x01<base64 link data>\x02<display text>\x01  -> keep display text
    internal static string Clean(string s)
    {
        if (s == null) return null;
        var sb = new StringBuilder(); bool inData = false, tag = false;
        foreach (var c in s)
        {
            if (c == '\x01') { inData = !inData; continue; }
            if (c == '\x02') { inData = false; continue; }
            if (inData) continue;
            if (c == '<') { tag = true; continue; }
            if (c == '>') { tag = false; continue; }
            if (tag || char.IsControl(c)) continue;
            sb.Append(c);
        }
        var r = sb.ToString().Trim();
        return r.Length > 90 ? r.Substring(0, 90) + "…" : r;
    }

    readonly Dictionary<string, string> rawValues = new();
    static readonly HashSet<string> PanelHidden = new() { "Fatigue", "ContextHumanClub.Name", "ContractClub.Name", "TeamFromHumanTeam", "IsCurrentHuman", "Club.UniqueID" };

    static string PropName(uint id)
    {
        try { var i = PropertyIdentifierSet.Instance.GetInfo(id); if (i != null) return i.Name; } catch { }
        return "#" + id;
    }

    // Unwraps Records / DynamicReferences (Dictionary<PropertyID, TypedValue>) into "{Name=value; ...}"
    internal static string Describe(TypedValue tv, int depth = 0)
    {
        if (tv == null) return null;
        try { if (tv.IsNull) return null; } catch { return null; }
        Il2CppSystem.Object o = null;
        try { o = tv.Get(); } catch { }
        if (o != null && depth < 3)
        {
            Record rec = null;
            try { rec = o.TryCast<Record>(); } catch { }
            if (rec != null)
            {
                var parts = new List<string>();
                try { foreach (var kv in rec) parts.Add(PropName(kv.Key) + "=" + (Describe(kv.Value, depth + 1) ?? "null")); } catch (Exception ex) { parts.Add("err:" + ex.GetType().Name); }
                return "{" + string.Join("; ", parts) + "}";
            }
        }
        try { return tv.AsString(); } catch { return o?.ToString(); }
    }

    static readonly string[] PreferredKeys = { "PropertyValue", "Value", "AttributeValue", "CurrentValue", "DisplayValue", "ActualValue", "Rating", "Number" };

    static Dictionary<string, string> ParseRecord(string d)
    {
        var r = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (d == null || !d.StartsWith("{")) return r;
        int depth = 0, start = 1;
        for (int i = 1; i < d.Length; i++)
        {
            char c = d[i];
            if (c == '{') depth++;
            else if (c == '}') { if (depth == 0) { Add(d.Substring(start, i - start)); break; } depth--; }
            else if (c == ';' && depth == 0) { Add(d.Substring(start, i - start)); start = i + 1; }
        }
        return r;
        void Add(string part) { var eq = part.IndexOf('='); if (eq > 0) r[part.Substring(0, eq).Trim()] = part.Substring(eq + 1).Trim(); }
    }

    static bool IsNum(string v) => v != null && double.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _);

    // Turn a raw described value into what the panel shows
    internal static string Display(string prop, string raw)
    {
        if (raw == null) return null;
        if (prop == "AverageRating")
        {
            var rr = raw.StartsWith("{") ? ParseRecord(raw).Values.FirstOrDefault(IsNum) : Clean(raw);
            if (rr != null && double.TryParse(rr, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var dr))
                return dr <= 0 ? null : dr.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
        }
        if (prop.EndsWith("StarRange") || prop.EndsWith("StarRating"))
        {
            if (!raw.StartsWith("{")) return CoachReadableText.ExplicitStars(Clean(raw));
            var stars = ParseRecord(raw);
            foreach (var key in new[] { "DisplayValue", "DisplayText", "Text" })
                if (stars.TryGetValue(key, out var display) && CoachReadableText.ExplicitStars(Clean(display)) is string shown) return shown;
            return null;
        }
        if (!raw.StartsWith("{")) { var c = Clean(raw); return c.StartsWith("FM.UI.") || c.StartsWith("SI.") || c.StartsWith("System.") ? null : c; }
        var rec = ParseRecord(raw);
        string min = rec.Keys.FirstOrDefault(k => k.EndsWith("MinValue") || k == "Min" || k.EndsWith("Min"));
        string max = rec.Keys.FirstOrDefault(k => k.EndsWith("MaxValue") || k == "Max" || k.EndsWith("Max"));
        if (prop.StartsWith("Attribute"))
        {
            foreach (var k in PreferredKeys) if (rec.TryGetValue(k, out var v) && IsNum(v)) return v;
            if (min != null && max != null && IsNum(rec[min]) && IsNum(rec[max])) return rec[min] == rec[max] ? rec[min] : rec[min] + "-" + rec[max];
            var firstNum = rec.FirstOrDefault(kv => IsNum(kv.Value) && !kv.Key.StartsWith("Can") && !kv.Key.StartsWith("Is"));
            if (firstNum.Value != null) return firstNum.Value;
        }
        foreach (var k in new[] { "TransferValueRangeString", "DisplayValue" })
            if (rec.TryGetValue(k, out var dv) && !string.IsNullOrWhiteSpace(dv) && dv != "-") return Clean(dv);
        var str = rec.FirstOrDefault(kv => kv.Key.EndsWith("String") || kv.Key.EndsWith("Text"));
        if (str.Value != null) return Clean(str.Value);
        if (min != null && max != null) return Clean(rec[min]) + " - " + Clean(rec[max]);
        return Clean(raw.Length > 80 ? raw.Substring(0, 80) + "…" : raw);
    }

    static string Read(Bindings b, Bindings.Key key, string path)
    {
        try
        {
            var span = Il2CppSystem.MemoryExtensions.AsSpan(path);
            var tv = b.Get(ref key, ref span);
            if (tv == null || tv.IsNull) return null;
            return Describe(tv);
        }
        catch { return null; }
    }

    VisualElement MainRoot()
    {
        var docs = Resources.FindObjectsOfTypeAll(Il2CppType.Of<UIDocument>());
        foreach (var o in docs)
        {
            var d = o.TryCast<UIDocument>();
            if (d == null || !d.isActiveAndEnabled) continue;
            if (d.name != "MainUIDocument") continue;
            try { return d.rootVisualElement; } catch { }
        }
        return null;
    }

    static string Classes(VisualElement e)
    {
        var sb = new StringBuilder();
        try
        {
            var en = e.GetClasses().GetEnumerator();
            var ie = en.Cast<Il2CppSystem.Collections.IEnumerator>();
            int n = 0;
            while (ie.MoveNext() && n++ < 12) sb.Append(ie.Current?.ToString()).Append(' ');
        }
        catch { }
        return sb.ToString().Trim();
    }

    // ---------- detection ----------
    struct Cand { public Bindings.Key k; public string scope, section, el, name, raw; public int depth; public float area; }

    List<Cand> FindCandidates(Bindings b, VisualElement root, List<string> debug)
    {
        var res = new List<Cand>();
        var seen = new HashSet<ulong>();
        var stack = new Stack<(VisualElement e, string sec, int d)>();
        var h0 = root.hierarchy;
        for (int i = 0; i < h0.childCount; i++) { var c = h0[i]; stack.Push((c, c.name, 1)); }
        int n = 0;
        while (stack.Count > 0 && n < 40000)
        {
            var (e, sec, d) = stack.Pop(); n++;
            if (e == null || e.panel == null) continue;
            if (panelRoot != null && e.Pointer == panelRoot.Pointer) continue;
            if (SkipSections.Contains(sec)) continue;
            try { if (e.resolvedStyle.display == DisplayStyle.None) continue; } catch { }
            Bindings.Key k = default; bool ok = false;
            try { k = BindingUtils.GetViewKey(e); ok = k.m_key != 0; } catch { }
            if (ok && seen.Add(k.m_key))
            {
                foreach (var sc in Scopes)
                {
                    var raw = Read(b, k, sc + ".Name");
                    if (string.IsNullOrWhiteSpace(raw)) continue;
                    float area = 0; try { var wb = e.worldBound; area = wb.width * wb.height; } catch { }
                    res.Add(new Cand { k = k, scope = sc, section = sec, el = e.name, name = Clean(raw), raw = raw, depth = d, area = area });
                    break;
                }
                if (debug != null && debug.Count < 400)
                    debug.Add($"key {k.m_key:x} sec={sec} d={d} el='{e.name}' type={e.GetIl2CppType().Name} classes='{Classes(e)}'");
            }
            var h = e.hierarchy;
            for (int i = 0; i < h.childCount; i++) stack.Push((h[i], sec, d + 1));
        }
        return res;
    }

    static bool IsProfileCandidate(Cand c) =>
        (c.section == "ReportBody" || c.section == "Report") && c.el != null && c.el.IndexOf("banner", StringComparison.OrdinalIgnoreCase) >= 0;

    // A profile is "valid" once real attribute values (1-20) have arrived; -1/missing everywhere means not a usable player profile.
    int RealAttributeCount() => AttrGroups.SelectMany(g => g.attrs).Count(a => { var v = V(a.prop); return v != null && int.TryParse(v, out var n) && n >= 1 && n <= 20; });
    internal bool ValidProfile => targetKey != 0 && RealAttributeCount() >= 8;

    static int SecRank(string s) { int i = Array.IndexOf(SectionPriority, s); return i < 0 ? 99 : i; }

    void Scan()
    {
        var bs = BS();
        if (bs == null) { SetStatus("Binding system not ready (load a save)"); return; }
        var b = bs.Cast<Bindings>();
        var root = MainRoot();
        if (root == null) { SetStatus("MainUIDocument not found"); return; }
        EnsurePanel(root);
        var all = FindCandidates(b, root, null);
        var titleCands = all.Where(c => c.section == "MenuTitle").ToList();
        UpdateHumanClub(b, titleCands);
        FindHumanInTree(b);
        ProbeHumans(b, all);
        if (HumanClub() == null && Time.unscaledTime >= nextClubDebug)
        {
            nextClubDebug = Time.unscaledTime + 60f;
            CoachPlugin.L.LogWarning("Club not detected. Title-bar person scopes: " + (titleCands.Count == 0 ? "(none)" : string.Join(" | ", titleCands.Select(c => $"{c.el}/{c.scope}:{c.name}")))
                + "; all person scopes: " + string.Join(" | ", all.Select(c => $"{c.section}/{c.el}/{c.scope}:{c.name}"))
                + "; viewed ContextHumanClub.Name=" + (V("ContextHumanClub.Name") ?? "(none)"));
        }
        // Only a player profile counts as "viewed": the report area's player banner (report-player-banner-max etc.).
        // Inbox/news people (MenuBody), search results (ModalDialog) and title-bar people are never the viewed player.
        var cands = all.Where(IsProfileCandidate).ToList();
        var log = string.Join(" | ", cands.Select(c => $"{c.section}/{c.el}/{c.scope}:{c.name}"));
        if (log != lastCandidatesLog) { lastCandidatesLog = log; CoachPlugin.L.LogInfo("Candidates: " + (log.Length == 0 ? "(none)" : log)); }
        if (cands.Count == 0)
        {
            if (targetKey != 0) { CoachPlugin.L.LogInfo("Viewing: none (no player profile open)"); ClearTarget(); }
            SetStatus("No player profile open");
            return;
        }
        // prefer popup/card sections, then the largest on-screen owner (the profile page), then deepest
        var best = cands.OrderBy(c => SecRank(c.section)).ThenByDescending(c => c.area).ThenByDescending(c => c.depth).First();
        if (best.k.m_key != targetKey || best.scope != targetScope)
        {
            ClearTarget();
            targetKey = best.k.m_key; targetK = best.k; targetScope = best.scope; targetSection = best.section; targetName = best.name; targetRaw = best.raw;
            CoachPlugin.L.LogInfo($"Viewing: {best.name} (section {best.section}, scope {best.scope})");
            Subscribe(b);
            dirty = true;
        }
    }

    void UpdateHumanClub(Bindings b, List<Cand> titleCands)
    {
        if (titleCands.Count == 0) return;
        var c = titleCands[0];
        if (c.k.m_key == humanKey) return;
        foreach (var (key, cb) in humanBinds) { try { var k = key; BindingUtils.UnbindIfValid(ref k, cb); } catch { } }
        humanBinds.Clear();
        humanKey = c.k.m_key; humanManager = c.name;
        var path = c.scope + ".Club.Name";
        var now = Read(b, c.k, path);
        if (!string.IsNullOrWhiteSpace(now)) { humanClub = Clean(now); humanClubSource = "title-bar manager " + path; dirty = true; }
        try
        {
            int callbackSession = dataSessionVersion;
            var cb = DelegateSupport.ConvertDelegate<Bindings.ValueChangedCallback>((Action<Bindings.Key, SI.Core.TypedValue>)((k, v) =>
            {
                try { if (callbackSession != dataSessionVersion) return; if (v != null && !v.IsNull) { humanClub = Clean(v.AsString()); humanClubSource = "title-bar manager " + path; dirty = true; CoachPlugin.L.LogInfo("Human club: " + humanClub + " (manager " + humanManager + ")"); } } catch { }
            }));
            keepAlive.Add(cb);
            var scope = c.k; var span = Il2CppSystem.MemoryExtensions.AsSpan(path);
            humanBinds.Add((BindingUtils.BindWithinScope(b, ref scope, ref span, panelRoot, cb), cb));
        }
        catch (Exception ex) { LogOnce("HumanClub", ex); }
    }

    readonly HashSet<ulong> humanProbed = new();
    // Find the human manager among visible people (inbox, news, staff lists) via person.IsCurrentHuman, then read his Club.Name.
    void ProbeHumans(Bindings b, List<Cand> all)
    {
        if (!string.IsNullOrWhiteSpace(humanClub)) return;
        foreach (var c in all)
        {
            if (humanProbed.Count >= 80) break;
            if (!humanProbed.Add(c.k.m_key)) continue;
            var cand = c;
            BindOnce(b, cand.k, cand.scope + ".IsCurrentHuman", humanBinds, d =>
            {
                if (d == null) return;
                if (d.Equals("True", StringComparison.OrdinalIgnoreCase) || d == "1")
                {
                    humanManager = cand.name;
                    CoachPlugin.L.LogInfo("Human manager found: " + cand.name + " (" + cand.section + ")");
                    BindOnce(b, cand.k, cand.scope + ".Club.Name", humanBinds, club =>
                    {
                        if (string.IsNullOrWhiteSpace(club)) return;
                        SetHumanClub(club, "IsCurrentHuman person '" + cand.name + "' -> Club.Name");
                    });
                }
            });
        }
    }

    void BindOnce(Bindings b, Bindings.Key scopeKey, string path, List<(Bindings.Key, Bindings.ValueChangedCallback)> store, Action<string> onValue)
    {
        try
        {
            var now = Read(b, scopeKey, path);
            if (now != null) onValue(now);
            int callbackSession = dataSessionVersion;
            var cb = DelegateSupport.ConvertDelegate<Bindings.ValueChangedCallback>((Action<Bindings.Key, TypedValue>)((k, v) =>
            {
                try { if (callbackSession == dataSessionVersion) onValue(Describe(v)); } catch { }
            }));
            keepAlive.Add(cb);
            var scope = scopeKey; var span = Il2CppSystem.MemoryExtensions.AsSpan(path);
            store.Add((BindingUtils.BindWithinScope(b, ref scope, ref span, panelRoot, cb), cb));
        }
        catch (Exception ex) { LogOnce("BindOnce " + path, ex); }
    }

    string humanClubId, humanTeamName;
    float nextTreeHumanSearch;
    readonly HashSet<ulong> humanScopesBound = new();

    // The game keeps global 'Human' (PersonReference) and 'humanteam' (TeamReference) nodes in the binding tree
    // (e.g. under OverviewTitleBar.../NavBarBindingSetter). Find their parent scopes and read the club through them.
    void FindHumanInTree(Bindings b)
    {
        if (!string.IsNullOrWhiteSpace(humanClub) || Time.unscaledTime < nextTreeHumanSearch) return;
        nextTreeHumanSearch = Time.unscaledTime + 5f;
        var found = new List<(Bindings.Key parent, string name, string path)>();
        try
        {
            var rk = Bindings.RootKey;
            var root = b.GetNode(ref rk)?.TryCast<Bindings.Node>();
            int budget = 20000;
            void Walk(Bindings.Node n, int depth, string path)
            {
                for (var c = n; c != null && budget > 0; c = c.NextSibling)
                {
                    budget--;
                    var nm = c.Name;
                    if ((nm == "Human" || nm == "humanteam") && c.Parent != null) found.Add((c.Parent.Key, nm, path + "/" + nm));
                    if (depth < 6 && c.FirstChild != null) Walk(c.FirstChild, depth + 1, path + "/" + nm);
                }
            }
            if (root?.FirstChild != null) Walk(root.FirstChild, 0, "");
        }
        catch (Exception ex) { LogOnce("HumanTree", ex); return; }
        // prefer the title bar (global), then the rest
        foreach (var f in found.OrderBy(f => f.path.Contains("TitleBar") ? 0 : 1).Take(6))
        {
            if (!humanScopesBound.Add(f.parent.m_key ^ (ulong)f.name.GetHashCode())) continue;
            var ff = f;
            CoachPlugin.L.LogInfo("Human node found at " + ff.path);
            if (ff.name == "Human")
            {
                BindOnce(b, ff.parent, "Human.Name", humanBinds, v => { if (!string.IsNullOrWhiteSpace(v)) { humanManager = Clean(v); dirty = true; } });
                BindOnce(b, ff.parent, "Human.Club.Name", humanBinds, v => SetHumanClub(v, ff.path + ".Club.Name"));
                BindOnce(b, ff.parent, "Human.ContextHumanClub.Name", humanBinds, v => SetHumanClub(v, ff.path + ".ContextHumanClub.Name"));
                BindOnce(b, ff.parent, "Human.Club.UniqueID", humanBinds, v => { if (!string.IsNullOrWhiteSpace(v) && IsNum(v)) { humanClubId = v; dirty = true; } });
            }
            else
            {
                BindOnce(b, ff.parent, "humanteam.Name", humanBinds, v => { if (!string.IsNullOrWhiteSpace(v)) { humanTeamName = Clean(v); dirty = true; } });
                BindOnce(b, ff.parent, "humanteam.TeamContainer.Name", humanBinds, v => SetHumanClub(v, ff.path + ".TeamContainer.Name"));
                BindOnce(b, ff.parent, "humanteam.TeamContainer.UniqueID", humanBinds, v => { if (!string.IsNullOrWhiteSpace(v) && IsNum(v)) { humanClubId ??= v; dirty = true; } });
            }
        }
        if (found.Count == 0 && loggedOnce.Add("nohumannode")) CoachPlugin.L.LogWarning("No 'Human'/'humanteam' nodes in the binding tree yet.");
    }

    void SetHumanClub(string v, string source)
    {
        if (string.IsNullOrWhiteSpace(v)) return;
        var c = Clean(v);
        if (string.IsNullOrWhiteSpace(c) || c.StartsWith("FM.UI.")) return;
        if (humanClub == c) return;
        humanClub = c; humanClubSource = source; dirty = true;
        CoachPlugin.L.LogInfo("Human club: " + humanClub + " via " + source + (humanManager != null ? " (manager " + humanManager + ")" : ""));
    }

    string HumanClub()
    {
        if (!string.IsNullOrWhiteSpace(humanClub)) return humanClub;
        var ctx = V("ContextHumanClub.Name"); if (ctx != null) { humanClubSource = "person.ContextHumanClub.Name"; return ctx; }
        humanClubSource = "not detected yet"; return null;
    }

    string Ownership()
    {
        var club = V("Club.Name"); var hc = HumanClub(); if (club == null || hc == null) return null;
        if (humanClubId != null && V("Club.UniqueID") != null) return humanClubId == V("Club.UniqueID") ? "YOUR SQUAD" : "EXTERNAL";
        return string.Equals(club.Trim(), hc.Trim(), StringComparison.OrdinalIgnoreCase) ? "YOUR SQUAD" : "EXTERNAL";
    }

    string toast; float toastUntil;
    void Toast(string t) { toast = t; toastUntil = Time.unscaledTime + 6f; dirty = true; }

    void SetStatus(string s) { if (status != s) { status = s; dirty = true; } }

    void ClearTarget()
    {
        foreach (var (key, cb) in binds) { try { var k = key; BindingUtils.UnbindIfValid(ref k, cb); } catch { } }
        binds.Clear(); values.Clear(); rawValues.Clear();
        targetKey = 0; targetName = null; dirty = true;
    }

    IEnumerable<string> AllProps() => InfoFields.Select(f => f.prop).Concat(AttrGroups.SelectMany(g => g.attrs.Select(a => a.prop)));

    // Read-only subscription: asks the binding layer for values (same mechanism the game's own widgets use to display data).
    void Subscribe(Bindings b)
    {
        foreach (var prop in AllProps())
        {
            var path = targetScope + "." + prop;
            var now = Read(b, targetK, path);
            if (now != null) { rawValues[prop] = now; var dsp = Display(prop, now); if (dsp != null) values[prop] = dsp; }
            try
            {
                var p = prop;
                int callbackSession = dataSessionVersion;
                var cb = DelegateSupport.ConvertDelegate<Bindings.ValueChangedCallback>(
                    (Action<Bindings.Key, SI.Core.TypedValue>)((k, v) =>
                    {
                        try { if (callbackSession != dataSessionVersion) return; var d = Describe(v); if (d != null) { rawValues[p] = d; var dsp = Display(p, d); if (dsp != null) values[p] = dsp; else values.Remove(p); dirty = true; } } catch { }
                    }));
                keepAlive.Add(cb);
                var scope = targetK;
                var span = Il2CppSystem.MemoryExtensions.AsSpan(path);
                var key = BindingUtils.BindWithinScope(b, ref scope, ref span, panelRoot, cb);
                binds.Add((key, cb));
            }
            catch (Exception ex) { LogOnce("Bind " + prop, ex); }
        }
    }

    // ---------- UI Toolkit panel ----------
    static StyleLength Px(float v) => new StyleLength(v);
    static StyleColor Col(Color c) => new StyleColor(c);

    static void Radius(IStyle s, float r) { s.borderTopLeftRadius = Px(r); s.borderTopRightRadius = Px(r); s.borderBottomLeftRadius = Px(r); s.borderBottomRightRadius = Px(r); }
    static void BorderAll(IStyle s, float w, Color c)
    {
        s.borderTopWidth = new StyleFloat(w); s.borderBottomWidth = new StyleFloat(w); s.borderLeftWidth = new StyleFloat(w); s.borderRightWidth = new StyleFloat(w);
        s.borderTopColor = Col(c); s.borderBottomColor = Col(c); s.borderLeftColor = Col(c); s.borderRightColor = Col(c);
    }
    static void Pad(IStyle s, float v, float hz) { s.paddingTop = Px(v); s.paddingBottom = Px(v); s.paddingLeft = Px(hz); s.paddingRight = Px(hz); }

    static Label Text(string t, float size, Color c, bool bold = false)
    {
        var l = new Label(t);
        var s = l.style;
        s.fontSize = Px(size); s.color = Col(c);
        s.unityFontStyleAndWeight = new StyleEnum<FontStyle>(bold ? FontStyle.Bold : FontStyle.Normal);
        s.marginTop = Px(0); s.marginBottom = Px(0); s.paddingTop = Px(1); s.paddingBottom = Px(1);
        s.whiteSpace = new StyleEnum<WhiteSpace>(WhiteSpace.NoWrap);
        s.flexShrink = new StyleFloat(0f); // v0.6.2: never squash rows under the panel's maxHeight (caused overlap)
        l.pickingMode = PickingMode.Ignore;
        return l;
    }

    static VisualElement Row()
    {
        var r = new VisualElement();
        r.style.flexDirection = new StyleEnum<FlexDirection>(FlexDirection.Row);
        r.style.justifyContent = new StyleEnum<Justify>(Justify.SpaceBetween);
        r.style.flexShrink = new StyleFloat(0f);
        r.pickingMode = PickingMode.Ignore;
        return r;
    }

    VisualElement currentRoot;
    void EnsurePanel(VisualElement root)
    {
        currentRoot = root;
        if (panelRoot == null) BuildPanel();
        AttachPanel(root, false);
    }

    void BuildPanel()
    {
        panelRoot = new VisualElement { name = "fmcoach-panel" };
        var s = panelRoot.style;
        s.position = new StyleEnum<Position>(Position.Absolute);
        s.width = Px(360); s.minWidth = Px(360);
        s.visibility = new StyleEnum<Visibility>(Visibility.Visible); s.opacity = new StyleFloat(1f);
        s.backgroundColor = Col(CardBg);
        BorderAll(s, 1, Border); Radius(s, 8);
        s.overflow = new StyleEnum<Overflow>(Overflow.Hidden);
        panelRoot.pickingMode = PickingMode.Position;

        header = new VisualElement { name = "fmcoach-header" };
        header.style.flexDirection = new StyleEnum<FlexDirection>(FlexDirection.Row);
        header.style.justifyContent = new StyleEnum<Justify>(Justify.SpaceBetween);
        header.style.alignItems = new StyleEnum<Align>(Align.Center);
        header.style.backgroundColor = Col(HeaderBg);
        Pad(header.style, 7, 12);
        header.style.borderBottomWidth = new StyleFloat(1f); header.style.borderBottomColor = Col(Divider);
        var left = Row(); left.style.alignItems = new StyleEnum<Align>(Align.Center);
        try
        {
            face ??= new CoachFace();
            left.Add(face.MakeView(1f)); // native 32x40, nearest-neighbour
            CoachPlugin.L.LogInfo("Coach face loaded (" + CoachFace.Frames + " frames)");
        }
        catch (Exception ex) { LogOnce("Icon", ex); }
        titleLbl = Text("ASSISTANT COACH", 12, TextMain, true); left.Add(titleLbl); header.Add(left);
        var right = Row(); right.style.alignItems = new StyleEnum<Align>(Align.Center);
        BuildWindowControls(right); // v0.7: drawn min/max/restore + ASCII x (FM27 font has no ▾)
        header.Add(right);
        WireHeaderDrag();
        panelRoot.Add(header);
        BuildChipAndTabs();
        try { BuildChatArea(); panelRoot.Add(chatArea); } catch (Exception ex) { LogOnce("ChatArea", ex); }

        // v0.6.2: the body scrolls inside the panel instead of being squashed by maxHeight.
        var bodyScroll = new ScrollView(ScrollViewMode.Vertical) { name = "fmcoach-body" };
        try { bodyScroll.horizontalScrollerVisibility = ScrollerVisibility.Hidden; } catch { }
        bodyScroll.style.flexShrink = new StyleFloat(1f); bodyScroll.style.flexGrow = new StyleFloat(1f);
        bodyScroll.style.minHeight = Px(60);
        body = bodyScroll;
        Pad(body.style, 10, 12);
        body.pickingMode = PickingMode.Position;
        panelRoot.Add(body);

        dirty = true;
    }

    internal CoachFace face;
    int debugMoodIdx;
    /// Public hook for the future AI chat: mood tag from the reply (neutral|pleased|disgusted|worried|angry|smug|disagree).
    public void SetMood(string tag) { try { face?.SetMood(tag); } catch (Exception ex) { LogOnce("SetMood", ex); } }
    public void SetThinking(bool on) { try { face?.SetThinking(on); } catch (Exception ex) { LogOnce("SetThinking", ex); } }

    // v0.6.2: all placement is in the panel's parent (MainUIDocument root) coordinates, using the root's
    // resolvedStyle size (e.g. 2090x1176 at UI scale 1.225), never Screen.width (2560). Default: right side,
    // just below FM's top navigation bar. Clamped every frame, so a root resize re-clamps automatically.
    const float Margin = 12f;
    bool userMoved; float lastRootW = -1, lastRootH = -1;
    bool RootSize(out float w, out float h)
    {
        w = h = 0;
        try
        {
            var parent = panelRoot?.parent;
            if (parent == null) return false;
            w = parent.resolvedStyle.width; h = parent.resolvedStyle.height;
            if (float.IsNaN(w) || w < 10) { w = parent.layout.width; h = parent.layout.height; }
        }
        catch { return false; }
        return !float.IsNaN(w) && !float.IsNaN(h) && w > 200 && h > 200;
    }

    void PlacePanel()
    {
        if (panelRoot == null) return;
        PanelHousekeeping();
        if (RootSize(out var pw, out var ph))
        {
            bool resized = Math.Abs(pw - lastRootW) > 0.5f || Math.Abs(ph - lastRootH) > 0.5f;
            float PanelW = CurPanelW(pw);
            // v0.7 fix: a separate 'placed' flag - dragging past the left edge clamps to x>=12 instead of snapping back.
            if (!placed || (resized && !userMoved))
            {
                placed = true;
                posLeft = pw - PanelW - Margin;
                posTop = Mathf.Clamp(ph * 0.085f, 70f, 140f); // below FM's top nav bar
            }
            posLeft = Mathf.Clamp(posLeft, Margin, Math.Max(Margin, pw - PanelW - Margin));
            // v0.6.3: keep at least the header + chat area (input box) on screen; the body scrolls in the rest.
            float keepVisible = 120f;
            try
            {
                float hh = header != null ? header.layout.height : 0, ch = (chatArea != null && !collapsed) ? chatArea.layout.height : 0;
                if (!float.IsNaN(hh) && !float.IsNaN(ch) && hh + ch > 20) keepVisible = hh + ch + 8f;
                float full = panelRoot.layout.height;
                if (!float.IsNaN(full) && full > 20) keepVisible = Math.Min(Math.Max(keepVisible, Math.Min(full, 260f)), ph - 2 * Margin);
            }
            catch { }
            posTop = Mathf.Clamp(posTop, Margin, Math.Max(Margin, ph - keepVisible - Margin));
            if (resized)
            {
                CoachPlugin.L.LogInfo($"Panel placement: root {pw:0}x{ph:0} (screen {Screen.width}x{Screen.height}) -> left={posLeft:0} top={posTop:0}{(userMoved ? " (user-dragged, clamped)" : " (default: right, below nav bar)")}");
                lastRootW = pw; lastRootH = ph;
            }
            try
            {
                panelRoot.style.maxHeight = Px(Math.Max(160f, ph - posTop - Margin));
                // chat-first: the history gets most of the height; maximized = taller as well as wider
                float chatMax = maximized ? Mathf.Clamp(ph - posTop - Margin - 190f, 200f, 760f) : Mathf.Clamp(ph * 0.40f, 160f, 440f);
                if (chatScroll != null) chatScroll.style.maxHeight = Px(chatMax);
                if (body != null) body.style.maxHeight = Px(maximized ? Math.Max(160f, ph - posTop - Margin - 90f) : Mathf.Clamp(ph * 0.55f, 200f, 620f));
                ApplyWindowState(pw);
            }
            catch { }
        }
        else if (!placed) return; // root not laid out yet; wait
        panelRoot.style.left = Px(posLeft); panelRoot.style.top = Px(posTop);
        panelRoot.style.display = new StyleEnum<DisplayStyle>(show ? DisplayStyle.Flex : DisplayStyle.None);
    }

    void SectionHeader(string t)
    {
        var l = Text(t, 11, TextDim, true);
        l.style.marginTop = Px(8);
        body.Add(l);
        var d = new VisualElement(); d.style.height = Px(1); d.style.backgroundColor = Col(Divider); d.style.marginTop = Px(2); d.style.marginBottom = Px(4);
        d.pickingMode = PickingMode.Ignore;
        body.Add(d);
    }

    string V(string prop) => values.TryGetValue(prop, out var v) && !string.IsNullOrWhiteSpace(v) ? v : null;

    void BuildPlayerTab()
    {
        if (body == null) return;
        body.Clear();
        if (targetKey == 0) { var st = Text(status + "\nOpen a player profile to see him here.", 13, TextDim); st.style.whiteSpace = new StyleEnum<WhiteSpace>(WhiteSpace.Normal); body.Add(st); return; }
        BuildPlayerInsightCards();
    }

    // v0.6.2: fixed label column + wrapping value column, so long values wrap instead of overflowing/overlapping.
    static VisualElement InfoRow(string label, string value)
    {
        var r = Row(); r.style.justifyContent = new StyleEnum<Justify>(Justify.FlexStart);
        r.style.alignItems = new StyleEnum<Align>(Align.FlexStart); r.style.marginBottom = Px(1);
        var l = Text(label, 12, TextDim); l.style.width = Px(118); l.style.minWidth = Px(118);
        var v = Text(value, 12, TextMain);
        v.style.flexGrow = new StyleFloat(1f); v.style.flexShrink = new StyleFloat(1f); v.style.minWidth = Px(0);
        v.style.whiteSpace = new StyleEnum<WhiteSpace>(WhiteSpace.Normal);
        v.style.unityTextAlign = new StyleEnum<TextAnchor>(TextAnchor.UpperRight);
        r.Add(l); r.Add(v);
        return r;
    }

    // ---------- input (Input System only; legacy Input is disabled in FM27) ----------
    Vector2 MouseInPanelSpace(Vector2 screen)
    {
        float pw = Screen.width;
        try { var r = panelRoot?.parent; if (r != null && r.layout.width > 10) pw = r.layout.width; } catch { }
        float sc = pw / Math.Max(1, Screen.width);
        return new Vector2(screen.x * sc, (Screen.height - screen.y) * sc);
    }

    static bool Hit(VisualElement e, Vector2 p) { try { return e != null && e.worldBound.Contains(p); } catch { return false; } }

    void Update()
    {
        bool toggle = false, dump = false;
        try
        {
            var kb = Keyboard.current;
            if (kb != null)
            {
                bool chord = (kb.leftCtrlKey.isPressed || kb.rightCtrlKey.isPressed) && (kb.leftShiftKey.isPressed || kb.rightShiftKey.isPressed);
                // FM binds single letters regardless of modifiers (C = Calendar, D = navigate right, Ctrl+F = Search),
                // so the plugin only uses function keys plus Ctrl+Shift with ` and \ (unbound in FM27; for keyboards without Fn).
                toggle = kb.f10Key.wasPressedThisFrame || (chord && kb.backquoteKey.wasPressedThisFrame);
                dump = kb.f11Key.wasPressedThisFrame || (chord && kb.backslashKey.wasPressedThisFrame);
                // F9 = debug: cycle the coach face moods (cosmetic only)
                if (kb.f9Key.wasPressedThisFrame && face != null)
                {
                    debugMoodIdx = (debugMoodIdx + 1) % CoachFace.DebugCycle.Length;
                    var dm = CoachFace.DebugCycle[debugMoodIdx];
                    face.SetMood(dm); if (!show) { show = true; OnShowChanged(); }
                    Toast("Face test: " + dm.ToString().ToLowerInvariant() + (dm == CoachMood.Disapproving ? " (tag: disagree)" : ""));
                }
            }
            var m = Mouse.current;
            if (m != null && panelRoot != null && show)
            {
                var p = MouseInPanelSpace(m.position.ReadValue());
                if (m.leftButton.wasPressedThisFrame)
                {
                    ChatMouseDown(p);
                    if (PanelClick(p, ref dump)) { }
                    else if (!nativeDragging && Hit(header, p)) { dragging = true; userMoved = true; dragStart = p; dragL = posLeft; dragT = posTop; }
                }
                if (dragging)
                {
                    if (m.leftButton.isPressed) { posLeft = dragL + (p.x - dragStart.x); posTop = dragT + (p.y - dragStart.y); }
                    else dragging = false;
                }
            }
        }
        catch (Exception ex) { LogOnce("Input", ex); }
        if (toggle) { show = !show; OnShowChanged(); if (!show) ChatDeactivate("panel hidden"); }
        if (dump)
        {
            if (!show) { show = true; OnShowChanged(); }
            if (collapsed) SetWindow(min: false);
            try { var f = Dump(); Toast("Saved dump for " + (targetName ?? "(no player)") + " · " + System.DateTime.Now.ToString("HH:mm:ss")); }
            catch (Exception ex) { LogOnce("Dump", ex); Toast("Dump failed: " + ex.GetType().Name); }
        }

        if (Time.unscaledTime >= nextScan)
        {
            nextScan = Time.unscaledTime + 1.0f;
            try { Scan(); } catch (Exception ex) { LogOnce("Scan", ex); }
            if (!autoDumped && ValidProfile) { autoDumped = true; try { Dump(); } catch (Exception ex) { LogOnce("AutoDump", ex); } }
        }
        if (toast != null && Time.unscaledTime >= toastUntil) { toast = null; dirty = true; }
        try
        {
            PlacePanel();
            try { DataTick(); } catch (Exception ex) { LogOnce("DataTick", ex); }
            FullSnapshotTick();
            ChatTick();
            if (show && face != null) face.Tick();
            if (dirty && Time.unscaledTime >= nextBuild) { dirty = false; nextBuild = Time.unscaledTime + 0.3f; BuildBody(); }
        }
        catch (Exception ex) { LogOnce("UI", ex); }
    }

    // Binding tree: global scopes (root) and the viewed player's view. Names + short values only.
    void DumpTree(Bindings b, string file)
    {
        var o = new List<string>();
        void Walk(Bindings.Node n, int depth, int maxDepth, ref int budget)
        {
            for (var c = n; c != null && budget > 0; c = c.NextSibling)
            {
                if (depth == 0 && (c.Name ?? "").StartsWith("OSR-")) continue;
                budget--;
                string val = null;
                try { var tv = c.Value; if (tv != null && !tv.IsNull) { val = Describe(tv); if (val != null && val.Length > 100) val = val.Substring(0, 100) + "…"; } } catch { }
                o.Add(new string(' ', depth * 2) + c.Name + (val != null ? " = " + Clean(val) : "") + "  [" + c.Key.m_key.ToString("x") + "]");
                try
                {
                    var key = c.Key; var span = Il2CppSystem.MemoryExtensions.AsSpan("Type");
                    var contextKey = b.FindContext(ref key, ref span);
                    var contextNode = contextKey.m_key != 0 ? b.GetNode(ref contextKey)?.TryCast<Bindings.Node>() : null;
                    if (contextNode?.Value != null && !contextNode.Value.IsNull)
                        o.Add(new string(' ', (depth + 1) * 2) + "# Type context = " + Describe(contextNode.Value));
                }
                catch { }
                if (depth < maxDepth && c.FirstChild != null) Walk(c.FirstChild, depth + 1, maxDepth, ref budget);
            }
        }
        try
        {
            var rk = Bindings.RootKey;
            var root = b.GetNode(ref rk)?.TryCast<Bindings.Node>();
            o.Add("# ROOT (native UI scopes, one-shot requests omitted), depth<=16");
            int budget = 18000;
            if (root?.FirstChild != null) Walk(root.FirstChild, 0, 16, ref budget);
        }
        catch (Exception ex) { o.Add("root error: " + ex); }
        if (targetKey != 0)
        {
            try
            {
                var tk = targetK;
                var n = b.GetNode(ref tk)?.TryCast<Bindings.Node>();
                o.Add("# VIEW of viewed player: parents");
                for (var p = n; p != null; p = p.Parent) o.Add("  ^ " + p.Name + " [" + p.Key.m_key.ToString("x") + "]");
                o.Add("# VIEW subtree depth<=4");
                int budget = 6000;
                if (n != null) { o.Add(n.Name); if (n.FirstChild != null) Walk(n.FirstChild, 1, 4, ref budget); }
            }
            catch (Exception ex) { o.Add("view error: " + ex); }
        }
        File.WriteAllLines(file, o);
    }

    // ---------- discovery dump ----------
    string Dump()
    {
        var ts = System.DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var o = new List<string>();
        var bs = BS();
        o.Add("# FM Coach v0.7.8 dump " + System.DateTime.Now);
        o.Add("target: " + (targetName ?? "(none)") + "  section=" + targetSection + " scope=" + targetScope + " key=" + targetKey.ToString("x"));
        o.Add("raw name: " + (targetRaw ?? "").Replace("\x01", "<01>").Replace("\x02", "<02>"));
        o.Add("quick chips: " + QuickChipsLine());
        o.Add("root binding nodes (newest first): " + string.Join(" ", RootNodeNames(40)));
        o.Add("human club: " + HumanClub() + " (source: " + humanClubSource + ", manager: " + humanManager + ", team: " + humanTeamName + ", clubId: " + humanClubId + ")  ownership: " + Ownership());
        o.Add("## values (" + values.Count + ")");
        foreach (var p in AllProps()) o.Add(p + " = " + (values.TryGetValue(p, out var v) ? v : "(no value)") + (rawValues.TryGetValue(p, out var rv) && rv != v ? "    RAW " + rv : ""));
        if (bs != null)
        {
            var b = bs.Cast<Bindings>();
            var root = MainRoot();
            if (root != null)
            {
                var dbg = new List<string>();
                var cands = FindCandidates(b, root, dbg);
                o.Add("## candidates (" + cands.Count + ")");
                foreach (var c in cands) o.Add($"{c.section} el='{c.el}' scope={c.scope} depth={c.depth} area={c.area:0} name='{c.name}'");
                o.Add("## top-level sections");
                var h = root.hierarchy;
                for (int i = 0; i < h.childCount; i++) o.Add(" " + h[i].name + " display=" + h[i].resolvedStyle.display + " children=" + h[i].hierarchy.childCount + " classes='" + Classes(h[i]) + "'");
                o.Add("## view-keyed elements with USS classes (for styling/detection)");
                o.AddRange(dbg);
            }
        }
        if (bs != null) { try { DumpTree(bs.Cast<Bindings>(), Path.Combine(CoachPlugin.OutDir, "tree_" + ts + ".txt")); } catch (Exception ex) { o.Add("tree error: " + ex.Message); } }
        var f = Path.Combine(CoachPlugin.OutDir, "player_" + ts + ".txt");
        File.WriteAllLines(f, o);
        // property registry (names only), once per session
        var allF = Path.Combine(CoachPlugin.OutDir, "all_properties.txt");
        if (!File.Exists(allF))
        {
            var set = PropertyIdentifierSet.Instance; var all = new List<string>();
            foreach (var kv in set.m_idToInfo) all.Add(kv.Value.Name + "\t" + kv.Key);
            all.Sort(StringComparer.OrdinalIgnoreCase);
            File.WriteAllLines(allF, all);
        }
        CoachPlugin.L.LogInfo("Dump: " + values.Count + " values for '" + targetName + "' -> " + f);
        return f;
    }
}
