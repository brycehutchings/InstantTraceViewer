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
            Directory.SetCurrentDirectory(AppContext.BaseDirectory);

            if (NativeInterop.WindowInitialize(out nint imguiContext) != 0)
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

                    if (NativeInterop.WindowBeginNextFrame(out int quit, out int occluded) != 0)
                    {
                        break;
                    }

                    if (quit != 0)
                    {
                        break;
                    }

                    if (occluded != 0)
                    {
                        System.Threading.Thread.Sleep(10);
                        continue;
                    }

#if PRIMARY_DOCKED_WINDOW
                uint dockId = ImGui.DockSpaceOverViewport(0, new ImGuiViewportPtr(nint.Zero), ImGuiDockNodeFlags.NoDockingOverCentralNode | ImGuiDockNodeFlags.AutoHideTabBar);

                // Force the next window to be docked.
                ImGui.SetNextWindowDockID(dockId);
                ImGuiWindowFlags flags = ImGuiWindowFlags.NoBringToFrontOnFocus | ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoSavedSettings;
                if (ImGui.Begin("Window", flags))
                {
                    ImGui.TextUnformatted("Hello World");
                }
#endif
                    mainWindow.Draw();

                    if (mainWindow.IsExitRequested)
                    {
                        break;
                    }

                    if (NativeInterop.WindowEndNextFrame() != 0)
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

            // ImGui Q&A recommends rounding down font size after applying DPI scaling.
            float CalcScaledFontSize(float fontSize) => (float)Math.Floor(fontSize * dpiScale);

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
                AddFontFromBytes(CalcScaledFontSize(Settings.FontSize), ttfFontBytes);
            }

            // Load symbol font
            {
                byte[] symbolFont = GetEmbeddedResourceBytes("Font Awesome 6 Free-Solid-900.otf");

                // Symbol font is reduced by 2 pixels, otherwise it has full height which looks awkward.
                AddFontFromBytes(CalcScaledFontSize(Settings.FontSize - 2), symbolFont, true /* merge */, [
                    // Use https://fontawesome.com/v6/search?o=r&m=free to search for icons.
                    0xF00C, // "check"
                    0xF00D, // "xmark"
                    0xF044, // "pen-to-square"
                    0xF06A, // "circle-exclamation"
                    0xF080, // "chart-bar"
                    0xF0B0, // "filter"
                    0xF0C5, // "copy"
                    0xF062, // "arrow-up"
                    0xF063, // "arrow-down"
                    0xF24D, // "clone"
                    0xF2ED, // "trash-can"
                ]);
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

        private static unsafe void AddFontFromBytes(float scaledFontSize, byte[] fontData, bool mergeMode = false, ushort[]? glyphRanges = null)
        {
            // Note this ImVector is leaked but that is OK because ImGui needs the memory kept alive for the lifetime of the font atlas.
            // It's a small amount of memory to leak and only when the user changes font settings.
            ImVector glyphRangesVector = new ImVector();
            if (glyphRanges != null)
            {
                ImFontGlyphRangesBuilderPtr builder = ImGuiNative.ImFontGlyphRangesBuilder_ImFontGlyphRangesBuilder();
                foreach (var glyphRange in glyphRanges)
                {
                    builder.AddChar(glyphRange);
                }
                builder.BuildRanges(out glyphRangesVector);
            }

            ImFontConfigPtr fontCfg = ImGuiNative.ImFontConfig_ImFontConfig();
            fontCfg.MergeMode = mergeMode;
            fontCfg.FontDataOwnedByAtlas = false;
            fontCfg.GlyphRanges = glyphRangesVector.Data;
            fixed (byte* fontDataPtr = fontData)
            {
                ImGui.GetIO().Fonts.AddFontFromMemoryTTF((nint)fontDataPtr, fontData.Length, scaledFontSize, fontCfg);
            }
        }
    }
}
