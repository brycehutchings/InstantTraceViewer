using System;
using System.Collections.Generic;
using AdvancedSharpAdbClient.Logs;
using InstantTraceViewer;

namespace InstantTraceViewerUI.Logcat
{
    class LogcatTraceTableSnapshot : ITraceTableSnapshot
    {
        public IReadOnlyDictionary<int, string> ProcessNames { get; init; }

        public ListBuilderSnapshot<LogcatRecord> RecordSnapshot { get; init; }

        #region ITraceRecordSnapshot
        public TraceTableSchema Schema { get; init; }

        public int RowCount => RecordSnapshot.Count;

        public int GenerationId { get; init; }

        public string GetColumnValueString(int rowIndex, TraceSourceSchemaColumn column, bool allowMultiline = false)
        {
            LogcatRecord traceRecord = RecordSnapshot[rowIndex];

            if (column == LogcatTraceSource.ColumnProcess)
            {
                return
                    traceRecord.ProcessId == -1 ? string.Empty :
                    ProcessNames.TryGetValue(traceRecord.ProcessId, out string name) && !string.IsNullOrEmpty(name) ? $"{traceRecord.ProcessId} ({name})" : traceRecord.ProcessId.ToString();
            }
            else if (column == LogcatTraceSource.ColumnThread)
            {
                return traceRecord.ThreadId.ToString();
            }
            else if (column == LogcatTraceSource.ColumnPriority)
            {
                return traceRecord.Priority.ToString();
            }
            else if (column == LogcatTraceSource.ColumnTime)
            {
                return traceRecord.Timestamp.ToString("yyyy-MM-dd HH:mm:ss.ffffff");
            }
            else if (column == LogcatTraceSource.ColumnBufferId)
            {
                return traceRecord.LogId.ToString();
            }
            else if (column == LogcatTraceSource.ColumnTag)
            {
                return traceRecord.Tag;
            }
            else if (column == LogcatTraceSource.ColumnMessage)
            {
                return traceRecord.Message;
            }

            throw new NotImplementedException();
        }
        public string GetColumnValueNameForId(int rowIndex, TraceSourceSchemaColumn column)
            => column == LogcatTraceSource.ColumnProcess ? (ProcessNames.TryGetValue(RecordSnapshot[rowIndex].ProcessId, out string processName) ? processName : null) :
               column == LogcatTraceSource.ColumnThread ? null :
               throw new NotSupportedException();


        public DateTime GetColumnValueDateTime(int rowIndex, TraceSourceSchemaColumn column)
            => column == LogcatTraceSource.ColumnTime ? RecordSnapshot[rowIndex].Timestamp :
               throw new NotSupportedException();

        public int GetColumnValueInt(int rowIndex, TraceSourceSchemaColumn column)
            => column == LogcatTraceSource.ColumnProcess ? RecordSnapshot[rowIndex].ProcessId :
               column == LogcatTraceSource.ColumnThread ? RecordSnapshot[rowIndex].ThreadId :
               throw new NotSupportedException();

        public UnifiedLevel GetColumnValueUnifiedLevel(int rowIndex, TraceSourceSchemaColumn column)
            => column == LogcatTraceSource.ColumnPriority ? ConvertPriority(RecordSnapshot[rowIndex].Priority) :
               throw new NotSupportedException();

        private UnifiedLevel ConvertPriority(Priority priority)
            => priority == Priority.Fatal ? UnifiedLevel.Fatal :
               priority == Priority.Error ? UnifiedLevel.Error :
               priority == Priority.Assert ? UnifiedLevel.Error :
               priority == Priority.Warn ? UnifiedLevel.Warning :
               priority == Priority.Verbose ? UnifiedLevel.Verbose :
               priority == Priority.Debug ? UnifiedLevel.Verbose : UnifiedLevel.Info;
        #endregion
    }
}