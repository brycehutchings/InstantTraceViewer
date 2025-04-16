using System;
using InstantTraceViewer;

namespace InstantTraceViewerUI.Perfetto
{
    class PerfettoTraceTableSnapshot : ITraceTableSnapshot
    {
        public ListBuilderSnapshot<PerfettoRecord> RecordSnapshot { get; init; }

        #region ITraceRecordSnapshot
        public TraceTableSchema Schema { get; init; }

        public int RowCount => RecordSnapshot.Count;

        public int GenerationId { get; init; }

        public string GetColumnValueString(int rowIndex, TraceSourceSchemaColumn column, bool allowMultiline = false)
        {
            PerfettoRecord traceRecord = RecordSnapshot[rowIndex];

            if (column == PerfettoTraceSource.ColumnProcess)
            {
                return
                    traceRecord.Pid == -1 ? string.Empty :
                    !string.IsNullOrEmpty(traceRecord.ProcessName) ? $"{traceRecord.Pid} ({traceRecord.ProcessName})" : traceRecord.Pid.ToString();
            }
            else if (column == PerfettoTraceSource.ColumnThread)
            {
                return
                    traceRecord.Tid == -1 ? string.Empty :
                    !string.IsNullOrEmpty(traceRecord.ThreadName) ? $"{traceRecord.Tid} ({traceRecord.ThreadName})" : traceRecord.Tid.ToString();
            }
            else if (column == PerfettoTraceSource.ColumnCategory)
            {
                return traceRecord.Category == Category.None ? "" :
                    traceRecord.Category.ToString();
            }
            else if (column == PerfettoTraceSource.ColumnPriority)
            {
                return traceRecord.Priority.ToString();
            }
            else if (column == PerfettoTraceSource.ColumnSource)
            {
                return traceRecord.Source.ToString();
            }
            else if (column == PerfettoTraceSource.ColumnTime)
            {
                return FriendlyStringify.ToString(traceRecord.Timestamp);
            }
            else if (column == PerfettoTraceSource.ColumnName)
            {
                return traceRecord.Name;
            }
            else if (column == PerfettoTraceSource.ColumnData)
            {
                return NamedValue.GetCollectionString(traceRecord.NamedValues, allowMultiline);
            }

            throw new NotImplementedException();
        }
        public string GetColumnValueNameForId(int rowIndex, TraceSourceSchemaColumn column)
            => column == PerfettoTraceSource.ColumnProcess ? RecordSnapshot[rowIndex].ProcessName :
               column == PerfettoTraceSource.ColumnThread ? null :
               throw new NotSupportedException();


        public DateTime GetColumnValueDateTime(int rowIndex, TraceSourceSchemaColumn column)
            => column == PerfettoTraceSource.ColumnTime ? RecordSnapshot[rowIndex].Timestamp :
               throw new NotSupportedException();

        public int GetColumnValueInt(int rowIndex, TraceSourceSchemaColumn column)
            => column == PerfettoTraceSource.ColumnProcess ? RecordSnapshot[rowIndex].Pid :
               column == PerfettoTraceSource.ColumnThread ? RecordSnapshot[rowIndex].Tid :
               throw new NotSupportedException();

        public UnifiedLevel GetColumnValueUnifiedLevel(int rowIndex, TraceSourceSchemaColumn column)
            => column == PerfettoTraceSource.ColumnPriority ? ConvertCategoryToLevel(RecordSnapshot[rowIndex].Priority) :
               throw new NotSupportedException();

        public UnifiedOpcode GetColumnValueUnifiedOpcode(int rowIndex, TraceSourceSchemaColumn column)
            => RecordSnapshot[rowIndex].Category switch
            {
                Category.Begin => UnifiedOpcode.Start,
                Category.End => UnifiedOpcode.Stop,
                _ => UnifiedOpcode.None
            };

        private UnifiedLevel ConvertCategoryToLevel(Priority priority)
            => priority == Priority.Verbose ? UnifiedLevel.Verbose :
               priority == Priority.Debug ? UnifiedLevel.Verbose :
               priority == Priority.Info ? UnifiedLevel.Info :
               priority == Priority.Warning ? UnifiedLevel.Warning :
               priority == Priority.Error ? UnifiedLevel.Error :
               priority == Priority.Fatal ? UnifiedLevel.Fatal : UnifiedLevel.Verbose;
        #endregion
    }
}