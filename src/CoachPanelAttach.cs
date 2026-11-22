// FM Coach v0.6.2 - robust panel attachment + visibility diagnostics (UI only; never touches game data).
using System;
using System.Text;
using UnityEngine;
using UnityEngine.UIElements;

public partial class CoachBehaviour
{
    float diagAt = -1f, nextFrontCheck;
    bool overlayLogged;

    static string PathOf(VisualElement e)
    {
        var sb = new StringBuilder(); int n = 0;
        for (var p = e; p != null && n < 12; p = p.parent, n++) sb.Insert(0, "/" + (string.IsNullOrEmpty(p.name) ? p.GetIl2CppType().Name : p.name));
        return sb.ToString();
    }

    static string StyleOf(VisualElement e)
    {
        try
        {
            var r = e.resolvedStyle; var l = e.layout;
            return $"{(string.IsNullOrEmpty(e.name) ? e.GetIl2CppType().Name : e.name)}[display={r.display} vis={r.visibility} op={r.opacity:0.##} size={l.width:0}x{l.height:0}]";
        }
        catch (Exception ex) { return e?.name + "[?" + ex.GetType().Name + "]"; }
    }

    // Attach to the CURRENT MainUIDocument root (not the 'Overlay' layer used up to v0.6) and keep it on top.
    void AttachPanel(VisualElement root, bool force)
    {
        if (root == null || panelRoot == null) return;
        EnsureCoachLauncher(root);
        bool ok = false;
        try { ok = panelRoot.panel != null && panelRoot.parent != null && panelRoot.parent.Pointer == root.Pointer; } catch { }
        if (!ok)
        {
            string was = "(none)";
            try { if (panelRoot.parent != null) was = PathOf(panelRoot.parent); } catch { }
            try { panelRoot.RemoveFromHierarchy(); } catch { }
            root.Add(panelRoot);
            CoachPlugin.L.LogInfo("Panel attached to MainUIDocument root '" + root.name + "' (was " + was + ")");
            LogOverlayOnce(root);
        }
        if (force || !ok) ForcePanelVisible();
    }

    void ForcePanelVisible()
    {
        var s = panelRoot.style;
        s.position = new StyleEnum<Position>(Position.Absolute);
        s.display = new StyleEnum<DisplayStyle>(show ? DisplayStyle.Flex : DisplayStyle.None);
        s.visibility = new StyleEnum<Visibility>(Visibility.Visible);
        s.opacity = new StyleFloat(1f);
        s.width = Px(360); s.minWidth = Px(360);
        try { panelRoot.BringToFront(); } catch (Exception ex) { LogOnce("BringToFront", ex); }
    }

    // Evidence for the v0.6 invisibility: how the old host layer looks at runtime.
    void LogOverlayOnce(VisualElement root)
    {
        if (overlayLogged) return; overlayLogged = true;
        try
        {
            var h = root.hierarchy; var sb = new StringBuilder();
            for (int i = 0; i < h.childCount; i++) sb.Append(StyleOf(h[i])).Append(' ');
            CoachPlugin.L.LogInfo("MainUIDocument root " + StyleOf(root) + " children: " + sb);
        }
        catch (Exception ex) { LogOnce("OverlayDiag", ex); }
    }

    void OnShowChanged()
    {
        try
        {
            var root = MainRoot() ?? currentRoot;
            if (root != null) { EnsurePanel(root); AttachPanel(root, true); }
            CoachPlugin.L.LogInfo("Coach panel " + (show ? "SHOW" : "HIDE") + (panelRoot == null ? " (panel not built yet: no MainUIDocument/binding system)" : ""));
            if (show) diagAt = Time.unscaledTime + 0.5f; // after the next layout pass
        }
        catch (Exception ex) { LogOnce("OnShowChanged", ex); }
    }

    // Called every frame from PlacePanel
    void PanelHousekeeping()
    {
        if (panelRoot == null) return;
        EnsureCoachLauncher(currentRoot);
        if (show && Time.unscaledTime >= nextFrontCheck)
        {
            nextFrontCheck = Time.unscaledTime + 1f;
            try
            {
                var p = panelRoot.parent;
                if (p != null)
                {
                    var h = p.hierarchy;
                    if (h.childCount > 0 && h[h.childCount - 1].Pointer != panelRoot.Pointer) panelRoot.BringToFront();
                    // maxHeight is set by PlacePanel (v0.6.2) from the root's resolved size.
                }
            }
            catch (Exception ex) { LogOnce("FrontCheck", ex); }
        }
        if (diagAt > 0 && Time.unscaledTime >= diagAt)
        {
            diagAt = -1f;
            try
            {
                var wb = panelRoot.worldBound;
                var sb = new StringBuilder();
                for (var a = panelRoot.parent; a != null; a = a.parent) sb.Append(StyleOf(a)).Append(" < ");
                int idx = -1, cnt = -1;
                try { var h = panelRoot.parent.hierarchy; cnt = h.childCount; idx = panelRoot.parent.hierarchy.IndexOf(panelRoot); } catch { }
                CoachPlugin.L.LogInfo($"Panel diag: worldBound=({wb.x:0},{wb.y:0},{wb.width:0}x{wb.height:0}) self={StyleOf(panelRoot)} path={PathOf(panelRoot)} siblingIndex={idx}/{cnt} screen={Screen.width}x{Screen.height} ancestors: {sb}");
            }
            catch (Exception ex) { LogOnce("PanelDiag", ex); }
        }
    }
}
