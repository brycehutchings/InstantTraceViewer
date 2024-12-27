using ImGuiNET;
using InstantTraceViewer;
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

        private string _addRuleInputText = "";
        private TraceTableRowSelectorParseResults _addRuleLastParseResult;

        IRule _editingRule = null;
        private string _editRuleInputText = "";
        private TraceTableRowSelectorParseResults _editRuleLastParseResult;

        private bool _open = true;

        public FiltersEditorWindow(string name)
        {
            _name = name;
            _windowId = _nextWindowId++;
        }

        public unsafe bool DrawWindow(IUiCommands uiCommands, ViewerRules rules, TraceTableSchema tableSchema)
        {
            ImGui.SetNextWindowSize(new Vector2(800, 400), ImGuiCond.FirstUseEver);
            ImGui.SetNextWindowSizeConstraints(new Vector2(400, 150), new Vector2(float.MaxValue, float.MaxValue));

            if (ImGui.Begin($"{WindowName} - {_name}###FiltersEditor_{_windowId}", ref _open))
            {
                if (_parserTableSchema != tableSchema)
                {
                    _parserTableSchema = tableSchema;
                    _parser = new TraceTableRowSelectorSyntax(tableSchema);
                }

                if (ImGui.BeginTabBar("###FiltersEditorTabs", ImGuiTabBarFlags.None))
                {
                    if (ImGui.BeginTabItem("Current Rules"))
                    {
                        DrawCurrentRules(rules);
                        ImGui.EndTabItem();
                    }
                    if (ImGui.BeginTabItem("Saved Rules"))
                    {
                        ImGui.EndTabItem();
                    }
                    ImGui.EndTabBar();
                }
            }

            ImGui.End();

            return _open;
        }

        private unsafe void DrawCurrentRules(ViewerRules rules)
        {
            Vector2 inputScreenPos = ImGui.GetCursorScreenPos();
            if (ImGui.InputText("##AddRule", ref _addRuleInputText, 1024) || _addRuleLastParseResult == null)
            {
                _addRuleLastParseResult = _parser.Parse(_addRuleInputText);
            }
            NativeInterop.CurrentInputTextState inputState = NativeInterop.GetCurrentInputTextState();

            ImGui.BeginDisabled(_addRuleLastParseResult.Expression == null);
            ImGui.SameLine();
            if (ImGui.Button("Add Include"))
            {
                rules.AddIncludeRule(_addRuleInputText);
            }
            ImGui.SameLine();
            if (ImGui.Button("Add Exclude"))
            {
                rules.AddExcludeRule(_addRuleInputText);
            }
            ImGui.EndDisabled();

            RenderParsingError(_addRuleInputText, _addRuleLastParseResult, inputState, inputScreenPos);

            if (ImGui.BeginTable("Rules", 4,
                ImGuiTableFlags.ScrollY | ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersOuter |
                ImGuiTableFlags.BordersV | ImGuiTableFlags.Resizable | ImGuiTableFlags.Sortable))
            {
                float dpiBase = ImGui.GetFontSize();

                ImGui.TableSetupScrollFreeze(0, 1); // Top row is always visible.
                ImGui.TableSetupColumn("Enabled", ImGuiTableColumnFlags.WidthFixed, dpiBase * 3.5f);
                ImGui.TableSetupColumn("Manage", ImGuiTableColumnFlags.WidthFixed, dpiBase * 5.0f);
                ImGui.TableSetupColumn("Action", ImGuiTableColumnFlags.WidthFixed, dpiBase * 5.0f);
                ImGui.TableSetupColumn("Query", ImGuiTableColumnFlags.WidthStretch, 1);
                ImGui.TableHeadersRow();

                for (int i = 0; i < rules.Rules.Count; i++)
                {
                    ImGui.PushID(i);

                    IRule rule = rules.Rules[i];

                    ImGui.TableNextRow();
                    ImGui.TableNextColumn();
                    bool enabled = rule.Enabled;
                    if (ImGui.Checkbox($"##Enabled", ref enabled))
                    {
                        rules.SetRuleEnabled(i, enabled);
                    }
                    ImGui.TableNextColumn();

                    if (_editingRule == rule)
                    {
                        // Currently editing this rule.

                        if (ImGuiWidgets.UndecoratedButton("\uf00d", "Cancel"))
                        {
                            _editingRule = null;
                        }
                        ImGui.SameLine();

                        ImGui.BeginDisabled(_editRuleLastParseResult?.Expression == null);
                        if (ImGuiWidgets.UndecoratedButton("\uf00c", "Save"))
                        {
                            rules.UpdateRule(i, _editRuleInputText);
                            _editingRule = null;
                        }
                        ImGui.EndDisabled();
                    }
                    else
                    {
                        if (ImGuiWidgets.UndecoratedButton("\uf044", "Edit"))
                        {
                            _editingRule = rule;
                            _editRuleInputText = rule.Query;
                        }

                        ImGui.SameLine();
                        if (i > 0 && ImGuiWidgets.UndecoratedButton("\uf062", "Move up"))
                        {
                            rules.MoveRule(i, i - 1);
                        }
                        ImGui.SameLine();
                        if (i < rules.Rules.Count - 1 && ImGuiWidgets.UndecoratedButton("\uf063", "Move down"))
                        {
                            rules.MoveRule(i, i + 1);
                        }
                        ImGui.SameLine();
                        if (ImGuiWidgets.UndecoratedButton("\uf2ed", "Delete rule"))
                        {
                            rules.RemoveRule(i);
                        }
                    }

                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(rule.Action.ToString());
                    ImGui.TableNextColumn();

                    if (_editingRule == rule)
                    {
                        inputScreenPos = ImGui.GetCursorScreenPos();
                        if (ImGui.InputText("##EditRule", ref _editRuleInputText, 1024) || _editRuleLastParseResult == null)
                        {
                            _editRuleLastParseResult = _parser.Parse(_editRuleInputText);
                        }
                        inputState = NativeInterop.GetCurrentInputTextState();

                        if (_editRuleLastParseResult?.Expression != null)
                        {
                            // If enter was pressed, save the rule.
                            if (ImGui.IsKeyPressed(ImGuiKey.Enter))
                            {
                                rules.UpdateRule(i, _editRuleInputText);
                                _editingRule = null;
                            }
                        }

                        RenderParsingError(_editRuleInputText, _editRuleLastParseResult, inputState, inputScreenPos);
                    }
                    else
                    {
                        ImGui.TextUnformatted(rule.Query);
                    }

                    ImGui.PopID();
                }

                ImGui.EndTable();
            }
        }

        private static void RenderParsingError(string inputText, TraceTableRowSelectorParseResults parseResults, NativeInterop.CurrentInputTextState inputState, Vector2 inputScreenPos)
        {
            var expectedTokens = parseResults.ExpectedTokens.ToArray();
            var matchingExpectedTokens = expectedTokens.Where(t => t.StartsWith(parseResults.ActualToken.Text)).ToArray();
            var autocompleteOptions = matchingExpectedTokens.Any() ? matchingExpectedTokens : expectedTokens;

            // If parsing was not successful, show expected tokens and underline where the error occurred
            if (autocompleteOptions.Any() && parseResults.Expression == null)
            {
                float InputTextPadding = 5; // Might need to be scaled by DPI?

                Vector2 skipSize = ImGui.CalcTextSize(inputText.Substring(0, parseResults.ExpectedTokenStartIndex));
                float expectedXOffset = skipSize.X - inputState.ScrollX + InputTextPadding;

                // Underline the bad token
                ImGui.SameLine();
                if (!matchingExpectedTokens.Any())
                {
                    ImDrawListPtr drawList = ImGui.GetWindowDrawList();

                    // Measure pixel length from start of text to start of parsing error.
                    Vector2 underlineSize = ImGui.CalcTextSize(parseResults.ActualToken.Text);
                    drawList.AddLine(
                        inputScreenPos + new Vector2(expectedXOffset, ImGui.GetTextLineHeightWithSpacing()),
                        inputScreenPos + new Vector2(expectedXOffset + underlineSize.X, ImGui.GetTextLineHeightWithSpacing()),
                        ImGui.GetColorU32(AppTheme.ErrorColor));
                }

                // Show expected tokens pointing at right spot.
                {
                    ImGui.NewLine();
                    Vector2 size = ImGui.CalcTextSize(inputText.Substring(0, parseResults.ExpectedTokenStartIndex));
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
        }
    }
}
