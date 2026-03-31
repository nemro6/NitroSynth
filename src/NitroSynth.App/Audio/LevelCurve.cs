using System;

namespace NitroSynth.App.Audio
{
    internal static class LevelCurve
    {
        // Single curve ratio used by all volume-related controls.
        public static float Ratio { get; set; } = 2.25f;

        public static float Apply(float normalized)
        {
            float x = Math.Clamp(normalized, 0f, 1f);
            if (x <= 0f) return 0f;
            if (x >= 1f) return 1f;

            float ratio = Math.Max(0.01f, Ratio);
            return (float)Math.Pow(x, ratio);
        }

        public static double Apply(double normalized)
        {
            return Apply((float)normalized);
        }
    }
}
