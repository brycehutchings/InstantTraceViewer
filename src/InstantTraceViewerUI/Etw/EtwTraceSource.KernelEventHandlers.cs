using System;
using System.Text;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers.Kernel;
using static System.Runtime.InteropServices.JavaScript.JSType;

namespace InstantTraceViewerUI.Etw
{
    internal partial class EtwTraceSource : ITraceSource
    {

        private void SubscribeToKernelEvents()
        {
            _etwSource.Kernel.ThreadStart += OnThreadEvent;
            _etwSource.Kernel.ThreadStartGroup += OnThreadEvent;
            _etwSource.Kernel.ThreadStop += OnThreadEvent;
            _etwSource.Kernel.ThreadEndGroup += OnThreadEvent;
            _etwSource.Kernel.ThreadDCStart += OnThreadEvent;
            _etwSource.Kernel.ThreadDCStop += OnThreadEvent;

            _etwSource.Kernel.ThreadSetName += OnThreadSetName;

            _etwSource.Kernel.ProcessStart += OnProcessEvent;
            _etwSource.Kernel.ProcessStop += OnProcessEvent;
            _etwSource.Kernel.ProcessStartGroup += OnProcessEvent;
            _etwSource.Kernel.ProcessEndGroup += OnProcessEvent;
            _etwSource.Kernel.ProcessGroup += OnProcessEvent;
            _etwSource.Kernel.ProcessDCStart += OnProcessEvent;
            _etwSource.Kernel.ProcessDCStop += OnProcessEvent;
            _etwSource.Kernel.ProcessDefunct += OnProcessEvent;
        }

        private void OnThreadSetName(ThreadSetNameTraceData data)
        {
            var newRecord = CreateBaseTraceRecord(data);
            newRecord.ProviderName = "Kernel";
            newRecord.Message = $"ThreadName:{data.ThreadName}";
            AddEvent(newRecord);
        }

        private void OnThreadEvent(ThreadTraceData data)
        {
            if (data.ProcessID == 0 && data.ThreadID == 0)
            {
                return; // Skip the idle process and thread.
            }

            if ((data.Opcode.HasFlag(TraceEventOpcode.DataCollectionStart) ||
                data.Opcode.HasFlag(TraceEventOpcode.DataCollectionStop)) && (_etwSession?.IsRealTime ?? false))
            {
                return; // DCStart/DCStop events are not useful in real-time mode. Lots of spam at the start.
            }

            var newRecord = CreateBaseTraceRecord(data);
            newRecord.ProviderName = "Kernel";

            StringBuilder sb = new();
            if (data.ParentProcessID > 0)
            {
                AppendField(sb, "ParentPid", data.ParentProcessID.ToString());
            }
            if (data.ParentThreadID > 0)
            {
                AppendField(sb, "ParentTid", data.ParentThreadID.ToString());
            }
            if (!string.IsNullOrEmpty(data.ThreadName))
            {
                AppendField(sb, "ThreadName", data.ThreadName);
            }
            newRecord.Message = sb.ToString();

            AddEvent(newRecord);
        }

        private void OnProcessEvent(ProcessTraceData data)
        {
            if ((data.Opcode.HasFlag(TraceEventOpcode.DataCollectionStart) ||
                data.Opcode.HasFlag(TraceEventOpcode.DataCollectionStop)) && (_etwSession?.IsRealTime ?? false))
            {
                return; // DCStart/DCStop events are not useful in real-time mode. Lots of spam at the start.
            }

            var newRecord = CreateBaseTraceRecord(data);
            newRecord.ProviderName = "Kernel";
            newRecord.Message = $"ParentPid:{data.ParentID} CommandLine:{data.CommandLine}";
            AddEvent(newRecord);
        }
    }
}