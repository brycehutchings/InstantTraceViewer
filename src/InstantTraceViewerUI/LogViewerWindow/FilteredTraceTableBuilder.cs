﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using InstantTraceViewer;

namespace InstantTraceViewerUI
{
    // This object tracks the list of rows that are included by the user's rules.
    // It updates itself incrementally to avoid having to rebuild the view from scratch every frame.
    class FilteredTraceTableBuilder
    {
        // Holds the row indices of the trace records that are included by the user's rules.
        private ListBuilder<int> _visibleRowsBuilder = new();

        private int _lastViewerRulesGenerationId = -1;
        protected ITraceTableSnapshot? _lastUnfilteredTraceRecordSnapshot = null;
        protected int _errorCount = 0;

        public bool Update(ViewerRules viewerRules, ITraceTableSnapshot newSnapshot)
        {
            bool rebuildFilteredView =
                newSnapshot.GenerationId != (_lastUnfilteredTraceRecordSnapshot?.GenerationId ?? 0) ||
                viewerRules.GenerationId != _lastViewerRulesGenerationId;
            if (rebuildFilteredView)
            {
                Debug.WriteLine("Rebuilding visible rows...");
                _visibleRowsBuilder = new ListBuilder<int>();
                _lastUnfilteredTraceRecordSnapshot = null;
                _errorCount = 0;
            }

            for (int i = (_lastUnfilteredTraceRecordSnapshot?.RowCount ?? 0); i < newSnapshot.RowCount; i++)
            {
                if (viewerRules.VisibleRules.Count == 0 || viewerRules.GetVisibleAction(newSnapshot, i) == TraceRowRuleAction.Include)
                {
                    if (newSnapshot.Schema.UnifiedLevelColumn != null)
                    {
                        UnifiedLevel level = newSnapshot.GetUnifiedLevel(i);
                        if (level == UnifiedLevel.Error || level == UnifiedLevel.Fatal)
                        {
                            _errorCount++;
                        }
                    }

                    _visibleRowsBuilder.Add(i);
                }
            }

            if (rebuildFilteredView)
            {
                Debug.WriteLine("Done rebuilding visible rows.");
            }

            _lastUnfilteredTraceRecordSnapshot = newSnapshot;
            _lastViewerRulesGenerationId = viewerRules.GenerationId;

            return rebuildFilteredView;
        }

        public FilteredTraceTableSnapshot Snapshot()
        {
            return new FilteredTraceTableSnapshot(_lastUnfilteredTraceRecordSnapshot, _visibleRowsBuilder.CreateSnapshot(), _errorCount);
        }
    }

    /// <summary>
    /// A read-only view of a full table but with only the rows that are included by the user's rules.
    /// </summary>
    class FilteredTraceTableSnapshot : ITraceTableSnapshot
    {
        private IReadOnlyList<int> _visibleRowIndiciesSnapshot;

        public FilteredTraceTableSnapshot(ITraceTableSnapshot fullTraceTableSnapshot, IReadOnlyList<int> visibleRowIndiciesSnapshot, int errorCount)
        {
            FullTable = fullTraceTableSnapshot;
            ErrorCount = errorCount;
            _visibleRowIndiciesSnapshot = visibleRowIndiciesSnapshot;
        }

        public int ErrorCount { get; private init; }

        public ITraceTableSnapshot FullTable { get; private init; }

        public int GetFullTableRowIndex(int filteredRowIndex)
            => _visibleRowIndiciesSnapshot[filteredRowIndex];

        public string GetColumnString(int filteredRowIndex, TraceSourceSchemaColumn column, bool allowMultiline = false)
            => FullTable.GetColumnString(GetFullTableRowIndex(filteredRowIndex), column, allowMultiline);

        public int GetColumnInt(int filteredRowIndex, TraceSourceSchemaColumn column)
            => FullTable.GetColumnInt(GetFullTableRowIndex(filteredRowIndex), column);

        public DateTime GetColumnDateTime(int filteredRowIndex, TraceSourceSchemaColumn column)
            => FullTable.GetColumnDateTime(GetFullTableRowIndex(filteredRowIndex), column);

        public UnifiedLevel GetColumnUnifiedLevel(int filteredRowIndex, TraceSourceSchemaColumn column)
            => FullTable.GetColumnUnifiedLevel(GetFullTableRowIndex(filteredRowIndex), column);

        public TraceTableSchema Schema
            => FullTable.Schema;

        public int RowCount
            => _visibleRowIndiciesSnapshot.Count;

        public int GenerationId
            => FullTable.GenerationId;
    }
}