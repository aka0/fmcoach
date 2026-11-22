// FM Coach v0.7.2 - the team's three tactic slots (names, primary = training familiarity focus, active slot, per-slot summary).
// Sources (FM27 binding registry, confirmed in the TacticReference/TeamTacticReference catalogues):
//   TacticSlot1Name..TacticSlot3Name, TacticSlot1Valid..3Valid, CurrentTacticSlot (0-based), PrimaryTrainingTacticSlotIndex (UInt32),
//   TacticsSlotForRadioGroup (list of the 3 slots). The native side only exposes EventFmxSetTacticSlot / EventFmxSetPrimaryTrainingTacticSlot;
//   there is no managed slot collection in the Cpp2IL dump, so everything goes through bindings.
// Details (instructions, positions) of a slot are read when it is the slot shown on the Tactics screen, or from its radio-group item if FM answers there.
// Read-only: nothing here sets a slot.
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using UnityEngine;
using SI.Core;
using SI.Bindable;
using DateTime = System.DateTime;

public partial class CoachBehaviour
{
    internal sealed class TacticSlotRec
    {
        public int slot; public string name; public bool? valid;
        public bool isPrimary, isActive, detailsRead, formationDerived;
        public string detailsSource, readAt, formation, mentality;
        public bool detailsComplete, playerInstructionsRead;
        public List<string> missing = new() { "formation", "mentality", "roles/positions not read", "team instructions not read", "player instructions not fully enumerated" };
        public Dictionary<string, Dictionary<string, string>> allInstructions = new();
        public List<Dictionary<string, string>> playerInstructions = new();
        public Dictionary<string, Dictionary<string, string>> keyInstructions = new();
        public List<Dictionary<string, string>> positions = new();
    }

    readonly Dictionary<int, TacticSlotRec> slotStore = new();
    int? primarySlot, activeSlot; string primarySlotRaw, activeSlotRaw, slotMetaReadAt; bool slotStoreLoaded;
    bool tacticInventoryRead;
    static readonly JsonSerializerOptions SlotJson = new() { IncludeFields = true, WriteIndented = true };

    string SlotStoreFile()
    {
        var who = humanManager ?? "manager"; foreach (var c in Path.GetInvalidFileNameChars()) who = who.Replace(c, '_');
        return Path.Combine(CacheDir(), "tactic_slots_" + who.Replace(' ', '_') + ".json");
    }

    void LoadSlotStore()
    {
        if (slotStoreLoaded || humanTv == null || sessionCareer == null) return;
        slotStoreLoaded = true;
        try
        {
            var f = SlotStoreFile(); if (!File.Exists(f)) return;
            var recs = JsonSerializer.Deserialize<List<TacticSlotRec>>(File.ReadAllText(f), SlotJson);
            foreach (var r in recs ?? new()) if (r.slot >= 1 && r.slot <= 3 && !slotStore.ContainsKey(r.slot)) { r.detailsSource = (r.detailsSource ?? "") + (r.detailsSource?.Contains("(cached)") == true ? "" : " (cached)"); slotStore[r.slot] = r; }
            if (primarySlot == null && slotStore.Values.FirstOrDefault(r => r.isPrimary) is TacticSlotRec pr0) { primarySlot = pr0.slot; primarySlotRaw = "cached"; }
            if (activeSlot == null && slotStore.Values.FirstOrDefault(r => r.isActive) is TacticSlotRec ac0) { activeSlot = ac0.slot; activeSlotRaw = null; }
            slotMetaReadAt ??= slotStore.Values.Select(r => r.readAt).Where(x => x != null).DefaultIfEmpty().Max();
            CoachPlugin.L.LogInfo("Tactic slots: loaded " + slotStore.Count + " cached slot(s) from " + f);
        }
        catch (Exception ex) { LogOnce("SlotStoreLoad", ex); }
    }

    void SaveSlotStore()
    {
        if (sessionCareer == null) return;
        try { File.WriteAllText(SlotStoreFile(), JsonSerializer.Serialize(slotStore.Values.OrderBy(r => r.slot).ToList(), SlotJson), Encoding.UTF8); }
        catch (Exception ex) { LogOnce("SlotStoreSave", ex); }
    }

    static int? SlotFromIndex(string raw)
    {   // CurrentTacticSlot / PrimaryTrainingTacticSlotIndex are 0-based (slot 1 = 0); "3" is taken as slot 3.
        if (!int.TryParse((raw ?? "").Trim(), out var n)) return null;
        return n >= 0 && n <= 2 ? n + 1 : n == 3 ? 3 : (int?)null;
    }

    static readonly string[] SlotMetaProps = { "PrimaryTrainingTacticSlotIndex", "CurrentTacticSlot", "CurrentTacticSlotName", "TacticCount", "TacticsSlotValue",
        "TacticSlot1Name", "TacticSlot2Name", "TacticSlot3Name", "TacticSlot1Valid", "TacticSlot2Valid", "TacticSlot3Valid" };

    TacticSlotRec RecFrom(Dictionary<string, object> t, int slot, string name, string source)
    {
        var rec = new TacticSlotRec { slot = slot, name = name, detailsSource = source, readAt = DateTime.Now.ToString("s") };
        rec.missing.Clear();
        var f = TI("formation", t);
        if (f != null) { rec.formationDerived = f.Contains("(derived"); rec.formation = Regex.Replace(f, @"\s*\(derived.*\)$", ""); }
        rec.mentality = TI("mentality", t);
        if (t.TryGetValue("teamInstructionsByPhase", out var o) && o is Dictionary<string, Dictionary<string, string>> ph)
            foreach (var p in ph.Where(p => p.Key != "general"))
            {
                var d = p.Value.Where(kv => kv.Value != null && !kv.Value.EndsWith(": Default") && !kv.Value.EndsWith("Default (0)")).ToDictionary(kv => kv.Key, kv => kv.Value);
                if (d.Count > 0) rec.keyInstructions[p.Key] = d;
            }
        if (t.TryGetValue("slots", out var so) && so is List<Dictionary<string, string>> sl)
            rec.positions = sl.Select(x => x.Where(kv => kv.Value != null && kv.Key is "position" or "role" or "roleShort" or "duty" or "player" or "roleOutOfPossession" or "roleOutOfPossessionShort").ToDictionary(kv => kv.Key, kv => kv.Value)).ToList();
        if (t.TryGetValue("teamInstructionsByPhase", out var full) && full is Dictionary<string, Dictionary<string, string>> all)
            rec.allInstructions = all.ToDictionary(p => p.Key, p => new Dictionary<string, string>(p.Value));
        if (t.TryGetValue("playerInstructions", out var pi) && pi is List<Dictionary<string, string>> rows) rec.playerInstructions = rows;
        rec.playerInstructionsRead = t.TryGetValue("playerInstructionsRead", out var read) && read is bool b && b;
        var expected = t.TryGetValue("expectedInstructionKeys", out var expectedValue) && expectedValue is List<string> expectedKeys ? expectedKeys : new List<string>();
        foreach (var key in expected.Where(k => !rec.allInstructions.Values.Any(d => d.ContainsKey(k)))) rec.missing.Add("team instruction: " + key);
        if (expected.Count == 0) rec.missing.Add("team instruction coverage unavailable");
        rec.missing.AddRange(CoachTacticSnapshot.RequiredDetails(rec.formation, rec.mentality, rec.positions));
        if (!rec.playerInstructionsRead) rec.missing.Add("player instructions not fully enumerated");
        rec.detailsComplete = rec.missing.Count == 0;
        rec.detailsRead = rec.mentality != null || rec.keyInstructions.Count > 0 || rec.positions.Count > 0;
        return rec;
    }

    void InvalidateCurrentTactic(string slotIndex, string reason, bool force = false)
    {
        if (!force && activeSlotRaw == slotIndex &&
            (!tacticSnap.ContainsKey("object") || tacticSnap.GetValueOrDefault("allValues") is SortedDictionary<string, string> current && current.GetValueOrDefault("CurrentTacticSlot") == slotIndex)) return;
        tacticSnap.Clear(); hintsSnap.Clear(); profileSnap.Clear();
        activeSlotRaw = slotIndex; activeSlot = SlotFromIndex(slotIndex);
        foreach (var record in slotStore.Values) record.isActive = record.slot == activeSlot;
        if (activeSlot is int slot && slotStore.TryGetValue(slot, out var selected)) tacticSnap["name"] = selected.name;
        snapshotStatus = reason; dirty = true; chatDirty = true;
        BuildSquadContext();
    }

    // Called by the Tactics-screen merge after the active tactic has been crawled and its XI built.
    IEnumerator ReadTacticSlots(TypedValue tac, StringBuilder pr, Bindings.Key scope = default, string readSource = "Tactics screen (slot shown)")
    {
        LoadSlotStore();
        var meta = SlotMetaProps.Select(p => Ask(tac, p, 6f, false, "slots", null, scope)).ToList();
        var radio = Ask(tac, "TacticsSlotForRadioGroup", 8f, true, "slots", null, scope);
        yield return WaitAll(meta.Append(radio), 8f);
        string M(string p) { var r = meta.FirstOrDefault(x => x.Path == p); return r != null && r.Got ? ValueText(r.Str) : null; }
        if (pr != null) { pr.AppendLine("\n## tactic slots (meta)"); foreach (var r in meta) pr.AppendLine("  " + r.Path + " = " + ReqText(r)); }

        var names = new string[4];
        for (int i = 1; i <= 3; i++) names[i] = M("TacticSlot" + i + "Name");
        var valid = Enumerable.Range(1, 3).Select(i => CoachSquadPolicy.Boolean(M("TacticSlot" + i + "Valid"))).ToArray();
        tacticInventoryRead = CoachTacticSnapshot.InventoryComplete(names.Skip(1).ToArray(), valid,
            int.TryParse(M("TacticCount"), out var savedCount) ? savedCount : (int?)null);
        activeSlotRaw = M("CurrentTacticSlot"); primarySlotRaw = M("PrimaryTrainingTacticSlotIndex");
        var activeName = M("CurrentTacticSlotName");
        activeSlot = SlotFromIndex(activeSlotRaw);
        activeName ??= activeSlot is int knownSlot ? names[knownSlot] : null;
        activeName ??= tacticSnap.GetValueOrDefault("name") as string;
        if (tacticSnap.GetValueOrDefault("allValues") is SortedDictionary<string, string> captured &&
            captured.TryGetValue("CurrentTacticSlot", out var capturedSlot) && capturedSlot != activeSlotRaw)
            InvalidateCurrentTactic(activeSlotRaw, "selected tactic changed during metadata read; retrying", true);
        if (activeSlot == null && activeName != null) { var k = Array.FindIndex(names, n => n == activeName); if (k > 0) activeSlot = k; }
        if (activeSlot != null && activeName != null && names[activeSlot.Value] != null && names[activeSlot.Value] != activeName)
        {   // index base disagrees with the names: trust the name
            CoachPlugin.L.LogWarning("Tactic metadata changed during read; discarding current details");
            InvalidateCurrentTactic(activeSlotRaw, "tactic metadata changed; retrying", true);
            activeName = names[activeSlot.Value];
        }
        primarySlot = SlotFromIndex(primarySlotRaw);
        slotMetaReadAt = DateTime.Now.ToString("s");

        // the active slot: everything the screen gave us
        if (activeSlot is int a)
        {
            var rec = RecFrom(tacticSnap, a, activeName ?? names[a], readSource);
            if (rec.detailsRead && (tacticSnap.GetValueOrDefault("CurrentTacticSlotName") as string ?? tacticSnap.GetValueOrDefault("name") as string) == activeName) slotStore[a] = rec;
        }

        // the other slots: try their radio-group items (FM may only answer for the slot on screen)
        var items = radio.Tv != null ? ListItems(radio.Tv, 3) : new List<TypedValue>();
        for (int i = 0; i < items.Count; i++)
        {
            var q = new[] { "Name", "MentalityString", "Tempo", "FormationName", "CurrentTacticSlot", "Index", "Selected", "Value" }.Select(p => Ask(items[i], p, 5f, false, "slotItem")).ToList();
            yield return WaitAll(q, 6f);
            pr?.AppendLine($"  slot item [{i}]: " + string.Join("; ", q.Select(r => r.Path + "=" + ReqText(r))));
            var nm = q[0].Got ? ValueText(q[0].Str) : null;
            int slot = nm != null && Array.FindIndex(names, n => n == nm) is int k && k > 0 ? k : i + 1;
            if (slot == activeSlot) continue;
            if (!(q[1].Got || q[2].Got)) continue;   // only the name answers: details need the slot to be selected on screen
            var temp = new Dictionary<string, object>();
            yield return CrawlTactic(items[i], "slot item " + (i + 1), false, pr, temp);
            var itemName = temp.GetValueOrDefault("CurrentTacticSlotName") as string ?? temp.GetValueOrDefault("name") as string;
            if (itemName == null || itemName != (nm ?? names[slot])) continue;
            var itemValues = temp.GetValueOrDefault("allValues") as SortedDictionary<string, string>;
            if (itemValues != null && int.TryParse(itemValues.GetValueOrDefault("CurrentTacticSlot"), out var itemIndex) && itemIndex != slot - 1) continue;
            if (names.Count(n => n == itemName) > 1 && (itemValues == null || !int.TryParse(itemValues.GetValueOrDefault("CurrentTacticSlot"), out _))) continue;
            BuildTacticSlotsFromPlayers(temp);
            var rec = RecFrom(temp, slot, nm ?? names[slot], readSource + " / slot list item");
            if (rec.detailsRead) slotStore[slot] = rec;
        }

        // names / flags for all three (also slots without details)
        for (int s = 1; s <= 3; s++)
        {
            if (names[s] == null && !slotStore.ContainsKey(s)) continue;
            if (!slotStore.TryGetValue(s, out var r)) slotStore[s] = r = new TacticSlotRec { slot = s, name = names[s] };
            if (names[s] != null && r.name != names[s])
            {   // the slot was renamed/replaced since it was cached: its details are stale
                if (s != activeSlot) { r.detailsRead = r.detailsComplete = r.playerInstructionsRead = false; r.allInstructions.Clear(); r.playerInstructions.Clear(); r.missing.Clear(); r.keyInstructions.Clear(); r.positions.Clear(); r.formation = r.mentality = null; r.detailsSource = "name changed since last read"; }
                r.name = names[s];
            }
            r.valid = valid[s - 1];
            r.isActive = s == activeSlot; r.isPrimary = s == primarySlot;
        }
        SaveSlotStore();
        CoachPlugin.L.LogInfo($"Tactic slots: primary={(primarySlot?.ToString() ?? "?")} (raw {primarySlotRaw ?? "-"}), active={(activeSlot?.ToString() ?? "?")} (raw {activeSlotRaw ?? "-"}); " +
            string.Join(", ", slotStore.Values.OrderBy(r => r.slot).Select(r => $"{r.slot}:{r.name}{(r.detailsRead ? "" : " (name only)")}")));
    }

    // tactic.json fields (stable names): tacticSlots[], primarySlot, activeSlot, primaryTacticName, activeTacticName, primarySlotSource, tacticSlotsReadAt
    void AttachTacticSlots()
    {
        LoadSlotStore();
        tacticSnap["tacticInventoryRead"] = tacticInventoryRead;
        if (slotStore.Count == 0) { tacticSnap.Remove("tacticSlots"); return; }
        foreach (var r in slotStore.Values) { r.isPrimary = r.slot == primarySlot; r.isActive = r.slot == activeSlot; }
        tacticSnap["tacticSlots"] = slotStore.Values.OrderBy(r => r.slot).Select(r => new Dictionary<string, object>
        {
            ["slot"] = r.slot, ["name"] = r.name, ["valid"] = r.valid, ["isPrimary"] = r.isPrimary, ["isActive"] = r.isActive,
            ["detailsRead"] = r.detailsRead, ["detailsComplete"] = r.detailsComplete, ["missing"] = r.missing, ["allInstructions"] = r.allInstructions, ["playerInstructions"] = r.playerInstructions, ["playerInstructionsRead"] = r.playerInstructionsRead, ["detailsSource"] = r.detailsSource, ["readAt"] = r.readAt,
            ["formation"] = r.formation, ["formationDerived"] = r.formationDerived, ["mentality"] = r.mentality,
            ["keyInstructions"] = r.keyInstructions, ["positions"] = r.positions,
        }).ToList();
        tacticSnap["primarySlot"] = primarySlot; tacticSnap["activeSlot"] = activeSlot;
        tacticSnap["primaryTacticName"] = primarySlot is int p && slotStore.TryGetValue(p, out var pr) ? pr.name : null;
        tacticSnap["activeTacticName"] = activeSlot is int a && slotStore.TryGetValue(a, out var ar) ? ar.name : null;
        tacticSnap["primarySlotSource"] = primarySlot != null ? (primarySlotRaw == "cached" ? "cached from the last Tactics-screen visit" : "PrimaryTrainingTacticSlotIndex=" + primarySlotRaw) + " (training familiarity focus)" : "not read yet (open the Tactics screen once)";
        tacticSnap["tacticSlotsReadAt"] = slotMetaReadAt;
    }

    static string ShortInstr(string v) => Regex.Replace(v ?? "", @"^(Passing Style|Line Of Engagement|Defensive Line|Pressing Intensity|Pressing Trap|Cross Engagement|Defence Transition|Attacking Transition|GKDistribution Target|GKDistribution Pace|Goal Kicks)\s+", m => m.Groups[1].Value switch
    {
        "Passing Style" => "Pass ", "Line Of Engagement" => "LoE ", "Defensive Line" => "DL ", "Pressing Intensity" => "Press ", "Pressing Trap" => "Trap ",
        "Cross Engagement" => "Cross ", "Defence Transition" => "OnLoss ", "Attacking Transition" => "OnWin ", "GKDistribution Target" => "GK to ", "GKDistribution Pace" => "GK pace ", _ => "GK ",
    });

    // AI context: one compact block for all three slots (the active slot's full detail stays in the TACTIC block).
    void AppendTacticSlotsContext(StringBuilder sb)
    {
        if (slotStore.Count == 0) { sb.AppendLine("TACTIC SLOTS: not read yet (capacity is up to 3 saved tactic slots; automatic tactic read pending)."); return; }
        var usable = slotStore.Values.Where(r => r.valid != false && !string.IsNullOrWhiteSpace(r.name)).OrderBy(r => r.slot).ToList();
        sb.AppendLine("TACTIC SLOTS: " + usable.Count + " observed saved tactics; capacity 3; inventory " + (tacticInventoryRead ? "verified" : "INCOMPLETE: other slots may exist") + "; PRIMARY = training familiarity focus.");
        foreach (var r in usable)
        {
            var tag = (r.isPrimary ? " PRIMARY" : "") + (r.isActive ? " ACTIVE" : "");
            var head = $"slot{r.slot} \"{r.name ?? "?"}\"{(tag.Length > 0 ? " [" + tag.Trim() + "]" : "")}";
            if (!CoachTacticSnapshot.HasFreshDetails(r.detailsRead, r.detailsSource))
            {
                sb.AppendLine(head + ": current details UNAVAILABLE (cached or not read); do not infer formation, mentality, pressing, roles or instructions from this name. " + string.Join("; ", r.missing));
                continue;
            }
            if (!r.detailsComplete) sb.AppendLine(head + ": PARTIAL current snapshot. Only the fields below are known; NOT READ: " + string.Join("; ", r.missing));
            if (!r.detailsRead) { sb.AppendLine(head + ": details not read yet - automatic slot read pending; do not infer its settings."); continue; }
            if (r.isActive && tacticSnap.ContainsKey("object")) { sb.AppendLine(head + ": the active tactic, detailed in TACTIC above."); continue; }
            int maxInstr = int.MaxValue;
            var instr = (r.allInstructions.Count > 0 ? r.allInstructions : r.keyInstructions).SelectMany(p => p.Value.Select(kv => p.Key + "." + kv.Key + "=" + kv.Value)).Select(ShortInstr).Take(maxInstr).ToList();
            var roles = r.positions.Count > 0 ? "; roles " + string.Join(", ", r.positions.Select(x => (x.GetValueOrDefault("position") ?? "?") + " " + (x.GetValueOrDefault("player") ?? "player not read") + " " + (x.GetValueOrDefault("roleShort") ?? x.GetValueOrDefault("role") ?? "?") + " duty=" + (x.GetValueOrDefault("duty") ?? "not read") + " OOP=" + (x.GetValueOrDefault("roleOutOfPossession") ?? "not read"))) : "";
            sb.AppendLine($"{head}: {r.formation ?? "formation ?"}{(r.formationDerived ? "*" : "")}, {r.mentality ?? "mentality ?"}; instructions: {(instr.Count > 0 ? string.Join(", ", instr) : "not read")}{roles} (read {r.readAt?.Replace('T', ' ')})");
        }
        foreach (var r in slotStore.Values.Where(r => r.valid != false && r.playerInstructionsRead && CoachTacticSnapshot.HasFreshDetails(r.detailsRead, r.detailsSource)))
        {
            if (r.playerInstructions.Count > 0) sb.AppendLine("slot" + r.slot + " PLAYER INSTRUCTIONS (complete effective group settings; Standard/No are known defaults): " + string.Join(" | ", r.playerInstructions.Select(CoachTacticSnapshot.PlayerInstructionContext).Where(text => text != null)));
            if (!r.detailsComplete && r.missing.Count == 0) sb.AppendLine("slot" + r.slot + " NOT READ: cached/incomplete instruction coverage; automatic refresh pending.");
            if (r.missing.Count > 0) sb.AppendLine("slot" + r.slot + " NOT READ: " + string.Join("; ", r.missing));
        }
        if (slotStore.Values.Any(r => r.formationDerived && CoachTacticSnapshot.HasFreshDetails(r.detailsRead, r.detailsSource))) sb.AppendLine("(* formation derived from the slot positions)");
    }
}

public partial class CoachBehaviour
{
    internal string TacticSlotsDoneLine() => slotStore.Count == 0 ? "(not read)" :
        string.Join(", ", slotStore.Values.Where(r => r.valid != false).OrderBy(r => r.slot).Select(r => r.slot + ":" + r.name + (r.isPrimary ? "[primary]" : "") + (r.isActive ? "[active]" : "") + (r.detailsRead ? "" : "(name only)")));
}
