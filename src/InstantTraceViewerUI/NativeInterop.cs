using ImGuiNET;
using System.Runtime.InteropServices;

namespace InstantTraceViewerUI
{
    internal static class NativeInterop
    {
#pragma warning disable CS0649
        public unsafe partial struct CurrentInputTextState
        {
            public uint Id;
            public int CursorPos;
            public float ScrollX;
        }
#pragma warning restore CS0649

        [DllImport("InstantTraceViewerNative.dll", CallingConvention = CallingConvention.StdCall)]
        public static extern int WindowInitialize(out nint imguiContext);

        [DllImport("InstantTraceViewerNative.dll", CallingConvention = CallingConvention.StdCall)]
        public static extern int WindowBeginNextFrame(out int quit, out int occluded);

        [DllImport("InstantTraceViewerNative.dll", CallingConvention = CallingConvention.StdCall)]
        public static extern int WindowEndNextFrame();

        [DllImport("InstantTraceViewerNative.dll", CallingConvention = CallingConvention.StdCall)]
        public static extern int WindowCleanup();

        [DllImport("InstantTraceViewerNative.dll", CallingConvention = CallingConvention.StdCall)]
        public static extern void RebuildFontAtlas();

        [DllImport("InstantTraceViewerNative.dll", CallingConvention = CallingConvention.StdCall)]
        public static extern ImGuiPlatformImeData GetPlatformImeData();

        [DllImport("InstantTraceViewerNative.dll", CallingConvention = CallingConvention.StdCall)]
        public static extern CurrentInputTextState GetCurrentInputTextState();
    }

    // Annoyingly ImGui.NET does not provide these.
    internal static class ImGuiInternal
    {
        // CIMGUI_API void igTableSetColumnSortDirection(int column_n,ImGuiSortDirection sort_direction,bool append_to_sort_specs)
        [DllImport("cimgui.dll", EntryPoint = "igTableSetColumnSortDirection", CallingConvention = CallingConvention.StdCall)]
        public static extern void TableSetColumnSortDirection(int column_n, ImGuiSortDirection sort_direction, bool append_to_sort_specs);
    }
}
