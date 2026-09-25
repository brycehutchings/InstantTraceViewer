using Hexa.NET.ImGui;
using System;
using System.Numerics;

namespace InstantTraceViewerUI.Symbols
{
    internal class SymbolPathsWindow
    {
        public const string WindowName = "Symbol Paths";

        private const int MaxSymbolPathBufferSize = 32768;

        private string _draftPath = string.Empty;
        private bool _draftIncludeEnvironment;

        public void DrawWindow(ref bool isOpen)
        {
            ImGui.SetNextWindowSize(new Vector2(800, 500), ImGuiCond.FirstUseEver);

            if (ImGui.Begin(WindowName, ref isOpen))
            {
                if (ImGui.IsWindowAppearing())
                {
                    _draftPath = ToMultiline(Settings.SymbolPath);
                    _draftIncludeEnvironment = Settings.SymbolPathIncludeEnvironment;
                }

                ImGui.TextUnformatted("Symbol paths (one entry per line). Use srv*<cache>*<server> for symbol servers.");
                float lineHeight = ImGui.GetTextLineHeightWithSpacing();
                ImGui.InputTextMultiline("##SymbolPath", ref _draftPath, MaxSymbolPathBufferSize, new Vector2(-1, lineHeight * 8));

                ImGui.Checkbox("Append _NT_SYMBOL_PATH and _NT_ALT_SYMBOL_PATH", ref _draftIncludeEnvironment);
                ImGui.Indent();
                DrawEnvironmentVariable("_NT_SYMBOL_PATH");
                DrawEnvironmentVariable("_NT_ALT_SYMBOL_PATH");
                ImGui.Unindent();

                ImGui.Separator();
                ImGui.TextUnformatted("Effective search path:");
                string effectivePath = ToMultiline(Settings.GetEffectiveSymbolPath(_draftPath, _draftIncludeEnvironment));
                ImGui.InputTextMultiline("##EffectiveSymbolPath", ref effectivePath, ImGuiWidgets.GetInputTextBufferSize(effectivePath, 1),
                    new Vector2(-1, -ImGui.GetFrameHeightWithSpacing()), ImGuiInputTextFlags.ReadOnly);

                string normalizedDraftPath = string.Join(";", Settings.SplitSymbolPath(_draftPath));
                bool isDirty = normalizedDraftPath != Settings.SymbolPath || _draftIncludeEnvironment != Settings.SymbolPathIncludeEnvironment;

                ImGui.BeginDisabled(!isDirty);
                if (ImGui.Button("Apply"))
                {
                    Settings.SymbolPath = normalizedDraftPath;
                    Settings.SymbolPathIncludeEnvironment = _draftIncludeEnvironment;
                    SymbolResolver.Instance.SetSearchPath(Settings.EffectiveSymbolPath);
                }
                ImGui.EndDisabled();

                ImGui.SameLine();
                if (ImGui.Button("Reset to default"))
                {
                    _draftPath = string.Empty;
                    _draftIncludeEnvironment = true;
                }
            }

            ImGui.End();
        }

        private static void DrawEnvironmentVariable(string name)
        {
            string? value = Environment.GetEnvironmentVariable(name);
            ImGui.TextUnformatted($"{name}: {(string.IsNullOrEmpty(value) ? "(not set)" : value)}");
        }

        private static string ToMultiline(string symbolPath) => string.Join("\n", Settings.SplitSymbolPath(symbolPath));
    }
}
