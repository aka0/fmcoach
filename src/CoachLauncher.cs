using System;
using Il2CppInterop.Runtime;
using UnityEngine;
using UnityEngine.UIElements;

public partial class CoachBehaviour
{
    Button coachLauncher;
    Il2CppSystem.Action coachLauncherClick;

    void EnsureCoachLauncher(VisualElement root)
    {
        if (root == null) return;
        if (coachLauncher == null)
        {
            coachLauncher = new Button { name = "fmcoach-launcher", text = "COACH", focusable = false };
            var style = coachLauncher.style;
            style.position = new StyleEnum<Position>(Position.Absolute);
            // Leave the bottom action row clear for FM's Apply / Cancel buttons.
            style.right = Px(18); style.bottom = Px(76); style.width = Px(96); style.height = Px(34);
            style.backgroundColor = Col(CardBg); style.color = Col(Accent); style.fontSize = Px(12);
            BorderAll(style, 1, Accent); Radius(style, 6);
            coachLauncherClick = DelegateSupport.ConvertDelegate<Il2CppSystem.Action>(new Action(() =>
            {
                show = true; collapsed = false; SetTab("Chat");
                CoachPlugin.L.LogInfo("Coach launcher clicked");
                OnShowChanged(); dirty = true; chatDirty = true;
            }));
            coachLauncher.add_clicked(coachLauncherClick);
        }
        if (coachLauncher.parent == null || coachLauncher.parent.Pointer != root.Pointer)
        {
            coachLauncher.RemoveFromHierarchy(); root.Add(coachLauncher);
            CoachPlugin.L.LogInfo("Coach launcher attached (above bottom action row; click to open)");
        }
        bool available = !show && humanTv != null;
        coachLauncher.style.display = new StyleEnum<DisplayStyle>(available ? DisplayStyle.Flex : DisplayStyle.None);
        if (available) coachLauncher.BringToFront();
    }
}
