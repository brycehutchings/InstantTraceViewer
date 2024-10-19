using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace InstantTraceViewerUI
{
    class FilteredTraceRecordCollection : IReadOnlyList<TraceRecord>
    {
        private readonly List<int> _visibleRows = new();

        private TraceRecordSnapshot _unfilteredSnapshot = new TraceRecordSnapshot();

        private int _errorCount = 0;
        private int _viewerRulesGenerationId = -1;

        #region IReadOnlyList<TraceRecord>
        public TraceRecord this[int index] => _unfilteredSnapshot.Records[GetRecordId(index)];

        public int GetRecordId(int index) => _visibleRows[index];

        public TraceRecord GetRecordFromId(int recordId) => _unfilteredSnapshot.Records[recordId];

        public int Count => _visibleRows.Count;

        public IEnumerator<TraceRecord> GetEnumerator() => _visibleRows.Select(i => _unfilteredSnapshot.Records[i]).GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        #endregion

        public int ErrorCount => _errorCount;

        public int UnfilteredCount => _unfilteredSnapshot.Records.Count;

        public bool Update(ViewerRules viewerRules, TraceRecordSnapshot newSnapshot)
        {
            bool rebuildFilteredView =
                newSnapshot.GenerationId != _unfilteredSnapshot.GenerationId ||
                viewerRules.GenerationId != _viewerRulesGenerationId;
            if (rebuildFilteredView)
            {
                Debug.WriteLine("Rebuilding visible rows...");
                _visibleRows.Clear();
                _unfilteredSnapshot = new TraceRecordSnapshot();
                _errorCount = 0;
            }

            for (int i = _unfilteredSnapshot.Records.Count; i < newSnapshot.Records.Count; i++)
            {
                if (viewerRules.VisibleRules.Count == 0 || viewerRules.GetVisibleAction(newSnapshot.Records[i]) == TraceRecordRuleAction.Include)
                {
                    if (newSnapshot.Records[i].Level == TraceLevel.Error)
                    {
                        _errorCount++;
                    }

                    _visibleRows.Add(i);
                }
            }

            if (rebuildFilteredView)
            {
                Debug.WriteLine("Done rebuilding visible rows.");
            }

            _unfilteredSnapshot = newSnapshot;
            _viewerRulesGenerationId = viewerRules.GenerationId;

            return rebuildFilteredView;
        }
    }
}
