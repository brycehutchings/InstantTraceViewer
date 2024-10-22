using System.Runtime.InteropServices;

namespace InstantTraceViewerUI
{
    internal static class NativeInterop
    {
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
    }
}
