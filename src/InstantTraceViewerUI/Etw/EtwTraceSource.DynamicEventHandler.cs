using System;
using System.Collections.Generic;
using Microsoft.Diagnostics.Tracing;
using InstantTraceViewer;

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
            if ((int)data.Opcode == 11 /* Terminate */ && data.ProviderGuid == EtwTraceSource.SystemProvider && data.TaskName == "Process")
            {
                // Ignore Process/Terminate events. They add no extra information over Process Start/Stop events, and because they are associated with
                // a thread, they will show up in the thread timeline viewer creating many new rows with just a terminate event when process events are
                // collected.
                return;
            }

            var newRecord = CreateBaseTraceRecord(data);

            List<NamedValue> namedValues = new(data.PayloadNames.Length);

            long? privTag = null;

            for (int i = 0; i < data.PayloadNames.Length; i++)
            {
                if (data.PayloadNames[i] == "PartA_PrivTags" && data.PayloadValue(i) is long)
                {
                    // Put the priv tag last since it's mostly noise.
                    privTag = (long)data.PayloadValue(i);
                    continue;
                }

                namedValues.Add(new NamedValue { Name = data.PayloadNames[i], Value = data.PayloadValue(i) });
            }

            if (privTag.HasValue)
            {
                namedValues.Add(new NamedValue { Name = "PartA_PrivTags", Value = privTag.Value });
            }

            // Fallback to using the formatted message if there are no payload values. This is the case for some types of trace events like WPP.
            if (namedValues.Count == 0 && !string.IsNullOrEmpty(data.FormattedMessage))
            {
                namedValues.Add(new NamedValue { Name = null, Value = data.FormattedMessage });
            }

            newRecord.NamedValues = namedValues.ToArray();

            AddEvent(newRecord);
        }
    }
}
