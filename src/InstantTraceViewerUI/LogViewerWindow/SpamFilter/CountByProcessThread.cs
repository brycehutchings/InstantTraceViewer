using ImGuiNET;
using InstantTraceViewer;
using System;
using System.Collections.Generic;
using System.Linq;

namespace InstantTraceViewerUI
{
    internal class CountByProcessThreadAdapter : CountByBaseAdapter
    {
        private class CountByProcessThread : CountByBase
        {
            public int ProcessId { get; init; }
            public int? ThreadId { get; init; }

            // Name is just for display purposes, not aggregation or comparison.
            public string ProcessName { get; init; }
            public string? ThreadName { get; init; }

            public bool IncludeThreadColumn { get; init; }

            public override void AddColumnValues()
            {
                ImGui.TextUnformatted(ProcessName);
                if (IncludeThreadColumn)
                {
                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(ThreadName);
                }
            }
        }

        private readonly TraceTableSchema _schema;
        private readonly bool _includeThreadColumn;

        public CountByProcessThreadAdapter(TraceTableSchema schema, bool includeThreadColumn = false)
        {
            _schema = schema;
            _includeThreadColumn = includeThreadColumn;
        }

        public override string Name => _includeThreadColumn ? $"{ProcessColumnName}, {ThreadColumnName}" : ProcessColumnName;

        public override int ColumnCount => _includeThreadColumn ? 2 : 1;

        public override void SetupColumns()
        {
            ImGui.TableSetupColumn(ProcessColumnName, ImGuiTableColumnFlags.WidthStretch, 1);

            if (_includeThreadColumn)
            {
                ImGui.TableSetupColumn(ThreadColumnName, ImGuiTableColumnFlags.WidthStretch, 2);
            }
        }

        public override IReadOnlyList<CountByBase> CountBy(ITraceTableSnapshot traceTable)
        {
            string OptParenthesisIfNeeded(string value) => string.IsNullOrEmpty(value) ? "" : $" ({value})";
            return
                Enumerable.Range(0, traceTable.RowCount)
                    .Select(t =>
                    {
                        var processId = traceTable.GetColumnValueInt(t, traceTable.Schema.ProcessIdColumn);
                        var processName = traceTable.GetColumnValueNameForId(t, traceTable.Schema.ProcessIdColumn);
                        var threadId = _includeThreadColumn ? traceTable.GetColumnValueInt(t, traceTable.Schema.ThreadIdColumn) : 0;
                        var threadName = _includeThreadColumn ? traceTable.GetColumnValueNameForId(t, traceTable.Schema.ThreadIdColumn) : null;
                        return (processId, processName, threadId, threadName);
                    })
                    .GroupBy(t => (t.processId, t.threadId))
                    .Select(g => new CountByProcessThread
                    {
                        ProcessId = g.Key.processId,
                        ThreadId = g.Key.threadId,
                        ProcessName = g.Key.processId < 0 ? "" : g.Key.processId.ToString() + OptParenthesisIfNeeded(g.Select(g => g.processName).FirstOrDefault()),
                        ThreadName = g.Key.threadId < 0 ? "" : g.Key.threadId.ToString() + OptParenthesisIfNeeded(g.Select(g => g.threadName).FirstOrDefault()),
                        IncludeThreadColumn = _includeThreadColumn,
                        Count = g.Count()
                    })
                    // Ensure initial default sort is descending so spammy stuff is at the top.
                    .OrderByDescending(t => t.Count)
                    .ToList();
        }

        public override bool IsSchemaSupported() => _schema.ProviderColumn != null && (_includeThreadColumn || _schema.ThreadIdColumn != null);

        public override IEnumerable<CountByBase> ImGuiSort(ImGuiTableColumnSortSpecsPtr spec, IEnumerable<CountByBase> list)
            => spec.ColumnIndex switch
            {
                0 => ImGuiSortInternal(spec.SortDirection, list, p => ((CountByProcessThread)p).ProcessName),
                1 => ImGuiSortInternal(spec.SortDirection, list, p => ((CountByProcessThread)p).ThreadName),
                2 => ImGuiSortInternal(spec.SortDirection, list, p => ((CountByProcessThread)p).Count),
                _ => throw new ArgumentOutOfRangeException(nameof(spec), "Unknown column index")
            };

        public override void CreateRules(ViewerRules viewerRules, IReadOnlyCollection<CountByBase> countByEventNames, TraceRowRuleAction ruleAction)
        {
            var selectedProcessIds = countByEventNames.Cast<CountByProcessThread>().Where(c => c.Selected).GroupBy(c => c.ProcessId).OrderBy(c => c.Key);
            if (_includeThreadColumn)
            {
                // One rule per pid
                foreach (var selectedProcessId in selectedProcessIds)
                {
                    var tidList = string.Join(", ", selectedProcessId.Select(c => c.ThreadId));

                    string query = $"{TraceTableRowSelectorSyntax.CreateColumnVariableName(_schema.ProcessIdColumn)} {TraceTableRowSelectorSyntax.EqualsOperatorName} {selectedProcessId.Key}";
                    query += $" {TraceTableRowSelectorSyntax.AndOperatorName} {TraceTableRowSelectorSyntax.CreateColumnVariableName(_schema.ThreadIdColumn)} {TraceTableRowSelectorSyntax.InOperatorName} [{tidList}]";
                    viewerRules.AddRule(query, ruleAction);
                }
            }
            else
            {
                // One rule for all pids
                string pidList = string.Join(", ", selectedProcessIds.Select(c => c.Key));
                string query = $"{TraceTableRowSelectorSyntax.CreateColumnVariableName(_schema.ProcessIdColumn)} {TraceTableRowSelectorSyntax.InOperatorName} [{pidList}]";
                viewerRules.AddRule(query, ruleAction);
            }
        }

        // Even though Provider is not optional, we still need to handle the case where it is not present for showing a Name.
        private string ProcessColumnName => _schema.ProcessIdColumn.Name;
        private string ThreadColumnName => _schema.ThreadIdColumn.Name;
    }
}
