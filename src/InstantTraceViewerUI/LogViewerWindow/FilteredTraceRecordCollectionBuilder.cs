using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
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
        protected ITraceRecordSnapshot? _lastUnfilteredTraceRecordSnapshot = null;
        protected int _errorCount = 0;

        public bool Update(ViewerRules viewerRules, ITraceRecordSnapshot newSnapshot)
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
                if (viewerRules.VisibleRules.Count == 0 || viewerRules.GetVisibleAction(i) == TraceRecordRuleAction.Include)
                {
                    /*
                    if (newSnapshot.Records[i].Level == TraceLevel.Error)
                    {
                        _errorCount++;
                    }
                    */

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
    class FilteredTraceRecordCollectionView : IReadOnlyList<int>
    {
        protected IReadOnlyList<int> _visibleRowsSnapshot;
        protected int _errorCount = 0;

        public FilteredTraceRecordCollectionView(ITraceRecordSnapshot unfilteredSnapshot, IReadOnlyList<int> visibleRowsSnapshot, int errorCount)
        {
            _visibleRowsSnapshot = visibleRowsSnapshot;
            _errorCount = errorCount;
            UnfilteredSnapshot = unfilteredSnapshot;
        }

        #region IReadOnlyList<int>
        public int this[int traceSourceIndex] => _visibleRowsSnapshot[traceSourceIndex];

        public int Count => _visibleRowsSnapshot.Count;

        public IEnumerator<int> GetEnumerator() => _visibleRowsSnapshot.GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        #endregion

        public ITraceRecordSnapshot UnfilteredSnapshot { get; init; }

        public int ErrorCount => _errorCount;
    }
}
