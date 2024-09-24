using System;
using System.Globalization;
using System.Text;
using Microsoft.Diagnostics.Tracing;

namespace InstantTraceViewerUI.Etw
{
    internal partial class EtwTraceSource
    {
        private void SubscribeToDynamicEvents()
        {
            _etwSource.Dynamic.All += OnDynamicEvent;
        }

        private void OnDynamicEvent(TraceEvent data)
        {
            var newRecord = CreateBaseTraceRecord(data);

            newRecord.Message = data.FormattedMessage;
            if (newRecord.Message == null)
            {
                StringBuilder sb = new();
                for (int i = 0; i < data.PayloadNames.Length; i++)
                {
                    // Extract process and thread IDs from events without them (e.g. Kernel events).
                    if (string.Equals(data.PayloadNames[i], "ProcessID", StringComparison.OrdinalIgnoreCase) && data.PayloadValue(i) is int pid)
                    {
                        if (newRecord.ProcessId == -1)
                        {
                            newRecord.ProcessId = pid;
                        }
                        continue;
                    }
                    else if (string.Equals(data.PayloadNames[i], "ThreadID", StringComparison.OrdinalIgnoreCase) && data.PayloadValue(i) is int tid)
                    {
                        if (newRecord.ThreadId == -1)
                        {
                            newRecord.ThreadId = tid;
                        }
                        continue;
                    }

                    // This format provider has no digit separators.
                    AppendField(sb, data.PayloadNames[i], data.PayloadString(i, CultureInfo.InvariantCulture));
                }

                newRecord.Message = sb.ToString();
            }

            AddEvent(newRecord);
        }
    }
}
