using Microsoft.Diagnostics.Tracing;
using System;
using System.Text;

namespace InstantTraceViewerUI.Etw
{
    internal partial class EtwTraceSource
    {
        // The ETW parser calls the kernel event provider "MSNT_SystemTrace" but we rename to Kernel for simplicity.
        private readonly static Guid SystemProvider = Guid.Parse("{9e814aad-3204-11d2-9a82-006008a86939}");

        private static TraceRecord CreateBaseTraceRecord(TraceEvent data)
        {
            var newRecord = new TraceRecord();
            newRecord.ProcessId = data.ProcessID;
            newRecord.ThreadId = data.ThreadID;
            newRecord.Timestamp = data.TimeStamp;
            newRecord.Level = ConvertLevel(data);
            newRecord.OpCode = (byte)data.Opcode;
            newRecord.Keywords = (ulong)data.Keywords;
            newRecord.ActivityId = data.ActivityID;
            newRecord.RelatedActivityId = data.RelatedActivityID;

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

        private static TraceLevel ConvertLevel(TraceEvent data)
        {
            return
                data.Level == TraceEventLevel.Always ? TraceLevel.Always :
                data.Level == TraceEventLevel.Critical ? TraceLevel.Critical :
                data.Level == TraceEventLevel.Error ? TraceLevel.Error :
                data.Level == TraceEventLevel.Warning ? TraceLevel.Warning :
                data.Level == TraceEventLevel.Informational ? TraceLevel.Info : TraceLevel.Verbose;
        }
    }
}
