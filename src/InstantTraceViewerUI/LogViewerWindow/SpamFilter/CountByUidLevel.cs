using Hexa.NET.ImGui;
using InstantTraceViewer;
using System;
using System.Collections.Generic;
using System.Linq;

namespace InstantTraceViewerUI
{
    // Groups events by UID and optionally by unified level. Intended primarily for logcat, but
    // works for any schema that exposes a UidColumn.
    internal class CountByUidLevelAdapter : CountByBaseAdapter
    {
        private class CountByUidLevel : CountByBase
        {
            public int Uid { get; init; }
            public UnifiedLevel? Level { get; init; }

            // Display-only combined "<uid> (<name>)" string.
            public string UidDisplayName { get; init; }

            public bool IncludeLevelColumn { get; init; }

            public override void AddColumnValues()
            {
                ImGui.TextUnformatted(UidDisplayName);
                if (IncludeLevelColumn)
                {
                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(Level?.ToString() ?? "");
                }
            }
        }

        private readonly TraceTableSchema _schema;
        private readonly bool _includeLevelColumn;

        public CountByUidLevelAdapter(TraceTableSchema schema, bool includeLevelColumn = false)
        {
            _schema = schema;
            _includeLevelColumn = includeLevelColumn;
        }

        public override string Name => _includeLevelColumn ? $"{UidColumnName}, {LevelColumnName}" : UidColumnName;

        public override int ColumnCount => _includeLevelColumn ? 2 : 1;

        public override void SetupColumns()
        {
            ImGui.TableSetupColumn(UidColumnName, ImGuiTableColumnFlags.WidthStretch, 1);
            if (_includeLevelColumn)
            {
                ImGui.TableSetupColumn(LevelColumnName, ImGuiTableColumnFlags.WidthFixed, 8 * ImGui.GetFontSize());
            }
        }

        public override IReadOnlyList<CountByBase> CountBy(ITraceTableSnapshot traceTable)
        {
            string OptParenthesisIfNeeded(string value) => string.IsNullOrEmpty(value) ? "" : $" ({value})";
            return
                Enumerable.Range(0, traceTable.RowCount)
                    .Select(t =>
                    {
                        var uid = traceTable.GetColumnValueInt(t, traceTable.Schema.UidColumn);
                        var uidName = traceTable.GetColumnValueNameForId(t, traceTable.Schema.UidColumn);
                        var level = _includeLevelColumn ? traceTable.GetUnifiedLevel(t) : (UnifiedLevel?)null;
                        return (uid, uidName, level);
                    })
                    .GroupBy(t => (t.uid, t.level))
                    .Select(g => new CountByUidLevel
                    {
                        Uid = g.Key.uid,
                        Level = g.Key.level,
                        UidDisplayName = g.Key.uid < 0 ? "" : g.Key.uid.ToString() + OptParenthesisIfNeeded(g.Select(v => v.uidName).FirstOrDefault()),
                        IncludeLevelColumn = _includeLevelColumn,
                        Count = g.Count(),
                    })
                    // Ensure initial default sort is descending so spammy stuff is at the top.
                    .OrderByDescending(t => t.Count)
                    .ToList();
        }

        public override bool IsSchemaSupported()
            => _schema.UidColumn != null && (!_includeLevelColumn || _schema.UnifiedLevelColumn != null);

        public override IEnumerable<CountByBase> ImGuiSort(ImGuiTableColumnSortSpecsPtr spec, IEnumerable<CountByBase> list)
        {
            if (_includeLevelColumn)
            {
                return spec.ColumnIndex switch
                {
                    0 => ImGuiSortInternal(spec.SortDirection, list, p => ((CountByUidLevel)p).UidDisplayName),
                    1 => ImGuiSortInternal(spec.SortDirection, list, p => ((CountByUidLevel)p).Level),
                    2 => ImGuiSortInternal(spec.SortDirection, list, p => ((CountByUidLevel)p).Count),
                    _ => throw new ArgumentOutOfRangeException(nameof(spec), "Unknown column index"),
                };
            }

            return spec.ColumnIndex switch
            {
                0 => ImGuiSortInternal(spec.SortDirection, list, p => ((CountByUidLevel)p).UidDisplayName),
                1 => ImGuiSortInternal(spec.SortDirection, list, p => ((CountByUidLevel)p).Count),
                _ => throw new ArgumentOutOfRangeException(nameof(spec), "Unknown column index"),
            };
        }

        public override void CreateRules(ViewerRules viewerRules, IReadOnlyCollection<CountByBase> countByEventNames, TraceRowRuleAction ruleAction)
        {
            var selectedUids = countByEventNames.Cast<CountByUidLevel>()
                .Where(c => c.Selected)
                .GroupBy(c => c.Uid)
                .OrderBy(c => c.Key);

            if (_includeLevelColumn)
            {
                // One rule per uid to keep generated rules easy to manage.
                foreach (var selectedUid in selectedUids)
                {
                    var levelStrings = selectedUid.Where(c => c.Level.HasValue).Select(c => c.Level.Value.ToString()).Distinct().ToList();
                    string query = $"{TraceTableRowSelectorSyntax.CreateColumnVariableName(_schema.UidColumn)} {TraceTableRowSelectorSyntax.EqualsOperatorName} {selectedUid.Key}";
                    if (levelStrings.Count == 1)
                    {
                        query += $" {TraceTableRowSelectorSyntax.AndOperatorName} {TraceTableRowSelectorSyntax.CreateColumnVariableName(_schema.UnifiedLevelColumn)} {TraceTableRowSelectorSyntax.EqualsOperatorName} {levelStrings.Single()}";
                    }
                    else if (levelStrings.Count > 1)
                    {
                        query += $" {TraceTableRowSelectorSyntax.AndOperatorName} {TraceTableRowSelectorSyntax.CreateColumnVariableName(_schema.UnifiedLevelColumn)} {TraceTableRowSelectorSyntax.InOperatorName} [{string.Join(", ", levelStrings)}]";
                    }
                    viewerRules.AddRule(query, ruleAction);
                }
            }
            else
            {
                // Single rule listing all selected UIDs.
                string uidList = string.Join(", ", selectedUids.Select(c => c.Key));
                string query = $"{TraceTableRowSelectorSyntax.CreateColumnVariableName(_schema.UidColumn)} {TraceTableRowSelectorSyntax.InOperatorName} [{uidList}]";
                viewerRules.AddRule(query, ruleAction);
            }
        }

        private string UidColumnName => _schema.UidColumn?.Name ?? "Uid";
        private string LevelColumnName => _schema.UnifiedLevelColumn?.Name ?? "Level";
    }
}
