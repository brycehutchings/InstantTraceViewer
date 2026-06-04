using Microsoft.Diagnostics.Tracing;
using System;
using System.Runtime.InteropServices;

namespace InstantTraceViewerUI.Etw
{
    internal partial class EtwTraceSource
    {
        private delegate void RecordUpdater(ref EtwRecord record);

        // Sometimes the tracing library will show this provider name is shown as "MSNT_SystemTrace" and other times as "Kernel Provider" (and maybe other names?).
        // https://learn.microsoft.com/en-us/windows/win32/etw/msnt-systemtrace
        public readonly static Guid SystemProvider = Guid.Parse("{9e814aad-3204-11d2-9a82-006008a86939}");

        private EtwRecord CreateBaseTraceRecord(TraceEvent data)
        {
            var newRecord = new EtwRecord();
            newRecord.ProcessId = data.ProcessID;
            newRecord.ThreadId = data.ThreadID;
            newRecord.Timestamp = data.TimeStamp;
            newRecord.TimestampRelativeMSec = data.TimeStampRelativeMSec;
            newRecord.Level = data.Level;
            newRecord.OpCode = (byte)data.Opcode;
            newRecord.Keywords = (ulong)data.Keywords;

            // Extract process and thread IDs from events without them (e.g. some Kernel events).
            if (newRecord.ProcessId == -1 || newRecord.ThreadId == -1)
            {
                for (int i = 0; i < data.PayloadNames.Length; i++)
                {
                    if (newRecord.ProcessId == -1 && string.Equals(data.PayloadNames[i], "ProcessID", StringComparison.OrdinalIgnoreCase) && data.PayloadValue(i) is int pid)
                    {
                        newRecord.ProcessId = pid;
                    }
                    else if (newRecord.ThreadId == -1 && string.Equals(data.PayloadNames[i], "ThreadID", StringComparison.OrdinalIgnoreCase) && data.PayloadValue(i) is int tid)
                    {
                        newRecord.ThreadId = tid;
                    }
                }
            }

            newRecord.ProcessName = _processNames.TryGetValue(newRecord.ProcessId, out string processName) ? processName : null;
            newRecord.ThreadName = _threadNames.TryGetValue(newRecord.ThreadId, out string threadName) ? threadName : null;
            newRecord.ProviderName = data.ProviderName;
            newRecord.Name = data.TaskName;

            return newRecord;
        }

        private void AddEvent(EtwRecord record)
        {
            if (IsPaused)
            {
                return;
            }

            _pendingTraceRecordsLock.EnterWriteLock();
            try
            {
                if (_pendingTraceRecords.Count == 0)
                {
                    _pendingTraceRecordsStartTime = DateTime.Now;
                }

                _pendingTraceRecords.Add(record);
            }
            finally
            {
                _pendingTraceRecordsLock.ExitWriteLock();
            }
        }

        private bool UpdatePendingRecord(int threadId, double timestampRelativeMSec, RecordUpdater recordUpdater)
        {
            _pendingTraceRecordsLock.EnterWriteLock();
            try
            {
                Span<EtwRecord> pendingTraceRecords = CollectionsMarshal.AsSpan(_pendingTraceRecords);

                for (int i = pendingTraceRecords.Length - 1; i >= 0; i--)
                {
                    ref var pendingRecord = ref pendingTraceRecords[i];

                    if (pendingRecord.TimestampRelativeMSec < timestampRelativeMSec)
                    {
                        break; // Not found and no need to go earlier.
                    }
                    else if (pendingRecord.ThreadId == threadId && pendingRecord.TimestampRelativeMSec == timestampRelativeMSec)
                    {
                        recordUpdater(ref pendingRecord);
                        return true;
                    }
                }

                return false;
            }
            finally
            {
                _pendingTraceRecordsLock.ExitWriteLock();
            }
        }
    }
}
