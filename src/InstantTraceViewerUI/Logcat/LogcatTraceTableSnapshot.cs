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

        public string GetColumnString(int rowIndex, TraceSourceSchemaColumn column, bool allowMultiline = false)
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
                return traceRecord.Timestamp.ToString("HH:mm:ss.ffffff");
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

        public DateTime GetColumnDateTime(int rowIndex, TraceSourceSchemaColumn column)
        {
            if (column != LogcatTraceSource.ColumnTime)
            {
                throw new NotSupportedException();
            }

            return RecordSnapshot[rowIndex].Timestamp;
        }

        public UnifiedLevel GetColumnUnifiedLevel(int rowIndex, TraceSourceSchemaColumn column)
        {
            if (column != LogcatTraceSource.ColumnPriority)
            {
                throw new NotSupportedException();
            }

            Priority priority = RecordSnapshot[rowIndex].Priority;
            return priority == Priority.Fatal ? UnifiedLevel.Fatal :
                    priority == Priority.Error ? UnifiedLevel.Error :
                    priority == Priority.Assert ? UnifiedLevel.Error :
                    priority == Priority.Warn ? UnifiedLevel.Warning :
                    priority == Priority.Verbose ? UnifiedLevel.Verbose :
                    priority == Priority.Debug ? UnifiedLevel.Verbose : UnifiedLevel.Info;
        }
        #endregion
    }
}