using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using InstantTraceViewer;

namespace InstantTraceViewerUI
{
    // This object tracks the list of trace records that are included by the user's rules. It can update itself
    // incrementally to avoid having to rebuild the view from scratch every frame.
    // It is mutable, and is not thread-safe. In fact it can't even be read from and a snapshot must be created to do that.
    class FilteredTraceRecordCollectionBuilder
    {
        private ListBuilder<int> _visibleRowsBuilder = new();
        private int _lastViewerRulesGenerationId = -1;
        protected TraceRecordSnapshot _lastUnfilteredTraceRecordSnapshot = new TraceRecordSnapshot();
        protected int _errorCount = 0;

        public bool Update(ViewerRules viewerRules, TraceRecordSnapshot newSnapshot)
        {
            bool rebuildFilteredView =
                newSnapshot.GenerationId != _lastUnfilteredTraceRecordSnapshot.GenerationId ||
                viewerRules.GenerationId != _lastViewerRulesGenerationId;
            if (rebuildFilteredView)
            {
                Debug.WriteLine("Rebuilding visible rows...");
                _visibleRowsBuilder = new ListBuilder<int>();
                _lastUnfilteredTraceRecordSnapshot = new TraceRecordSnapshot();
                _errorCount = 0;
            }

            for (int i = _lastUnfilteredTraceRecordSnapshot.Records.Count; i < newSnapshot.Records.Count; i++)
            {
                if (viewerRules.VisibleRules.Count == 0 || viewerRules.GetVisibleAction(newSnapshot.Records[i]) == TraceRecordRuleAction.Include)
                {
                    if (newSnapshot.Records[i].Level == TraceLevel.Error)
                    {
                        _errorCount++;
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

        public FilteredTraceRecordCollectionView Snapshot()
        {
            return new FilteredTraceRecordCollectionView(_lastUnfilteredTraceRecordSnapshot, _visibleRowsBuilder.CreateSnapshot(), _errorCount);
        }
    }

    /// <summary>
    /// A read-only view of the trace records that are included by the user's rules.
    /// </summary>
    class FilteredTraceRecordCollectionView : IReadOnlyList<TraceRecord>
    {
        protected IReadOnlyList<int> _visibleRowsSnapshot = new int[0];
        protected TraceRecordSnapshot _unfilteredSnapshot = new TraceRecordSnapshot();
        protected int _errorCount = 0;

        public FilteredTraceRecordCollectionView(TraceRecordSnapshot unfilteredSnapshot, IReadOnlyList<int> visibleRowsSnapshot, int errorCount)
        {
            _visibleRowsSnapshot = visibleRowsSnapshot;
            _unfilteredSnapshot = unfilteredSnapshot;
            _errorCount = errorCount;
        }

        #region IReadOnlyList<TraceRecord>
        public TraceRecord this[int index] => _unfilteredSnapshot.Records[GetRecordId(index)];

        public int GetRecordId(int index) => _visibleRowsSnapshot[index];

        public TraceRecord GetRecordFromId(int recordId) => _unfilteredSnapshot.Records[recordId];

        public int Count => _visibleRowsSnapshot.Count;

        public IEnumerator<TraceRecord> GetEnumerator() => _visibleRowsSnapshot.Select(i => _unfilteredSnapshot.Records[i]).GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        #endregion

        public int ErrorCount => _errorCount;

        public int UnfilteredCount => _unfilteredSnapshot.Records.Count;
    }
}
