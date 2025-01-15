using ImGuiNET;
using InstantTraceViewer;
using System;
using System.Collections.Generic;
using System.Linq;

namespace InstantTraceViewerUI
{
    internal class CountByProviderAdapter : CountByBaseAdapter
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

        private readonly TraceTableSchema _schema;

        public CountByProviderAdapter(TraceTableSchema schema)
        {
            _schema = schema;
        }

        public override string Name => $"{ProviderColumnName}, {LevelColumnName}";

        public override int ColumnCount => 2;

        public override void SetupColumns()
        {
            ImGuiTableColumnFlags DefaultHideIfColumnNull(TraceSourceSchemaColumn column) => column == null ? ImGuiTableColumnFlags.DefaultHide : 0;
            ImGui.TableSetupColumn(ProviderColumnName, ImGuiTableColumnFlags.WidthStretch, 1);
            ImGui.TableSetupColumn(LevelColumnName, ImGuiTableColumnFlags.WidthFixed | DefaultHideIfColumnNull(_schema.UnifiedLevelColumn), 8 * ImGui.GetFontSize());
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

        public override bool IsSchemaSupported() => _schema.ProviderColumn != null;

        public override IEnumerable<CountByBase> ImGuiSort(ImGuiTableColumnSortSpecsPtr spec, IEnumerable<CountByBase> list)
            => spec.ColumnIndex switch
            {
                0 => ImGuiSortInternal(spec.SortDirection, list, p => ((CountByProvider)p).ProviderName),
                1 => ImGuiSortInternal(spec.SortDirection, list, p => ((CountByProvider)p).Level),
                2 => ImGuiSortInternal(spec.SortDirection, list, p => ((CountByProvider)p).Count),
                _ => throw new ArgumentOutOfRangeException(nameof(spec), "Unknown column index")
            };

        public override void CreateExcludeRules(ViewerRules viewerRules, IReadOnlyCollection<CountByBase> countByEventNames)
        {
            // Create a rule for every provider so that it is easier for the user to manage the generated rules.
            foreach (var providerCount in countByEventNames.Cast<CountByProvider>().Where(c => c.Selected).GroupBy(s => (ProviderName: s.ProviderName, Dummy: 0)).OrderBy(s => s.Key))
            {
                string query = $"{TraceTableRowSelectorSyntax.CreateColumnVariableName(_schema.ProviderColumn)} {TraceTableRowSelectorSyntax.EqualsOperatorName} {TraceTableRowSelectorSyntax.CreateEscapedStringLiteral(providerCount.Key.ProviderName)}";

                var levelStrings = providerCount.Where(pc => pc.Level.HasValue).Select(pc => pc.Level.Value.ToString()).ToList();
                if (levelStrings.Count == 1)
                {
                    query += $" {TraceTableRowSelectorSyntax.AndOperatorName} {TraceTableRowSelectorSyntax.CreateColumnVariableName(_schema.UnifiedLevelColumn)} {TraceTableRowSelectorSyntax.EqualsOperatorName} {levelStrings.Single()}";
                }
                else if (levelStrings.Count > 1)
                {
                    query += $" {TraceTableRowSelectorSyntax.AndOperatorName} {TraceTableRowSelectorSyntax.CreateColumnVariableName(_schema.UnifiedLevelColumn)} in [{string.Join(", ", levelStrings)}]";
                }

                viewerRules.AddExcludeRule(query);
            }
        }

        // Even though Provider is not optional, we still need to handle the case where it is not present for showing a Name.
        private string ProviderColumnName => _schema.ProviderColumn?.Name ?? "Provider";
        private string LevelColumnName => _schema.UnifiedLevelColumn?.Name ?? "Level";
    }
}
