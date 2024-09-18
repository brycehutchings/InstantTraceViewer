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
            _etwSession.Source.Kernel.ThreadStart += OnThreadEvent;
            _etwSession.Source.Kernel.ThreadStartGroup += OnThreadEvent;
            _etwSession.Source.Kernel.ThreadStop += OnThreadEvent;
            _etwSession.Source.Kernel.ThreadEndGroup += OnThreadEvent;
            _etwSession.Source.Kernel.ThreadDCStart += OnThreadEvent;
            _etwSession.Source.Kernel.ThreadDCStop += OnThreadEvent;

            _etwSession.Source.Kernel.ThreadSetName += OnThreadSetName;

            _etwSession.Source.Kernel.ProcessStart += OnProcessEvent;
            _etwSession.Source.Kernel.ProcessStop += OnProcessEvent;
            _etwSession.Source.Kernel.ProcessStartGroup += OnProcessEvent;
            _etwSession.Source.Kernel.ProcessEndGroup += OnProcessEvent;
            _etwSession.Source.Kernel.ProcessGroup += OnProcessEvent;
            _etwSession.Source.Kernel.ProcessDCStart += OnProcessEvent;
            _etwSession.Source.Kernel.ProcessDCStop += OnProcessEvent;
            _etwSession.Source.Kernel.ProcessDefunct += OnProcessEvent;
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
                data.Opcode.HasFlag(TraceEventOpcode.DataCollectionStop)) && _etwSession.IsRealTime)
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
                data.Opcode.HasFlag(TraceEventOpcode.DataCollectionStop)) && _etwSession.IsRealTime)
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