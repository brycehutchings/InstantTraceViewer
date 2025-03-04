using InstantTraceViewer;
using Microsoft.Diagnostics.Tracing;
using System;
using System.Text;

namespace InstantTraceViewerUI.Etw
{
    internal partial class EtwTraceSource
    {
        // The ETW parser calls the kernel event provider "MSNT_SystemTrace" but we rename to Kernel for simplicity.
        private readonly static Guid SystemProvider = Guid.Parse("{9e814aad-3204-11d2-9a82-006008a86939}");

        private EtwRecord CreateBaseTraceRecord(TraceEvent data)
        {
            var newRecord = new EtwRecord();
            newRecord.ProcessId = data.ProcessID;
            newRecord.ThreadId = data.ThreadID;
            newRecord.Timestamp = data.TimeStamp;
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

            if (data.ProviderGuid == SystemProvider)
            {
                newRecord.ProviderName = $"Kernel.{data.TaskName}";
                newRecord.Name = data.OpcodeName;
            }
            else
            {
                newRecord.ProviderName = data.ProviderName;
                newRecord.Name = data.TaskName;
            }

            return newRecord;
        }
    }
}
