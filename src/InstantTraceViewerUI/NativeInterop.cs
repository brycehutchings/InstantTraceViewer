using System.Runtime.InteropServices;

namespace InstantTraceViewerUI
{
    internal static class NativeInterop
    {
        [DllImport("InstantTraceViewerNative.dll", CallingConvention = CallingConvention.StdCall)]
        public static extern bool WindowInitialize(out nint imguiContext);

        [DllImport("InstantTraceViewerNative.dll", CallingConvention = CallingConvention.StdCall)]
        public static extern bool WindowBeginNextFrame(out bool quit);

        [DllImport("InstantTraceViewerNative.dll", CallingConvention = CallingConvention.StdCall)]
        public static extern bool WindowEndNextFrame();

        [DllImport("InstantTraceViewerNative.dll", CallingConvention = CallingConvention.StdCall)]
        public static extern bool WindowCleanup();

        [DllImport("InstantTraceViewerNative.dll", CallingConvention = CallingConvention.StdCall)]
        public static extern void RebuildFontAtlas();
    }
}
