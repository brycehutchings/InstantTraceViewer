using ImGuiNET;
using InstantTraceViewer;
using System;
using System.Diagnostics;
using System.Linq;
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


        private string _editFilter = "";
        private TraceTableRowSelectorParseResults _lastParseResult;
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

                Vector2 inputScreenPos = ImGui.GetCursorScreenPos();
                if (ImGui.InputText("Filter", ref _editFilter, 1024) || _lastParseResult == null)
                {
                    Trace.WriteLine($"Filter changed: {_editFilter}");
                    _lastParseResult = _parser.Parse(_editFilter);
                }

                var expectedTokens = _lastParseResult.ExpectedTokens.ToArray();
                var matchingExpectedTokens = expectedTokens.Where(t => t.StartsWith(_lastParseResult.ActualToken.Text)).ToArray();
                var autocompleteOptions = matchingExpectedTokens.Any() ? matchingExpectedTokens : expectedTokens;

                NativeInterop.CurrentInputTextState inputState = NativeInterop.GetCurrentInputTextState();
                // If parsing was not successful, show expected tokens and underline where the error occurred
                if (autocompleteOptions.Any() && _lastParseResult.Expression == null)
                {
                    float InputTextPadding = 5; // Might need to be scaled by DPI?

                    Vector2 skipSize = ImGui.CalcTextSize(_editFilter.Substring(0, _lastParseResult.ExpectedTokenStartIndex));
                    float expectedXOffset = skipSize.X - inputState.ScrollX + InputTextPadding;

                    // Underline the bad token
                    {
                        ImGui.SameLine();
                        ImDrawListPtr drawList = ImGui.GetWindowDrawList();

                        // Measure pixel length from start of text to start of parsing error.
                        Vector2 underlineSize = ImGui.CalcTextSize(_lastParseResult.ActualToken.Text);
                        drawList.AddLine(
                            inputScreenPos + new Vector2(expectedXOffset, ImGui.GetTextLineHeightWithSpacing()),
                            inputScreenPos + new Vector2(expectedXOffset + underlineSize.X, ImGui.GetTextLineHeightWithSpacing()), ImGui.GetColorU32(AppTheme.ErrorColor));
                    }

                    // Show expected tokens pointing at right spot.
                    {
                        ImGui.NewLine();
                        Vector2 size = ImGui.CalcTextSize(_editFilter.Substring(0, _lastParseResult.ExpectedTokenStartIndex));
                        if (expectedXOffset < ImGui.CalcItemWidth())
                        {
                            var savedPos = ImGui.GetCursorPos();
                            ImGui.SetCursorPos(savedPos + new Vector2(expectedXOffset - 3, -4));
                            ImGui.PushStyleColor(ImGuiCol.Text, ImGui.GetColorU32(AppTheme.ErrorColor));
                            ImGui.TextUnformatted($"^ Expected: {string.Join(", ", autocompleteOptions)}");
                            ImGui.PopStyleColor();
                            ImGui.SetCursorPos(savedPos);
                        }
                    }
                }
                ImGui.NewLine(); // Move the cursor down a line since this space is needed for the "expected" tokens.

                if (ImGui.BeginTable("Rules", 3,
                    ImGuiTableFlags.ScrollY | ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersOuter |
                    ImGuiTableFlags.BordersV | ImGuiTableFlags.Resizable | ImGuiTableFlags.Sortable))
                {
                    float dpiBase = ImGui.GetFontSize();

                    ImGui.TableSetupScrollFreeze(0, 1); // Top row is always visible.
                    ImGui.TableSetupColumn("Enabled", ImGuiTableColumnFlags.WidthFixed, dpiBase * 3.5f);
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
