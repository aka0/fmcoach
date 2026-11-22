// FM Coach v0.6.2 - chat typing that does not depend on UI Toolkit keyboard focus.
//
// Why: in v0.6.1 a UI Toolkit TextField got the focus band but never received characters (FM routes keyboard
// input through its own SI.Input.InputManager / Input System action maps), and FM's global shortcuts
// (U = messages, C = calendar, digits, W/A/S/D, Space ...) fired while "typing".
//
// How (input routing only, no game data is touched):
//  * The box is our own element (label + caret). Clicking it activates typing; Esc, a click outside the box,
//    hiding/collapsing the panel deactivates it.
//  * Characters come from UnityEngine.InputSystem.Keyboard.onTextInput (OS text events, respects layout/Shift);
//    Backspace/Delete/arrows/Home/End/Enter/Esc/Ctrl+V are polled from the Keyboard device with key-repeat.
//  * While active, FM's own input-blocking API is used: SI.Input.InputManager.BlockActionMap(requestKey, map,
//    keepPointerAndClickActions:true) for every enabled action map (and "GameWorld"), so FM's shortcuts are
//    suppressed the same way FM suppresses them itself (ContinueManagerModule blocks "GameWorld" while
//    processing). Everything is unblocked with UnblockActionMap + RemoveAllInputBlockRequestsFromRequester.
//  * Fallback: if the text event never arrives, a polled US-layout key map is used; if the keyboard hook
//    cannot be installed at all, the box says "type-to-ask disabled" and points to the ask.txt hook.
using System;
using System.Collections.Generic;
using System.Text;
using Il2CppInterop.Runtime;
using UnityEngine;
using UnityEngine.UIElements;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;
using FmInput = SI.Input.InputManager;
using InputRequestKey = SI.Input.InputRequestKey;

public partial class CoachBehaviour
{
    const int MaxQuestionChars = 500;
    VisualElement chatBox; Label chatBoxText;
    TextField nativeChatField;
    SI.Bindable.BindableTextEditBox nativeChatEditor;
    bool nativeChatThemed;
    EventCallback<ChangeEvent<string>> nativeChatChanged;
    EventCallback<FocusInEvent> nativeChatFocusIn;
    EventCallback<FocusOutEvent> nativeChatFocusOut;
    EventCallback<KeyDownEvent> nativeChatKey;
    readonly StringBuilder inputText = new();
    int caret;
    bool inputActive, typeToAskDisabled, polledTextMode;
    string typeDisabledReason;
    Keyboard hookedKb; Il2CppSystem.Action<char> textHandler;
    readonly Queue<char> pendingChars = new();
    int textEvents, polledChars, backspaces, letterPressesWithoutText, activations;
    InputRequestKey blockKey; readonly List<InputActionMap> blockedMaps = new(); readonly HashSet<string> blockedNames = new();
    bool blockOk; float nextBlockRefresh, repeatAt; Key repeatKey = Key.None; bool loggedMaps;

    void BuildChatBox()
    {
        try
        {
            nativeChatEditor = new SI.Bindable.BindableTextEditBox
            {
                name = "fmcoach-chatbox", MaxLength = MaxQuestionChars, IsDelayed = false,
                ClearTextOnFocusChange = false, FocusTextFieldOnInit = false,
            };
            nativeChatField = nativeChatEditor.m_textField;
            if (nativeChatField == null) throw new InvalidOperationException("FM editor did not create its text field");
            chatBox = nativeChatEditor;
            nativeChatEditor.SetPlaceholderText("Ask about your squad and tactics…");
        }
        catch (Exception ex)
        {
            LogOnce("FMNativeEditor", ex); nativeChatEditor = null;
            nativeChatField = new TextField { name = "fmcoach-chatbox", multiline = false, maxLength = MaxQuestionChars, isDelayed = false, focusable = true };
            chatBox = nativeChatField;
        }
        nativeChatField.style.fontSize = Px(12);
        nativeChatField.style.color = Col(TextMain);
        nativeChatChanged = DelegateSupport.ConvertDelegate<EventCallback<ChangeEvent<string>>>(new Action<ChangeEvent<string>>(evt =>
        {
            SyncNativeChatText(); ThemeNativeChat(); textEvents++;
            CoachPlugin.L.LogInfo("Chat input: native text changed (" + inputText.Length + " chars)");
        }));
        nativeChatFocusIn = DelegateSupport.ConvertDelegate<EventCallback<FocusInEvent>>(new Action<FocusInEvent>(evt => { if (ChatVisible) ChatActivate(); }));
        nativeChatFocusOut = DelegateSupport.ConvertDelegate<EventCallback<FocusOutEvent>>(new Action<FocusOutEvent>(evt =>
        {
            var next = evt.relatedTarget?.TryCast<VisualElement>();
            for (var e = next; e != null; e = e.parent) if (e.Pointer == nativeChatField.Pointer) return;
            ChatDeactivate("native field lost focus");
        }));
        nativeChatKey = DelegateSupport.ConvertDelegate<EventCallback<KeyDownEvent>>(new Action<KeyDownEvent>(evt =>
        {
            if (!inputActive) return;
            if (evt.keyCode == KeyCode.Return || evt.keyCode == KeyCode.KeypadEnter)
            {
                SyncNativeChatText(); evt.PreventDefault(); evt.StopImmediatePropagation(); SubmitTyped();
            }
            else if (evt.keyCode == KeyCode.Escape)
            {
                evt.PreventDefault(); evt.StopImmediatePropagation(); ChatDeactivate("native Esc");
            }
            // Other keys must reach the editor; its native focus bridge owns shortcut isolation.
        }));
        nativeChatField.RegisterCallback<ChangeEvent<string>>(nativeChatChanged, TrickleDown.NoTrickleDown);
        nativeChatField.RegisterCallback<FocusInEvent>(nativeChatFocusIn, TrickleDown.NoTrickleDown);
        nativeChatField.RegisterCallback<FocusOutEvent>(nativeChatFocusOut, TrickleDown.NoTrickleDown);
        nativeChatField.RegisterCallback<KeyDownEvent>(nativeChatKey, TrickleDown.TrickleDown);
        var s = chatBox.style;
        s.flexGrow = new StyleFloat(1f); s.flexShrink = new StyleFloat(1f); s.minWidth = Px(0);
        s.marginRight = Px(6); s.minHeight = Px(24);
        s.backgroundColor = Col(new Color(0f, 0f, 0f, 0.35f));
        BorderAll(s, 1, Divider); Radius(s, 4); Pad(s, 3, 6);
        s.justifyContent = new StyleEnum<Justify>(Justify.Center);
        chatBox.pickingMode = PickingMode.Position;
        chatBoxText = Text("", 12, TextDim);
        chatBoxText.style.whiteSpace = new StyleEnum<WhiteSpace>(WhiteSpace.Normal);
        chatBoxText.style.flexShrink = new StyleFloat(1f);
        chatBoxText.style.display = new StyleEnum<DisplayStyle>(DisplayStyle.None); // legacy fallback diagnostics, native field owns visible text
        chatBox.Add(chatBoxText);
        ThemeNativeChat(); RefreshChatBox();
    }

    void ThemeNativeChat()
    {
        if (nativeChatField == null) return;
        var pending = new Stack<VisualElement>(); pending.Push(nativeChatField);
        while (pending.Count > 0)
        {
            var element = pending.Pop();
            element.style.backgroundColor = Col(new Color(0f, 0f, 0f, 0f));
            element.style.backgroundImage = new StyleBackground(StyleKeyword.None);
            element.style.color = Col(TextMain); element.style.fontSize = Px(12);
            BorderAll(element.style, 0, Divider);
            var children = element.hierarchy;
            for (int i = 0; i < children.childCount; i++) pending.Push(children[i]);
        }
        nativeChatThemed = nativeChatField.panel != null;
    }

    void SyncNativeChatText()
    {
        if (nativeChatField == null) return;
        var value = CoachInputText.Normalize(nativeChatField.value, MaxQuestionChars);
        inputText.Clear(); inputText.Append(value);
        caret = Math.Max(0, Math.Min(nativeChatField.cursorIndex, inputText.Length));
        if (value != nativeChatField.value) nativeChatField.SetValueWithoutNotify(value);
    }

    void RefreshChatBox()
    {
        if (nativeChatField != null)
        {
            var value = inputText.ToString();
            if (nativeChatField.value != value) nativeChatField.SetValueWithoutNotify(value);
            BorderAll(chatBox.style, 1, inputActive ? Accent : Divider);
            return;
        }
        if (chatBoxText == null) return;
        if (typeToAskDisabled)
        {
            chatBoxText.text = "Type-to-ask disabled (" + typeDisabledReason + "). Use Documents\\fmcoach\\test\\ask.txt";
            chatBoxText.style.color = Col(new Color(0.95f, 0.70f, 0.30f, 1f));
            return;
        }
        string t = inputText.ToString();
        if (!inputActive)
        {
            chatBoxText.text = t.Length > 0 ? t : "Click here to type a question…";
            chatBoxText.style.color = Col(t.Length > 0 ? TextMain : TextDim);
            BorderAll(chatBox.style, 1, Divider);
            return;
        }
        bool on = ((int)(Time.unscaledTime * 2f)) % 2 == 0;
        chatBoxText.text = t.Substring(0, caret) + (on ? "|" : "\u2009") + t.Substring(caret);
        chatBoxText.style.color = Col(TextMain);
        BorderAll(chatBox.style, 1, Accent);
    }

    // Called from Update() on every left click inside the panel's coordinate space.
    void ChatMouseDown(Vector2 p)
    {
        if (chatBox == null || !ChatVisible) return;
        if (Hit(chatBox, p))
        {
            if (inputActive) return;
            // v0.7 fix (stray "Chat input ACTIVE" after an FM search): the pointer-down's topmost picked element must be
            // the chat box (or inside it), and the box must actually be laid out and visible. Log the target either way.
            string target = "?"; bool inside = false;
            try
            {
                var picked = chatBox.panel?.Pick(p);
                target = picked == null ? "(nothing)" : DescribeEl(picked);
                // v0.7.1: compare native pointers - Il2Cpp wrappers for the same element are different C# objects,
                // so '==' rejected the box itself in v0.7. Walk up from the picked target to the box.
                var boxPtr = chatBox.Pointer;
                for (var e = picked; e != null; e = e.parent) if (e.Pointer == boxPtr) { inside = true; break; }
                var wb = chatBox.worldBound;
                if (wb.width < 20 || wb.height < 10 || chatBox.resolvedStyle.display == DisplayStyle.None) inside = false;
            }
            catch (Exception ex) { LogOnce("ChatPick", ex); target = "pick failed: " + ex.GetType().Name; inside = false; }
            if (inside) { CoachPlugin.L.LogInfo("Chat input: pointer-down target " + target + " -> activate"); ChatActivate(); }
            else CoachPlugin.L.LogInfo($"Chat input: click at ({p.x:0},{p.y:0}) within the box bounds but target was {target} -> NOT activating");
        }
        else if (inputActive && !Hit(askBtn, p)) ChatDeactivate("click outside the box");
    }

    void ChatActivate()
    {
        if (inputActive) return;
        if (nativeChatField != null)
        {
            inputActive = true; activations++; pendingChars.Clear();
            nextBlockRefresh = 0;
            if (nativeChatEditor != null) nativeChatEditor.ForceStartEdit(); else nativeChatField.Focus();
            BlockFmInput(); ThemeNativeChat();
            CoachPlugin.L.LogInfo("Chat input ACTIVE (#" + activations + "): mode=" + (nativeChatEditor != null ? "fm-native-editor" : "native-ui") + ", FM action maps blocked=" + blockedMaps.Count);
            RefreshChatBox(); return;
        }
        if (typeToAskDisabled) { CoachPlugin.L.LogWarning("Chat input: click ignored, type-to-ask disabled (" + typeDisabledReason + ")"); return; }
        try
        {
            var kb = Keyboard.current;
            if (kb == null) throw new InvalidOperationException("no Keyboard.current");
            if (hookedKb == null || hookedKb.Pointer != kb.Pointer)
            {
                UnhookText();
                textHandler ??= DelegateSupport.ConvertDelegate<Il2CppSystem.Action<char>>(new Action<char>(OnTextInput));
                kb.add_onTextInput(textHandler);
                hookedKb = kb;
            }
        }
        catch (Exception ex)
        {
            typeToAskDisabled = true; typeDisabledReason = "keyboard hook failed: " + ex.GetType().Name;
            CoachPlugin.L.LogError("Chat input: " + typeDisabledReason + " - " + ex.Message + ". Fallback: ask.txt test hook.");
            RefreshChatBox(); return;
        }
        inputActive = true; activations++;
        caret = inputText.Length; pendingChars.Clear(); letterPressesWithoutText = 0;
        nextBlockRefresh = 0; BlockFmInput();
        CoachPlugin.L.LogInfo($"Chat input ACTIVE (#{activations}): text hook on {hookedKb?.name}, FM action maps blocked={blockedMaps.Count} [{string.Join(",", blockedNames)}] blockApi={(blockOk ? "ok" : "FAILED")}, mode={(polledTextMode ? "polled" : "text-events")}");
        RefreshChatBox();
    }

    void ChatDeactivate(string why)
    {
        if (!inputActive) return;
        inputActive = false; repeatKey = Key.None;
        try { if (nativeChatEditor != null && nativeChatEditor.HasInput) nativeChatEditor.EndEdit(); } catch (Exception ex) { LogOnce("NativeEndEdit", ex); }
        nativeChatField?.Blur();
        UnblockFmInput();
        CoachPlugin.L.LogInfo($"Chat input inactive ({why}): textEvents={textEvents} polledChars={polledChars} backspaces={backspaces}; FM shortcuts unblocked");
        RefreshChatBox();
    }

    void UnhookText()
    {
        try { if (hookedKb != null && textHandler != null) hookedKb.remove_onTextInput(textHandler); } catch (Exception ex) { LogOnce("UnhookText", ex); }
        hookedKb = null;
    }

    // Input System callback (main thread, during the Input System update). Only queue here.
    void OnTextInput(char c)
    {
        try { textEvents++; if (inputActive) pendingChars.Enqueue(c); } catch { }
    }

    // ---- FM shortcut suppression via FM's own InputManager block requests ----
    FmInput FmInputManager()
    {
        try { var im = SI.Core.ManualSingleton<FmInput>.Instance; if (im != null) return im; } catch (Exception ex) { LogOnce("InputManagerInstance", ex); }
        try
        {
            var all = Resources.FindObjectsOfTypeAll(Il2CppType.Of<FmInput>());
            if (all != null && all.Length > 0) return all[0].TryCast<FmInput>();
        }
        catch (Exception ex) { LogOnce("InputManagerFind", ex); }
        return null;
    }

    void BlockFmInput()
    {
        if (Time.unscaledTime < nextBlockRefresh) return;
        nextBlockRefresh = Time.unscaledTime + 0.5f; // maps enabled later (screen change) get blocked too
        try
        {
            var im = FmInputManager();
            if (im == null) { blockOk = false; LogOnceMsg("NoInputManager", "Chat input: FM InputManager not found; FM shortcuts may fire while typing"); return; }
            if (blockedMaps.Count == 0 && blockedNames.Count == 0) blockKey = new InputRequestKey(panelRoot, "FMCoachChatTyping");
            var maps = new List<InputActionMap>();
            try
            {
                var enabled = new Il2CppSystem.Collections.Generic.List<InputAction>();
                InputSystem.ListEnabledActions(enabled);
                for (int i = 0; i < enabled.Count; i++) { var map = enabled[i]?.actionMap; if (map != null) maps.Add(map); }
            }
            catch (Exception ex) { LogOnce("ListEnabledActions", ex); }
            try { var gw = im.m_asset?.FindActionMap("GameWorld", false); if (gw != null) maps.Add(gw); } catch (Exception ex) { LogOnce("FindGameWorldMap", ex); }
            if (!loggedMaps)
            {
                loggedMaps = true;
                var names = new SortedSet<string>(); foreach (var m in maps) names.Add(m.name + (m.enabled ? "" : "(off)"));
                CoachPlugin.L.LogInfo("Chat input: FM action maps seen: " + string.Join(", ", names));
            }
            foreach (var map in maps)
            {
                var n = map.name;
                if (nativeChatEditor != null && n != "GameWorld") continue; // FM's editor owns Global/Navigation input routing and isolation.
                if (blockedNames.Contains(n)) continue;
                im.BlockActionMap(blockKey, map, true);
                blockedNames.Add(n); blockedMaps.Add(map);
            }
            blockOk = blockedNames.Count > 0;
        }
        catch (Exception ex) { blockOk = false; LogOnce("BlockActionMap", ex); }
    }

    void UnblockFmInput()
    {
        var im = FmInputManager();
        if (im != null)
        {
            foreach (var map in blockedMaps) { try { im.UnblockActionMap(blockKey, map); } catch (Exception ex) { LogOnce("UnblockActionMap", ex); } }
            try { if (panelRoot != null) im.RemoveAllInputBlockRequestsFromRequester(panelRoot); } catch (Exception ex) { LogOnce("RemoveBlockRequests", ex); }
        }
        blockedMaps.Clear(); blockedNames.Clear();
    }

    void OnDestroy() { try { ChatDeactivate("plugin destroyed"); UnhookText(); coachLauncher?.RemoveFromHierarchy(); } catch { } }

    // ---- per-frame editing ----
    void ChatInputTick()
    {
        if (nativeChatField != null)
        {
            if (!nativeChatThemed && nativeChatField.panel != null) ThemeNativeChat();
            if (!ChatVisible) { ChatDeactivate("chat hidden"); RefreshChatBox(); return; }
            if (inputActive) { BlockFmInput(); SyncNativeChatText(); }
            else RefreshChatBox();
            return; // Native editing and change events own characters; never also poll/append keyboard characters.
        }
        if (!inputActive) { if (chatBoxText != null && Time.frameCount % 30 == 0) RefreshChatBox(); return; }
        if (!ChatVisible) { ChatDeactivate(!show ? "panel hidden" : collapsed ? "panel minimized" : "tab " + tab); return; }
        var kb = Keyboard.current;
        if (kb == null) { ChatDeactivate("keyboard gone"); return; }
        BlockFmInput();
        bool ctrl = kb.leftCtrlKey.isPressed || kb.rightCtrlKey.isPressed;
        bool changed = false;

        if (kb.escapeKey.wasPressedThisFrame) { pendingChars.Clear(); ChatDeactivate("Esc"); return; }
        if (kb.enterKey.wasPressedThisFrame || kb.numpadEnterKey.wasPressedThisFrame) { pendingChars.Clear(); SubmitTyped(); return; }

        // printable characters from OS text events
        while (pendingChars.Count > 0)
        {
            var c = pendingChars.Dequeue();
            if (c < 0x20 || c == 0x7F) continue; // control chars (Backspace/Enter/Esc/Ctrl+V) are handled as keys below
            if (ctrl && !(kb.rightAltKey.isPressed)) continue; // Ctrl+letter is a shortcut, not text (AltGr = Ctrl+Alt is allowed)
            changed |= InsertChar(c);
        }
        // fallback when the text event never arrives (logged once)
        if (!polledTextMode && textEvents == 0 && AnyLetterPressed(kb) && ++letterPressesWithoutText >= 3)
        {
            polledTextMode = true;
            CoachPlugin.L.LogWarning("Chat input: Keyboard.onTextInput delivered no characters after 3 letter presses; switching to polled US-layout input");
        }
        if (polledTextMode && !ctrl) changed |= PollChars(kb);

        // Ctrl+V paste
        if (ctrl && kb.vKey.wasPressedThisFrame)
        {
            try
            {
                var clip = GUIUtility.systemCopyBuffer ?? "";
                clip = clip.Replace("\r", " ").Replace("\n", " ").Replace("\t", " ");
                int n = 0; foreach (var c in clip) if (c >= 0x20 && InsertChar(c)) n++;
                CoachPlugin.L.LogInfo("Chat input: pasted " + n + " chars"); changed = true;
            }
            catch (Exception ex) { LogOnce("Paste", ex); }
        }
        // editing keys with key repeat
        changed |= EditKey(kb, Key.Backspace, kb.backspaceKey, () => { if (caret > 0) { inputText.Remove(caret - 1, 1); caret--; backspaces++; } });
        changed |= EditKey(kb, Key.Delete, kb.deleteKey, () => { if (caret < inputText.Length) inputText.Remove(caret, 1); });
        changed |= EditKey(kb, Key.LeftArrow, kb.leftArrowKey, () => { if (caret > 0) caret--; });
        changed |= EditKey(kb, Key.RightArrow, kb.rightArrowKey, () => { if (caret < inputText.Length) caret++; });
        changed |= EditKey(kb, Key.Home, kb.homeKey, () => caret = 0);
        changed |= EditKey(kb, Key.End, kb.endKey, () => caret = inputText.Length);
        if (repeatKey != Key.None && !kb[repeatKey].isPressed) repeatKey = Key.None;
        if (changed || Time.frameCount % 15 == 0) RefreshChatBox();
    }

    bool EditKey(Keyboard kb, Key key, KeyControl ctl, Action act)
    {
        if (ctl.wasPressedThisFrame) { act(); repeatKey = key; repeatAt = Time.unscaledTime + 0.45f; return true; }
        if (repeatKey == key && ctl.isPressed && Time.unscaledTime >= repeatAt) { act(); repeatAt = Time.unscaledTime + 0.04f; return true; }
        return false;
    }

    bool InsertChar(char c)
    {
        if (inputText.Length >= MaxQuestionChars) return false;
        inputText.Insert(caret, c); caret++;
        return true;
    }

    static readonly Key[] LetterKeys = { Key.A, Key.B, Key.C, Key.D, Key.E, Key.F, Key.G, Key.H, Key.I, Key.J, Key.K, Key.L, Key.M, Key.N, Key.O, Key.P, Key.Q, Key.R, Key.S, Key.T, Key.U, Key.V, Key.W, Key.X, Key.Y, Key.Z };
    static bool AnyLetterPressed(Keyboard kb) { foreach (var k in LetterKeys) if (kb[k].wasPressedThisFrame) return true; return false; }

    bool PollChars(Keyboard kb)
    {
        bool shift = kb.leftShiftKey.isPressed || kb.rightShiftKey.isPressed, any = false;
        for (int i = 0; i < LetterKeys.Length; i++)
            if (kb[LetterKeys[i]].wasPressedThisFrame) { any |= InsertChar((char)((shift ? 'A' : 'a') + i)); polledChars++; }
        const string digits = "0123456789", shifted = ")!@#$%^&*(";
        Key[] dk = { Key.Digit0, Key.Digit1, Key.Digit2, Key.Digit3, Key.Digit4, Key.Digit5, Key.Digit6, Key.Digit7, Key.Digit8, Key.Digit9 };
        for (int i = 0; i < 10; i++) if (kb[dk[i]].wasPressedThisFrame) { any |= InsertChar(shift ? shifted[i] : digits[i]); polledChars++; }
        (Key k, char n, char s)[] punct = { (Key.Space, ' ', ' '), (Key.Comma, ',', '<'), (Key.Period, '.', '>'), (Key.Slash, '/', '?'), (Key.Quote, '\'', '"'), (Key.Minus, '-', '_'), (Key.Semicolon, ';', ':'), (Key.Equals, '=', '+') };
        foreach (var (k, n, s) in punct) if (kb[k].wasPressedThisFrame) { any |= InsertChar(shift ? s : n); polledChars++; }
        return any;
    }

    void SubmitTyped()
    {
        if (nativeChatField != null) SyncNativeChatText();
        var q = inputText.ToString();
        if (string.IsNullOrWhiteSpace(q)) return;
        if (Chat != null && Chat.Busy) { Toast("Coach is still thinking..."); return; }
        CoachPlugin.L.LogInfo($"Chat input: submit {q.Length} chars (textEvents={textEvents} polledChars={polledChars} backspaces={backspaces}): {q}");
        inputText.Clear(); caret = 0;
        SubmitChat(q, false);
        RefreshChatBox();
    }

    readonly HashSet<string> loggedOnceMsgs = new();
    void LogOnceMsg(string key, string msg) { if (loggedOnceMsgs.Add(key)) CoachPlugin.L.LogWarning(msg); }

    static string DescribeEl(VisualElement e)
    {
        try { return (string.IsNullOrEmpty(e.name) ? e.GetIl2CppType().Name : e.name) + (e.parent != null ? " < " + (string.IsNullOrEmpty(e.parent.name) ? e.parent.GetIl2CppType().Name : e.parent.name) : ""); }
        catch { return "?"; }
    }
}
