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
            // Adjust the dark theme to match VSCode/VS/Teams color. Instead of harsh pure black background, a dark gray is used.
            if (Settings.Theme == ImGuiTheme.Dark)
            {
                ImGui.GetStyle().Colors[(int)ImGuiCol.WindowBg] = ImGui.ColorConvertU32ToFloat4(0xff1f1f1f);
                ImGui.GetStyle().Colors[(int)ImGuiCol.ScrollbarBg] = ImGui.ColorConvertU32ToFloat4(0xff1f1f1f);

                ImGui.GetStyle().Colors[(int)ImGuiCol.ChildBg] = ImGui.ColorConvertU32ToFloat4(0xff292929);
                ImGui.GetStyle().Colors[(int)ImGuiCol.PopupBg] = ImGui.ColorConvertU32ToFloat4(0xff292929);

                // Make alternate row color have less contrast. Goes from 6% to 3% alpha tint.
                ImGui.GetStyle().Colors[(int)ImGuiCol.TableRowBgAlt] = new Vector4(1, 1, 1, 0.03f);
            }
            else
            {
                // Make alternate row color have less contrast. Goes from 9% to 4.5% alpha tint.
                ImGui.GetStyle().Colors[(int)ImGuiCol.TableRowBgAlt] = new Vector4(0.3f, 0.3f, 0.3f, 0.045f);
            }

            InfoColor = ImGui.GetStyle().Colors[(int)ImGuiCol.Text];
            VerboseColor = InterpolateColor(0.4f, ImGuiCol.Text, ImGuiCol.WindowBg);
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
