using ImGuiNET;
using System.Numerics;
using Windows.UI.WebUI;

namespace InstantTraceViewerUI
{
    internal static class ImGuiWidgets
    {
        // It seems the only way to test hover/active state with non-internal API is to
        // call IsItemActive/IsItemHovered and store the result for the next frame.
        private static uint _lastActiveButton = uint.MaxValue;
        private static int _lastActiveButtonFrame = 0;
        private static uint _lastHoveredButton = uint.MaxValue;
        private static int _lastHoveredButtonFrame = 0;

        // Renders a button without any background or border. Text color changes instead.
        public static bool UndecoratedButton(string text, string? tooltip = null)
        {
            uint buttonId = ImGui.GetID(text);

            bool isActive = buttonId == _lastActiveButton && _lastActiveButtonFrame == ImGui.GetFrameCount() - 1;
            bool isHovered = buttonId == _lastHoveredButton && _lastHoveredButtonFrame == ImGui.GetFrameCount() - 1;
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
                _lastActiveButton = ImGui.GetItemID();
                _lastActiveButtonFrame = ImGui.GetFrameCount();
            }
            else if (ImGui.IsItemHovered())
            {
                _lastHoveredButton = ImGui.GetItemID();
                _lastHoveredButtonFrame = ImGui.GetFrameCount();
            }

            if (ImGui.IsItemHovered(ImGuiHoveredFlags.DelayNormal))
            {
                ImGui.SetTooltip(tooltip);
            }

            return ret;
        }
    }
}
