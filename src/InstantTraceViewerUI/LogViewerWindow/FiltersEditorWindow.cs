using ImGuiNET;
using InstantTraceViewer;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Windows.Forms;

namespace InstantTraceViewerUI
{
    internal class FiltersEditorWindow
    {
        private const string WindowName = "Filters Editor";

        private readonly string _name;

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
        }

        public unsafe bool DrawWindow(IUiCommands uiCommands, ViewerRules rules, TraceTableSchema tableSchema)
        {
            ImGui.SetNextWindowSize(new Vector2(800, 400), ImGuiCond.FirstUseEver);
            ImGui.SetNextWindowSizeConstraints(new Vector2(400, 150), new Vector2(float.MaxValue, float.MaxValue));

            if (ImGui.Begin($"{WindowName} - {_name}###FiltersEditor", ref _open))
            {
                if (_parserTableSchema != tableSchema)
                {
                    _parserTableSchema = tableSchema;
                    _parser = new TraceTableRowSelectorSyntax(tableSchema);
                }

                DrawCurrentRules(rules);
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
            uint addRuleId = ImGui.GetItemID();

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

            RenderParsingError(addRuleId, _addRuleInputText, _addRuleLastParseResult, inputScreenPos);

            // Size table to fit the window but with room for one rows of buttons at the bottom.
            float buttonHeight = ImGui.GetFrameHeightWithSpacing();
            Vector2 tableSize = new Vector2(-1, -buttonHeight);
            if (ImGui.BeginTable("Rules", 4,
                ImGuiTableFlags.ScrollY | ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersOuter |
                ImGuiTableFlags.BordersV | ImGuiTableFlags.Resizable | ImGuiTableFlags.Sortable, tableSize))
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
                    ImGui.BeginDisabled(rule.ParseResult?.Expression == null);
                    bool enabled = rule.Enabled;
                    if (ImGui.Checkbox($"##Enabled", ref enabled))
                    {
                        rules.SetRuleEnabled(i, enabled);
                    }
                    ImGui.EndDisabled();

                    // The rule will be disabled if it has a parsing error so show an icon here to explain why.
                    if (rule.ParseResult != null && rule.ParseResult.Expression == null)
                    {
                        ImGui.SameLine();
                        ImGui.PushStyleColor(ImGuiCol.Text, ImGui.GetColorU32(AppTheme.ErrorColor));
                        ImGui.TextUnformatted("\uF06A");
                        ImGui.PopStyleColor();
                        if (ImGui.IsItemHovered(ImGuiHoveredFlags.DelayNormal))
                        {
                            ImGui.SetTooltip("Parsing error");
                        }
                    }

                    ImGui.TableNextColumn();

                    if (_editingRule == rule)
                    {
                        // Currently editing this rule.

                        if (ImGuiWidgets.UndecoratedButton("\uf00d", "Cancel"))
                        {
                            _editingRule = null;
                            _editRuleInputText = "";
                            _editRuleLastParseResult = null;
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
                            _editRuleLastParseResult = null;
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
                        uint editRuleId = ImGui.GetItemID();

                        if (_editRuleLastParseResult?.Expression != null)
                        {
                            // If enter was pressed, save the rule.
                            if (ImGui.IsKeyPressed(ImGuiKey.Enter))
                            {
                                rules.UpdateRule(i, _editRuleInputText);
                                _editingRule = null;
                            }
                        }

                        RenderParsingError(editRuleId, _editRuleInputText, _editRuleLastParseResult, inputScreenPos);
                    }
                    else
                    {
                        ImGui.TextUnformatted(rule.Query);
                    }

                    ImGui.PopID();
                }

                ImGui.EndTable();

                if (ImGui.Button("Import"))
                {
                    IReadOnlyList<string> files = FileDialog.OpenMultipleFiles("Instant Trace Viewer Filters (*.itvf)|*.itvf",
                        Settings.InstantTraceViewerFiltersLocation,
                        s => Settings.InstantTraceViewerFiltersLocation = s);
                    foreach (string file in files)
                    {
                        try
                        {
                            using TsvTableSource tsv = new TsvTableSource(file, firstRowIsHeader: true, readInBackground: false);
                            ITraceTableSnapshot tsvSnapshot = tsv.CreateSnapshot();
                            TraceSourceSchemaColumn enabledColumn = tsvSnapshot.Schema.Columns.Single(c => c.Name == "Enabled");
                            TraceSourceSchemaColumn actionColumn = tsvSnapshot.Schema.Columns.Single(c => c.Name == "Action");
                            TraceSourceSchemaColumn queryColumn = tsvSnapshot.Schema.Columns.Single(c => c.Name == "Query");
                            for (int i = 0; i < tsvSnapshot.RowCount; i++)
                            {
                                string enabledStr = tsvSnapshot.GetColumnString(i, enabledColumn);
                                string actionStr = tsvSnapshot.GetColumnString(i, actionColumn);
                                string query = tsvSnapshot.GetColumnString(i, queryColumn);
                                if (string.IsNullOrWhiteSpace(enabledStr) && string.IsNullOrWhiteSpace(actionStr) && string.IsNullOrWhiteSpace(query))
                                {
                                    continue; // Ignore empty lines.
                                }

                                rules.AppendRule(bool.Parse(enabledStr), Enum.Parse<TraceRowRuleAction>(actionStr), query);
                            }
                        }
                        catch (Exception ex)
                        {
                            MessageBox.Show($"Failed to open .ITVF file.\n\n{ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                        }
                    }
                }

                ImGui.SameLine();

                if (ImGui.Button("Export"))
                {
                    try
                    {
                        string file = FileDialog.SaveFile("Instant Trace Viewer Filters (*.itvf)|*.itvf",
                            Settings.InstantTraceViewerFiltersLocation,
                            s => Settings.InstantTraceViewerFiltersLocation = s);
                        if (file != null)
                        {
                            using StreamWriter sw = new StreamWriter(file);
                            sw.WriteLine("Enabled\tAction\tQuery");
                            foreach (IRule filter in rules.Rules)
                            {
                                sw.WriteLine($"{filter.Enabled}\t{filter.Action}\t{filter.Query}");
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show($"Failed to save .ITVF file.\n\n{ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                }

                ImGui.SameLine();
                ImGui.TextUnformatted("Tip 1: You can select multiple .itvf files when importing. Tip 2: .itvf files are stored as tab-separated text files");
            }
        }

        private static void RenderParsingError(uint inputTextId, string inputText, TraceTableRowSelectorParseResults parseResults, Vector2 inputScreenPos)
        {
            NativeInterop.CurrentInputTextState inputState = NativeInterop.GetCurrentInputTextState();

            // Don't use the input state to align the error message unless it is in focus so that we have the correct ScrollX.
            bool inputStateUsable = inputState.Id == inputTextId;

            var expectedTokens = parseResults.ExpectedTokens.ToArray();
            var matchingExpectedTokens = expectedTokens.Where(t => t.StartsWith(parseResults.ActualToken.Text)).ToArray();
            var autocompleteOptions = matchingExpectedTokens.Any() ? matchingExpectedTokens : expectedTokens;

            // If parsing was not successful and InputText has the focus, show expected tokens and underline where the error occurred
            if (autocompleteOptions.Any() && parseResults.Expression == null)
            {
                float InputTextPadding = 5; // Might need to be scaled by DPI?

                Vector2 skipSize = ImGui.CalcTextSize(inputText.Substring(0, parseResults.ExpectedTokenStartIndex));

                float expectedXOffset = skipSize.X - (inputStateUsable ? inputState.ScrollX : 0) + InputTextPadding;

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
