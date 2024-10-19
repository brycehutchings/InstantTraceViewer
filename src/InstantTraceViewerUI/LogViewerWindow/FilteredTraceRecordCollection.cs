using System.Collections;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Linq;

namespace InstantTraceViewerUI
{
    // This object is a view of a list of trace records, filtered by the user's rules. It can update itself
    // incrementally to avoid having to rebuild the view from scratch every frame.
    // It is mutable, and is not thread-safe. If something needs to operate on it, it should be cloned first.
    class FilteredTraceRecordCollection : IReadOnlyList<TraceRecord>
    {
        private ImmutableList<int>.Builder _visibleRows = ImmutableList.CreateBuilder<int>();
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

        // This is not thread-safe. It must be called on the main thread.
        public FilteredTraceRecordCollection Clone()
        {
            return new FilteredTraceRecordCollection
            {
                _unfilteredSnapshot = _unfilteredSnapshot,
                _visibleRows = _visibleRows.ToImmutableList().ToBuilder(),
                _errorCount = _errorCount,
                _viewerRulesGenerationId = _viewerRulesGenerationId
            };
        }
    }
}
