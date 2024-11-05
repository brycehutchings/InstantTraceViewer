using System;
using System.Collections.Generic;
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
                return traceRecord.Level.ToString();
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
        #endregion
    }
}