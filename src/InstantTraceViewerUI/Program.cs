using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using ImGuiNET;

namespace InstantTraceViewerUI
{
    internal class Program
    {
        [STAThread] // For WinForms file browser usage :-\
        public static int Main(string[] args)
        {
            if (!NativeInterop.WindowInitialize(out nint imguiContext))
            {
                return 1;
            }

            ImGui.SetCurrentContext(imguiContext);

            FontType? lastSetFont = null;
            int? lastSetFontSize = null;
            ImGuiTheme? lastThemeSet = null;

            using (MainWindow mainWindow = new(args))
            {
                while (true)
                {
                    // Font can only change outside of Begin/End frame.
                    if (lastSetFont != Settings.Font || lastSetFontSize != Settings.FontSize)
                    {
                        LoadFontAndScaleSizes();
                        lastSetFont = Settings.Font;
                        lastSetFontSize = Settings.FontSize;
                    }

                    if (lastThemeSet != Settings.Theme)
                    {
                        if (Settings.Theme == ImGuiTheme.Dark)
                        {
                            ImGui.StyleColorsDark();
                        }
                        else
                        {
                            ImGui.StyleColorsLight();
                        }
                        AppTheme.UpdateTheme();
                        lastThemeSet = Settings.Theme;
                    }

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
            Debug.WriteLine("Loading and building font atlas...");

            float dpiScale = 1.0f;
            // For now, use the scale of the primary monitor
            ImPtrVector<ImGuiPlatformMonitorPtr> monitors = ImGui.GetPlatformIO().Monitors;
            if (monitors.Size > 0)
            {
                dpiScale = monitors[0].DpiScale;

                ImGui.GetStyle().ScrollbarSize = 18;
                ImGui.GetStyle().ScaleAllSizes(dpiScale);
            }

            bool needsRebuild = ImGui.GetIO().Fonts.TexID != nint.Zero;
            ImGui.GetIO().Fonts.Clear();

            FontType font = Settings.Font;
            if (font == FontType.ProggyClean)
            {
                ImGui.GetIO().Fonts.AddFontDefault();
            }
            else
            {
                string systemFontPath = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
                string segoeUiPath = Path.Combine(systemFontPath, "segoeui.ttf");
                string segoeUiVariablePath = Path.Combine(systemFontPath, "SegUIVar.ttf"); // Windows 11 font with better legibility.

                byte[] ttfFontBytes =
                    font == FontType.SegoeUI && File.Exists(segoeUiVariablePath) ? File.ReadAllBytes(segoeUiVariablePath) :
                    font == FontType.SegoeUI && File.Exists(segoeUiPath) ? File.ReadAllBytes(segoeUiPath) : // Fallback to old segoe ui font if the new one is not available.
                    font == FontType.CascadiaMono ? GetEmbeddedResourceBytes("CascadiaMono.ttf") :
                    GetEmbeddedResourceBytes("DroidSans.ttf");
                fixed (byte* ttfFontBytesPtr = ttfFontBytes)
                {
                    // ImGui Q&A recommends rounding down font size after applying DPI scaling.
                    float scaledFontSize = (float)Math.Floor(Settings.FontSize * dpiScale);

                    ImFontConfigPtr fontCfg = ImGuiNative.ImFontConfig_ImFontConfig();
                    fontCfg.FontDataOwnedByAtlas = false; // https://github.com/ocornut/imgui/issues/220
                    ImGui.GetIO().Fonts.AddFontFromMemoryTTF((nint)ttfFontBytesPtr, ttfFontBytes.Length, scaledFontSize, fontCfg);
                }
            }

            if (needsRebuild)
            {
                ImGui.GetIO().Fonts.Build();
                NativeInterop.RebuildFontAtlas(); // Reupload the font texture to the GPU
            }
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
