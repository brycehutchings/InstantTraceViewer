using ImGuiNET;
using System;
using System.Numerics;

namespace InstantTraceViewerUI
{
    internal static class ImGuiWidgets
    {
        // It seems the only way to test hover/active state with non-internal API is to
        // call IsItemActive/IsItemHovered and store the result for the next frame.
        private static uint _lastActiveItem = uint.MaxValue;
        private static int _lastActiveItemFrame = 0;
        private static uint _lastHoveredItem = uint.MaxValue;
        private static int _lastHoveredItemFrame = 0;

        public static void ColorSquare(uint color, int verticalOffset = 0, float widthMultiplier = 1)
        {
            float sz = ImGui.GetTextLineHeight();
            Vector2 p = ImGui.GetCursorScreenPos() + new Vector2(0, verticalOffset);
            ImGui.GetWindowDrawList().AddRectFilled(p, new Vector2(p.X + sz * widthMultiplier, p.Y + sz + verticalOffset), color);
            ImGui.Dummy(new Vector2(sz * widthMultiplier, sz));
        }

        // Renders a button without any background or border. Text color changes instead.
        public static bool UndecoratedButton(string text, string? tooltip = null)
        {
            uint buttonId = ImGui.GetID(text);

            bool isActive = buttonId == _lastActiveItem && _lastActiveItemFrame == ImGui.GetFrameCount() - 1;
            bool isHovered = buttonId == _lastHoveredItem && _lastHoveredItemFrame == ImGui.GetFrameCount() - 1;
            ImGui.PushStyleColor(ImGuiCol.Text,
                isActive ? ImGui.GetColorU32(ImGuiCol.ButtonActive) :
                isHovered ? ImGui.GetColorU32(ImGuiCol.ButtonHovered) :
                            ImGui.GetColorU32(ImGuiCol.Text));
            ImGui.PushStyleColor(ImGuiCol.Button, 0x00000000);
            ImGui.PushStyleColor(ImGuiCol.ButtonActive, 0x00000000);
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, 0x00000000);

            // Remove padding from button
            ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(0, 0));

            bool ret = ImGui.SmallButton(text);

            ImGui.PopStyleVar();

            ImGui.PopStyleColor(4);

            if (ImGui.IsItemActive())
            {
                _lastActiveItem = ImGui.GetItemID();
                _lastActiveItemFrame = ImGui.GetFrameCount();
            }
            else if (ImGui.IsItemHovered())
            {
                _lastHoveredItem = ImGui.GetItemID();
                _lastHoveredItemFrame = ImGui.GetFrameCount();
            }

            if (ImGui.IsItemHovered(ImGuiHoveredFlags.DelayNormal))
            {
                ImGui.SetTooltip(tooltip);
            }

            return ret;
        }

        public static void HelpIconToolip(string helpText)
        {
            ImGui.PushID(helpText);

            // Text has an ID of 0 so we use the ID on the ID stack.
            var helpId = ImGui.GetItemID();
            bool isHovered = helpId == _lastHoveredItem && _lastHoveredItemFrame == ImGui.GetFrameCount() - 1;

            ImGui.PushStyleColor(ImGuiCol.Text, isHovered ? ImGui.GetColorU32(ImGuiCol.ButtonHovered) : ImGui.GetColorU32(ImGuiCol.Text));
            ImGui.TextUnformatted("\uF059");
            ImGui.PopStyleColor();

            if (ImGui.IsItemHovered(ImGuiHoveredFlags.DelayNormal))
            {
                ImGui.SetTooltip(helpText);
            }
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.DelayNone))
            {
                _lastHoveredItem = helpId;
                _lastHoveredItemFrame = ImGui.GetFrameCount();
            }

            ImGui.PopID();
        }

        public static void AddHighlightRowBgColorMenuItems(Action<HighlightRowBgColor> selectionAction)
        {
            float sz = ImGui.GetTextLineHeight();
            foreach (HighlightRowBgColor color in Enum.GetValues<HighlightRowBgColor>())
            {
                ImGui.PushID((int)color);

                Vector2 p = ImGui.GetCursorScreenPos();

                if (ImGui.MenuItem(""))
                {
                    selectionAction(color);
                }

                uint colorU32 = AppTheme.GetHighlightRowBgColorU32(color);

                ImGui.SetCursorScreenPos(p);
                ColorSquare(colorU32, widthMultiplier: 4);

                ImGui.PopID();
            }
        }
    }
}
