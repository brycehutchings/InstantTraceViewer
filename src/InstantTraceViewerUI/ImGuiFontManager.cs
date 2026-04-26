using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using Hexa.NET.ImGui;

namespace InstantTraceViewerUI
{
    internal static unsafe class ImGuiFontManager
    {
        // Hexa.NET.ImGui's bundled cimgui.dll is built with IMGUI_ENABLE_FREETYPE, but the
        // managed binding doesn't expose the loader factory. Import it directly.
        [DllImport("cimgui", CallingConvention = CallingConvention.Cdecl, EntryPoint = "ImGuiFreeType_GetFontLoader")]
        private static extern ImFontLoader* ImGuiFreeType_GetFontLoader();

        private const float ReferenceFontSize = 17;
        private const float ProggyCleanFontSize = 13;

        private static readonly List<GCHandle> s_pinnedFontData = [];

        public static void ApplyFontSize()
        {
            float fontSize = Settings.Font == FontType.ProggyClean ? ProggyCleanFontSize : Settings.FontSize;
            float scaledFontSize = CalcScaledFontSize(fontSize);
            ImGuiStyle* style = ImGui.GetStyle().Handle;
            style->FontSizeBase = scaledFontSize;
            style->NextFrameFontSizeBase = scaledFontSize;
        }

        public static void LoadFontSources()
        {
            Debug.WriteLine("Loading font sources...");

            FreePinnedFontData();
            ImGui.GetIO().Fonts.Clear();

            ImFontAtlasPtr atlas = ImGui.GetIO().Fonts;
            atlas.SetFontLoader(ImGuiFreeType_GetFontLoader());

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
                AddFontFromBytes(ReferenceFontSize, ttfFontBytes);
            }

            byte[] symbolFont = GetEmbeddedResourceBytes("Font Awesome 6 Free-Solid-900.otf");
            AddFontFromBytes(ReferenceFontSize - 2, symbolFont, true /* merge */, [
                // Use https://fontawesome.com/v6/search?ic=free to search for icons.
                0xE4BF, // "arrows-to-eye"
                0xE68F, // "thumbtack-slash"
                0xF002, // "magnifying-glass"
                0xF00C, // "check"
                0xF00D, // "xmark"
                0xF00E, // "magnifying-glass-plus"
                0xF010, // "magnifying-glass-minus"
                0xF044, // "pen-to-square"
                0xF04B, // "play"
                0xF04C, // "pause"
                0xF059, // "circle-question"
                0xF05B, // "crosshairs"
                0xF060, // "arrow-left"
                0xF061, // "arrow-right"
                0xF06A, // "circle-exclamation"
                0xF080, // "chart-bar"
                0xF08D, // "thumbtack"
                0xF0B0, // "filter"
                0xF0C5, // "copy"
                0xF0CE, // "table"
                0xF062, // "arrow-up"
                0xF063, // "arrow-down"
                0xF0FE, // "square-plus"
                0xF12D, // "eraser"
                0xF2ED, // "trash-can"
                0xF31E, // "maximize"
                0xF53F, // "palette"
                0xF78C, // "minimize"
            ]);
        }

        public static void FreePinnedFontData()
        {
            foreach (GCHandle handle in s_pinnedFontData)
            {
                handle.Free();
            }

            s_pinnedFontData.Clear();
        }

        private static float CalcScaledFontSize(float fontSize)
        {
            // ImGui Q&A recommends rounding down font size after applying DPI scaling.
            return (float)Math.Floor(fontSize * Win32ImGuiHost.GetDpiScale());
        }

        private static byte[] GetEmbeddedResourceBytes(string resourceName)
        {
            Assembly assembly = typeof(ImGuiFontManager).Assembly;
            using (Stream s = assembly.GetManifestResourceStream(resourceName))
            {
                byte[] ret = new byte[s.Length];
                int readLength = s.Read(ret, 0, (int)s.Length);
                Debug.Assert(readLength == s.Length, "Failed to read the entire embedded resource stream.");
                return ret;
            }
        }

        private static void AddFontFromBytes(float scaledFontSize, byte[] fontData, bool mergeMode = false, ushort[]? glyphRanges = null)
        {
            // Note this ImVector is leaked but that is OK because ImGui needs the memory kept alive for the lifetime of the font atlas.
            // It's a small amount of memory to leak and only when the user changes font settings.
            ImVector<uint> glyphRangesVector = default;
            if (glyphRanges != null)
            {
                ImFontGlyphRangesBuilderPtr builder = ImGui.ImFontGlyphRangesBuilder();
                foreach (var glyphRange in glyphRanges)
                {
                    builder.AddChar(glyphRange);
                }
                builder.BuildRanges(ref glyphRangesVector);
            }

            ImFontConfigPtr fontCfg = ImGui.ImFontConfig();
            fontCfg.MergeMode = mergeMode;
            fontCfg.FontDataOwnedByAtlas = false;
            fontCfg.GlyphRanges = (uint*)glyphRangesVector.Data;
            GCHandle fontDataHandle = GCHandle.Alloc(fontData, GCHandleType.Pinned);
            s_pinnedFontData.Add(fontDataHandle);
            ImGui.GetIO().Fonts.AddFontFromMemoryTTF((byte*)fontDataHandle.AddrOfPinnedObject(), fontData.Length, scaledFontSize, fontCfg);
        }
    }
}
