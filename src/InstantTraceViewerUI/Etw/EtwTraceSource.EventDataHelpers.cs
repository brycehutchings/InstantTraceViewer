using Microsoft.Diagnostics.Tracing;
using System;

namespace InstantTraceViewerUI.Etw
{
    internal partial class EtwTraceSource
    {
        // Sometimes the tracing library will show this provider name is shown as "MSNT_SystemTrace" and other times as "Kernel Provider" (and maybe other names?).
        // https://learn.microsoft.com/en-us/windows/win32/etw/msnt-systemtrace
        public readonly static Guid SystemProvider = Guid.Parse("{9e814aad-3204-11d2-9a82-006008a86939}");

        private EtwRecord CreateBaseTraceRecord(TraceEvent data)
        {
            // In very special cases, events for a process may come in before the process start event
            // so we need to track when this happens so that when process name data comes in later, we
            // can flag the generation id needs to increment to invalid past filtering which did not see
            // the process name on earlier events.
            _processDatabase.ProcessEnsure(data.ProcessID, data.TimeStamp);
            _processDatabase.ThreadEnsure(data.ThreadID, data.TimeStamp);

            var newRecord = new EtwRecord();
            newRecord.ProcessId = data.ProcessID;
            newRecord.ThreadId = data.ThreadID;
            newRecord.Timestamp = data.TimeStamp;
            newRecord.Level = data.Level;
            newRecord.OpCode = (TraceEventOpcodeExtended)data.Opcode;
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

            newRecord.ProviderName = data.ProviderName;
            newRecord.Name = data.TaskName;

            return newRecord;
        }
    }
}
