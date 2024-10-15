using System;
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

            LoadFont();

            // Increase scrollbar size to make it easier to use.
            ImGui.PushStyleVar(ImGuiStyleVar.ScrollbarSize, 18.0f);

            using var mainWindow = new MainWindow(args);

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

            ImGui.PopStyleVar(); // ScrollbarSize

            NativeInterop.WindowCleanup();

            return 0;
        }

        private static unsafe void LoadFont()
        {
#if USE_PIXEL_PERFECT_FONT
            // ImGui also embeds a 13 pixel high pixel-perfect font (ProggyClean). It is sharper but on the small side.
            ImGui.GetIO().Fonts.AddFontDefault();
#else
            byte[] ttfFontBytes = GetEmbeddedResourceBytes("DroidSans.ttf");
            // byte[] ttfFontBytes = GetEmbeddedResourceBytes("CascadiaMono.ttf");
            fixed (byte* ttfFontBytesPtr = ttfFontBytes)
            {
                ImGui.GetIO().Fonts.AddFontFromMemoryTTF((nint)ttfFontBytesPtr, ttfFontBytes.Length, FontSize);
            }
#endif
        }

        private static  byte[] GetEmbeddedResourceBytes(string resourceName)
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
