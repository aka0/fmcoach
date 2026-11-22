using System.Text.RegularExpressions;
// FM Coach v0.7 - chat area in the panel, context builder, system prompt, ask.txt test hook.
// READ-ONLY toward the game: it only reads values the plugin already holds; the only network host is OpenRouter.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using UnityEngine;
using UnityEngine.UIElements;
using UnityEngine.InputSystem;

public partial class CoachBehaviour
{
    internal static CoachChat Chat;
    internal static string TestDir;
    VisualElement chatArea; ScrollView chatScroll; Label askBtn, chatStatus;
    readonly List<(bool coach, string text)> chatLines = new();
    readonly Dictionary<int, List<string>> chatChips = new(); // v0.7 callout chips per coach line (values from game data)
    bool chatDirty = true, wasBusy;
    int scrollToEndFrames;
    float nextAskPoll;
    static string promptTemplate;

    internal static string PromptFile;
    static DateTime promptFileStamp; static string promptFromFile, promptSourceLogged;
    static string EmbeddedPrompt()
    {
        if (promptTemplate != null) return promptTemplate;
        using var s = typeof(CoachBehaviour).Assembly.GetManifestResourceStream("FMCoach.coach_system_prompt.txt");
        using var r = new StreamReader(s, Encoding.UTF8);
        return promptTemplate = r.ReadToEnd();
    }
    // v0.6.3: the editable file (Documents\fmcoach\coach_system_prompt.txt, shared with test\ai-smoke.ps1) wins;
    // it is re-read whenever its timestamp changes. Embedded copy = fallback.
    static string PromptTemplate()
    {
        string src = "embedded", text = null;
        try
        {
            if (!string.IsNullOrWhiteSpace(PromptFile) && File.Exists(PromptFile))
            {
                var ts = File.GetLastWriteTimeUtc(PromptFile);
                if (promptFromFile == null || ts != promptFileStamp) { promptFromFile = File.ReadAllText(PromptFile, Encoding.UTF8); promptFileStamp = ts; }
                if (!string.IsNullOrWhiteSpace(promptFromFile)) { text = promptFromFile; src = "file " + PromptFile; }
            }
        }
        catch (Exception ex) { CoachPlugin.L.LogWarning("System prompt file unreadable (" + ex.GetType().Name + "); using embedded copy"); }
        text ??= EmbeddedPrompt();
        var tag = src + " (" + text.Length + " chars)";
        if (tag != promptSourceLogged) { promptSourceLogged = tag; CoachPlugin.L.LogInfo("System prompt: " + tag); }
        return text;
    }

    string BuildSystemPrompt()
    {
        var club = HumanClub();
        return (PromptTemplate() + "\nUse the loaded registered first-team squad snapshot, including every listed player's attributes and current-season club stats, for named-player and adjustment questions. A player profile does not need to be open when that player's data is already in the snapshot. Read DATA COVERAGE and per-slot NOT READ fields before drawing conclusions. Missing data is being retried automatically; never claim all tactics or instructions are loaded when coverage is partial. Do not invent missing settings, player statistics or team totals. Tactic names and cached conversation claims do not establish formation, mentality, pressing, roles or instructions; only the current trusted context does.")
            .Replace("{CLUB}", club ?? "the manager's club (not detected yet)")
            .Replace("{MANAGER}", string.IsNullOrWhiteSpace(humanManager) ? "name not detected" : humanManager)
            .Replace("{CLUB_NOTE}", club == null
                ? "NOTE: the plugin has not detected which club the manager runs in this save. Say so if it matters, and ask him to open his club or manager profile so it can be detected. Never guess a club."
                : "");
    }

    string BuildContextJson()
    {
        var ctx = new Dictionary<string, object>
        {
            ["club"] = HumanClub() ?? "(not detected)",
            ["clubDetectedVia"] = humanClubSource,
            ["manager"] = humanManager,
            ["humanTeam"] = humanTeamName,
            ["localTime"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm"),
        };
        if (ValidProfile)
        {
            var info = new Dictionary<string, string>();
            foreach (var (label, prop) in InfoFields) { var v = V(prop); if (v != null && prop != "Club.UniqueID" && label != "Wage") info[label] = v; }
            var attrs = new Dictionary<string, Dictionary<string, string>>();
            bool viewedIsGk = info.Any(kv => kv.Key.StartsWith("Pos") && Regex.IsMatch(kv.Value ?? "", @"\bGK\b"));
            foreach (var (group, list) in AttrGroups)
            {
                if (group == "GOALKEEPING" && !viewedIsGk) continue;   // v0.7.1: GK-only attributes are meaningless for outfield players
                var g = new Dictionary<string, string>();
                foreach (var (abbr, prop) in list) { var v = V(prop); if (v != null) g[prop.Replace("Attribute", "")] = v; }
                if (g.Count > 0) attrs[group] = g;
            }
            ctx["viewedPlayer"] = new Dictionary<string, object>
            {
                ["name"] = targetName, ["relation"] = Ownership() ?? "unknown", ["screenSection"] = targetSection,
                ["info"] = info, ["attributes_1to20"] = attrs,
            };
            // v0.7 (t86u): wage period + currency are the manager's FM preferences; pass them explicitly, never assume.
            var wd = V("ContextContractWeeklyWage");
            if (wd != null)
            {
                var vp = (Dictionary<string, object>)ctx["viewedPlayer"];
                vp["wage_display"] = wd;
                vp["wage_unit"] = WageUnit(wd) ?? "unknown - quote wage_display exactly";
                vp["currency"] = CurrencyOf(wd) ?? "unknown - quote wage_display exactly";
            }
        }
        else
        {
            ctx["viewedPlayer"] = null;
            ctx["viewedPlayerNote"] = "No player profile is open. Use the registered squad snapshot for named players; do not require a profile visit when their data is already loaded.";
        }
        // v0.7: the manager's own squad + full tactic (snapshot read from the save, ~2-3k tokens).
        ctx["mySquadAndTactic"] = SquadContextForAI();
        ctx["dataCoverage"] = DataLoadSummary();
        ctx["notReadYet"] = "Use dataCoverage and per-slot missing fields to identify unavailable data. Registered squad/tactics are loaded automatically; do not request a page visit for facts already in the snapshot. Scouting shortlist and data not present in the context remain unavailable.";
        return JsonSerializer.Serialize(ctx);
    }

    void BuildChatArea()
    {
        chatArea = new VisualElement { name = "fmcoach-chat" };
        Pad(chatArea.style, 8, 12);
        chatArea.style.borderBottomWidth = new StyleFloat(1f); chatArea.style.borderBottomColor = Col(Divider);
        chatArea.pickingMode = PickingMode.Ignore;
        chatArea.style.flexShrink = new StyleFloat(0f);

        chatScroll = new ScrollView(ScrollViewMode.Vertical);
        try { chatScroll.horizontalScrollerVisibility = ScrollerVisibility.Hidden; } catch { }
        chatScroll.style.maxHeight = Px(220); // re-set relative to root height in PlacePanel
        chatScroll.style.flexShrink = new StyleFloat(0f);
        chatScroll.style.marginBottom = Px(6);
        chatArea.Add(chatScroll);

        BuildQuickRow();                 // v0.7.3: one-tap quick questions above the input
        chatArea.Add(quickRow);
        var row = Row(); row.style.alignItems = new StyleEnum<Align>(Align.Center);
        BuildChatBox();
        row.Add(chatBox);
        askBtn = Text("ASK", 11, Accent, true);
        askBtn.style.backgroundColor = Col(new Color(0.40f, 0.85f, 0.45f, 0.14f));
        Pad(askBtn.style, 4, 10); Radius(askBtn.style, 4);
        row.Add(askBtn);
        chatArea.Add(row);
        chatStatus = Text("", 10, TextDim); chatStatus.style.marginTop = Px(3);
        chatArea.Add(chatStatus);
        CoachPlugin.L.LogInfo("Chat area ready (native UI Toolkit TextField + FM action-map block while focused)");
        chatDirty = true;
    }

    void RebuildChatHistory()
    {
        if (chatScroll == null) return;
        chatScroll.Clear();
        if (chatLines.Count == 0)
        {
            var hint = Text("Ask your assistant about the player on screen, your squad or the next match.", 11, TextDim);
            hint.style.whiteSpace = new StyleEnum<WhiteSpace>(WhiteSpace.Normal);
            chatScroll.Add(hint);
        }
        for (int i = Math.Max(0, chatLines.Count - 12); i < chatLines.Count; i++)
        {
            var (coach, text) = chatLines[i];
            var l = Text((coach ? "" : "You: ") + text, 12, coach ? TextMain : TextDim, false);
            l.style.whiteSpace = new StyleEnum<WhiteSpace>(WhiteSpace.Normal);
            l.style.marginBottom = Px(5); l.style.flexShrink = new StyleFloat(0f);
            if (!coach) l.style.unityFontStyleAndWeight = new StyleEnum<FontStyle>(FontStyle.Italic);
            chatScroll.Add(l);
            if (coach && chatChips.TryGetValue(i, out var chips) && chips.Any(ChipHasValue)) chatScroll.Add(ChipRow(chips));
        }
        scrollToEndFrames = 4; // layout is not computed yet; scroll to the latest line over the next frames
    }

    void SubmitChat(string q, bool fromTestHook, string shownAs = null)
    {
        q = q?.Trim();
        if (string.IsNullOrEmpty(q) || Chat == null) return;
        if (Chat.Busy) { Toast("Coach is still thinking..."); return; }
        string sys, ctx;
        try { sys = BuildSystemPrompt(); ctx = BuildContextJson(); }
        catch (Exception ex) { LogOnce("ChatContext", ex); return; }
        var about = ValidProfile && !string.IsNullOrWhiteSpace(targetName) ? targetName : "no player open";
        if (OffTopicPrecheck(q, out var why))
        {
            chatLines.Add((false, q)); chatLines.Add((true, "Gaffer, I'm here for the football. Ask me about the squad.")); chatDirty = true;
            SetMood("disgusted");
            CoachPlugin.L.LogInfo("AI ask blocked locally (off-topic pre-check: " + why + "), no API call: " + q);
            if (fromTestHook) WriteAnswerFile(q, "Gaffer, I'm here for the football. Ask me about the squad.", "disgusted", null, "local pre-check (" + why + "), no API call");
            return;
        }
        CoachPlugin.L.LogInfo($"AI context: {ctx.Length} chars ≈{ctx.Length / 3.6:0} tokens (squad/tactic block ≈{squadContextTokens} tokens; {snapshotStatus})");
        if (Chat.Ask(q, sys, ctx, fromTestHook, about))
        {
            chatLines.Add((false, shownAs ?? q)); chatDirty = true;
            CoachPlugin.L.LogInfo("AI ask" + (fromTestHook ? " (test hook)" : "") + ": " + q.Length + " chars, context " + ctx.Length + " chars, club=" + (HumanClub() ?? "not detected") + ", player=" + (ValidProfile ? targetName : "none (no profile open)"));
        }
    }

    // Called every frame from Update()
    void ChatTick()
    {
        if (Chat == null) return;
        // typing / Enter / Esc / ASK button
        try
        {
            ChatInputTick();
            LoadQuickTable(); UpdateScreenKey(); RefreshQuickRow();
            var m = Mouse.current;
            if (m != null && ChatVisible && m.leftButton.wasPressedThisFrame)
            {
                var mp = MouseInPanelSpace(m.position.ReadValue());
                if (QuickChipClick(mp)) { }
                else if (askBtn != null && Hit(askBtn, mp)) SubmitTyped();
            }
        }
        catch (Exception ex) { LogOnce("ChatInput", ex); }

        // test hook: Documents\fmcoach\test\ask.txt -> answer.txt
        if (Time.unscaledTime >= nextAskPoll)
        {
            nextAskPoll = Time.unscaledTime + 1f;
            try
            {
                var ask = Path.Combine(TestDir, "ask.txt");
                if (!Chat.Busy && File.Exists(ask))
                {
                    var q = File.ReadAllText(ask, Encoding.UTF8);
                    File.Delete(ask);
                    CoachPlugin.L.LogInfo("Test hook: picked up ask.txt");
                    SubmitChat(q, true);
                }
            }
            catch (Exception ex) { LogOnce("AskHook", ex); }
        }

        bool busy = Chat.Busy;
        if (busy != wasBusy) { SetThinking(busy); wasBusy = busy; if (chatStatus != null) chatStatus.text = busy ? "Thinking... (" + Chat.Model + ")" : ""; }

        while (Chat.Done.TryDequeue(out var r))
        {
            if (!Chat.IsCurrent(r)) continue;
            SetThinking(false); wasBusy = false;
            if (r.Error != null)
            {
                chatLines.Add((true, "(" + r.Error + ")"));
                CoachPlugin.L.LogWarning($"AI request #{r.Id}: model={r.Model} status={r.Status} latency={r.LatencyMs} ms error={r.Error}");
                SetMood("worried");
            }
            else
            {
                var shown = StripRefs(r.Reply, out var refs);
                int limit = TacticalQuestionRx.IsMatch(r.Question ?? "") ? 150 : 75;
                var trimmed = SoftTrim(shown, limit);
                if (trimmed != shown) { CoachPlugin.L.LogInfo($"Reply trimmed to the {limit}-word limit: {WordCount(shown)} -> {WordCount(trimmed)} words"); shown = trimmed; }
                var chips = ResolveRefs(refs);
                chatLines.Add((true, shown));
                if (chips.Count > 0) chatChips[chatLines.Count - 1] = chips;
                if (refs.Count > 0) CoachPlugin.L.LogInfo($"Callout refs: [{string.Join(", ", refs)}] -> chips [{string.Join(" | ", chips)}]");
                r.Reply = shown + (refs.Count > 0 ? "\n[refs: " + string.Join(", ", refs) + "]\n[chips: " + string.Join(" | ", chips) + "]" : "");
                CoachPlugin.L.LogInfo($"AI request #{r.Id}: model={r.Model} status={r.Status} latency={r.LatencyMs} ms tokens={r.PromptTokens}+{r.CompletionTokens} cost=${r.Cost:0.#####} finish={r.Finish} mood={r.Mood}");
                SetMood(r.Mood);
            }
            if (chatStatus != null) chatStatus.text = r.Error == null ? $"{r.LatencyMs / 1000.0:0.0} s · mood {r.Mood}" : "";
            if (r.FromTestHook)
            {
                WriteAnswerFile(r.Question, r.Reply, r.Mood, r.Error, $"model: {r.Model}  status: {r.Status}  latency_ms: {r.LatencyMs}  tokens: {r.PromptTokens}+{r.CompletionTokens}  cost: {r.Cost:0.#####}");
            }
            chatDirty = true;
        }
        if (chatDirty && chatScroll != null) { chatDirty = false; try { RebuildChatHistory(); } catch (Exception ex) { LogOnce("ChatUI", ex); } }
        if (scrollToEndFrames > 0 && chatScroll != null)
        {
            scrollToEndFrames--;
            try
            {
                if (chatScroll.childCount > 0) chatScroll.ScrollTo(chatScroll[chatScroll.childCount - 1]);
                var vs = chatScroll.verticalScroller; if (vs != null) vs.value = vs.highValue;
            }
            catch (Exception ex) { LogOnce("ChatScroll", ex); }
        }
    }

    void WriteAnswerFile(string q, string reply, string mood, string error, string meta)
    {
        try
        {
            var sb = new StringBuilder();
            sb.AppendLine("question: " + (q ?? "").Trim());
            sb.AppendLine("reply: " + (reply ?? ""));
            sb.AppendLine("mood: " + mood);
            sb.AppendLine("error: " + (error ?? ""));
            sb.AppendLine(meta);
            sb.AppendLine("club: " + (HumanClub() ?? "(not detected)") + "  player: " + (ValidProfile ? targetName : "(none)") + "  time: " + DateTime.Now.ToString("s"));
            var tmp = Path.Combine(TestDir, "answer.tmp"); var ans = Path.Combine(TestDir, "answer.txt");
            File.WriteAllText(tmp, sb.ToString(), Encoding.UTF8);
            if (File.Exists(ans)) File.Delete(ans);
            File.Move(tmp, ans);
            CoachPlugin.L.LogInfo("Test hook: wrote answer.txt");
        }
        catch (Exception ex) { LogOnce("AnswerWrite", ex); }
    }

    // v0.6.3: conservative local pre-check. Only unmistakable prompt-injection / identity-probe phrases are caught;
    // everything else (all football questions, and ordinary off-topic ones) goes to the model, which refuses in character.
    static readonly System.Text.RegularExpressions.Regex OffTopicRx = new(
        @"\b(ignore|disregard|forget)\s+(all\s+)?(your|the|previous|prior|above|earlier|these|those)\s+(previous\s+|prior\s+)?(instructions|prompts?)\b" +
        @"|\bsystem\s+prompt\b|\byour\s+prompt\b|\bas\s+an\s+ai\b|\bare\s+you\s+an?\s+(ai|bot|chatbot|language\s+model|llm)\b" +
        @"|\bjailbreak\b|\bdeveloper\s+mode\b",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);
    internal static bool OffTopicPrecheck(string q, out string why)
    {
        why = null;
        var m = OffTopicRx.Match(q ?? "");
        if (!m.Success) return false;
        why = "matched '" + m.Value.Trim() + "'";
        return true;
    }

    // v0.7.2: the word limit is enforced locally too (the model overshoots by ~10-20%): cut at the last sentence that fits.
    static readonly Regex TacticalQuestionRx = new(@"\b(tactic|formation|instruction|press|mentality|shape|system|roles?\b|dut(y|ies)|compar|match plan|game plan|plan for|line of|defensive line|tempo|width|transition|set.?piece|line-?up|starting xi|counter)", RegexOptions.IgnoreCase);
    static int WordCount(string t) => string.IsNullOrWhiteSpace(t) ? 0 : Regex.Matches(t, @"\S+").Count;
    internal static string SoftTrim(string text, int maxWords)
    {
        if (WordCount(text) <= maxWords) return text;
        var sentences = Regex.Split(text.Trim(), @"(?<=[.!?])\s+");
        var sb = new StringBuilder(); int n = 0;
        foreach (var se in sentences)
        {
            int w = WordCount(se);
            if (n + w > maxWords) break;
            if (sb.Length > 0) sb.Append(' ');
            sb.Append(se); n += w;
        }
        if (n >= Math.Min(25, maxWords / 2)) return sb.ToString();
        var words = Regex.Matches(text, @"\S+").Cast<Match>().Take(maxWords).Select(m => m.Value);   // one very long sentence
        return string.Join(" ", words).TrimEnd(',', ';', ':') + "...";
    }
}
