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
        // https://sashamaps.net/docs/resources/20-colors/
        Maroon, Brown, Olive, Teal, Navy, Black,
        Red, Orange, Yellow, Lime, Green, Cyan, Blue, Purple, Magenta, Grey,
        Pink, Apricot, Beige, Mint, Lavender, White
    };

    internal static class AppTheme
    {
        public static uint InfoColor;
        public static uint VerboseColor;
        public static uint WarningColor;
        public static uint ErrorColor;
        public static uint FatalColor;

        private static IReadOnlyList<uint> HighlightRowBgColors;

        // This color ends up alphablended over the alternating row colors.
        public static uint MatchingRowBgColor;

            uint[] highlightRowBgColors = new uint[Enum.GetValues<HighlightRowBgColor>().Length];
            highlightRowBgColors[(int)HighlightRowBgColor.Maroon] = 0xFF000080;
            highlightRowBgColors[(int)HighlightRowBgColor.Brown] = 0xFF24639A;
            highlightRowBgColors[(int)HighlightRowBgColor.Olive] = 0xFF008080;
            highlightRowBgColors[(int)HighlightRowBgColor.Teal] = 0xFF909946;
            highlightRowBgColors[(int)HighlightRowBgColor.Navy] = 0xFF750000;
            highlightRowBgColors[(int)HighlightRowBgColor.Black] = 0xFF000000;
            highlightRowBgColors[(int)HighlightRowBgColor.Red] = 0xFF4B19E6;
            highlightRowBgColors[(int)HighlightRowBgColor.Orange] = 0xFF3182F5;
            highlightRowBgColors[(int)HighlightRowBgColor.Yellow] = 0xFF19E1FF;
            highlightRowBgColors[(int)HighlightRowBgColor.Lime] = 0xFF45EFBF;
            highlightRowBgColors[(int)HighlightRowBgColor.Green] = 0xFF4BB43C;
            highlightRowBgColors[(int)HighlightRowBgColor.Cyan] = 0xFFF4D442;
            highlightRowBgColors[(int)HighlightRowBgColor.Blue] = 0xFFD86343;
            highlightRowBgColors[(int)HighlightRowBgColor.Purple] = 0xFFB41E91;
            highlightRowBgColors[(int)HighlightRowBgColor.Magenta] = 0xFFE632F0;
            highlightRowBgColors[(int)HighlightRowBgColor.Grey] = 0xFFA9A9A9;
            highlightRowBgColors[(int)HighlightRowBgColor.Pink] = 0xFFD4BEFA;
            highlightRowBgColors[(int)HighlightRowBgColor.Apricot] = 0xFFB1D8FF;
            highlightRowBgColors[(int)HighlightRowBgColor.Beige] = 0xFFC8FAFF;
            highlightRowBgColors[(int)HighlightRowBgColor.Mint] = 0xFFC3FFAA;
            highlightRowBgColors[(int)HighlightRowBgColor.Lavender] = 0xFFffbedc;
            highlightRowBgColors[(int)HighlightRowBgColor.White] = 0xFFFFFFFF;
            HighlightRowBgColors = highlightRowBgColors;

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
            }
            else
            {
                ImGui.StyleColorsLight();

                // Make alternate row color have less contrast. Goes from 9% to 4.5% alpha tint.
                ImGui.GetStyle().Colors[(int)ImGuiCol.TableRowBgAlt] = new Vector4(0.3f, 0.3f, 0.3f, 0.045f);

                ThreadTimelineLogViewRegionColor = 0xFFC0C0C0;
            }

            InfoColor = ImGui.ColorConvertFloat4ToU32(ImGui.GetStyle().Colors[(int)ImGuiCol.Text]);
            VerboseColor = ImGui.ColorConvertFloat4ToU32(InterpolateColor(0.4f, ImGuiCol.Text, ImGuiCol.WindowBg));
            MatchingRowBgColor = ImGui.ColorConvertFloat4ToU32(AdjustColorAlpha(ImGuiCol.TableRowBgAlt, 3.0f));
            WarningColor = 0xff00a6e6;  // Orange
            ErrorColor = 0xff0000e6;    // Red
            FatalColor = 0xff0000b3;    // Dark Red
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
