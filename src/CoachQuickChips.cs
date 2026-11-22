// FM Coach v0.7.3 - one-tap quick questions (FM27 dialogue-style chips) above the chat input, chosen by the current screen.
// Screen key: "player" (a valid player profile is open), "tactics" (our TeamTacticReference bound by the Tactics screen),
// "squad" (a root binding node whose name matches the squad pattern - heuristic, logged), otherwise "general".
// Questions come from Documents\fmcoach\quick_questions.json (next to the prompt file) when present, else the built-ins below.
// A chip click goes through SubmitChat() exactly like a typed question (off-topic pre-check, mood, refs, word limit).
// Chips never take keyboard focus; clicking one while typing deactivates the box like any click outside it (FM shortcuts unblocked).
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using UnityEngine;
using UnityEngine.UIElements;
using SI.Bindable;

public partial class CoachBehaviour
{
    // ---- built-in table (no club names) ----
    static readonly Dictionary<string, string[]> BuiltInQuick = new()
    {
        // "Label" or "Label => question actually sent" (the label is what the chip shows)
        ["player"] = new[] { "Does he suit us?", "Worth adding to our squad?" },
        ["squad"] = new[] { "Pick my XI for next match", "Where are we weak?" },
        ["tactics"] = new[] { "Review current tactic => Review our current (active) tactic: formation, mentality, team instructions in possession, in transition and out of possession, and each position's role, duty and player instructions. Check them against the squad and the players picked in the XI: flag role/player mismatches (e.g. a slow player in a role that needs pace) and instructions that clash with players' attributes, with concrete fixes. Do not recommend anything already set." },
        ["general"] = new[] { "Next opponent => Using the opponent facts and statistics available, and our known squad and current tactic, give an initial match plan. Explain what to keep or adjust and what to watch in the opening exchanges. Keep unknown opposition details conditional; exact roles or a confirmed lineup are not required.", "How are we doing?" },
    };
    static readonly Dictionary<string, string> BuiltInScreenPatterns = new()
    {
        // matched against root binding-node names (hex counter suffix stripped), newest first. Unverified in game for "squad": see TESTPLAN-v0.7.3.
        ["squad"] = @"^(TeamSquad|SquadScreen|SquadView|SquadPlanner|PlayersScreen|TeamPlayers|SquadList)(?:[-_].*)?$",
        ["tactics"] = @"^(TacticsScreen|TacticsOverview|TacticsView|TacticsPitch|TeamTactics)(?:[-_].*)?$",
    };

    Dictionary<string, string[]> quickTable = BuiltInQuick;
    Dictionary<string, Regex> screenRx = BuiltInScreenPatterns.ToDictionary(kv => kv.Key, kv => new Regex(kv.Value, RegexOptions.IgnoreCase));
    int quickMax = 5; string quickSource = "built-in"; DateTime quickFileStamp; float nextQuickFileCheck;

    VisualElement quickRow; readonly List<(Label el, string q)> quickChips = new();
    string screenKey = "general", screenVia = "default", quickShownKey; bool quickShownBusy;
    float nextScreenCheck, lastTacticScreenSeen = -999f;
    List<string> rootNamesSample = new(); string lastTopRootName;

    string QuickFile => Path.Combine(Path.GetDirectoryName(string.IsNullOrWhiteSpace(PromptFile) ? Path.Combine(TestDir ?? ".", "..", "x") : PromptFile) ?? ".", "quick_questions.json");

    void LoadQuickTable()
    {
        if (Time.unscaledTime < nextQuickFileCheck) return;
        nextQuickFileCheck = Time.unscaledTime + 5f;
        try
        {
            var f = QuickFile;
            if (!File.Exists(f)) { if (quickSource != "built-in") { quickTable = BuiltInQuick; quickSource = "built-in"; quickShownKey = null; } return; }
            var ts = File.GetLastWriteTimeUtc(f);
            if (ts == quickFileStamp) return;
            quickFileStamp = ts;
            using var doc = JsonDocument.Parse(File.ReadAllText(f, Encoding.UTF8), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            var t = new Dictionary<string, string[]>(BuiltInQuick);
            var rx = BuiltInScreenPatterns.ToDictionary(kv => kv.Key, kv => new Regex(kv.Value, RegexOptions.IgnoreCase));
            int max = 5;
            foreach (var p in doc.RootElement.EnumerateObject())
            {
                if (p.Name == "maxChips" && p.Value.ValueKind == JsonValueKind.Number) max = Math.Clamp(p.Value.GetInt32(), 1, 8);
                else if (p.Name == "screenPatterns" && p.Value.ValueKind == JsonValueKind.Object)
                    foreach (var sp in p.Value.EnumerateObject()) { try { rx[sp.Name] = new Regex(sp.Value.GetString() ?? "^$", RegexOptions.IgnoreCase); } catch (Exception ex) { CoachPlugin.L.LogWarning("quick_questions.json: bad pattern for " + sp.Name + ": " + ex.Message); } }
                else if (p.Value.ValueKind == JsonValueKind.Array)
                    t[p.Name] = p.Value.EnumerateArray().Select(e =>
                        e.ValueKind == JsonValueKind.String ? Clean(e.GetString()) :
                        e.ValueKind == JsonValueKind.Object && e.TryGetProperty("label", out var lb) ? Clean(lb.GetString()) + (e.TryGetProperty("question", out var qq) ? " => " + Clean(qq.GetString()) : "") : null)
                        .Where(x => !string.IsNullOrWhiteSpace(x)).Take(8).ToArray();
            }
            quickTable = t; screenRx = rx; quickMax = max; quickSource = "file " + f; quickShownKey = null;
            CoachPlugin.L.LogInfo("Quick questions: loaded " + f + " (" + string.Join(", ", t.Select(kv => kv.Key + "=" + kv.Value.Length)) + ", maxChips=" + max + ")");
        }
        catch (Exception ex) { quickTable = BuiltInQuick; quickSource = "built-in (quick_questions.json invalid: " + ex.GetType().Name + ")"; LogOnce("QuickFile", ex); }
    }


    void UpdateScreenKey()
    {
        if (Time.unscaledTime < nextScreenCheck) return;
        nextScreenCheck = Time.unscaledTime + 1f;
        string key = "general", via = "default";
        rootNamesSample = RootNodeNames(40);
        var top = rootNamesSample.FirstOrDefault();
        if (top != lastTopRootName) { lastTopRootName = top; if (!oppComplete) nextOppRetry = Math.Min(nextOppRetry, Time.unscaledTime + 3f); }
        if (ValidProfile && targetName != null) { key = "player"; via = "profile open: " + targetName; }
        else
        {
            foreach (var n in VisibleScreenNames())
            {
                var hit = screenRx.FirstOrDefault(kv => kv.Value.IsMatch(n));
                if (hit.Key != null) { key = hit.Key; via = "visible screen element '" + n + "'"; break; }
            }
        }
        if (key != screenKey) CoachPlugin.L.LogInfo("Screen: " + screenKey + " -> " + key + " (" + via + ")");
        screenKey = key; screenVia = via;
    }

    List<string> VisibleScreenNames()
    {
        var names = new List<string>(); var root = currentRoot; if (root == null) return names;
        var pending = new Stack<VisualElement>(); pending.Push(root); int budget = 8000;
        while (pending.Count > 0 && budget-- > 0)
        {
            var element = pending.Pop();
            if (element == null || (panelRoot != null && element.Pointer == panelRoot.Pointer) ||
                (coachLauncher != null && element.Pointer == coachLauncher.Pointer)) continue;
            try
            {
                if (element.resolvedStyle.display == DisplayStyle.None || element.resolvedStyle.visibility == Visibility.Hidden) continue;
                var bounds = element.worldBound;
                if (bounds.width > 1 && bounds.height > 1 && !string.IsNullOrEmpty(element.name))
                    names.Add(Regex.Replace(element.name, "[0-9A-F]{2}$", ""));
                var children = element.hierarchy;
                for (int i = 0; i < children.childCount; i++) pending.Push(children[i]);
            }
            catch { }
        }
        return names;
    }

    List<string> RootNodeNames(int max)
    {
        var res = new List<string>();
        try
        {
            if (dataB == null) return res;
            var rk = Bindings.RootKey;
            var root = dataB.GetNode(ref rk)?.TryCast<Bindings.Node>();
            for (var c = root?.FirstChild; c != null && res.Count < max; c = c.NextSibling)
            {
                string nm = null; try { nm = c.Name; } catch { }
                if (string.IsNullOrEmpty(nm)) continue;
                res.Add(Regex.Replace(nm, "[0-9A-F]{2}$", ""));
            }
        }
        catch (Exception ex) { LogOnce("RootNodeNames", ex); }
        return res;
    }

    static (string label, string question) SplitChip(string entry)
    {
        var i = entry.IndexOf("=>", StringComparison.Ordinal);
        var label = (i < 0 ? entry : entry.Substring(0, i)).Trim(); var q = i < 0 ? label : entry.Substring(i + 2).Trim();
        if (label.Length > 40) label = label.Substring(0, 40);
        if (q.Length > 600) q = q.Substring(0, 600);
        return (label, q);
    }

    internal List<string> CurrentQuickQuestions()
    {
        var list = new List<string>();
        if (screenKey != "general" && quickTable.TryGetValue(screenKey, out var sp)) list.AddRange(sp);
        if (quickTable.TryGetValue("general", out var g)) list.AddRange(g);
        return list.Distinct().Take(quickMax).ToList();
    }

    void BuildQuickRow()
    {
        quickRow = new VisualElement { name = "fmcoach-quick" };
        quickRow.style.flexDirection = new StyleEnum<FlexDirection>(FlexDirection.Row);
        quickRow.style.flexWrap = new StyleEnum<UnityEngine.UIElements.Wrap>(UnityEngine.UIElements.Wrap.Wrap);
        quickRow.style.marginBottom = Px(6);
        quickRow.style.flexShrink = new StyleFloat(0f);
        quickRow.pickingMode = PickingMode.Ignore;
    }

    void RefreshQuickRow()
    {
        if (quickRow == null || Chat == null) return;
        bool busy = Chat.Busy;
        var qs = CurrentQuickQuestions();
        var sig = screenKey + "|" + string.Join("|", qs);
        if (sig == quickShownKey && busy == quickShownBusy) return;
        bool setChanged = sig != quickShownKey;
        quickShownKey = sig; quickShownBusy = busy;
        quickRow.Clear(); quickChips.Clear();
        foreach (var entry in qs)
        {
            var (label, q) = SplitChip(entry);
            // FM dialogue-option look: dark pill, thin border, green text when available, dimmed while a reply is pending
            var l = Text(label, 11, busy ? TextDim : TextMain);
            l.name = "fmcoach-quick-chip";
            l.style.backgroundColor = Col(new Color(1f, 1f, 1f, busy ? 0.03f : 0.06f));
            BorderAll(l.style, 1, busy ? Divider : new Color(0.40f, 0.85f, 0.45f, 0.45f));
            Radius(l.style, 10); Pad(l.style, 3, 9);
            l.style.marginRight = Px(5); l.style.marginBottom = Px(4);
            l.style.flexShrink = new StyleFloat(0f);
            l.focusable = false;
            quickRow.Add(l); quickChips.Add((l, q));
        }
        if (setChanged)
        {
            CoachPlugin.L.LogInfo("Quick chips: screen=" + screenKey + " [" + string.Join(" | ", qs.Select(x => SplitChip(x).label)) + "] (" + quickSource + ")");
            WriteQuickChipsFile(qs);
        }
    }

    // test hook mirror: Documents\fmcoach\test\quick_chips.txt
    void WriteQuickChipsFile(List<string> qs)
    {
        try
        {
            if (TestDir == null || !Directory.Exists(TestDir)) return;
            File.WriteAllText(Path.Combine(TestDir, "quick_chips.txt"),
                $"time: {DateTime.Now:s}\nscreen: {screenKey}\nvia: {screenVia}\nsource: {quickSource}\nchips: {string.Join(" | ", qs.Select(x => SplitChip(x).label))}\n", Encoding.UTF8);
        }
        catch (Exception ex) { LogOnce("QuickChipsFile", ex); }
    }

    internal string QuickChipsLine() => $"screen={screenKey} via={screenVia} chips=[{string.Join(" | ", CurrentQuickQuestions().Select(x => SplitChip(x).label))}] source={quickSource}";

    // called from ChatTick on a left click (panel space); returns true when a chip consumed the click
    bool QuickChipClick(Vector2 p)
    {
        if (!ChatVisible || quickChips.Count == 0) return false;
        foreach (var (el, q) in quickChips)
        {
            if (!Hit(el, p)) continue;
            if (Chat.Busy) { Toast("Coach is still thinking..."); return true; }
            CoachPlugin.L.LogInfo("Quick chip clicked: '" + el.text + "' (screen=" + screenKey + ", sends " + q.Length + " chars)");
            SubmitChat(q, false, el.text);
            quickShownKey = null;   // redraw disabled state
            return true;
        }
        return false;
    }
}
