using System;
using System.Collections.Generic;
using System.IO.Hashing;
using System.Linq;
using System.Numerics;
using ImGuiNET;

namespace InstantTraceViewerUI
{
    enum ImGuiTheme
    {
        Dark,
        Light
    }

    // Color is computed using HSV where the hue is varied in 30 degree increments (12 * 30 = 360). This must happen at render time because the theme can change.
    public enum HighlightRowBgColor
    {
        _1, _2, _3, _4, _5, _6,
        _7, _8, _9, _10, _11, _12
    };

    internal static class AppTheme
    {
        public static Vector4 InfoColor;
        public static Vector4 VerboseColor;
        public static Vector4 WarningColor;
        public static Vector4 ErrorColor;
        public static Vector4 FatalColor;

        private static IReadOnlyList<uint> HighlightRowBgColors;

        // This color ends up alphablended over the alternating row colors.
        public static uint MatchingRowBgColor;

        static AppTheme()
        {
            UpdateTheme();
        }

        public static uint GetHighlightRowBgColorU32(HighlightRowBgColor index)
        {
            return HighlightRowBgColors[(int)index];
        }

        public static void UpdateTheme()
        {
            float highlightRowBgColorValue;

            // Adjust the dark theme to match VSCode/VS/Teams color. Instead of harsh pure black background, a dark gray is used.
            if (Settings.Theme == ImGuiTheme.Dark)
            {
                ImGui.StyleColorsDark();

                ImGui.GetStyle().Colors[(int)ImGuiCol.WindowBg] = ImGui.ColorConvertU32ToFloat4(0xff1f1f1f);
                ImGui.GetStyle().Colors[(int)ImGuiCol.ScrollbarBg] = ImGui.ColorConvertU32ToFloat4(0xff1f1f1f);

                ImGui.GetStyle().Colors[(int)ImGuiCol.TitleBg] = ImGui.ColorConvertU32ToFloat4(0xff1f1f1f);
                ImGui.GetStyle().Colors[(int)ImGuiCol.TitleBgActive] = ImGui.ColorConvertU32ToFloat4(0xff1f1f1f);

                ImGui.GetStyle().Colors[(int)ImGuiCol.ChildBg] = ImGui.ColorConvertU32ToFloat4(0xff292929);
                ImGui.GetStyle().Colors[(int)ImGuiCol.PopupBg] = ImGui.ColorConvertU32ToFloat4(0xff292929);

                // Make alternate row color have less contrast. Goes from 6% to 3% alpha tint.
                ImGui.GetStyle().Colors[(int)ImGuiCol.TableRowBgAlt] = new Vector4(1, 1, 1, 0.03f);

                ThreadTimelineLogViewRegionColor = 0xFF404040;

                highlightRowBgColorValue = 0.4f; // Highlight colors need to be darker so the bright text is visible.
            }
            else
            {
                ImGui.StyleColorsLight();

                // Make alternate row color have less contrast. Goes from 9% to 4.5% alpha tint.
                ImGui.GetStyle().Colors[(int)ImGuiCol.TableRowBgAlt] = new Vector4(0.3f, 0.3f, 0.3f, 0.045f);

                ThreadTimelineLogViewRegionColor = 0xFFC0C0C0;

                highlightRowBgColorValue = 0.75f; // Highlight colors need to be brighter so the dark text is visible.
            }

            // FIXME: Red is too similar to error text.
            int highlightRowBgColorCount = Enum.GetValues<HighlightRowBgColor>().Length;
            HighlightRowBgColors = Enumerable.Range(0, highlightRowBgColorCount).Select(i =>
            {
                int hue = i * (360 / highlightRowBgColorCount);
                ImGui.ColorConvertHSVtoRGB(hue / 360.0f, 1.0f /* saturation */, highlightRowBgColorValue, out float r, out float g, out float b);
                return ImGui.GetColorU32(new Vector4(r, g, b, 1.0f));
            }).ToArray();

            InfoColor = ImGui.GetStyle().Colors[(int)ImGuiCol.Text];
            VerboseColor = InterpolateColor(0.4f, ImGuiCol.Text, ImGuiCol.WindowBg);
            MatchingRowBgColor = ImGui.ColorConvertFloat4ToU32(AdjustColorAlpha(ImGuiCol.TableRowBgAlt, 3.0f));
            WarningColor = new Vector4(1.0f, 0.65f, 0.0f, 1.0f);      // Orange
            ErrorColor = new Vector4(0.9f, 0.0f, 0.0f, 1.0f);         // Red
            FatalColor = new Vector4(0.70f, 0.0f, 0.0f, 1.0f);        // Dark Red
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

        public static uint ThreadTimelineLogViewRegionColor;
    }
}
