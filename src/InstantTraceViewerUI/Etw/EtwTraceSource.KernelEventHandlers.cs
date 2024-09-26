using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers.Kernel;
using System.Collections.Generic;

namespace InstantTraceViewerUI.Etw
{
    internal partial class EtwTraceSource : ITraceSource
    {
        private void SubscribeToKernelEvents()
        {
            _etwSource.Kernel.ThreadStart += OnThreadEvent;
            _etwSource.Kernel.ThreadStop += OnThreadEvent;
            _etwSource.Kernel.ThreadDCStart += OnThreadEvent;
            _etwSource.Kernel.ThreadDCStop += OnThreadEvent;

            _etwSource.Kernel.ThreadSetName += OnThreadSetName;

            _etwSource.Kernel.ProcessStart += OnProcessEvent;
            _etwSource.Kernel.ProcessStop += OnProcessEvent;
            _etwSource.Kernel.ProcessDCStart += OnProcessEvent;
            _etwSource.Kernel.ProcessDCStop += OnProcessEvent;
            _etwSource.Kernel.ProcessDefunct += OnProcessEvent;
        }

        private void OnThreadSetName(ThreadSetNameTraceData data)
        {
            _threadNames.AddOrUpdate(data.ThreadID, data.ThreadName, (key, oldValue) => data.ThreadName);
        }

        private void OnThreadEvent(ThreadTraceData data)
        {
            if (data.Opcode == TraceEventOpcode.Start || data.Opcode == TraceEventOpcode.DataCollectionStart)
            {
                if (!string.IsNullOrEmpty(data.ThreadName))
                {
                    _threadNames.AddOrUpdate(data.ThreadID, data.ThreadName, (key, oldValue) => data.ThreadName);
                }
                else
                {
                    // In case a thread id is reused, we want to make sure we don't have stale data. We need to use a timestamp if we want to keep old and new names around.
                    _threadNames.TryRemove(data.ThreadID, out _);
                }
            }

            // Very noisy--Do we think anyone will want to see the thread events?
#if false
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
#endif
        }

        private void OnProcessEvent(ProcessTraceData data)
        {
            if (data.Opcode == TraceEventOpcode.Start || data.Opcode == TraceEventOpcode.DataCollectionStart)
            {
                _processNames.AddOrUpdate(data.ProcessID, data.ProcessName, (key, oldValue) => data.ProcessName);
            }

            if ((data.Opcode.HasFlag(TraceEventOpcode.DataCollectionStart) ||
                data.Opcode.HasFlag(TraceEventOpcode.DataCollectionStop)) && (_etwSession?.IsRealTime ?? false))
            {
                return; // DCStart/DCStop events are not useful in real-time mode. Lots of spam at the start.
            }

#if false
            var newRecord = CreateBaseTraceRecord(data);
            newRecord.Message = $"ParentPid:{data.ParentID} CommandLine:{data.CommandLine}";
            AddEvent(newRecord);
#endif
        }
    }
}