// FM Coach v0.7 - UI redesign (t84u): chat-first, Discussing chip, tabs, window controls drawn as shapes
// (FM27's font has no ▾/□ glyphs - v0.6.3 tester), callout chips from [refs:...] resolved from game data.
using System;
using Il2CppInterop.Runtime;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEngine;
using UnityEngine.UIElements;

public partial class CoachBehaviour
{
    internal static readonly string[] Tabs = { "Chat", "Player", "Insights", "Tactic", "Debug" };
    internal static string tab = "Chat";
    internal static bool maximized, startMinimized;
    internal static Action<string, bool, bool> SaveUiState; // set by the plugin (config), cheap persistence
    const float NormalW = 400f, MaxW = 580f;
    VisualElement minBtn, maxBtn, closeBtn, tabBar, refreshBtn; Label discussLbl, toastLbl;
    Button previewFaceButton; Il2CppSystem.Action previewFaceClick;
    readonly List<(string name, Label lbl)> tabLbls = new();
    bool placed;

    float CurPanelW(float rootW) => Math.Min(maximized ? MaxW : NormalW, Math.Max(260f, rootW - 2 * Margin));

    static VisualElement Box(float w, float h) { var e = new VisualElement(); e.style.width = Px(w); e.style.height = Px(h); e.pickingMode = PickingMode.Ignore; return e; }
    static void BorderW(IStyle s, float w, Color c) { s.borderTopWidth = s.borderBottomWidth = s.borderLeftWidth = s.borderRightWidth = new StyleFloat(w); s.borderTopColor = s.borderBottomColor = s.borderLeftColor = s.borderRightColor = Col(c); }

    VisualElement CtrlButton()
    {
        var b = new VisualElement();
        b.style.width = Px(20); b.style.height = Px(20); b.style.marginLeft = Px(4);
        b.style.alignItems = new StyleEnum<Align>(Align.Center); b.style.justifyContent = new StyleEnum<Justify>(Justify.Center);
        b.style.backgroundColor = Col(new Color(1f, 1f, 1f, 0.06f)); Radius(b.style, 3);
        b.pickingMode = PickingMode.Position;
        return b;
    }

    // minimize = bar, maximize = hollow square, restore = two offset squares, close = ASCII "x" (present in every font)
    void BuildWindowControls(VisualElement right)
    {
        minBtn = CtrlButton(); var bar = Box(10, 2); bar.style.backgroundColor = Col(TextMain); bar.style.marginTop = Px(8); minBtn.Add(bar);
        maxBtn = CtrlButton(); DrawMaxGlyph();
        closeBtn = CtrlButton(); var x = Text("x", 13, TextMain, true); x.style.marginTop = Px(-2); x.pickingMode = PickingMode.Ignore; closeBtn.Add(x);
        right.Add(minBtn); right.Add(maxBtn); right.Add(closeBtn);
    }

    void DrawMaxGlyph()
    {
        if (maxBtn == null) return;
        maxBtn.Clear();
        if (!maximized) { var sq = Box(10, 9); BorderW(sq.style, 1.5f, TextMain); maxBtn.Add(sq); }
        else
        {
            var holder = Box(12, 11);
            var back = Box(8, 7); BorderW(back.style, 1.2f, TextDim); back.style.position = new StyleEnum<Position>(Position.Absolute); back.style.left = Px(4); back.style.top = Px(0);
            var front = Box(8, 7); BorderW(front.style, 1.5f, TextMain); front.style.position = new StyleEnum<Position>(Position.Absolute); front.style.left = Px(0); front.style.top = Px(4);
            front.style.backgroundColor = Col(HeaderBg);
            holder.Add(back); holder.Add(front); maxBtn.Add(holder);
        }
    }

    void BuildChipAndTabs()
    {
        dataLoadLabel = Text(DataLoadSummary(), 10, TextDim);
        dataLoadLabel.style.whiteSpace = new StyleEnum<WhiteSpace>(WhiteSpace.Normal);
        dataLoadLabel.style.height = dataLoadLabel.style.minHeight = dataLoadLabel.style.maxHeight = Px(56);
        dataLoadLabel.style.flexShrink = new StyleFloat(0f);
        dataLoadLabel.style.overflow = new StyleEnum<Overflow>(Overflow.Hidden);
        Pad(dataLoadLabel.style, 4, 12); dataLoadLabel.pickingMode = PickingMode.Ignore;
        panelRoot.Add(dataLoadLabel);
        discussLbl = Text("", 11, TextDim);
        discussLbl.style.backgroundColor = Col(new Color(1f, 1f, 1f, 0.04f));
        Pad(discussLbl.style, 3, 12);
        discussLbl.style.whiteSpace = new StyleEnum<WhiteSpace>(WhiteSpace.NoWrap);
        discussLbl.style.overflow = new StyleEnum<Overflow>(Overflow.Hidden);
        try { discussLbl.style.textOverflow = new StyleEnum<TextOverflow>(TextOverflow.Ellipsis); } catch { }
        discussLbl.pickingMode = PickingMode.Ignore;
        panelRoot.Add(discussLbl);

        tabBar = Row(); tabBar.style.justifyContent = new StyleEnum<Justify>(Justify.FlexStart);
        tabBar.style.borderBottomWidth = new StyleFloat(1f); tabBar.style.borderBottomColor = Col(Divider);
        tabBar.style.paddingLeft = Px(8); tabBar.style.paddingRight = Px(8);
        tabLbls.Clear();
        foreach (var t in Tabs)
        {
            var l = Text(t.ToUpperInvariant(), 10, TextDim, true);
            Pad(l.style, 5, 7);
            l.style.borderBottomWidth = new StyleFloat(2f); l.style.borderBottomColor = Col(new Color(0, 0, 0, 0));
            l.pickingMode = PickingMode.Position;
            tabBar.Add(l); tabLbls.Add((t, l));
        }
        panelRoot.Add(tabBar);

        toastLbl = Text("", 11, Accent, true);
        toastLbl.style.backgroundColor = Col(new Color(0.40f, 0.85f, 0.45f, 0.12f));
        Pad(toastLbl.style, 3, 12); toastLbl.pickingMode = PickingMode.Ignore;
        toastLbl.style.whiteSpace = new StyleEnum<WhiteSpace>(WhiteSpace.Normal);
        panelRoot.Add(toastLbl);
        StyleTabs();
    }

    void StyleTabs()
    {
        foreach (var (n, l) in tabLbls)
        {
            bool on = n == tab;
            l.style.color = Col(on ? TextMain : TextDim);
            l.style.borderBottomColor = Col(on ? Accent : new Color(0, 0, 0, 0));
        }
    }

    void SetTab(string t)
    {
        if (tab == t) return;
        tab = t; StyleTabs(); dirty = true; chatDirty = true;
        if (t != "Chat") ChatDeactivate("tab " + t);
        CoachPlugin.L.LogInfo("Panel tab: " + t);
        SaveUiState?.Invoke(tab, maximized, collapsed);
    }

    void SetWindow(bool? min = null, bool? max = null)
    {
        if (min != null) collapsed = min.Value;
        if (max != null && max.Value != maximized)
        {
            // v0.7.1: keep the panel's right edge where it was, so restoring from maximized does not leave it shifted left
            float oldW = CurPanelW(lastRootW > 0 ? lastRootW : 2000f);
            maximized = max.Value; collapsed = false; DrawMaxGlyph();
            float newW = CurPanelW(lastRootW > 0 ? lastRootW : 2000f);
            if (placed) posLeft += oldW - newW;
        }
        if (collapsed) ChatDeactivate("panel minimized");
        CoachPlugin.L.LogInfo($"Panel window: {(collapsed ? "minimized" : maximized ? "maximized" : "normal")}");
        SaveUiState?.Invoke(tab, maximized, collapsed);
        dirty = true; chatDirty = true;
    }

    /// Returns true when the click was consumed by a header control / tab / debug button.
    bool PanelClick(Vector2 p, ref bool dump)
    {
        if (Hit(closeBtn, p)) { show = false; OnShowChanged(); ChatDeactivate("panel closed (x)"); CoachPlugin.L.LogInfo("Panel closed via x (F10 brings it back)"); return true; }
        if (Hit(minBtn, p)) { SetWindow(min: !collapsed); return true; }
        if (Hit(maxBtn, p)) { SetWindow(max: !maximized); return true; }
        if (!collapsed)
        {
            foreach (var (n, l) in tabLbls) if (Hit(l, p)) { SetTab(n); return true; }
            if (tab == "Debug")
            {
                if (Hit(dumpBtn, p)) { dump = true; return true; }
                if (Hit(refreshBtn, p)) { StartSnapshot("manual refresh (Debug tab)", false); Toast("Re-reading squad and tactic…"); return true; }
            }
        }
        return false;
    }

    void ApplyWindowState(float rootW)
    {
        var w = CurPanelW(rootW);
        panelRoot.style.width = Px(w); panelRoot.style.minWidth = Px(w); panelRoot.style.maxWidth = Px(w);
        var none = new StyleEnum<DisplayStyle>(DisplayStyle.None); var flex = new StyleEnum<DisplayStyle>(DisplayStyle.Flex);
        bool open = !collapsed;
        if (tabBar != null) tabBar.style.display = open ? flex : none;
        if (discussLbl != null) discussLbl.style.display = open && (tab == "Chat" || tab == "Player") ? flex : none;
        if (toastLbl != null) toastLbl.style.display = open && toast != null && Time.unscaledTime < toastUntil ? flex : none;
        if (chatArea != null) chatArea.style.display = open && tab == "Chat" ? flex : none;
        if (body != null) body.style.display = open && tab != "Chat" ? flex : none;
    }

    bool ChatVisible => show && !collapsed && tab == "Chat";

    string DiscussingText()
    {
        if (ValidProfile && targetName != null)
        {
            var parts = new[] { targetName, V("PositionCombinedString") ?? V("NaturalPositionShortString"), V("Age") }.Where(x => !string.IsNullOrWhiteSpace(x));
            return "Discussing: " + string.Join(" · ", parts);
        }
        return "Discussing: " + (HumanClub() != null ? "your squad & tactics" : "nothing yet (open a player profile)");
    }

    void UpdateChrome()
    {
        var hcName = HumanClub();
        if (titleLbl != null) titleLbl.text = "ASSISTANT COACH · " + (hcName != null ? hcName.ToUpperInvariant() : "CLUB NOT DETECTED");
        if (discussLbl != null) discussLbl.text = DiscussingText();
        if (toastLbl != null) toastLbl.text = toast != null ? "✓ " + toast : "";
    }

    void BuildBody()
    {
        if (body == null) return;
        UpdateChrome();
        switch (tab)
        {
            case "Player": BuildPlayerTab(); break;
            case "Insights": body.Clear(); BuildSquadTab(); break;
            case "Tactic": body.Clear(); BuildTacticTab(); break;
            case "Debug": body.Clear(); BuildDebugTab(); break;
            default: body.Clear(); break;
        }
    }

    void Foot(string t) { var f = Text(t, 10, TextDim); f.style.marginTop = Px(8); f.style.whiteSpace = new StyleEnum<WhiteSpace>(WhiteSpace.Normal); body.Add(f); }
    Label Wrap(string t, float size, Color c, bool bold = false) { var l = Text(t, size, c, bold); l.style.whiteSpace = new StyleEnum<WhiteSpace>(WhiteSpace.Normal); return l; }

    void BuildSquadTab()
    {
        body.Add(Wrap("TEAM INSIGHTS", 14, TextMain, true));
        if (squad.Count == 0) { body.Add(Wrap(humanTv == null ? "Waiting for your save." : "Reading your registered first-team squad…", 12, TextMain)); return; }
        if (!SnapshotCoverage().SquadComplete)
            body.Add(InsightBadge("Partial squad data", false));

        var areas = ProfileGroups.Select(g => new { name = g.group, score = GroupAvg(squad, g.attrs), attrs = g.attrs })
            .Where(g => g.score.HasValue).OrderByDescending(g => g.score.Value).ToList();
        if (areas.Count == 0) { body.Add(Wrap("Team strengths will appear once player attributes are available.", 12, TextMain)); return; }
        SectionHeader("RELATIVE STRENGTHS");
        foreach (var area in areas.Take(3))
            body.Add(InsightMeter(QualityTitle(area.name), area.score.Value, 20, string.Join(", ", area.attrs), true));
        if (areas.Count > 3)
        {
            SectionHeader("LOWER-RATED AREAS");
            foreach (var area in areas.AsEnumerable().Reverse().Take(2))
                body.Add(InsightMeter(QualityTitle(area.name), area.score.Value, 20, string.Join(", ", area.attrs), false));
        }
        Foot(squad.Count(player => !player.IsGK) + " available first-team outfield players · attribute averages · within-squad comparison");
    }

    void BuildTacticTab()
    {
        body.Add(Wrap("TACTICAL FIT", 14, TextMain, true));
        var coverage = SnapshotCoverage();
        bool complete = coverage.TacticInventoryRead && coverage.TacticsExpected > 0 && coverage.TacticsComplete == coverage.TacticsExpected;
        if (!complete) body.Add(InsightBadge("Provisional assessment", false));
        var notes = hintsSnap.Where(h => !h.StartsWith("Squad not readable") && !h.StartsWith("Team instructions not readable")).ToList();
        if (notes.Count > 0)
        {
            SectionHeader("FIT NOTES");
            foreach (var note in notes.Take(4)) body.Add(FitInsight(note));
        }
        else body.Add(Wrap(complete ? "No clear mismatch found in the current attribute checks." : "Fit notes will appear as the coach reads your tactics and player attributes.", 12, TextMain));
        SectionHeader("COACH KNOWLEDGE");
        var saved = slotStore.Values.Where(r => r.valid != false && !string.IsNullOrWhiteSpace(r.name)).OrderBy(r => r.slot).ToList();
        if (saved.Count == 0) body.Add(Wrap("Reading your saved tactics…", 11, TextDim));
        foreach (var tactic in saved)
        {
            bool fresh = CoachTacticSnapshot.HasFreshDetails(tactic.detailsRead, tactic.detailsSource);
            var card = InsightCard(tactic.name, fresh && !tactic.playerInstructionsRead ? "Player instructions incomplete" : null);
            card.Add(InsightBadge(!fresh ? "Awaiting current read" : tactic.detailsComplete ? "Loaded" : "Partially read", fresh && tactic.detailsComplete));
            body.Add(card);
        }
    }

    void BuildDebugTab()
    {
        var r = Row(); r.style.justifyContent = new StyleEnum<Justify>(Justify.FlexStart);
        dumpBtn = Text("DUMP (F11)", 11, Accent, true); Pad(dumpBtn.style, 4, 8); Radius(dumpBtn.style, 4); dumpBtn.style.backgroundColor = Col(new Color(0.40f, 0.85f, 0.45f, 0.14f)); dumpBtn.style.marginRight = Px(8);
        var rb = Text("REFRESH SQUAD/TACTIC", 11, Accent, true); Pad(rb.style, 4, 8); Radius(rb.style, 4); rb.style.backgroundColor = Col(new Color(0.40f, 0.85f, 0.45f, 0.14f));
        refreshBtn = rb;
        r.Add(dumpBtn); r.Add(rb); body.Add(r);
        if (previewFaceButton == null)
        {
            previewFaceButton = new Button { name = "fmcoach-next-face", text = "NEXT FACE", focusable = false };
            previewFaceButton.style.color = Col(Accent); previewFaceButton.style.backgroundColor = Col(CardBg);
            previewFaceButton.style.marginTop = Px(6); previewFaceButton.style.height = Px(28);
            previewFaceClick = DelegateSupport.ConvertDelegate<Il2CppSystem.Action>(new Action(() =>
            {
                if (face == null) return;
                var frame = face.PreviewNextFrame(); Toast("Face preview: " + frame + " (12s)");
                CoachPlugin.L.LogInfo("Face preview: " + frame);
            }));
            previewFaceButton.add_clicked(previewFaceClick);
        }
        previewFaceButton.RemoveFromHierarchy(); body.Add(previewFaceButton);
        var coverage = SnapshotCoverage();
        var summary = InsightCard("Data coverage");
        summary.Add(InsightBadge(coverage.Ready ? "Ready" : "Partially loaded", coverage.Ready));
        summary.Add(InfoRow("Player attributes", coverage.AttributesComplete + "/" + coverage.Players));
        summary.Add(InfoRow("Season stats", coverage.SeasonStatsComplete + "/" + coverage.Players));
        summary.Add(InfoRow("Saved tactics", coverage.TacticsComplete + "/" + (coverage.TacticsExpected > 0 ? coverage.TacticsExpected.ToString() : "?")));
        body.Add(summary);
        var details = new Foldout { text = "Technical details", value = false };
        details.style.color = Col(TextDim); details.style.marginTop = Px(6);
        details.Add(InfoRow("Viewing", ValidProfile && targetName != null ? targetName + (targetSection != null ? " (" + targetSection + ")" : "") : (status ?? "-")));
        details.Add(InfoRow("Club", (HumanClub() ?? "-") + (humanClubSource != null ? " (" + humanClubSource + ")" : "")));
        details.Add(InfoRow("Snapshot", snapshotStatus));
        details.Add(InfoRow("Game date", lastGameDate ?? "-"));
        details.Add(InfoRow("Requests", $"{reqIssued} issued · {reqResolved} ok · {reqTimedOut} timeout · {reqFailed} failed · {reqLive.Count} live"));
        details.Add(InfoRow("AI context", squadContextText != null ? $"≈{squadContextTokens} tokens ({squadContextText.Length} chars)" : "not built"));
        details.Add(InfoRow("Probe", probeFile ?? (ProbeEnabled ? "pending" : "off ([Debug] Probe=false)")));
        details.Add(InfoRow("Tactics screens", tacticScreensSeen.Count > 0 ? string.Join(", ", tacticScreensSeen) : "none visited yet"));
        details.Add(InfoRow("Wage unit", (squad.Select(p => p.wage).FirstOrDefault(w => w != null) is string w0 ? (WageUnit(w0) ?? "?") + " · " + (CurrencyOf(w0) ?? "?") + " (from \"" + Trunc(w0, 24) + "\")" : "-")));
        body.Add(details);
        Foot("F10 show/hide · F11 dump · F9 face test · drag the header · click the chat box to type, Esc to stop");
    }

    // ---------------- callout chips: [refs:Finishing,Passing,Kane.Pace,Mentality] ----------------
    static readonly Regex RefsRx = new(@"\[\s*refs?\s*:([^\]]*)\]", RegexOptions.IgnoreCase);
    internal static string StripRefs(string reply, out List<string> refs)
    {
        refs = new List<string>();
        if (string.IsNullOrEmpty(reply)) return reply;
        foreach (Match m in RefsRx.Matches(reply))
            refs.AddRange(m.Groups[1].Value.Split(new[] { ',', ';', '|' }, StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()).Where(x => x.Length > 0));
        return Regex.Replace(RefsRx.Replace(reply, ""), @"[ \t]{2,}", " ").Trim();
    }

    static string Norm(string s) => Regex.Replace(s ?? "", @"[^a-z0-9]", "", RegexOptions.IgnoreCase).ToLowerInvariant();
    static readonly Dictionary<string, string> StatAlias = new()
    {
        ["goals"] = "Goals", ["assists"] = "Assists", ["apps"] = "Appearances", ["appearances"] = "Appearances",
        ["avgrating"] = "AverageRating", ["averagerating"] = "AverageRating", ["rating"] = "AverageRating",
        ["age"] = "Age", ["value"] = "TransferValueRange", ["wage"] = "ContextContractWeeklyWage", ["height"] = "Height",
        ["condition"] = "ConditionString", ["sharpness"] = "Sharpness", ["morale"] = "MoraleString",
    };
    static readonly Dictionary<string, string> TacticAlias = new()
    {
        ["mentality"] = "mentality", ["formation"] = "formation", ["lineofengagement"] = "lineOfEngagement", ["defensiveline"] = "defensiveLine",
        ["pressingintensity"] = "pressingIntensity", ["pressing"] = "pressingIntensity", ["tempo"] = "tempo", ["width"] = "width", ["attackingwidth"] = "width",
        ["passingdirectness"] = "passingDirectness", ["passing directness"] = "passingDirectness", ["creativefreedom"] = "creativeFreedom",
        ["onwinningball"] = "onWinningBall", ["counter"] = "onWinningBall", ["onlosingball"] = "onLosingBall", ["counterpress"] = "onLosingBall",
        ["crossingstyle"] = "crossingStyle", ["timewasting"] = "timeWasting", ["dribbling"] = null, ["marking"] = null, ["tackling"] = null,
    };

    // Values come from game data only (viewed player values / squad snapshot / tactic snapshot), never from the model.
    List<string> ResolveRefs(List<string> refs)
    {
        var chips = new List<string>();
        foreach (var raw in refs.Take(8))
        {
            string who = null, what = raw;
            int dot = raw.LastIndexOf('.');
            if (dot > 0 && dot < raw.Length - 1) { who = raw.Substring(0, dot).Trim(); what = raw.Substring(dot + 1).Trim(); }
            var key = Norm(what);
            string chip = null;
            if (who != null && !who.Equals("tactic", StringComparison.OrdinalIgnoreCase))
            {
                var sp = squad.FirstOrDefault(s => s.name != null && (s.name.Equals(who, StringComparison.OrdinalIgnoreCase) || s.name.EndsWith(" " + who, StringComparison.OrdinalIgnoreCase)));
                if (sp == null && ValidProfile && targetName != null && targetName.IndexOf(who, StringComparison.OrdinalIgnoreCase) >= 0) { chip = ViewedRef(what, key); if (chip != null) chip = Short(targetName) + " " + chip; }
                else if (sp != null) chip = SquadRef(sp, what, key);
            }
            else
            {
                if (who == null && ValidProfile) chip = ViewedRef(what, key);
                if (chip == null && TacticAlias.TryGetValue(key, out var tk) && tk != null && TI(tk) is string tv) chip = tv.StartsWith(Pretty(what), StringComparison.OrdinalIgnoreCase) ? Trunc(tv, 40) : Pretty(what) + ": " + Trunc(tv, 28);
            }
            if (chip != null && ChipHasValue(chip) && !chips.Contains(chip)) chips.Add(chip);
        }
        return chips;
    }

    // a chip must carry a real value after its label (v0.7.1: no-profile replies produced label-only chips)
    static bool ChipHasValue(string chip)
    {
        if (string.IsNullOrWhiteSpace(chip)) return false;
        var t = chip.Trim();
        if (t.EndsWith(":") || t.EndsWith("-") || Regex.IsMatch(t, @"(^|\s)(null|n/a|-|\?)$", RegexOptions.IgnoreCase)) return false;
        return Regex.IsMatch(t, @"\S\s+\S") || t.Contains(":");
    }
    static string Short(string n) { var p = (n ?? "").Split(' '); return p.Length > 1 ? p[^1] : n; }
    static string Pretty(string s) => Regex.Replace(s.Trim(), "(?<=[a-z])(?=[A-Z])", " ");

    string ViewedRef(string what, string key)
    {
        foreach (var (g, attrs) in AttrGroups)
            foreach (var (abbr, prop) in attrs)
                if (Norm(prop.Replace("Attribute", "")) == key || Norm(abbr) == key)
                    return V(prop) is string v ? Pretty(prop.Replace("Attribute", "")) + " " + v : null;
        if (StatAlias.TryGetValue(key, out var sp)) { var v = V(sp) ?? (sp == "ConditionString" ? V("Condition") : null); if (v != null) return Pretty(what) + " " + (sp == "AverageRating" ? Display(sp, v) : Trunc(v, 26)); }
        return null;
    }

    string SquadRef(SquadPlayer sp, string what, string key)
    {
        var a = sp.attrs.FirstOrDefault(kv => Norm(kv.Key) == key);
        if (a.Key != null) return Short(sp.name) + " " + Pretty(a.Key) + " " + a.Value.ToString("0");
        foreach (var (g, attrs) in AttrGroups) foreach (var (abbr, prop) in attrs)
                if (Norm(abbr) == key && sp.attrs.TryGetValue(prop.Replace("Attribute", ""), out var v)) return Short(sp.name) + " " + Pretty(prop.Replace("Attribute", "")) + " " + v.ToString("0");
        string val = key switch
        {
            "goals" => sp.goals.ToString("0"), "assists" => sp.assists.ToString("0"), "apps" or "appearances" => sp.apps.ToString("0"),
            "avgrating" or "averagerating" or "rating" => sp.avgRating > 0 ? sp.avgRating.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) : null,
            "age" => sp.age, "condition" => sp.condition, "morale" => sp.morale, "sharpness" => sp.sharpness, "wage" => sp.wage, "value" => sp.value, _ => null,
        };
        return val != null ? Short(sp.name) + " " + Pretty(what) + " " + Trunc(val, 26) : null;
    }

    VisualElement ChipRow(List<string> chips)
    {
        var r = Row(); r.style.justifyContent = new StyleEnum<Justify>(Justify.FlexStart);
        r.style.flexWrap = new StyleEnum<Wrap>(UnityEngine.UIElements.Wrap.Wrap); r.style.marginTop = Px(2); r.style.marginBottom = Px(4);
        foreach (var c in chips.Where(ChipHasValue))
        {
            var l = Text(c, 10, Accent, true);
            l.style.backgroundColor = Col(new Color(0.40f, 0.85f, 0.45f, 0.10f));
            BorderW(l.style, 1f, new Color(0.40f, 0.85f, 0.45f, 0.35f));
            Pad(l.style, 2, 6); Radius(l.style, 8); l.style.marginRight = Px(4); l.style.marginTop = Px(2);
            l.pickingMode = PickingMode.Ignore;
            r.Add(l);
        }
        return r;
    }
}
