using ImGuiNET;
using InstantTraceViewer;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace InstantTraceViewerUI
{
    internal class SpamFilterWindow
    {
        private const string WindowName = "Spam Filter";

        private readonly string _name;
        private readonly string _parentWindowId;


        private IReadOnlyList<CountByBaseAdapter> _adapters;
        private int _adaptersLastGenerationId = -1;
        private CountByBaseAdapter _currentAdapter;

        private IReadOnlyCollection<CountByBase> _eventCounts = null;
        private int _eventCountsLastGenerationId = -1;

        private bool _open = true;

        public SpamFilterWindow(string name, string parentWindowId)
        {
            _name = name;
            _parentWindowId = parentWindowId;
        }

        public unsafe bool DrawWindow(IUiCommands uiCommands, ViewerRules viewerRules, ITraceTableSnapshot traceTable)
        {
            if (_adapters == null || _adaptersLastGenerationId != traceTable.GenerationId)
            {
                _adapters = [new CountByProviderAdapter(traceTable.Schema), new CountByEventNameAdapter(traceTable.Schema)];
                _adaptersLastGenerationId = traceTable.GenerationId;
                _currentAdapter = _adapters.First();
                _eventCounts = null;
            }

            ImGui.SetNextWindowSize(new Vector2(800, 400), ImGuiCond.FirstUseEver);
            ImGui.SetNextWindowSizeConstraints(new Vector2(400, 150), new Vector2(float.MaxValue, float.MaxValue));

            if (ImGui.Begin($"{WindowName} - {_name}###SpamFilter_{_parentWindowId}", ref _open))
            {
                ImGui.SetNextItemWidth(15 * ImGui.GetFontSize());
                if (ImGui.BeginCombo("Count by group", _currentAdapter.Name))
                {
                    foreach (var adapter in _adapters)
                    {
                        if (ImGui.Selectable(adapter.Name, _currentAdapter == adapter))
                        {
                            _currentAdapter = adapter;
                            _eventCounts = null;
                        }
                    }
                    ImGui.EndCombo();
                }

                ImGui.NewLine();

                if (!_currentAdapter.IsSchemaSupported())
                {
                    ImGui.TextUnformatted($"Cannot group by {_currentAdapter.Name} because the schema does not have the required columns.");
                    return _open;
                }

                if (_eventCounts == null || _eventCountsLastGenerationId != traceTable.GenerationId)
                {
                    _eventCounts = _currentAdapter.CountBy(traceTable);
                    _eventCountsLastGenerationId = traceTable.GenerationId;
                }

                int totalTraces = _eventCounts.Sum(c => c.Count);
                var selectedCount = _eventCounts.Count(c => c.Selected);
                var selectedTraceCount = _eventCounts.Where(c => c.Selected).Sum(c => c.Count);

                ImGui.BeginDisabled(selectedCount == 0);
                if (ImGui.Button($"Exclude selected events"))
                {
                    _currentAdapter.CreateExcludeRules(viewerRules, _eventCounts);
                }
                ImGui.EndDisabled();
                ImGui.SameLine();
                if (totalTraces == 0)
                {
                    // Avoids divide-by-zero in the else case.
                    ImGui.TextUnformatted("No events to filter.");
                }
                else
                {
                    ImGui.TextUnformatted($"{selectedCount} event types selected ({selectedTraceCount * 100.0f / totalTraces:F2}% of all events)");
                }

                ImGui.TextUnformatted("Tip: Use CTRL, SHIFT and mouse dragging to select the events that you want excluded.");

                if (ImGui.BeginTable($"TraceCounts_{_currentAdapter.Name}", 1 + _currentAdapter.ColumnCount /* columns */,
                        ImGuiTableFlags.ScrollY | ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersOuter | ImGuiTableFlags.Hideable |
                        ImGuiTableFlags.BordersV | ImGuiTableFlags.Resizable | ImGuiTableFlags.Sortable | ImGuiTableFlags.SortMulti))
                {
                    ImGui.TableSetupScrollFreeze(0, 1); // Top row is always visible.
                    _currentAdapter.SetupColumns();
                    ImGui.TableSetupColumn("Count", ImGuiTableColumnFlags.WidthFixed | ImGuiTableColumnFlags.PreferSortDescending | ImGuiTableColumnFlags.DefaultSort, 10 * ImGui.GetFontSize());
                    ImGui.TableHeadersRow();

                    IReadOnlyList<CountByBase> sortedCounts;
                    {
                        IEnumerable<CountByBase> sortedCountsEnumerable = _eventCounts;
                        ImGuiTableSortSpecsPtr sortSpecs = ImGui.TableGetSortSpecs();
                        for (int i = 0; i < sortSpecs.SpecsCount; i++)
                        {
                            var spec = new ImGuiTableColumnSortSpecsPtr(sortSpecs.NativePtr->Specs + i);
                            sortedCountsEnumerable = _currentAdapter.ImGuiSort(spec, sortedCountsEnumerable);
                        }

                        sortedCounts = sortedCountsEnumerable.ToList();
                    }

                    var multiselectIO = ImGui.BeginMultiSelect(ImGuiMultiSelectFlags.ClearOnEscape | ImGuiMultiSelectFlags.BoxSelect2d);
                    var applyMultiselectRequests = () =>
                    {
                        for (int reqIdx = 0; reqIdx < multiselectIO.Requests.Size; reqIdx++)
                        {
                            var req = multiselectIO.Requests[reqIdx];
                            if (req.Type == ImGuiSelectionRequestType.SetAll)
                            {
                                req.RangeFirstItem = 0;
                                req.RangeLastItem = sortedCounts.Count - 1;
                                req.RangeDirection = 1;
                            }

                            // RangeLastItem can be less than RangeFirstItem with RangeDirection = -1. We don't care about order so ignore direction.
                            long startIndex = Math.Min(req.RangeFirstItem, req.RangeLastItem);
                            long endIndex = Math.Max(req.RangeFirstItem, req.RangeLastItem);
                            for (long i = startIndex; i <= endIndex; i++)
                            {
                                sortedCounts[(int)i].Selected = req.Selected;
                            }
                        }
                    };
                    applyMultiselectRequests();

                    int rowIndex = 0;
                    foreach (var traceCount in sortedCounts)
                    {
                        ImGui.PushID(traceCount.GetHashCode());

                        ImGui.TableNextRow();

                        ImGui.TableNextColumn();
                        ImGui.SetNextItemSelectionUserData(rowIndex++);
                        ImGui.Selectable("##TableRow", traceCount.Selected, ImGuiSelectableFlags.SpanAllColumns);
                        ImGui.SameLine();
                        traceCount.AddColumnValues();
                        ImGui.TableNextColumn();
                        ImGui.TextUnformatted($"{traceCount.Count:N0} ({traceCount.Count * 100.0 / totalTraces:F2}%)");

                        ImGui.PopID();
                    }

                    ImGui.EndMultiSelect();
                    applyMultiselectRequests();

                    ImGui.EndTable();
                }
            }

            ImGui.End();

            return _open;
        }
    }
}
