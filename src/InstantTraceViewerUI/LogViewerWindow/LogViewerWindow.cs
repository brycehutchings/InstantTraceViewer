using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using System.Text;
using ImGuiNET;
using InstantTraceViewer;

namespace InstantTraceViewerUI
{
    unsafe internal class LogViewerWindow : IDisposable
    {
        private static readonly MinUniqueId MinUniqueIdPool = new();

        private readonly ImGuiListClipperPtr _tableClipper = new ImGuiListClipperPtr(ImGuiNative.ImGuiListClipper_ImGuiListClipper());
        private readonly SharedTraceSource _traceSource;
        private readonly int _windowId;
        private readonly string _windowIdString;

        private ViewerRules _viewerRules = new();
        private FilteredTraceTableBuilder _filteredTraceTableBuilder = new();

        // Selected rows are stored as row indices into the full table so selections persist correctly as filtering changes.
        private HashSet<int> _selectedFullTableRowIndices = new HashSet<int>();

        // The last row that was selected by the user. This is used to start searching from the next row.
        private int? _lastSelectedVisibleRowIndex;
        private int? _lastSelectedFullTableRowIndex;
        private float? _lastSelectedRowYOffset; // If this is null then the last selected row is not currently rendered.

        // The row index into the full table of the top-most row that that is rendered.
        // This is used to maintain scroll position when the view is rebuilt and so the full table index is used to find the closest row after filtering changes.
        private int? _topmostRenderedFullTableRowIndex;

        // The topmost/bottommost row index that is rendered. This is used show the current viewed span of rows in the timeline window.
        private int? _topmostRenderedVisibleRowIndex;
        private int? _bottommostRenderedVisibleRowIndex;

        private int? _hoveredProcessId;
        private int? _hoveredThreadId;

        private ThreadTimelineWindow _threadTimelineWindow = null;
        private FiltersEditorWindow _filtersEditorWindow = null;
        private SpamFilterWindow _spamFilterWindow = null;

        private int? _cellContentPopupFullTableRowIndex = null;
        private TraceSourceSchemaColumn? _cellContentPopupColumn = null;

        private string _findBuffer = string.Empty;
        private bool _findForward = true;
        private bool _isDisposed;

        public LogViewerWindow(SharedTraceSource traceSource)
        {
            _windowId = MinUniqueIdPool.TakeId();
            // In addition to using the window id for uniqueness, we also use the trace source type (e.g. ETW, Logcat, Csv) so
            // that persisted column settings from one source don't affect another.
            _windowIdString = $"LogViewerWindow_{traceSource.TraceSource.GetType().Name}_{_windowId}";

            _traceSource = traceSource;
            _traceSource.AddRef(this);
        }

        public LogViewerWindow(ITraceSource traceSource)
            : this(new SharedTraceSource(traceSource))
        {
        }

        public bool IsClosed => _isDisposed;

        public void DrawWindow(IUiCommands uiCommands)
        {
            if (IsClosed)
            {
                return;
            }

            bool filteredViewRebuilt = _filteredTraceTableBuilder.Update(_viewerRules, _traceSource.TraceSource.CreateSnapshot());
            FilteredTraceTableSnapshot visibleTraceTable = _filteredTraceTableBuilder.Snapshot();

            ImGui.SetNextWindowSize(new Vector2(1000, 500), ImGuiCond.FirstUseEver);
            ImGui.SetNextWindowSizeConstraints(new Vector2(200, 200), new Vector2(float.MaxValue, float.MaxValue));

            bool opened = true;

            // The trace source type is used for the window name imgui id/hash so that changes to size and reorder of columns is remembered.
            if (ImGui.Begin($"{_traceSource.TraceSource.DisplayName}###{_windowIdString}", ref opened))
            {
                DrawWindowContents(uiCommands, visibleTraceTable, filteredViewRebuilt);
            }

            ImGui.End();

            if (_filtersEditorWindow != null)
            {
                if (!_filtersEditorWindow.DrawWindow(uiCommands, _viewerRules, visibleTraceTable.Schema))
                {
                    _filtersEditorWindow = null;
                }
            }

            if (_spamFilterWindow != null)
            {
                if (!_spamFilterWindow.DrawWindow(uiCommands, _viewerRules, visibleTraceTable))
                {
                    _spamFilterWindow = null;
                }
            }

            if (_threadTimelineWindow != null)
            {
                // The topmost/bottommost row index may not reflect a filtering or clear change, so it may be out of bounds for one frame, so we have to do a bounds check too.
                DateTime? startWindow = _topmostRenderedVisibleRowIndex.HasValue && _topmostRenderedVisibleRowIndex < visibleTraceTable.RowCount ?
                    visibleTraceTable.GetTimestamp(_topmostRenderedVisibleRowIndex.Value) : null;
                DateTime? endWindow = _bottommostRenderedVisibleRowIndex.HasValue && _bottommostRenderedVisibleRowIndex < visibleTraceTable.RowCount ?
                    visibleTraceTable.GetTimestamp(_bottommostRenderedVisibleRowIndex.Value) : null;
                if (!_threadTimelineWindow.DrawWindow(uiCommands, visibleTraceTable, _lastSelectedVisibleRowIndex, _viewerRules, startWindow, endWindow))
                {
                    _threadTimelineWindow = null;
                }
            }

            if (!opened)
            {
                Dispose();
            }
        }

        private unsafe void DrawWindowContents(IUiCommands uiCommands, FilteredTraceTableSnapshot visibleTraceTable, bool filteredViewRebuilt)
        {
            if (_lastSelectedVisibleRowIndex >= visibleTraceTable.RowCount)
            {
                // The last selected row is no longer visible, so clear it to avoid later code potentially going out of bounds.
                _lastSelectedVisibleRowIndex = null;
                _lastSelectedFullTableRowIndex = null;
            }

            // Row index to scroll to (it will be the topmost row that is visible).
            int? setScrollIndex = null;

            DrawToolStrip(uiCommands, visibleTraceTable, ref setScrollIndex);

            if (setScrollIndex == null && _threadTimelineWindow?.ClickedVisibleRowIndex != null)
            {
                // If the user clicked on a row in the timeline window, then scroll to that row.
                _lastSelectedVisibleRowIndex = _threadTimelineWindow.ClickedVisibleRowIndex;
                _selectedFullTableRowIndices.Clear();
                _selectedFullTableRowIndices.Add(visibleTraceTable.GetFullTableRowIndex(_lastSelectedVisibleRowIndex.Value));

                setScrollIndex = _lastSelectedVisibleRowIndex;
            }

            // TODO: For inline thread timeline
            /*
            if (_timelineInline != null)
            {
                // The topmost/bottommost row index may not reflect a filtering or clear change, so it may be out of bounds for one frame, so we have to do a bounds check too.
                DateTime? startWindow = _topmostRenderedVisibleRowIndex.HasValue && _topmostRenderedVisibleRowIndex < visibleTraceTable.RowCount ?
                    visibleTraceTable.GetTimestamp(_topmostRenderedVisibleRowIndex.Value) : null;
                DateTime? endWindow = _bottommostRenderedVisibleRowIndex.HasValue && _bottommostRenderedVisibleRowIndex < visibleTraceTable.RowCount ?
                    visibleTraceTable.GetTimestamp(_bottommostRenderedVisibleRowIndex.Value) : null;
                _timelineInline.DrawTimelineGraph(visibleTraceTable, startWindow, endWindow);
            }
            */

            // TODO: ImGui.GetTextLineHeightWithSpacing() is the correct number, but is it technically the right thing to rely on?
            Vector2 remainingRegion = ImGui.GetContentRegionAvail();

            if (ImGui.BeginTable("TraceTable", visibleTraceTable.Schema.Columns.Count,
                ImGuiTableFlags.ScrollX | ImGuiTableFlags.ScrollY |
                ImGuiTableFlags.RowBg |
                ImGuiTableFlags.BordersOuter | ImGuiTableFlags.BordersV |
                ImGuiTableFlags.Resizable | ImGuiTableFlags.Reorderable | ImGuiTableFlags.Hideable))
            {
                float dpiBase = ImGui.GetFontSize();

                ImGui.TableSetupScrollFreeze(0, 1); // Top row is always visible.
                foreach (var column in visibleTraceTable.Schema.Columns)
                {
                    if (column.DefaultColumnSize.HasValue)
                    {
                        ImGui.TableSetupColumn(column.Name, ImGuiTableColumnFlags.WidthFixed, column.DefaultColumnSize.Value * dpiBase);
                    }
                    else
                    {
                        ImGui.TableSetupColumn(column.Name, ImGuiTableColumnFlags.WidthStretch, 1);
                    }
                }
                ImGui.TableHeadersRow();

                ImGui.PushStyleVar(ImGuiStyleVar.CellPadding, new Vector2(2, 2)); // Tighten spacing

                uint? lastColor = null;
                void SetColor(uint? color)
                {
                    if (color != lastColor)
                    {
                        if (lastColor.HasValue)
                        {
                            ImGui.PopStyleColor(); // ImGuiCol_Text changed.
                        }
                        if (color.HasValue)
                        {
                            ImGui.PushStyleColor(ImGuiCol.Text, color.Value);
                        }
                        lastColor = color;
                    }
                }

                // Maintain scroll position when the view was rebuilt.
                if (filteredViewRebuilt)
                {
                    void ScrollToRow(int topmostVisibleFullTableRowIndex, float yOffset)
                    {
                        Debug.WriteLine("Trying to maintain scroll position...");
                        for (int i = 0; i < visibleTraceTable.RowCount; i++)
                        {
                            // Find the first row that we want to maintain and scroll to it.
                            // TODO: This could be a binary search.
                            if (visibleTraceTable.GetFullTableRowIndex(i) >= topmostVisibleFullTableRowIndex)
                            {
                                Debug.WriteLine($"Scrolling to index {i} / {visibleTraceTable.RowCount}");
                                ImGui.SetScrollY(i * _tableClipper.ItemsHeight + yOffset);
                                break;
                            }
                        }
                    }

                    // Try to maintain selected row if it is still visible, otherwise maintain scroll of topmost-visible row.
                    if (_lastSelectedRowYOffset.HasValue && _lastSelectedFullTableRowIndex.HasValue)
                    {
                        ScrollToRow(_lastSelectedFullTableRowIndex.Value, -_lastSelectedRowYOffset.Value);
                    }
                    else if (_topmostRenderedFullTableRowIndex.HasValue)
                    {
                        // If we're trying to precisely maintain the content after the count has changed, we need to account for the partial row scroll.
                        float partialRowScroll = (int)ImGui.GetScrollY() % (int)_tableClipper.ItemsHeight;
                        ScrollToRow(_topmostRenderedFullTableRowIndex.Value, partialRowScroll);
                    }
                }

                ImGuiMultiSelectIOPtr multiselectIO = ImGui.BeginMultiSelect(ImGuiMultiSelectFlags.ClearOnEscape | ImGuiMultiSelectFlags.BoxSelect2d);
                ApplyMultiSelectRequests(visibleTraceTable, multiselectIO);

                _topmostRenderedFullTableRowIndex = null;
                _topmostRenderedVisibleRowIndex = null;
                _bottommostRenderedVisibleRowIndex = null;
                _lastSelectedRowYOffset = null;

                int? newHoveredProcessId = null, newHoveredThreadId = null;

                _tableClipper.Begin(visibleTraceTable.RowCount);

                // The table clipper seems to start the first row with a Y offset as if it is the second row (possibly so the first row can scroll partially above the header without going negative).
                // So we need to substract this out of later calls to GetCursorPosY to get a relative position for maintaining scroll to a specific row.
                float topVisibleRowYOffset = ImGui.GetCursorPosY();

                while (_tableClipper.Step())
                {
                    for (int i = _tableClipper.DisplayStart; i < _tableClipper.DisplayEnd; i++)
                    {
                        int fullTableRowIndex = visibleTraceTable.GetFullTableRowIndex(i);

                        ImGui.PushID(fullTableRowIndex);

                        ImGui.TableNextRow();

                        float rowYOffset = ImGui.GetCursorPosY();

                        if (visibleTraceTable.Schema.ProcessIdColumn != null && visibleTraceTable.GetProcessId(i) == _hoveredProcessId)
                        {
                            ImGui.TableSetBgColor(ImGuiTableBgTarget.CellBg, AppTheme.MatchingRowBgColor, 0);
                        }
                        if (visibleTraceTable.Schema.ThreadIdColumn != null && visibleTraceTable.GetThreadId(i) == _hoveredThreadId)
                        {
                            ImGui.TableSetBgColor(ImGuiTableBgTarget.CellBg, AppTheme.MatchingRowBgColor, 1);
                        }

                        HighlightRowBgColor? highlightColor = _viewerRules.TryGetHighlightColor(visibleTraceTable, i);
                        if (highlightColor.HasValue)
                        {
                            ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg0, AppTheme.GetHighlightRowBgColorU32(highlightColor.Value));
                        }

                        uint rowColor = LevelToColor(UnifiedLevel.Info);
                        if (visibleTraceTable.Schema.UnifiedLevelColumn != null)
                        {
                            UnifiedLevel unifiedLevel = visibleTraceTable.GetUnifiedLevel(i);
                            rowColor = LevelToColor(unifiedLevel);
                        }
                        SetColor(rowColor);

                        ImGui.TableNextColumn();

                        // We must store the index instead of the id for the multiselect system so that when we handle selection ranges, we can
                        // look up the ids which might be sparse.
                        ImGui.SetNextItemSelectionUserData(i);

                        // Create an empty selectable that spans the full row to enable row selection.
                        bool isSelected = _selectedFullTableRowIndices.Contains(fullTableRowIndex);
                        if (ImGui.Selectable($"##TableRow", isSelected, ImGuiSelectableFlags.SpanAllColumns))
                        {
                            _lastSelectedVisibleRowIndex = i;
                            _lastSelectedFullTableRowIndex = fullTableRowIndex;
                        }

                        // The list clipper will prevent rows that aren't visible from being processed EXCEPT for the first row, which it always processes
                        // to determine row height (all rows must have the same height). So we use IsItemVisible to determine if actually visible.
                        if (ImGui.IsItemVisible())
                        {
                            // Don't bother to scroll to the selected index if it's already in view.
                            if (i == setScrollIndex)
                            {
                                setScrollIndex = null;
                            }

                            // If this is the last selected row, remember its vertical offset from the top of the table so it can be preserved if the filter changes.
                            if (fullTableRowIndex == _lastSelectedFullTableRowIndex)
                            {
                                _lastSelectedRowYOffset = rowYOffset - ImGui.GetScrollY() - topVisibleRowYOffset;
                            }
                            if (_topmostRenderedFullTableRowIndex == null)
                            {
                                _topmostRenderedFullTableRowIndex = fullTableRowIndex;
                            }
                            if (_topmostRenderedVisibleRowIndex == null)
                            {
                                _topmostRenderedVisibleRowIndex = i;
                            }

                            _bottommostRenderedVisibleRowIndex = i; // Each visible row stomps on the previous value so that the last one wins.
                        }

                        // Selectable spans all columns so this makes it easy to tell if a row is hovered.
                        bool isRowHovered = ImGui.IsItemHovered();
                        int hoveredCol = ImGui.TableGetHoveredColumn();

                        if (isRowHovered)
                        {
                            if (visibleTraceTable.Schema.ProcessIdColumn != null && hoveredCol == 0)
                            {
                                newHoveredProcessId = visibleTraceTable.GetProcessId(i);
                            }
                            else if (visibleTraceTable.Schema.ThreadIdColumn != null && hoveredCol == 1)
                            {
                                newHoveredThreadId = visibleTraceTable.GetThreadId(i);
                            }
                        }

                        int columnIndex = 0;
                        foreach (var column in visibleTraceTable.Schema.Columns)
                        {
                            if (columnIndex == 0)
                            {
                                ImGui.SameLine(); // The first column is already started with a special whole-row selectable.
                            }
                            else
                            {
                                ImGui.TableNextColumn();
                            }

                            string displayText = visibleTraceTable.GetColumnValueString(i, column, allowMultiline: false).Replace('\n', ' ');
                            ImGui.TextUnformatted(displayText);

                            if (ImGui.IsMouseReleased(ImGuiMouseButton.Right) && isRowHovered && hoveredCol == columnIndex)
                            {
                                ImGui.OpenPopup($"tableViewPopup{columnIndex}");
                            }

                            if (ImGui.BeginPopup($"tableViewPopup{columnIndex}", ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoSavedSettings))
                            {
                                SetColor(null);

                                string displayTextTruncated = displayText.Length > 48 ? displayText.Substring(0, 48) + "..." : displayText;

                                AddIncludeExcludeRuleMenuItems(visibleTraceTable, i, column, displayTextTruncated);

                                ImGui.Separator();
                                if (ImGui.MenuItem($"Copy '{displayTextTruncated}'"))
                                {
                                    ImGui.SetClipboardText(displayText);
                                }
                                ImGui.EndPopup();

                                SetColor(rowColor); // Resume color for remainder of row.
                            }

                            // Double-click on a cell will pop up a read-only edit box so the user can read long content or copy parts of the text.
                            {
                                if (!string.IsNullOrEmpty(displayText))
                                {
                                    if (isRowHovered && hoveredCol == columnIndex && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
                                    {
                                        ImGui.OpenPopup("CellContentPopup", ImGuiPopupFlags.AnyPopupLevel);
                                        _cellContentPopupFullTableRowIndex = fullTableRowIndex;
                                        _cellContentPopupColumn = column;
                                    }
                                }

                                if (_cellContentPopupFullTableRowIndex == fullTableRowIndex && _cellContentPopupColumn == column)
                                {
                                    if (ImGui.BeginPopup("CellContentPopup", ImGuiWindowFlags.NoSavedSettings))
                                    {
                                        SetColor(null); // Clear color back to default
                                        RenderCellContentPopup(visibleTraceTable, fullTableRowIndex, column);
                                        SetColor(rowColor); // Resume color
                                        ImGui.EndPopup();
                                    }
                                    else
                                    {
                                        // User scrolled away from the row so it isn't being shown anymore.
                                        _cellContentPopupFullTableRowIndex = null;
                                        _cellContentPopupColumn = null;
                                    }
                                }
                            }

                            columnIndex++;
                        }

                        ImGui.PopID(); // Trace row id
                    }
                }
                _tableClipper.End();

                SetColor(null);

                ImGui.EndMultiSelect();
                ApplyMultiSelectRequests(visibleTraceTable, multiselectIO);

                _hoveredProcessId = newHoveredProcessId;
                _hoveredThreadId = newHoveredThreadId;

                ImGui.PopStyleVar(); // CellPadding

                if (setScrollIndex.HasValue)
                {
                    // If we're trying to precisely maintain the content after the count has changed, we need to account for the partial row scroll.
                    // 0.3f means the selected row will be 30% down in the table so some of the earlier trace messages can also be seen.
                    int setScrollIncludePriorRowCount = (int)((remainingRegion.Y * 0.3f) / _tableClipper.ItemsHeight);
                    float partialRowScroll = setScrollIncludePriorRowCount == 0 ? ((int)ImGui.GetScrollY() % (int)_tableClipper.ItemsHeight) : 0;
                    ImGui.SetScrollY((setScrollIndex.Value - setScrollIncludePriorRowCount) * _tableClipper.ItemsHeight + partialRowScroll);
                }
                // ImGui has a bug with large scroll areas where you can't quite reach the MaxY with the scrollbar (e.g. ScrollY is 103545660 and ScrollMaxY is 103545670).
                // ImGui seems to stop 10 pixels shy of the end when the scroll region is very large. This is a workaround to ensure the user can scroll to the very end.
                else if (ImGui.GetScrollMaxY() > 0 && ImGui.GetScrollY() >= (float)ImGui.GetScrollMaxY() - 10)
                {
                    // Auto scroll - AKA keep the table scrolled to the bottom as new messages come in, but only if the table is already
                    // scrolled to the bottom.
                    ImGui.SetScrollHereY(1.0f);
                }

                ImGui.EndTable();
            }
        }

        private void AddIncludeExcludeRuleMenuItems(FilteredTraceTableSnapshot visibleTraceTable, int i, TraceSourceSchemaColumn column, string displayTextTruncated)
        {
            string TruncatedString(string s)
            {
                return s.Length > 128 ? s.Substring(0, 128) + "..." : s;
            }

            void AddIncludeRule(IEnumerable<string> newRule)
            {
                string newRuleStr = string.Join(' ', newRule);
                if (ImGui.MenuItem($"Include: {TruncatedString(newRuleStr)}"))
                {
                    _viewerRules.AddRule(newRuleStr, TraceRowRuleAction.Include);
                }
            }
            void AddExcludeRule(IEnumerable<string> newRule)
            {
                string newRuleStr = string.Join(' ', newRule);
                if (ImGui.MenuItem($"Exclude: {TruncatedString(newRuleStr)}"))
                {
                    _viewerRules.AddRule(newRuleStr, TraceRowRuleAction.Exclude);
                }
            }

            void AddHighlightRule(IEnumerable<string> newRule)
            {
                string newRuleStr = string.Join(' ', newRule);

                if (ImGui.BeginMenu($"Hightlight: {TruncatedString(newRuleStr)}"))
                {
                    ImGuiWidgets.AddHighlightRowBgColorMenuItems(selectedColor =>
                    {
                        _viewerRules.AddRule(newRuleStr, TraceRowRuleAction.Highlight, selectedColor);
                    });

                    ImGui.EndMenu();
                }
            }
            void AddCustomRule(IEnumerable<string> newRule)
            {
                string newRuleStr = string.Join(' ', newRule);
                if (ImGui.MenuItem($"Custom rule..."))
                {
                    // Open filter editor window if not already open.
                    if (_filtersEditorWindow == null)
                    {
                        _filtersEditorWindow = new FiltersEditorWindow(_traceSource.TraceSource.DisplayName, _windowIdString);
                    }

                    _filtersEditorWindow.SetRuleEditText(newRuleStr);
                }
            }

            if (column == visibleTraceTable.Schema.UnifiedLevelColumn)
            {
                var levelStr = visibleTraceTable.GetColumnValueUnifiedLevel(i, column).ToString();
                string provStr = null;
                string[] andProvEquals = null, newRuleGtOrEq = null, newRuleLtOrEq = null;
                if (visibleTraceTable.Schema.ProviderColumn != null)
                {
                    provStr = visibleTraceTable.GetColumnValueString(i, visibleTraceTable.Schema.ProviderColumn, allowMultiline: false);
                    andProvEquals = [
                       "AND",
                        TraceTableRowSelectorSyntax.CreateColumnVariableName(visibleTraceTable.Schema.ProviderColumn),
                        TraceTableRowSelectorSyntax.EqualsOperatorName,
                        TraceTableRowSelectorSyntax.CreateEscapedStringLiteral(provStr)];
                    newRuleGtOrEq = [TraceTableRowSelectorSyntax.CreateColumnVariableName(column), TraceTableRowSelectorSyntax.GreaterThanOrEqualOperatorName, levelStr];
                    newRuleLtOrEq = [TraceTableRowSelectorSyntax.CreateColumnVariableName(column), TraceTableRowSelectorSyntax.LessThanOrEqualOperatorName, levelStr];
                }

                AddIncludeRule([TraceTableRowSelectorSyntax.CreateColumnVariableName(column), TraceTableRowSelectorSyntax.EqualsOperatorName, levelStr]);
                AddIncludeRule([TraceTableRowSelectorSyntax.CreateColumnVariableName(column), TraceTableRowSelectorSyntax.GreaterThanOrEqualOperatorName, levelStr]);
                if (visibleTraceTable.Schema.ProviderColumn != null)
                {
                    AddIncludeRule(newRuleGtOrEq.Concat(andProvEquals));
                }

                ImGui.Separator();
                AddExcludeRule([TraceTableRowSelectorSyntax.CreateColumnVariableName(column), TraceTableRowSelectorSyntax.EqualsOperatorName, levelStr]);
                AddExcludeRule([TraceTableRowSelectorSyntax.CreateColumnVariableName(column), TraceTableRowSelectorSyntax.LessThanOrEqualOperatorName, levelStr]);
                if (visibleTraceTable.Schema.ProviderColumn != null)
                {
                    AddExcludeRule(newRuleLtOrEq.Concat(andProvEquals));
                }

                ImGui.Separator();
                AddHighlightRule([TraceTableRowSelectorSyntax.CreateColumnVariableName(column), TraceTableRowSelectorSyntax.EqualsOperatorName, levelStr]);
                AddHighlightRule([TraceTableRowSelectorSyntax.CreateColumnVariableName(column), TraceTableRowSelectorSyntax.GreaterThanOrEqualOperatorName, levelStr]);
                AddHighlightRule([TraceTableRowSelectorSyntax.CreateColumnVariableName(column), TraceTableRowSelectorSyntax.LessThanOrEqualOperatorName, levelStr]);
                if (visibleTraceTable.Schema.ProviderColumn != null)
                {
                    AddHighlightRule(newRuleGtOrEq.Concat(andProvEquals));
                    AddHighlightRule(newRuleLtOrEq.Concat(andProvEquals));
                }

                ImGui.Separator();
                if (visibleTraceTable.Schema.ProviderColumn != null)
                {
                    AddCustomRule(newRuleLtOrEq.Concat(andProvEquals));
                }
                else
                {
                    AddCustomRule([TraceTableRowSelectorSyntax.CreateColumnVariableName(column), TraceTableRowSelectorSyntax.EqualsOperatorName, levelStr]);
                }
            }
            else if (column == visibleTraceTable.Schema.TimestampColumn)
            {
                var timeStr = TraceTableRowSelectorSyntax.CreateEscapedStringLiteral(visibleTraceTable.GetColumnValueDateTime(i, column));
                AddIncludeRule([TraceTableRowSelectorSyntax.CreateColumnVariableName(column), TraceTableRowSelectorSyntax.GreaterThanOrEqualOperatorName, timeStr]);
                AddIncludeRule([TraceTableRowSelectorSyntax.CreateColumnVariableName(column), TraceTableRowSelectorSyntax.LessThanOrEqualOperatorName, timeStr]);

                ImGui.Separator();
                AddExcludeRule([TraceTableRowSelectorSyntax.CreateColumnVariableName(column), TraceTableRowSelectorSyntax.GreaterThanOperatorName, timeStr]);
                AddExcludeRule([TraceTableRowSelectorSyntax.CreateColumnVariableName(column), TraceTableRowSelectorSyntax.LessThanOperatorName, timeStr]);

                ImGui.Separator();
                AddHighlightRule([TraceTableRowSelectorSyntax.CreateColumnVariableName(column), TraceTableRowSelectorSyntax.GreaterThanOperatorName, timeStr]);
                AddHighlightRule([TraceTableRowSelectorSyntax.CreateColumnVariableName(column), TraceTableRowSelectorSyntax.GreaterThanOrEqualOperatorName, timeStr]);
                AddHighlightRule([TraceTableRowSelectorSyntax.CreateColumnVariableName(column), TraceTableRowSelectorSyntax.EqualsOperatorName, timeStr]);
                AddHighlightRule([TraceTableRowSelectorSyntax.CreateColumnVariableName(column), TraceTableRowSelectorSyntax.LessThanOrEqualOperatorName, timeStr]);
                AddHighlightRule([TraceTableRowSelectorSyntax.CreateColumnVariableName(column), TraceTableRowSelectorSyntax.LessThanOperatorName, timeStr]);

                ImGui.Separator();
                AddCustomRule([TraceTableRowSelectorSyntax.CreateColumnVariableName(column), TraceTableRowSelectorSyntax.LessThanOrEqualOperatorName, timeStr]);
            }
            else if (column == visibleTraceTable.Schema.ProcessIdColumn || column == visibleTraceTable.Schema.ThreadIdColumn)
            {
                string intValue = visibleTraceTable.GetColumnValueInt(i, column).ToString();
                string nameValue = visibleTraceTable.GetColumnValueNameForId(i, column);

                AddIncludeRule([TraceTableRowSelectorSyntax.CreateColumnVariableName(column), TraceTableRowSelectorSyntax.EqualsOperatorName, intValue]);
                if (!string.IsNullOrEmpty(nameValue))
                {
                    string nameValueLiteral = TraceTableRowSelectorSyntax.CreateEscapedStringLiteral(nameValue);
                    AddIncludeRule([TraceTableRowSelectorSyntax.CreateColumnVariableName(column), TraceTableRowSelectorSyntax.StringContainsOperatorName, nameValueLiteral]);
                }

                ImGui.Separator();
                AddExcludeRule([TraceTableRowSelectorSyntax.CreateColumnVariableName(column), TraceTableRowSelectorSyntax.EqualsOperatorName, intValue]);
                if (!string.IsNullOrEmpty(nameValue))
                {
                    string nameValueLiteral = TraceTableRowSelectorSyntax.CreateEscapedStringLiteral(nameValue);
                    AddExcludeRule([TraceTableRowSelectorSyntax.CreateColumnVariableName(column), TraceTableRowSelectorSyntax.StringContainsOperatorName, nameValueLiteral]);
                }

                ImGui.Separator();
                AddHighlightRule([TraceTableRowSelectorSyntax.CreateColumnVariableName(column), TraceTableRowSelectorSyntax.EqualsOperatorName, intValue]);
                if (!string.IsNullOrEmpty(nameValue))
                {
                    string nameValueLiteral = TraceTableRowSelectorSyntax.CreateEscapedStringLiteral(nameValue);
                    AddHighlightRule([TraceTableRowSelectorSyntax.CreateColumnVariableName(column), TraceTableRowSelectorSyntax.StringContainsOperatorName, nameValueLiteral]);
                }

                ImGui.Separator();
                if (!string.IsNullOrEmpty(nameValue))
                {
                    string nameValueLiteral = TraceTableRowSelectorSyntax.CreateEscapedStringLiteral(nameValue);
                    AddCustomRule([TraceTableRowSelectorSyntax.CreateColumnVariableName(column), TraceTableRowSelectorSyntax.StringContainsOperatorName, nameValueLiteral]);
                }
                else
                {
                    AddCustomRule([TraceTableRowSelectorSyntax.CreateColumnVariableName(column), TraceTableRowSelectorSyntax.EqualsOperatorName, intValue]);
                }
            }
            else
            {
                string quotedStringValue = TraceTableRowSelectorSyntax.CreateEscapedStringLiteral(visibleTraceTable.GetColumnValueString(i, column, allowMultiline: false));
                AddIncludeRule([TraceTableRowSelectorSyntax.CreateColumnVariableName(column), TraceTableRowSelectorSyntax.EqualsOperatorName, quotedStringValue]);

                ImGui.Separator();
                AddExcludeRule([TraceTableRowSelectorSyntax.CreateColumnVariableName(column), TraceTableRowSelectorSyntax.EqualsOperatorName, quotedStringValue]);

                ImGui.Separator();
                AddHighlightRule([TraceTableRowSelectorSyntax.CreateColumnVariableName(column), TraceTableRowSelectorSyntax.EqualsOperatorName, quotedStringValue]);

                ImGui.Separator();
                // Assume a "contains" for a custom rule as a good starting point...
                AddCustomRule([TraceTableRowSelectorSyntax.CreateColumnVariableName(column), TraceTableRowSelectorSyntax.StringContainsOperatorName, quotedStringValue]);
            }
        }

        private void RenderCellContentPopup(FilteredTraceTableSnapshot visibleTraceTable, int fullTableRowIndex, TraceSourceSchemaColumn column)
        {
            string message = visibleTraceTable.FullTable.GetColumnValueString(fullTableRowIndex, column, true /*allow multiline*/);

            // Analyze the text to measure it's width and height in pixels so we can pick a reasonable popup size within limits.
            string[] lines = message.Split('\n');
            float maxLineLength = lines.Max(line => ImGui.CalcTextSize(line).X);

            ImGui.InputTextMultiline(
                "##Message",
                ref message,
                0,
                new Vector2(
                    Math.Clamp(maxLineLength + 40, 200, 800),
                    ImGui.GetTextLineHeight() * Math.Min(32, lines.Length) + 10),
                ImGuiInputTextFlags.ReadOnly | ImGuiInputTextFlags.AutoSelectAll);
        }

        private void ApplyMultiSelectRequests(FilteredTraceTableSnapshot visibleTraceTable, ImGuiMultiSelectIOPtr multiselectIO)
        {
            bool lastSelectedRowUnselected = false, lastSelectedRowSelected = false;
            for (int reqIdx = 0; reqIdx < multiselectIO.Requests.Size; reqIdx++)
            {
                ImGuiSelectionRequestPtr req = multiselectIO.Requests[reqIdx];
                if (req.Type == ImGuiSelectionRequestType.SetAll)
                {
                    if (!req.Selected)
                    {
                        // This is an optimization to unselect everything without iterating over all rows.
                        _selectedFullTableRowIndices.Clear();
                        lastSelectedRowUnselected = true;
                        continue;
                    }

                    req.RangeFirstItem = 0;
                    req.RangeLastItem = visibleTraceTable.RowCount - 1;
                    req.RangeDirection = 1;
                }

                // RangeLastItem can be less than RangeFirstItem with RangeDirection = -1. We don't care about order so ignore direction.
                long startIndex = Math.Min(req.RangeFirstItem, req.RangeLastItem);
                long endIndex = Math.Max(req.RangeFirstItem, req.RangeLastItem);
                for (long i = startIndex; i <= endIndex; i++)
                {
                    int fullTableRowIndex = visibleTraceTable.GetFullTableRowIndex((int)i);
                    if (req.Selected)
                    {
                        _selectedFullTableRowIndices.Add(fullTableRowIndex);
                        lastSelectedRowSelected |= (fullTableRowIndex == _lastSelectedFullTableRowIndex);
                    }
                    else
                    {
                        _selectedFullTableRowIndices.Remove(fullTableRowIndex);
                        lastSelectedRowUnselected |= (fullTableRowIndex == _lastSelectedFullTableRowIndex);
                    }
                }
            }

            // When clicking a row, this manifests as a "unselect all rows" + "select this row" request. So often a row will be unselected+reselected
            // which is why lastSelectedRowUnselected is ignored if lastSelectedRowSelected is true.
            if (lastSelectedRowUnselected && !lastSelectedRowSelected)
            {
                _lastSelectedRowYOffset = null;
                _lastSelectedVisibleRowIndex = null;
                _lastSelectedFullTableRowIndex = null;
            }
        }

        private void DrawToolStrip(IUiCommands uiCommands, FilteredTraceTableSnapshot visibleTraceTable, ref int? setScrollIndex)
        {
            ImGui.BeginDisabled(!_selectedFullTableRowIndices.Any());
            if (ImGui.Button("\uF0C5 Copy rows"))
            {
                CopySelectedRows(visibleTraceTable);
            }
            ImGui.EndDisabled();

            if (_traceSource.TraceSource.CanClear)
            {
                ImGui.SameLine();
                if (ImGui.Button("\uF12D Clear"))
                {
                    _traceSource.TraceSource.Clear();
                    _lastSelectedVisibleRowIndex = null;
                    _selectedFullTableRowIndices.Clear();

                    // Updating the filtered trace table here so it will see the generation id changed and clear itself.
                    _filteredTraceTableBuilder.Update(_viewerRules, _traceSource.TraceSource.CreateSnapshot());
                }
            }

            if (_traceSource.TraceSource.CanPause)
            {
                ImGui.SameLine();
                if (ImGui.Button(_traceSource.TraceSource.IsPaused ? "\uF04B Resume" : "\uF04C Pause"))
                {
                    _traceSource.TraceSource.TogglePause();
                }
            }

            ImGui.SameLine();
            string filterCountSuffix =
                !_viewerRules.ApplyFiltering ? " (Off)" :
                _viewerRules.Rules.Count > 0 ? $" ({_viewerRules.Rules.Count})" : string.Empty;
            if (ImGui.Button($"\uf0b0 Rules{filterCountSuffix}..."))
            {
                ImGui.OpenPopup("Rules");
            }
            if (ImGui.BeginPopup("Rules"))
            {
                if (ImGui.MenuItem("Edit rules...", "Ctrl+E"))
                {
                    _filtersEditorWindow = new FiltersEditorWindow(_traceSource.TraceSource.DisplayName, _windowIdString);
                }

                if (ImGui.MenuItem($"Clear rules", _viewerRules.Rules.Count > 0))
                {
                    _viewerRules.ClearRules();
                }

                IReadOnlyList<string> itvfMru = Settings.GetRecentlyOpenedItfv();
                if (itvfMru.Count > 0)
                {
                    ImGui.Separator();
                    if (ImGui.BeginMenu("Recent .ITVF files"))
                    {
                        foreach (var file in itvfMru)
                        {
                            if (ImGui.MenuItem($"Import {file}"))
                            {
                                _viewerRules.Import(uiCommands, file);
                            }
                        }

                        ImGui.EndMenu();
                    }
                }

                ImGui.Separator();

                bool applyFiltering = _viewerRules.ApplyFiltering;
                if (ImGui.MenuItem("Apply filtering", "Ctrl+H", ref applyFiltering))
                {
                    _viewerRules.ApplyFiltering = applyFiltering;
                }

                ImGui.Separator();

                if (ImGui.MenuItem("Spam filter...", "Ctrl+S"))
                {
                    _spamFilterWindow = new SpamFilterWindow(_traceSource.TraceSource.DisplayName, _windowIdString);
                }

                ImGui.EndPopup();
            }

            ImGui.SameLine();
            if (ImGui.Button("\uF05B Navigation..."))
            {
                ImGui.OpenPopup("Navigation");
            }

            if (ImGui.BeginPopup("Navigation"))
            {
                ImGui.BeginDisabled(!_lastSelectedVisibleRowIndex.HasValue);
                string scrollToHint = "";
                if (_lastSelectedVisibleRowIndex.HasValue && visibleTraceTable.Schema.NameColumn != null)
                {
                    scrollToHint = $" ({visibleTraceTable.GetColumnValueString(_lastSelectedVisibleRowIndex.Value, visibleTraceTable.Schema.NameColumn)})";
                }
                if (ImGui.MenuItem($"Go to last selected row{scrollToHint}", "Alt+Left Arrow"))
                {
                    setScrollIndex = _lastSelectedVisibleRowIndex;
                }
                ImGui.EndDisabled();

                if (visibleTraceTable.Schema.UnifiedLevelColumn != null)
                {
                    ImGui.Separator();
                    if (ImGui.MenuItem("Go to previous error", "Alt+,"))
                    {
                        setScrollIndex = FindNextError(visibleTraceTable, _findBuffer, findForward: false);
                    }
                    if (ImGui.MenuItem("Go to next error", "Alt+."))
                    {
                        setScrollIndex = FindNextError(visibleTraceTable, _findBuffer, findForward: true);
                    }
                }

                if (visibleTraceTable.Schema.ThreadIdColumn != null && _lastSelectedVisibleRowIndex.HasValue)
                {
                    int lastSelectedRowThreadId = visibleTraceTable.GetThreadId(_lastSelectedVisibleRowIndex.Value);
                    ImGui.Separator();
                    if (ImGui.MenuItem($"Go to previous row for thread {lastSelectedRowThreadId}", "Alt+["))
                    {
                        setScrollIndex = FindNextThreadRow(visibleTraceTable, lastSelectedRowThreadId, findForward: false);
                    }
                    if (ImGui.MenuItem($"Go to next row for thread {lastSelectedRowThreadId}", "Alt+]"))
                    {
                        setScrollIndex = FindNextThreadRow(visibleTraceTable, lastSelectedRowThreadId, findForward: true);
                    }
                }

                ImGui.Separator();
                if (ImGui.MenuItem($"Go to previously highlighted row", "Alt+;"))
                {
                    setScrollIndex = FindNextHighlightedRow(visibleTraceTable, findForward: false);
                }
                if (ImGui.MenuItem($"Go to next highlighted row", "Alt+'"))
                {
                    setScrollIndex = FindNextHighlightedRow(visibleTraceTable, findForward: true);
                }

                ImGui.EndPopup();
            }

            ImGui.SameLine();
            if (ImGui.Button("\uF080 Visualizations..."))
            {
                ImGui.OpenPopup("Visualizations");
            }
            if (ImGui.BeginPopup("Visualizations"))
            {
                ImGui.BeginDisabled(!ThreadTimelineWindow.IsSupported(visibleTraceTable.Schema));
                if (ImGui.MenuItem("Thread timeline", "", _threadTimelineWindow != null))
                {
                    _threadTimelineWindow = (_threadTimelineWindow == null) ? new ThreadTimelineWindow(_traceSource.TraceSource.DisplayName, _windowIdString) : null;
                }
                ImGui.EndDisabled();

                ImGui.EndPopup();
            }

            ImGui.SameLine();
            if (ImGui.Button("\uF0FE Clone"))
            {
                uiCommands.AddLogViewerWindow(Clone());
            }

            bool findRequested = false;
            ImGui.SameLine();
            ImGui.PushItemWidth(300);
            if (ImGui.Shortcut(ImGuiKey.F | ImGuiKey.ModCtrl))
            {
                ImGui.SetKeyboardFocusHere();
            }
            if (ImGui.InputTextWithHint("##Find", "Find...", ref _findBuffer, 1024, ImGuiInputTextFlags.EnterReturnsTrue | ImGuiInputTextFlags.AutoSelectAll))
            {
                // Focus goes somewhere else when enter is pressed but we want to keep focus so the user can keep pressing enter to go to the next match.
                ImGui.SetKeyboardFocusHere(-1);
                findRequested = true;
            }
            ImGui.PopItemWidth();
            ImGui.SameLine();
            if (ImGui.Button(_findForward ? "\uF061" : "\uF060"))
            {
                findRequested = true;
            }

            if (_traceSource.TraceSource.IsPreprocessingData)
            {
                // The intent is that we only show "Loading..." when we can't stream in events yet. Parsing some file types sometimes takes time before the data can be returned.
                Debug.Assert(visibleTraceTable.RowCount == 0, "Visible trace table should be empty while preprocessing data.");

                ImGui.SameLine();
                ImGui.SetNextItemWidth(ImGui.GetFontSize() * 10);
                ImGui.ProgressBar(-1.0f * (float)ImGui.GetTime(), new Vector2(0.0f, 0.0f), "Loading..");
            }
            else
            {
                ImGui.SameLine();
                ImGui.TextUnformatted($"{visibleTraceTable.RowCount:N0} rows");
                if (visibleTraceTable.RowCount != visibleTraceTable.FullTable.RowCount)
                {
                    ImGui.SameLine();
                    ImGui.TextUnformatted($"({visibleTraceTable.FullTable.RowCount - visibleTraceTable.RowCount:N0} excluded)");
                }

                if (visibleTraceTable.ErrorCount > 0)
                {
                    ImGui.SameLine();
                    ImGui.PushStyleColor(ImGuiCol.Text, LevelToColor(UnifiedLevel.Error));
                    ImGui.TextUnformatted($"{visibleTraceTable.ErrorCount:N0} Errors");
                    ImGui.PopStyleColor();
                }
            }

            if (!string.IsNullOrEmpty(_findBuffer))
            {
                // ImGui.Shortcut handles focus management but it doesn't allow the key to be held down to repeat searches quickly, so
                // we have to check focus and key press manually.
                if (ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows))
                {
                    if (ImGui.IsKeyPressed(ImGuiKey.F3) && ImGui.IsKeyDown(ImGuiKey.ModShift))
                    {
                        _findForward = false;
                        findRequested = true;
                    }
                    else if (ImGui.IsKeyPressed(ImGuiKey.F3))
                    {
                        _findForward = true;
                        findRequested = true;
                    }
                }

                if (findRequested)
                {
                    setScrollIndex = FindText(visibleTraceTable, _findBuffer, _findForward);
                }
            }

            //
            // Handle hotkeys for menu bar here. They can't be nested inside the 'if's.
            //

            if (ImGui.Shortcut(ImGuiKey.ModCtrl | ImGuiKey.C))
            {
                CopySelectedRows(visibleTraceTable);
            }

            if (ImGui.Shortcut(ImGuiKey.ModCtrl | ImGuiKey.E))
            {
                _filtersEditorWindow = new FiltersEditorWindow(_traceSource.TraceSource.DisplayName, _windowIdString);
            }

            if (ImGui.Shortcut(ImGuiKey.ModCtrl | ImGuiKey.S))
            {
                _spamFilterWindow = new SpamFilterWindow(_traceSource.TraceSource.DisplayName, _windowIdString);
            }

            if (ImGui.Shortcut(ImGuiKey.ModCtrl | ImGuiKey.H))
            {
                _viewerRules.ApplyFiltering = !_viewerRules.ApplyFiltering;
            }

            if (_lastSelectedVisibleRowIndex.HasValue && ImGui.Shortcut(ImGuiKey.ModAlt | ImGuiKey.LeftArrow))
            {
                setScrollIndex = _lastSelectedVisibleRowIndex;
            }

            if (visibleTraceTable.Schema.UnifiedLevelColumn != null)
            {
                if (ImGui.Shortcut(ImGuiKey.ModAlt | ImGuiKey.Comma, ImGuiInputFlags.Repeat))
                {
                    setScrollIndex = FindNextError(visibleTraceTable, _findBuffer, findForward: false);
                }

                if (ImGui.Shortcut(ImGuiKey.ModAlt | ImGuiKey.Period, ImGuiInputFlags.Repeat))
                {
                    setScrollIndex = FindNextError(visibleTraceTable, _findBuffer, findForward: true);
                }
            }

            if (visibleTraceTable.Schema.ThreadIdColumn != null && _lastSelectedVisibleRowIndex.HasValue)
            {
                int lastSelectedRowThreadId = visibleTraceTable.GetThreadId(_lastSelectedVisibleRowIndex.Value);
                if (ImGui.Shortcut(ImGuiKey.ModAlt | ImGuiKey.LeftBracket, ImGuiInputFlags.Repeat))
                {
                    setScrollIndex = FindNextThreadRow(visibleTraceTable, lastSelectedRowThreadId, findForward: false);
                }
                if (ImGui.Shortcut(ImGuiKey.ModAlt | ImGuiKey.RightBracket, ImGuiInputFlags.Repeat))
                {
                    setScrollIndex = FindNextThreadRow(visibleTraceTable, lastSelectedRowThreadId, findForward: true);
                }
            }

            if (ImGui.Shortcut(ImGuiKey.ModAlt | ImGuiKey.Semicolon, ImGuiInputFlags.Repeat))
            {
                setScrollIndex = FindNextHighlightedRow(visibleTraceTable, findForward: false);
            }
            if (ImGui.Shortcut(ImGuiKey.ModAlt | ImGuiKey.Apostrophe, ImGuiInputFlags.Repeat))
            {
                setScrollIndex = FindNextHighlightedRow(visibleTraceTable, findForward: true);
            }
        }

        private LogViewerWindow Clone()
        {
            LogViewerWindow newWindow = new(_traceSource);
            newWindow._findBuffer = _findBuffer;
            newWindow._viewerRules = _viewerRules.Clone();
            return newWindow;
        }

        private static uint LevelToColor(UnifiedLevel level)
        {
            return level == UnifiedLevel.Verbose ? AppTheme.VerboseColor
                   : level == UnifiedLevel.Warning ? AppTheme.WarningColor
                   : level == UnifiedLevel.Error ? AppTheme.ErrorColor
                   : level == UnifiedLevel.Fatal ? AppTheme.FatalColor
                                                 : AppTheme.InfoColor;
        }

        private void CopySelectedRows(FilteredTraceTableSnapshot visibleTraceTable)
        {
            StringBuilder copyText = new();

            foreach (var fullTableRowIndex in _selectedFullTableRowIndices.OrderBy(i => i))
            {
                bool isFirstColumn = true;
                foreach (var column in visibleTraceTable.Schema.Columns)
                {
                    if (!isFirstColumn)
                    {
                        copyText.Append('\t');
                    }
                    isFirstColumn = false;

                    string displayText = visibleTraceTable.FullTable.GetColumnValueString(fullTableRowIndex, column, allowMultiline: false);
                    copyText.Append(displayText);
                }
                copyText.AppendLine();
            }

            ImGui.SetClipboardText(copyText.ToString());
        }

        private int? FindText(FilteredTraceTableSnapshot visibleTraceTable, string text, bool findForward)
            => FindNextRow(visibleTraceTable, (visibleTraceTable, i) =>
                {
                    foreach (var column in visibleTraceTable.Schema.Columns)
                    {
                        string displayText = visibleTraceTable.GetColumnValueString(i, column, allowMultiline: false);
                        if (displayText.Contains(text, StringComparison.InvariantCultureIgnoreCase))
                        {
                            return true;
                        }
                    }
                    return false;
                }, findForward);

        private int? FindNextThreadRow(FilteredTraceTableSnapshot visibleTraceTable, int threadId, bool findForward)
            => FindNextRow(visibleTraceTable, (visibleTraceTable, i) => visibleTraceTable.GetThreadId(i) == threadId, findForward);

        private int? FindNextError(FilteredTraceTableSnapshot visibleTraceTable, string text, bool findForward)
            => FindNextRow(visibleTraceTable, (visibleTraceTable, i) => visibleTraceTable.GetUnifiedLevel(i) >= UnifiedLevel.Error, findForward);

        private int? FindNextHighlightedRow(FilteredTraceTableSnapshot visibleTraceTable, bool findForward)
            => FindNextRow(visibleTraceTable, (visibleTraceTable, i) => _viewerRules.TryGetHighlightColor(visibleTraceTable.FullTable, visibleTraceTable.GetFullTableRowIndex(i)).HasValue, findForward);

        private int? FindNextRow(FilteredTraceTableSnapshot visibleTraceTable, Func<FilteredTraceTableSnapshot, int, bool> predicate, bool findForward)
        {
            int visibleRowIndex =
                findForward ?
                   (_lastSelectedVisibleRowIndex.HasValue ? _lastSelectedVisibleRowIndex.Value + 1 : 0) :
                   (_lastSelectedVisibleRowIndex.HasValue ? _lastSelectedVisibleRowIndex.Value - 1 : visibleTraceTable.RowCount - 1);
            while (visibleRowIndex >= 0 && visibleRowIndex < visibleTraceTable.RowCount)
            {
                if (predicate(visibleTraceTable, visibleRowIndex))
                {
                    _lastSelectedVisibleRowIndex = visibleRowIndex;
                    _selectedFullTableRowIndices.Clear();
                    _selectedFullTableRowIndices.Add(visibleTraceTable.GetFullTableRowIndex(visibleRowIndex));
                    return visibleRowIndex;
                }

                visibleRowIndex = (findForward ? visibleRowIndex + 1 : visibleRowIndex - 1);
            }
            return null;
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!_isDisposed)
            {
                MinUniqueIdPool.ReturnId(_windowId);

                if (disposing)
                {
                    _tableClipper.Destroy();
                    _traceSource.ReleaseRef(this);
                }

                _isDisposed = true;
            }
        }

        public void Dispose()
        {
            Dispose(disposing: true);
            GC.SuppressFinalize(this);
        }
    }
}
