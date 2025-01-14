using ImGuiNET;
using InstantTraceViewer;
using Microsoft.Diagnostics.Tracing.Parsers.Clr;
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
        private readonly Guid _windowId;

        private abstract class CountByBaseAdapter
        {
            public abstract string Name { get; }
            public abstract int ColumnCount { get; }

            public abstract void SetupColumns(TraceTableSchema schema);
            public abstract IReadOnlyList<CountByBase> CountBy(ITraceTableSnapshot traceTable);
            public abstract bool IsSchemaSupported(TraceTableSchema schema);
            public abstract IEnumerable<CountByBase> ImGuiSort(ImGuiTableColumnSortSpecsPtr spec, IEnumerable<CountByBase> list);
            public abstract void CreateExcludeRules(TraceTableSchema schema, ViewerRules viewerRules, IReadOnlyCollection<CountByBase> countByEventNames);

            protected IEnumerable<CountByBase> ImGuiSortInternal<TKey>(ImGuiSortDirection sortDirection, IEnumerable<CountByBase> list, Func<CountByBase, TKey> keySelector)
                 => sortDirection == ImGuiSortDirection.Ascending ? list.OrderBy(keySelector) : list.OrderByDescending(keySelector);
        }

        private abstract class CountByBase
        {
            public int Count { get; init; }
            public bool Selected { get; set; }

            public abstract override bool Equals(object obj);
            public abstract override int GetHashCode();

            public abstract void AddColumnValues();
        }

        // Count by event name and optionally provider too if available.
        private class CountByEventNameAdapter : CountByBaseAdapter
        {
            private class CountByEventName : CountByBase
            {
                public string ProviderName { get; init; }
                public string Name { get; init; }
                public UnifiedLevel? MaxLevel { get; init; }

                public override void AddColumnValues()
                {
                    ImGui.TextUnformatted(ProviderName);
                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(Name);
                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(MaxLevel?.ToString() ?? "");
                }

                public override bool Equals(object obj) => obj is CountByEventName key && ProviderName == key.ProviderName && Name == key.Name;
                public override int GetHashCode() => HashCode.Combine(ProviderName, Name);
            }

            public override string Name => "Event name";

            public override int ColumnCount => 3;

            public override void SetupColumns(TraceTableSchema schema)
            {
                ImGuiTableColumnFlags DefaultHideIfColumnNull(TraceSourceSchemaColumn column) => column == null ? ImGuiTableColumnFlags.DefaultHide : 0;
                ImGui.TableSetupColumn("Provider", ImGuiTableColumnFlags.WidthStretch | DefaultHideIfColumnNull(schema.ProviderColumn), 1);
                ImGui.TableSetupColumn("Name", ImGuiTableColumnFlags.WidthStretch, 1);
                ImGui.TableSetupColumn("Level", ImGuiTableColumnFlags.WidthFixed | DefaultHideIfColumnNull(schema.UnifiedLevelColumn), 140.0f);
            }

            public override IReadOnlyList<CountByBase> CountBy(ITraceTableSnapshot traceTable)
            {
                return
                    Enumerable.Range(0, traceTable.RowCount)
                        .GroupBy(t =>
                        {
                            var name = traceTable.GetColumnString(t, traceTable.Schema.NameColumn);
                            var providerName = traceTable.Schema.ProviderColumn != null ? traceTable.GetColumnString(t, traceTable.Schema.ProviderColumn) : null;
                            return (providerName, name);
                        })
                        .Select(g => new CountByEventName
                        {
                            ProviderName = g.Key.Item1,
                            Name = g.Key.Item2,
                            MaxLevel = traceTable.Schema.TimestampColumn == null ? null : g.Max(rowIndex => traceTable.GetUnifiedLevel(rowIndex)),
                            Count = g.Count()
                        })
                        // Ensure initial default sort is descending so spammy stuff is at the top.
                        .OrderByDescending(t => t.Count)
                        .ToList();
            }

            public override bool IsSchemaSupported(TraceTableSchema schema) => schema.NameColumn != null;

            public override IEnumerable<CountByBase> ImGuiSort(ImGuiTableColumnSortSpecsPtr spec, IEnumerable<CountByBase> list)
                => spec.ColumnIndex switch
                {
                    0 => ImGuiSortInternal(spec.SortDirection, list, p => ((CountByEventName)p).ProviderName),
                    1 => ImGuiSortInternal(spec.SortDirection, list, p => ((CountByEventName)p).Name),
                    2 => ImGuiSortInternal(spec.SortDirection, list, p => ((CountByEventName)p).MaxLevel),
                    3 => ImGuiSortInternal(spec.SortDirection, list, p => ((CountByEventName)p).Count),
                    _ => throw new ArgumentOutOfRangeException(nameof(spec), "Unknown column index")
                };

            public override void CreateExcludeRules(TraceTableSchema schema, ViewerRules viewerRules, IReadOnlyCollection<CountByBase> countByEventNames)
            {
                // Create a rule for every provider so that it is easier for the user to manage the generated rules. It also ensures traces that use the same name across providers are distinguished.
                foreach (var selectedTraceTypes in countByEventNames.Cast<CountByEventName>().Where(c => c.Selected).GroupBy(s => (ProviderName: s.ProviderName, Dummy: 0)).OrderBy(s => s.Key))
                {
                    string query = "";

                    if (selectedTraceTypes.Key.ProviderName != null)
                    {
                        query += $"{TraceTableRowSelectorSyntax.CreateColumnVariableName(schema.ProviderColumn)} {TraceTableRowSelectorSyntax.EqualsOperatorName} {TraceTableRowSelectorSyntax.CreateEscapedStringLiteral(selectedTraceTypes.Key.ProviderName)}";
                    }

                    if (query.Length > 0)
                    {
                        query += $" {TraceTableRowSelectorSyntax.AndOperatorName} ";
                    }

                    query += $"{TraceTableRowSelectorSyntax.CreateColumnVariableName(schema.NameColumn)} {TraceTableRowSelectorSyntax.StringInOperatorName} [{string.Join(", ", selectedTraceTypes.Select(s => TraceTableRowSelectorSyntax.CreateEscapedStringLiteral(s.Name)))}]";

                    viewerRules.AddExcludeRule(query);
                }
            }
        }

        private class CountByProviderAdapter : CountByBaseAdapter
        {
            private class CountByProvider : CountByBase
            {
                public string ProviderName { get; init; }
                public UnifiedLevel? Level { get; init; }
                public override void AddColumnValues()
                {
                    ImGui.TextUnformatted(ProviderName);
                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(Level?.ToString() ?? "");
                }
                public override bool Equals(object obj) => obj is CountByProvider key && ProviderName == key.ProviderName && Level == key.Level;
                public override int GetHashCode() => HashCode.Combine(ProviderName, Level);
            }

            public override string Name => "Provider";

            public override int ColumnCount => 2;

            public override void SetupColumns(TraceTableSchema schema)
            {
                ImGuiTableColumnFlags DefaultHideIfColumnNull(TraceSourceSchemaColumn column) => column == null ? ImGuiTableColumnFlags.DefaultHide : 0;
                ImGui.TableSetupColumn("Provider", ImGuiTableColumnFlags.WidthStretch, 1);
                ImGui.TableSetupColumn("Level", ImGuiTableColumnFlags.WidthFixed | DefaultHideIfColumnNull(schema.UnifiedLevelColumn), 140.0f);
            }

            public override IReadOnlyList<CountByBase> CountBy(ITraceTableSnapshot traceTable)
            {
                return
                    Enumerable.Range(0, traceTable.RowCount)
                        .GroupBy(t =>
                        {
                            var providerName = traceTable.GetColumnString(t, traceTable.Schema.ProviderColumn);
                            var level = traceTable.Schema.UnifiedLevelColumn != null ? traceTable.GetUnifiedLevel(t) : (UnifiedLevel?)null;
                            return (providerName, level);
                        })
                        .Select(g => new CountByProvider
                        {
                            ProviderName = g.Key.Item1,
                            Level = g.Key.Item2,
                            Count = g.Count()
                        })
                        // Ensure initial default sort is descending so spammy stuff is at the top.
                        .OrderByDescending(t => t.Count)
                        .ToList();
            }

            public override bool IsSchemaSupported(TraceTableSchema schema) => schema.ProviderColumn != null;

            public override IEnumerable<CountByBase> ImGuiSort(ImGuiTableColumnSortSpecsPtr spec, IEnumerable<CountByBase> list)
                => spec.ColumnIndex switch
                {
                    0 => ImGuiSortInternal(spec.SortDirection, list, p => ((CountByProvider)p).ProviderName),
                    1 => ImGuiSortInternal(spec.SortDirection, list, p => ((CountByProvider)p).Level),
                    2 => ImGuiSortInternal(spec.SortDirection, list, p => ((CountByProvider)p).Count),
                    _ => throw new ArgumentOutOfRangeException(nameof(spec), "Unknown column index")
                };

            public override void CreateExcludeRules(TraceTableSchema schema, ViewerRules viewerRules, IReadOnlyCollection<CountByBase> countByEventNames)
            {
                // Create a rule for every provider so that it is easier for the user to manage the generated rules.
                foreach (var providerCount in countByEventNames.Cast<CountByProvider>().Where(c => c.Selected).GroupBy(s => (ProviderName: s.ProviderName, Dummy: 0)).OrderBy(s => s.Key))
                {
                    string query = $"{TraceTableRowSelectorSyntax.CreateColumnVariableName(schema.ProviderColumn)} {TraceTableRowSelectorSyntax.EqualsOperatorName} {TraceTableRowSelectorSyntax.CreateEscapedStringLiteral(providerCount.Key.ProviderName)}";

                    var levelStrings = providerCount.Where(pc => pc.Level.HasValue).Select(pc => pc.Level.Value.ToString()).ToList();
                    if (levelStrings.Count == 1)
                    {
                        query += $" {TraceTableRowSelectorSyntax.AndOperatorName} {TraceTableRowSelectorSyntax.CreateColumnVariableName(schema.UnifiedLevelColumn)} {TraceTableRowSelectorSyntax.EqualsOperatorName} {levelStrings.Single()}";
                    }
                    else if (levelStrings.Count > 1)
                    {
                        query += $" {TraceTableRowSelectorSyntax.AndOperatorName} {TraceTableRowSelectorSyntax.CreateColumnVariableName(schema.UnifiedLevelColumn)} in [{string.Join(", ", levelStrings)}]";
                    }

                    viewerRules.AddExcludeRule(query);
                }
            }
        }

        private readonly IReadOnlyList<CountByBaseAdapter> _adapters;
        private CountByBaseAdapter _currentAdapter;
        private IReadOnlyCollection<CountByBase> _eventCounts = null;

        private int _providerTraceCountsGenerationId = -1;

        private TraceTableSchema _schema;

        private bool _open = true;

        public SpamFilterWindow(string name, Guid windowId)
        {
            _name = name;
            _windowId = windowId;

            _adapters = [new CountByProviderAdapter(), new CountByEventNameAdapter()];
            _currentAdapter = _adapters.First();
        }

        public unsafe bool DrawWindow(IUiCommands uiCommands, ViewerRules viewerRules, ITraceTableSnapshot traceTable)
        {
            ImGui.SetNextWindowSize(new Vector2(800, 400), ImGuiCond.FirstUseEver);
            ImGui.SetNextWindowSizeConstraints(new Vector2(400, 150), new Vector2(float.MaxValue, float.MaxValue));

            if (ImGui.Begin($"{WindowName} - {_name}###SpamFilter_{_windowId}", ref _open))
            {
                // Create dropdown to select aggregation type.
                if (ImGui.BeginCombo("##CountByType", _currentAdapter.Name))
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
                ImGui.SameLine();
                ImGui.TextUnformatted("Group by");

                if (!_currentAdapter.IsSchemaSupported(traceTable.Schema))
                {
                    ImGui.TextUnformatted($"Cannot group by {_currentAdapter.Name} because the schema does not have the required columns.");
                    return _open;
                }

                if (_eventCounts == null || _providerTraceCountsGenerationId != traceTable.GenerationId)
                {
                    _schema = traceTable.Schema;
                    _eventCounts = _currentAdapter.CountBy(traceTable);
                    _providerTraceCountsGenerationId = traceTable.GenerationId;
                }

                int totalTraces = _eventCounts.Sum(c => c.Count);
                var selectedCount = _eventCounts.Count(c => c.Selected);
                var selectedTraceCount = _eventCounts.Where(c => c.Selected).Sum(c => c.Count);

                ImGui.BeginDisabled(selectedCount == 0);
                if (ImGui.Button($"Exclude selected events"))
                {
                    _currentAdapter.CreateExcludeRules(_schema, viewerRules, _eventCounts);
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

                if (ImGui.BeginTable("TraceCounts", 1 + _currentAdapter.ColumnCount /* columns */,
                        ImGuiTableFlags.ScrollY | ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersOuter | ImGuiTableFlags.Hideable |
                        ImGuiTableFlags.BordersV | ImGuiTableFlags.Resizable | ImGuiTableFlags.Sortable | ImGuiTableFlags.SortMulti))
                {
                    ImGui.TableSetupScrollFreeze(0, 1); // Top row is always visible.
                    _currentAdapter.SetupColumns(_schema);
                    ImGui.TableSetupColumn("Count", ImGuiTableColumnFlags.WidthFixed | ImGuiTableColumnFlags.PreferSortDescending | ImGuiTableColumnFlags.DefaultSort, 140.0f);
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
