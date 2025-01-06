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

        class ProviderTraceCount
        {
            public string ProviderName;
            public string Name;
            public UnifiedLevel? MaxLevel;
            public int Count;

            public bool Selected;
        }

        private List<ProviderTraceCount> _providerTraceCounts;
        private int _providerTraceCountsGenerationId = -1;

        private TraceTableSchema _schema;

        private bool _open = true;

        public SpamFilterWindow(string name)
        {
            _name = name;
        }

        public static bool SupportsSchema(TraceTableSchema schema)
        {
            // Provider and Level are optional. Only name is required.
            // TODO: Provide a way for the user to pick the column to aggregate counts by and default to Name column if present.
            return schema.NameColumn != null;
        }

        public unsafe bool DrawWindow(IUiCommands uiCommands, ViewerRules viewerRules, ITraceTableSnapshot traceTable)
        {
            if (!SupportsSchema(traceTable.Schema))
            {
                // Caller should have not created/used this object if the schema is not supported.
                throw new InvalidOperationException("SpamFilterWindow does not support the provided schema.");
            }

            if (_providerTraceCounts == null || _providerTraceCountsGenerationId != traceTable.GenerationId)
            {
                ComputeCounts(traceTable);
            }

            ImGui.SetNextWindowSize(new Vector2(800, 400), ImGuiCond.FirstUseEver);
            ImGui.SetNextWindowSizeConstraints(new Vector2(400, 150), new Vector2(float.MaxValue, float.MaxValue));

            if (ImGui.Begin($"{WindowName} - {_name}###SpamFilter", ref _open))
            {
                int totalTraces = _providerTraceCounts.Sum(c => c.Count);
                var selectedCount = _providerTraceCounts.Count(c => c.Selected);
                var selectedTraceCount = _providerTraceCounts.Where(c => c.Selected).Sum(c => c.Count);

                ImGui.BeginDisabled(selectedCount == 0);
                if (ImGui.Button($"Exclude selected events"))
                {
                    CreateExcludeRules(viewerRules);
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
                    ImGui.TextUnformatted($"{selectedCount} event types selected ({selectedTraceCount * 100.0f / totalTraces:F1}% of all events)");
                }

                ImGui.TextUnformatted("Tip: Use CTRL, SHIFT and mouse dragging to select the events that you want excluded.");

                if (ImGui.BeginTable("TraceCounts", 4 /* columns */,
                        ImGuiTableFlags.ScrollY | ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersOuter | ImGuiTableFlags.Hideable |
                        ImGuiTableFlags.BordersV | ImGuiTableFlags.Resizable | ImGuiTableFlags.Sortable | ImGuiTableFlags.SortMulti))
                {
                    ImGui.TableSetupScrollFreeze(0, 1); // Top row is always visible.

                    // If schema doesn't have optional columns, don't show them.
                    ImGuiTableColumnFlags DefaultHideIfColumnNull(TraceSourceSchemaColumn column) => column == null ? ImGuiTableColumnFlags.DefaultHide : 0;

                    // WARNING: If columns are changed, the ImGuiSort() helper function and TableSetColumnSortDirection must be updated to match the new column indices.
                    ImGui.TableSetupColumn("Provider", ImGuiTableColumnFlags.WidthStretch | DefaultHideIfColumnNull(_schema.ProviderColumn), 1);
                    ImGui.TableSetupColumn("Name", ImGuiTableColumnFlags.WidthStretch, 1);
                    ImGui.TableSetupColumn("Level", ImGuiTableColumnFlags.WidthFixed | DefaultHideIfColumnNull(_schema.UnifiedLevelColumn), 140.0f);
                    ImGui.TableSetupColumn("Count", ImGuiTableColumnFlags.WidthFixed | ImGuiTableColumnFlags.PreferSortDescending | ImGuiTableColumnFlags.DefaultSort, 140.0f);
                    ImGui.TableHeadersRow();

                    IReadOnlyList<ProviderTraceCount> sortedProviderTraceCounts;
                    {
                        IEnumerable<ProviderTraceCount> sortedProviderTraceCountsEnumerable = _providerTraceCounts;
                        ImGuiTableSortSpecsPtr sortSpecs = ImGui.TableGetSortSpecs();
                        for (int i = 0; i < sortSpecs.SpecsCount; i++)
                        {
                            var spec = new ImGuiTableColumnSortSpecsPtr(sortSpecs.NativePtr->Specs + i);
                            sortedProviderTraceCountsEnumerable = ImGuiSort(spec, sortedProviderTraceCountsEnumerable);
                        }

                        sortedProviderTraceCounts = sortedProviderTraceCountsEnumerable.ToList();
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
                                req.RangeLastItem = sortedProviderTraceCounts.Count - 1;
                                req.RangeDirection = 1;
                            }

                            // RangeLastItem can be less than RangeFirstItem with RangeDirection = -1. We don't care about order so ignore direction.
                            long startIndex = Math.Min(req.RangeFirstItem, req.RangeLastItem);
                            long endIndex = Math.Max(req.RangeFirstItem, req.RangeLastItem);
                            for (long i = startIndex; i <= endIndex; i++)
                            {
                                sortedProviderTraceCounts[(int)i].Selected = req.Selected;
                            }
                        }
                    };
                    applyMultiselectRequests();

                    int rowIndex = 0;
                    foreach (var providerTraceCount in sortedProviderTraceCounts)
                    {
                        ImGui.PushID((providerTraceCount.ProviderName?.GetHashCode() ?? 0) ^ providerTraceCount.Name.GetHashCode());

                        ImGui.TableNextRow();

                        ImGui.TableNextColumn();
                        ImGui.SetNextItemSelectionUserData(rowIndex++);
                        ImGui.Selectable("##TableRow", providerTraceCount.Selected, ImGuiSelectableFlags.SpanAllColumns);
                        ImGui.SameLine();
                        ImGui.TextUnformatted(providerTraceCount.ProviderName);
                        ImGui.TableNextColumn();
                        ImGui.TextUnformatted(providerTraceCount.Name);
                        ImGui.TableNextColumn();
                        ImGui.TextUnformatted(providerTraceCount.MaxLevel?.ToString() ?? "");
                        ImGui.TableNextColumn();
                        ImGui.TextUnformatted($"{providerTraceCount.Count:N0} ({providerTraceCount.Count * 100.0 / totalTraces:F1}%)");

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

        private unsafe void CreateExcludeRules(ViewerRules viewerRules)
        {
            // Create a rule for every provider so that it is easier for the user to manage the generated rules. It also ensures traces that use the same name across providers are distinguished.
            foreach (var selectedTraceTypes in _providerTraceCounts.Where(c => c.Selected).GroupBy(s => (ProviderName: s.ProviderName, Dummy: 0)).OrderBy(s => s.Key))
            {
                string query = "";

                if (selectedTraceTypes.Key.ProviderName != null)
                {
                    query += $"{TraceTableRowSelectorSyntax.CreateColumnVariableName(_schema.ProviderColumn)} {TraceTableRowSelectorSyntax.EqualsOperatorName} {TraceTableRowSelectorSyntax.CreateEscapedStringLiteral(selectedTraceTypes.Key.ProviderName)}";
                }

                if (query.Length > 0)
                {
                    query += $" {TraceTableRowSelectorSyntax.AndOperatorName} ";
                }

                query += $"{TraceTableRowSelectorSyntax.CreateColumnVariableName(_schema.NameColumn)} {TraceTableRowSelectorSyntax.StringInOperatorName} [{string.Join(", ", selectedTraceTypes.Select(s => TraceTableRowSelectorSyntax.CreateEscapedStringLiteral(s.Name)))}]";

                viewerRules.AddExcludeRule(query);
            }
        }

        private static IEnumerable<ProviderTraceCount> ImGuiSort(ImGuiTableColumnSortSpecsPtr spec, IEnumerable<ProviderTraceCount> list)
        {
            IEnumerable<ProviderTraceCount> ImGuiSortInternal<TKey>(ImGuiSortDirection sortDirection, IEnumerable<ProviderTraceCount> list, Func<ProviderTraceCount, TKey> keySelector)
                => sortDirection == ImGuiSortDirection.Ascending ? list.OrderBy(keySelector) : list.OrderByDescending(keySelector);

            return spec.ColumnIndex switch
            {
                0 => ImGuiSortInternal(spec.SortDirection, list, p => p.ProviderName),
                1 => ImGuiSortInternal(spec.SortDirection, list, p => p.Name),
                2 => ImGuiSortInternal(spec.SortDirection, list, p => p.MaxLevel),
                3 => ImGuiSortInternal(spec.SortDirection, list, p => p.Count),
                _ => throw new ArgumentOutOfRangeException(nameof(spec), "Unknown column index")
            };
        }

        private void ComputeCounts(ITraceTableSnapshot traceTable)
        {
            _schema = traceTable.Schema;
            _providerTraceCounts =
                Enumerable.Range(0, traceTable.RowCount)
                    .AsParallel()
                    .GroupBy(t =>
                    {
                        var name = traceTable.GetColumnString(t, traceTable.Schema.NameColumn);
                        var providerName = traceTable.Schema.ProviderColumn != null ? traceTable.GetColumnString(t, traceTable.Schema.ProviderColumn) : null;
                        return (providerName, name);
                    })
                    .Select(g => new ProviderTraceCount
                    {
                        ProviderName = g.Key.Item1,
                        Name = g.Key.Item2,
                        MaxLevel = traceTable.Schema.TimestampColumn == null ? null : g.Max(rowIndex => traceTable.GetUnifiedLevel(rowIndex)),
                        Count = g.Count()
                    })
                    // Ensure initial default sort is descending so spammy stuff is at the top.
                    .OrderByDescending(t => t.Count)
                    .ToList();
            _providerTraceCountsGenerationId = traceTable.GenerationId;
        }
    }
}
