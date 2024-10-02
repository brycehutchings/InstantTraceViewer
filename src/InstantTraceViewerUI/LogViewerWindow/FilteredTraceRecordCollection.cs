using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;

namespace InstantTraceViewerUI
{
    class FilteredTraceRecordCollection : IReadOnlyList<TraceRecord>
    {
        private readonly List<int> _visibleRows = new();

        private IReadOnlyList<TraceRecord> _unfilteredTraceRecords;
        private int _nextTraceSourceRowIndex = 0;
        private int _errorCount = 0;
        private int _unfilteredCount = 0;
        private int _unfilteredTraceRecordGenerationId = -1;
        private int _viewerRulesGenerationId = -1;

        #region IReadOnlyList<TraceRecord>
        public TraceRecord this[int index] => _unfilteredTraceRecords[GetRecordId(index)];

        public int GetRecordId(int index) => _visibleRows[index];

        public TraceRecord GetRecordFromId(int recordId) => _unfilteredTraceRecords[recordId];

        public int Count => _visibleRows.Count;

        public IEnumerator<TraceRecord> GetEnumerator() => throw new NotImplementedException();
        IEnumerator IEnumerable.GetEnumerator() => throw new NotImplementedException();
        #endregion

        public int ErrorCount => _errorCount;

        public int UnfilteredCount => _unfilteredCount;

        public void Update(ViewerRules viewerRules, int generationId, IReadOnlyList<TraceRecord> traceRecords)
        {
            if (generationId != _unfilteredTraceRecordGenerationId ||
                viewerRules.GenerationId != _viewerRulesGenerationId)
            {
                Debug.WriteLine("Rebuilding visible rows...");
                _visibleRows.Clear();
                _nextTraceSourceRowIndex = 0;
                _errorCount = 0;
            }

            int? scrollPosition = null;
            for (int i = _nextTraceSourceRowIndex; i < traceRecords.Count; i++)
            {
                if (viewerRules.VisibleRules.Count == 0 || viewerRules.GetVisibleAction(traceRecords[i]) == TraceRecordRuleAction.Include)
                {
                    if (traceRecords[i].Level == TraceLevel.Error)
                    {
                        _errorCount++;
                    }

                    _visibleRows.Add(i);
                }
            }

            if (_nextTraceSourceRowIndex == 0)
            {
                Debug.WriteLine("Done rebuilding visible rows.");
            }

            _viewerRulesGenerationId = viewerRules.GenerationId;
            _unfilteredTraceRecords = traceRecords;
            _unfilteredTraceRecordGenerationId = generationId;
            _nextTraceSourceRowIndex = traceRecords.Count;
            _unfilteredCount = traceRecords.Count;
        }
    }
}
