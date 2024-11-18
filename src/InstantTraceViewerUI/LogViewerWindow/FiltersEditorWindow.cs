using ImGuiNET;
using InstantTraceViewer;
using System;
using System.Diagnostics;
using System.Numerics;

namespace InstantTraceViewerUI
{
    internal class FiltersEditorWindow
    {
        private const string WindowName = "Filters Editor";
        private static int _nextWindowId = 1;

        private readonly string _name;
        private readonly int _windowId;

        private TraceTableRowSelectorSyntax? _parser;
        private TraceTableSchema? _parserTableSchema;

        private bool _open = true;

        public FiltersEditorWindow(string name)
        {
            _name = name;
            _windowId = _nextWindowId++;
        }


        unsafe int TextInputCallback(ImGuiInputTextCallbackData* data)
        {
            Trace.WriteLine("Callback");

            if (data->EventChar == '\t')
            {
                Trace.WriteLine("Tab pressed");
            }

            return 0;
        }

        private string _editFilter = "";
        public unsafe bool DrawWindow(IUiCommands uiCommands, ViewerRules rules, TraceTableSchema tableSchema)
        {
            ImGui.SetNextWindowSize(new Vector2(800, 400), ImGuiCond.FirstUseEver);
            ImGui.SetNextWindowSizeConstraints(new Vector2(400, 150), new Vector2(float.MaxValue, float.MaxValue));

            if (ImGui.Begin($"{WindowName} - {_name}###Timeline_{_windowId}", ref _open))
            {
                if (_parserTableSchema != tableSchema)
                {
                    _parserTableSchema = tableSchema;
                    _parser = new TraceTableRowSelectorSyntax(tableSchema);
                }

                if (ImGui.InputText("Filter", ref _editFilter, 1024, ImGuiInputTextFlags.CallbackAlways | ImGuiInputTextFlags.CallbackCompletion, TextInputCallback))
                {
                    Trace.WriteLine($"Filter changed: {_editFilter}");
                }

                try
                {
                    var result = _parser.Parse(_editFilter);
                }
                catch (Exception ex)
                {
                    ImGui.TextUnformatted($"Parse error: {ex.Message}");
                }

                /*
                foreach (var a in result.Expectations)
                {
                    ImGui.TextUnformatted(a);
                }
                */

                if (ImGui.BeginTable("Rules", 3,
                    ImGuiTableFlags.ScrollY | ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersOuter |
                    ImGuiTableFlags.BordersV | ImGuiTableFlags.Resizable | ImGuiTableFlags.Sortable))
                {
                    float dpiBase = ImGui.GetFontSize();

                    ImGui.TableSetupScrollFreeze(0, 1); // Top row is always visible.
                    ImGui.TableSetupColumn("Enabled", ImGuiTableColumnFlags.WidthFixed, dpiBase * 3.0f);
                    ImGui.TableSetupColumn("Action", ImGuiTableColumnFlags.WidthFixed, dpiBase * 5.0f);
                    ImGui.TableSetupColumn("Query", ImGuiTableColumnFlags.WidthStretch, 1);
                    ImGui.TableHeadersRow();

                    foreach (var rule in rules.Rules)
                    {
                        ImGui.TableNextRow();
                        ImGui.TableNextColumn();
                        bool enabled = rule.Enabled;
                        if (ImGui.Checkbox($"##Enabled", ref enabled))
                        {
                            rule.Enabled = enabled;
                        }
                        ImGui.TableNextColumn();
                        ImGui.TextUnformatted(rule.Action.ToString());
                        ImGui.TableNextColumn();
                        ImGui.TextUnformatted(rule.Query);
                    }

                    ImGui.EndTable();
                }
            }

            ImGui.End();

            return _open;
        }
    }
}
