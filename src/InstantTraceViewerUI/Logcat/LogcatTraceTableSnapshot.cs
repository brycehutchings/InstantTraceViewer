﻿using System;
using AdvancedSharpAdbClient.Logs;
using InstantTraceViewer;

namespace InstantTraceViewerUI.Logcat
{
    class LogcatTraceTableSnapshot : ITraceTableSnapshot
    {
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
                    !string.IsNullOrEmpty(traceRecord.ProcessName) ? $"{traceRecord.ProcessId} ({traceRecord.ProcessName})" : traceRecord.ProcessId.ToString();
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
                return FriendlyStringify.ToString(traceRecord.Timestamp);
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
            => column == LogcatTraceSource.ColumnProcess ? RecordSnapshot[rowIndex].ProcessName :
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

        public UnifiedOpcode GetColumnValueUnifiedOpcode(int rowIndex, TraceSourceSchemaColumn column)
            => throw new NotSupportedException();

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