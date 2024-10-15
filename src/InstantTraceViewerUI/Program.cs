using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using ImGuiNET;

namespace InstantTraceViewerUI
{
    internal class Program
    {
        // DPI awareness is disabled in the app.manifest file. If you start looking into making this app DPI-aware, you'll need to
        // re-enable that. Good luck.
        private const int FontSize = 16;

        [STAThread] // For WinForms file browser usage :-\
        public static int Main(string[] args)
        {
            if (!NativeInterop.WindowInitialize(out nint imguiContext))
            {
                return 1;
            }

            ImGui.SetCurrentContext(imguiContext);

            LoadFontAndScaleSizes();

            using (MainWindow mainWindow = new(args))
            {
                while (true)
                {
                    if (!NativeInterop.WindowBeginNextFrame(out bool quit) || quit)
                    {
                        break;
                    }

#if PRIMARY_DOCKED_WINDOW
                uint dockId = ImGui.DockSpaceOverViewport(0, new ImGuiViewportPtr(nint.Zero), ImGuiDockNodeFlags.NoDockingOverCentralNode | ImGuiDockNodeFlags.AutoHideTabBar);

                // Force the next window to be docked.
                ImGui.SetNextWindowDockID(dockId);
                ImGuiWindowFlags flags = ImGuiWindowFlags.NoBringToFrontOnFocus | ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoSavedSettings;
                if (ImGui.Begin("Window", flags))
                {
                    ImGui.Text("Hello World");
                }
#endif
                    mainWindow.Draw();

                    if (mainWindow.IsExitRequested)
                    {
                        break;
                    }

                    if (!NativeInterop.WindowEndNextFrame())
                    {
                        break;
                    }
                }
            }

            NativeInterop.WindowCleanup();

            return 0;
        }

        private static unsafe void LoadFontAndScaleSizes()
        {
            float dpiScale = 1.0f;
            // For now, use the scale of the primary monitor
            ImPtrVector<ImGuiPlatformMonitorPtr> monitors = ImGui.GetPlatformIO().Monitors;
            if (monitors.Size > 0)
            {
                dpiScale = monitors[0].DpiScale;

                ImGui.GetStyle().ScrollbarSize = 18;
                ImGui.GetStyle().ScaleAllSizes(dpiScale);
            }

#if USE_PIXEL_PERFECT_FONT
            // ImGui also embeds a 13 pixel high pixel-perfect font (ProggyClean). It is sharper but on the small side.
            ImGui.GetIO().Fonts.AddFontDefault();
#else
            byte[] ttfFontBytes = GetEmbeddedResourceBytes("DroidSans.ttf");
            // byte[] ttfFontBytes = GetEmbeddedResourceBytes("CascadiaMono.ttf");
            fixed (byte* ttfFontBytesPtr = ttfFontBytes)
            {
                // ImGui Q&A recommends rounding down font size after applying DPI scaling.
                float scaledFontSize = (float)Math.Floor(FontSize * dpiScale);

                ImFontConfigPtr fontCfg = ImGuiNative.ImFontConfig_ImFontConfig();
                fontCfg.FontDataOwnedByAtlas = false; // https://github.com/ocornut/imgui/issues/220
                ImGui.GetIO().Fonts.AddFontFromMemoryTTF((nint)ttfFontBytesPtr, ttfFontBytes.Length, scaledFontSize, fontCfg);
            }
#endif
        }

        private static byte[] GetEmbeddedResourceBytes(string resourceName)
        {
            Assembly assembly = typeof(Program).Assembly;
            using (Stream s = assembly.GetManifestResourceStream(resourceName))
            {
                byte[] ret = new byte[s.Length];
                s.Read(ret, 0, (int)s.Length);
                return ret;
            }
        }
    }
}
