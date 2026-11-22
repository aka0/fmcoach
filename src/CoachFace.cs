// FM Coach - animated pixel face (Doom status-bar style). Purely cosmetic UI; reads nothing from and writes nothing to the game.
using System;
using System.IO;
using UnityEngine;
using UnityEngine.UIElements;

public enum CoachMood { Neutral, Thinking, Pleased, Disgusted, Worried, Angry, Smug, Disapproving }

public class CoachFace
{
    // Sheet order (assets/faces/order.txt): 0 neutral, 1 idle_left, 2 idle_right, 3 blink, 4 thinking, 5 pleased,
    // 6 disgusted, 7 worried, 8 angry, 9 smug, 10 disapproving
    public const int W = 32, H = 40, Frames = 11;
    const int FNeutral = 0, FLeft = 1, FRight = 2, FBlink = 3;
    public const float ReactionSeconds = 3f;
    static readonly int[] MoodFrame = { 0, 4, 5, 6, 7, 8, 9, 10 };
    public static readonly CoachMood[] DebugCycle = { CoachMood.Neutral, CoachMood.Thinking, CoachMood.Pleased, CoachMood.Disgusted, CoachMood.Worried, CoachMood.Angry, CoachMood.Smug, CoachMood.Disapproving };

    readonly Texture2D[] tex = new Texture2D[Frames];
    public Image View { get; private set; }
    readonly System.Random rng = new System.Random();
    int shown = -1, lastDx = 99;
    CoachMood mood = CoachMood.Neutral;
    float reactionStart = -1f, reactionUntil = -1f;
    bool thinking;
    int debugFrame = -1; float debugUntil;
    int idleFrame = FNeutral; float idleUntil, nextIdle;

    public CoachMood Mood => mood;
    public bool Thinking => thinking;

    public CoachFace()
    {
        byte[] sheet;
        using (var s = typeof(CoachFace).Assembly.GetManifestResourceStream("FMCoach.coach_faces_32x40.rgba"))
        {
            if (s == null) throw new InvalidOperationException("face sheet resource missing");
            sheet = new byte[s.Length]; int o = 0; while (o < sheet.Length) { int r = s.Read(sheet, o, sheet.Length - o); if (r <= 0) break; o += r; }
        }
        int sheetW = W * Frames;
        if (sheet.Length != sheetW * H * 4) throw new InvalidOperationException("face sheet size " + sheet.Length);
        CoachFacePixels.NormalizeBackground(sheet);
        for (int f = 0; f < Frames; f++)
        {
            var px = new byte[W * H * 4];
            for (int y = 0; y < H; y++) Buffer.BlockCopy(sheet, (y * sheetW + f * W) * 4, px, y * W * 4, W * 4); // rows already bottom-up
            var t = new Texture2D(W, H, TextureFormat.RGBA32, false);
            t.filterMode = FilterMode.Point; t.wrapMode = TextureWrapMode.Clamp;
            t.LoadRawTextureData(px); t.Apply(false, false);
            t.hideFlags = HideFlags.HideAndDontSave; UnityEngine.Object.DontDestroyOnLoad(t);
            tex[f] = t;
        }
    }

    public Image MakeView(float scale = 1f)
    {
        View = new Image();
        View.scaleMode = ScaleMode.ScaleToFit;
        View.style.width = new StyleLength(W * scale); View.style.height = new StyleLength(H * scale);
        View.style.marginRight = new StyleLength(8f); View.style.marginLeft = new StyleLength(0f);
        View.pickingMode = PickingMode.Ignore;
        shown = -1; lastDx = 99; Show(FNeutral, 0);
        return View;
    }

    /// Tags from the AI reply: neutral | pleased | disgusted | worried | angry | smug | disagree (alias disapproving) | thinking.
    public static bool TryParse(string tag, out CoachMood m)
    {
        m = CoachMood.Neutral;
        switch ((tag ?? "").Trim().ToLowerInvariant())
        {
            case "neutral": case "": m = CoachMood.Neutral; return true;
            case "thinking": m = CoachMood.Thinking; return true;
            case "pleased": case "happy": m = CoachMood.Pleased; return true;
            case "disgusted": m = CoachMood.Disgusted; return true;
            case "worried": m = CoachMood.Worried; return true;
            case "angry": m = CoachMood.Angry; return true;
            case "smug": case "smirk": m = CoachMood.Smug; return true;
            case "disagree": case "disapproving": case "disapprove": m = CoachMood.Disapproving; return true;
        }
        return false;
    }

    public void SetMood(string tag) { if (TryParse(tag, out var m)) SetMood(m); }

    /// Shows the reaction for ~3 s, then returns to idle. Neutral returns to idle immediately.
    public void SetMood(CoachMood m)
    {
        debugFrame = -1;
        mood = m;
        float now = Time.unscaledTime;
        reactionStart = now; reactionUntil = m == CoachMood.Neutral ? now : now + ReactionSeconds;
        nextIdle = now + ReactionSeconds + 1f;
    }

    /// Hold the thinking face while an AI request is in flight (the future chat sets this true/false).
    public void SetThinking(bool on) { thinking = on; if (!on) nextIdle = Time.unscaledTime + 1f; }

    public string PreviewNextFrame()
    {
        debugFrame = (debugFrame + 1) % Frames;
        debugUntil = Time.unscaledTime + 12f;
        var names = new[] { "neutral", "look left", "look right", "blink", "thinking", "pleased", "disgusted", "worried", "angry", "smug", "disapproving" };
        return (debugFrame + 1) + "/" + Frames + " · " + names[debugFrame];
    }

    public void Tick()
    {
        if (View == null) return;
        float now = Time.unscaledTime;
        int frame, dx = 0;
        if (thinking)
        {
            frame = MoodFrame[(int)CoachMood.Thinking];
        }
        else if (debugFrame >= 0 && now < debugUntil)
        {
            frame = debugFrame;
        }
        else if (now < reactionUntil)
        {
            frame = MoodFrame[(int)mood];
            if (mood == CoachMood.Disapproving)
            {
                // slow head shake ("no, no...") during the first second, then hold the stern look
                float t = now - reactionStart;
                if (t < 1.1f) { int step = (int)(t / 0.14f); dx = (step % 4) switch { 0 => -1, 1 => 1, 2 => -1, _ => 1 }; }
            }
            else if (mood == CoachMood.Angry)
            {
                float t = now - reactionStart;
                if (t < 0.5f) dx = ((int)(t / 0.06f) % 2 == 0) ? -1 : 1; // short angry shudder
            }
        }
        else
        {
            if (now >= nextIdle)
            {
                int r = rng.Next(100);
                if (r < 40) { idleFrame = FBlink; idleUntil = now + 0.15f; }
                else if (r < 70) { idleFrame = FLeft; idleUntil = now + 0.6f + (float)rng.NextDouble() * 0.6f; }
                else { idleFrame = FRight; idleUntil = now + 0.6f + (float)rng.NextDouble() * 0.6f; }
                nextIdle = now + 2f + (float)rng.NextDouble() * 3f;
            }
            frame = now < idleUntil ? idleFrame : FNeutral;
        }
        Show(frame, dx);
    }

    void Show(int frame, int dx)
    {
        if (frame != shown) { View.image = tex[frame]; shown = frame; }
        if (dx != lastDx)
        {
            // shift inside a constant 8px gutter so the header layout never moves
            View.style.marginLeft = new StyleLength(2f + dx * 2f);
            View.style.marginRight = new StyleLength(6f - dx * 2f);
            lastDx = dx;
        }
    }
}
