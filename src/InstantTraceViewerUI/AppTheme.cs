using System;
using System.Numerics;
using Hexa.NET.ImGui;

namespace InstantTraceViewerUI
{
    enum ImGuiTheme
    {
        Dark,
        Light
    }

    public enum HighlightRowBgColor
    {
        // This palette is dynamic based on light/dark theme:
        _Gray = 0,
        _Red,
        _Orange,
        _Amber,
        _YellowLime,
        _Lime,
        _Green,
        _Teal,
        _Cyan,
        _Sky,
        _Blue,
        _Indigo,
        _Violet,
        _Purple,
        _Pink,

        // LEGACY COLOR PALETTE (Does not change with light/dark theme):
        // This is needed for loading old ITVF files.
        // https://sashamaps.net/docs/resources/20-colors/
        [Obsolete] Maroon, [Obsolete] Brown, [Obsolete] Olive, [Obsolete] Teal, [Obsolete] Navy, [Obsolete] Black,
        [Obsolete] Red, [Obsolete] Orange, [Obsolete] Yellow, [Obsolete] Lime, [Obsolete] Green, [Obsolete] Cyan, 
        [Obsolete] Blue, [Obsolete] Purple, [Obsolete] Magenta, [Obsolete] Grey, [Obsolete] Pink, [Obsolete] Apricot,
        [Obsolete] Beige, [Obsolete] Mint, [Obsolete] Lavender, [Obsolete] White
    };

    internal static class AppTheme
    {
        public static uint InfoColor;
        public static uint VerboseColor;
        public static uint WarningColor;
        public static uint ErrorColor;
        public static uint FatalColor;

        private static uint[] HighlightRowBgColors = new uint[Enum.GetValues<HighlightRowBgColor>().Length];

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

        public static string GetHighlightRowBgColorName(HighlightRowBgColor index)
        {
            return Enum.GetName(typeof(HighlightRowBgColor), index).TrimStart('_');
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

            SetHighlightRowBgColors();

            InfoColor = ImGui.ColorConvertFloat4ToU32(ImGui.GetStyle().Colors[(int)ImGuiCol.Text]);
            VerboseColor = ImGui.ColorConvertFloat4ToU32(InterpolateColor(0.4f, ImGuiCol.Text, ImGuiCol.WindowBg));
            MatchingRowBgColor = ImGui.ColorConvertFloat4ToU32(AdjustColorAlpha(ImGuiCol.TableRowBgAlt, 3.0f));
            WarningColor = 0xff00a6e6;  // Orange
            ErrorColor = 0xff0000e6;    // Red
            FatalColor = 0xff0000b3;    // Dark Red
        }

        private static void SetHighlightRowBgColors()
        {
#pragma warning disable CS0612 // Type or member is obsolete
            HighlightRowBgColors[(int)HighlightRowBgColor.Maroon] = 0xFF000080;
            HighlightRowBgColors[(int)HighlightRowBgColor.Brown] = 0xFF24639A;
            HighlightRowBgColors[(int)HighlightRowBgColor.Olive] = 0xFF008080;
            HighlightRowBgColors[(int)HighlightRowBgColor.Teal] = 0xFF909946;
            HighlightRowBgColors[(int)HighlightRowBgColor.Navy] = 0xFF750000;
            HighlightRowBgColors[(int)HighlightRowBgColor.Black] = 0xFF000000;
            HighlightRowBgColors[(int)HighlightRowBgColor.Red] = 0xFF4B19E6;
            HighlightRowBgColors[(int)HighlightRowBgColor.Orange] = 0xFF3182F5;
            HighlightRowBgColors[(int)HighlightRowBgColor.Yellow] = 0xFF19E1FF;
            HighlightRowBgColors[(int)HighlightRowBgColor.Lime] = 0xFF45EFBF;
            HighlightRowBgColors[(int)HighlightRowBgColor.Green] = 0xFF4BB43C;
            HighlightRowBgColors[(int)HighlightRowBgColor.Cyan] = 0xFFF4D442;
            HighlightRowBgColors[(int)HighlightRowBgColor.Blue] = 0xFFD86343;
            HighlightRowBgColors[(int)HighlightRowBgColor.Purple] = 0xFFB41E91;
            HighlightRowBgColors[(int)HighlightRowBgColor.Magenta] = 0xFFE632F0;
            HighlightRowBgColors[(int)HighlightRowBgColor.Grey] = 0xFFA9A9A9;
            HighlightRowBgColors[(int)HighlightRowBgColor.Pink] = 0xFFD4BEFA;
            HighlightRowBgColors[(int)HighlightRowBgColor.Apricot] = 0xFFB1D8FF;
            HighlightRowBgColors[(int)HighlightRowBgColor.Beige] = 0xFFC8FAFF;
            HighlightRowBgColors[(int)HighlightRowBgColor.Mint] = 0xFFC3FFAA;
            HighlightRowBgColors[(int)HighlightRowBgColor.Lavender] = 0xFFffbedc;
            HighlightRowBgColors[(int)HighlightRowBgColor.White] = 0xFFFFFFFF;
#pragma warning restore CS0612 // Type or member is obsolete

            if (Settings.Theme == ImGuiTheme.Dark)
            {
                HighlightRowBgColors[(int)HighlightRowBgColor._Gray] = 0xFF514137; // #374151
                HighlightRowBgColors[(int)HighlightRowBgColor._Red] = 0xFF111178; // #781111
                HighlightRowBgColors[(int)HighlightRowBgColor._Orange] = 0xFF113878; // #783811
                HighlightRowBgColors[(int)HighlightRowBgColor._Amber] = 0xFF115F78; // #785F11
                HighlightRowBgColors[(int)HighlightRowBgColor._YellowLime] = 0xFF11786C; // #6C7811
                HighlightRowBgColors[(int)HighlightRowBgColor._Lime] = 0xFF117845; // #457811
                HighlightRowBgColors[(int)HighlightRowBgColor._Green] = 0xFF11781E; // #1E7811
                HighlightRowBgColors[(int)HighlightRowBgColor._Teal] = 0xFF527811; // #117852
                HighlightRowBgColors[(int)HighlightRowBgColor._Cyan] = 0xFF787811; // #117878
                HighlightRowBgColors[(int)HighlightRowBgColor._Sky] = 0xFF785211; // #115278
                HighlightRowBgColors[(int)HighlightRowBgColor._Blue] = 0xFF782B11; // #112B78
                HighlightRowBgColors[(int)HighlightRowBgColor._Indigo] = 0xFF78111E; // #1E1178
                HighlightRowBgColors[(int)HighlightRowBgColor._Violet] = 0xFF781145; // #451178
                HighlightRowBgColors[(int)HighlightRowBgColor._Purple] = 0xFF78116C; // #6C1178
                HighlightRowBgColors[(int)HighlightRowBgColor._Pink] = 0xFF381178; // #781138
            }
            else
            {
                HighlightRowBgColors[(int)HighlightRowBgColor._Gray] = 0xFFDBD5D1; // #D1D5DB
                HighlightRowBgColors[(int)HighlightRowBgColor._Red] = 0xFF9A9AF4; // #F49A9A
                HighlightRowBgColors[(int)HighlightRowBgColor._Orange] = 0xFF9ABCF4; // #F4BC9A
                HighlightRowBgColors[(int)HighlightRowBgColor._Amber] = 0xFF9ADDF4; // #F4DD9A
                HighlightRowBgColors[(int)HighlightRowBgColor._YellowLime] = 0xFF9AF4E9; // #E9F49A
                HighlightRowBgColors[(int)HighlightRowBgColor._Lime] = 0xFF9AF4C7; // #C7F49A
                HighlightRowBgColors[(int)HighlightRowBgColor._Green] = 0xFF9AF4A5; // #A5F49A
                HighlightRowBgColors[(int)HighlightRowBgColor._Teal] = 0xFFD2F49A; // #9AF4D2
                HighlightRowBgColors[(int)HighlightRowBgColor._Cyan] = 0xFFF4F49A; // #9AF4F4
                HighlightRowBgColors[(int)HighlightRowBgColor._Sky] = 0xFFF4D29A; // #9AD2F4
                HighlightRowBgColors[(int)HighlightRowBgColor._Blue] = 0xFFF4B09A; // #9AB0F4
                HighlightRowBgColors[(int)HighlightRowBgColor._Indigo] = 0xFFF49AA5; // #A59AF4
                HighlightRowBgColors[(int)HighlightRowBgColor._Violet] = 0xFFF49AC7; // #C79AF4
                HighlightRowBgColors[(int)HighlightRowBgColor._Purple] = 0xFFF49AE9; // #E99AF4
                HighlightRowBgColors[(int)HighlightRowBgColor._Pink] = 0xFFBC9AF4; // #F49ABC
            }
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
