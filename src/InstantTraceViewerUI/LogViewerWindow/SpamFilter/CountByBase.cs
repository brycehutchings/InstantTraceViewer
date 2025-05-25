using ImGuiNET;
using InstantTraceViewer;
using System;
using System.Collections.Generic;
using System.Linq;

namespace InstantTraceViewerUI
{
    internal abstract class CountByBaseAdapter
    {
        public abstract string Name { get; }
        public abstract int ColumnCount { get; }

        public abstract void SetupColumns();
        public abstract IReadOnlyList<CountByBase> CountBy(ITraceTableSnapshot traceTable);
        public abstract bool IsSchemaSupported();
        public abstract IEnumerable<CountByBase> ImGuiSort(ImGuiTableColumnSortSpecsPtr spec, IEnumerable<CountByBase> list);
        public abstract void CreateExcludeRules(ViewerRules viewerRules, IReadOnlyCollection<CountByBase> countByEventNames);

        protected IEnumerable<CountByBase> ImGuiSortInternal<TKey>(ImGuiSortDirection sortDirection, IEnumerable<CountByBase> list, Func<CountByBase, TKey> keySelector)
             => sortDirection == ImGuiSortDirection.Ascending ? list.OrderBy(keySelector) : list.OrderByDescending(keySelector);
    }

    internal abstract class CountByBase
    {
        public int Count { get; init; }
        public bool Selected { get; set; }

        public abstract void AddColumnValues();
    }
}
