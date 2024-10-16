using System;
using System.Numerics;
using ImGuiNET;

namespace InstantTraceViewerUI
{
    enum ImGuiTheme
    {
        Dark,
        Light
    }

    internal static class AppTheme
    {
        public static Vector4 InfoColor;
        public static Vector4 VerboseColor;
        public static Vector4 WarningColor;
        public static Vector4 ErrorColor;
        public static Vector4 CriticalColor;

        // This color ends up alphablended over the alternating row colors.
        public static Vector4 MatchingRowBgColor = AdjustColorAlpha(ImGuiCol.TableRowBgAlt, 3.0f);

        static AppTheme()
        {
            UpdateTheme();
        }

        public static void UpdateTheme()
        {
            InfoColor = ImGui.GetStyle().Colors[(int)ImGuiCol.Text];
            VerboseColor = InterpolateColor(0.6f, ImGuiCol.Text, ImGuiCol.WindowBg);
            MatchingRowBgColor = AdjustColorAlpha(ImGuiCol.TableRowBgAlt, 3.0f);
            WarningColor = new Vector4(1.0f, 0.65f, 0.0f, 1.0f);      // Orange
            ErrorColor = new Vector4(0.9f, 0.0f, 0.0f, 1.0f);         // Red
            CriticalColor = new Vector4(0.70f, 0.0f, 0.0f, 1.0f);     // Dark Red
        }

        private static Vector4 InterpolateColor(float t, ImGuiCol startColor, ImGuiCol endColor)
        {
            Vector4 startColorVec = ImGui.GetStyle().Colors[(int)startColor];
            Vector4 endColorVec = ImGui.GetStyle().Colors[(int)endColor];
            Vector4 lerpedColor = Vector4.Min(Vector4.Max(Vector4.Lerp(startColorVec, endColorVec, t), Vector4.Zero), Vector4.One);
            return lerpedColor;
        }

        private static Vector4 AdjustColorAlpha(ImGuiCol color, float alphaScale)
        {
            Vector4 colorVec = ImGui.GetStyle().Colors[(int)color];
            return new Vector4(colorVec.X, colorVec.Y, colorVec.Z, Math.Clamp(colorVec.W * alphaScale, 0.0f, 1.0f));
        }
    }
}
