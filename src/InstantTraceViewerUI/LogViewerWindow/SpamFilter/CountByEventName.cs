using ImGuiNET;
using InstantTraceViewer;
using System;
using System.Collections.Generic;
using System.Linq;

namespace InstantTraceViewerUI
{
    // Count by event name and optionally provider too if available.
    internal class CountByEventNameAdapter : CountByBaseAdapter
    {
        private class CountByEventName : CountByBase
        {
            public string ProviderName { get; init; }
            public string Name { get; init; }

            // MaxLevel is just for display purposes, it is not used for aggregation.
            public UnifiedLevel? MaxLevel { get; init; }

            public override void AddColumnValues()
            {
                ImGui.TextUnformatted(Name);
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(ProviderName);
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(MaxLevel?.ToString() ?? "");
            }

            public override bool Equals(object obj) => obj is CountByEventName key && ProviderName == key.ProviderName && Name == key.Name;
            public override int GetHashCode() => HashCode.Combine(ProviderName, Name);
        }

        private readonly TraceTableSchema _schema;

        public CountByEventNameAdapter(TraceTableSchema schema)
        {
            _schema = schema;
        }

        public override string Name => $"{NameColumnName}, {ProviderColumnName}";

        public override int ColumnCount => 3;

        public override void SetupColumns()
        {
            ImGuiTableColumnFlags DefaultHideIfColumnNull(TraceSourceSchemaColumn column) => column == null ? ImGuiTableColumnFlags.DefaultHide : 0;
            ImGui.TableSetupColumn(NameColumnName, ImGuiTableColumnFlags.WidthStretch, 1);
            ImGui.TableSetupColumn(ProviderColumnName, ImGuiTableColumnFlags.WidthStretch | DefaultHideIfColumnNull(_schema.ProviderColumn), 1);
            ImGui.TableSetupColumn(LevelColumnName, ImGuiTableColumnFlags.WidthFixed | DefaultHideIfColumnNull(_schema.UnifiedLevelColumn), 8 * ImGui.GetFontSize());
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

        public override bool IsSchemaSupported() => _schema.NameColumn != null;

        public override IEnumerable<CountByBase> ImGuiSort(ImGuiTableColumnSortSpecsPtr spec, IEnumerable<CountByBase> list)
            => spec.ColumnIndex switch
            {
                0 => ImGuiSortInternal(spec.SortDirection, list, p => ((CountByEventName)p).Name),
                1 => ImGuiSortInternal(spec.SortDirection, list, p => ((CountByEventName)p).ProviderName),
                2 => ImGuiSortInternal(spec.SortDirection, list, p => ((CountByEventName)p).MaxLevel),
                3 => ImGuiSortInternal(spec.SortDirection, list, p => ((CountByEventName)p).Count),
                _ => throw new ArgumentOutOfRangeException(nameof(spec), "Unknown column index")
            };

        public override void CreateExcludeRules(ViewerRules viewerRules, IReadOnlyCollection<CountByBase> countByEventNames)
        {
            // Create a rule for every provider so that it is easier for the user to manage the generated rules. It also ensures traces that use the same name across providers are distinguished.
            foreach (var selectedTraceTypes in countByEventNames.Cast<CountByEventName>().Where(c => c.Selected).GroupBy(s => (ProviderName: s.ProviderName, Dummy: 0)).OrderBy(s => s.Key))
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

        // Even though Name is not optional, we still need to handle the case where it is not present for showing a Name.
        private string NameColumnName => _schema.NameColumn?.Name ?? "Name";
        private string ProviderColumnName => _schema.ProviderColumn?.Name ?? "Provider";
        private string LevelColumnName => _schema.UnifiedLevelColumn?.Name ?? "Level";
    }
}
