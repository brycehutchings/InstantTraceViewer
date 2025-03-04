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
            var newRecord = CreateBaseTraceRecord(data);

            List<NamedValue> namedValues = new(data.PayloadNames.Length);

            // data.FormattedMessage contains a friendly formatted message of the payload values. But often it's just "Key: Value ..." anyway so we'll skip it.
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

            newRecord.NamedValues = namedValues.ToArray();

            AddEvent(newRecord);
        }
    }
}
