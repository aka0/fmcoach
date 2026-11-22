using System;

// Shared palette quantization can leave a near-duplicate of the transparent background opaque.
internal static class CoachFacePixels
{
    internal static int NormalizeBackground(byte[] rgba)
    {
        if (rgba == null || rgba.Length % 4 != 0) throw new ArgumentException("Expected RGBA pixels", nameof(rgba));
        int background = -1;
        for (int i = 0; i < rgba.Length; i += 4)
            if (rgba[i + 3] == 0) { background = i; break; }
        if (background < 0) return 0;

        int red = rgba[background], green = rgba[background + 1], blue = rgba[background + 2], removed = 0;
        for (int i = 0; i < rgba.Length; i += 4)
        {
            if (rgba[i + 3] == 0) continue;
            if (Math.Abs(rgba[i] - red) > 2 || Math.Abs(rgba[i + 1] - green) > 2 || Math.Abs(rgba[i + 2] - blue) > 2) continue;
            rgba[i + 3] = 0;
            removed++;
        }
        return removed;
    }
}
