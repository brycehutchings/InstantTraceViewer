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
            if (IsPaused)
            {
                return;
            }

            if ((int)data.Opcode == 11 /* Terminate */ && data.ProviderGuid == EtwTraceSource.SystemProvider && data.TaskName == "Process")
            {
                // Ignore Process/Terminate events. They add no extra information over Process Start/Stop events, and because they are associated with
                // a thread, they will show up in the thread timeline viewer creating many new rows with just a terminate event when process events are
                // collected.
                return;
            }

            var newRecord = CreateBaseTraceRecord(data);

            int namedValueCount = data.PayloadNames.Length;

            if (data.ProviderGuid == Win32kProvider && data.TaskName == "ModifyRgn")
            {
                // TraceEvent library throws an exception internally when reading the last payload value of this potentially very noisy
                // event, which greatly slows down processing, so we need to skip it.
                namedValueCount--;
            }

            List<NamedValue> namedValues = new(namedValueCount);

            long? privTag = null;
            for (int i = 0; i < namedValueCount; i++)
            {
                object payloadValue = data.PayloadValue(i);
                if (data.PayloadNames[i] == "PartA_PrivTags" && payloadValue is long)
                {
                    // Put the priv tag last since it's mostly noise.
                    privTag = (long)payloadValue;
                    continue;
                }

                namedValues.Add(new NamedValue { Name = data.PayloadNames[i], Value = payloadValue });
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

            AddPendingRecord(newRecord);
        }
    }
}
