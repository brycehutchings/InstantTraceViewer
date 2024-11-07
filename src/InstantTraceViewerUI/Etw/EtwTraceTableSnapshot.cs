using Microsoft.Diagnostics.Tracing;
using System;
using System.Collections.Generic;
using System.Text;
using InstantTraceViewer;

namespace InstantTraceViewerUI.Etw
{
    class EtwTraceTableSnapshot : ITraceTableSnapshot
    {
        public IReadOnlyDictionary<int, string> ThreadNames { get; init; }
        public IReadOnlyDictionary<int, string> ProcessNames { get; init; }
        public ListBuilderSnapshot<EtwRecord> RecordSnapshot { get; init; }

        public TraceTableSchema Schema { get; init; }

        public int RowCount => RecordSnapshot.Count;

        public int GenerationId { get; init; }

        public string GetColumnString(int rowIndex, TraceSourceSchemaColumn column, bool allowMultiline = false)
        {
            EtwRecord traceRecord = RecordSnapshot[rowIndex];

            if (column == EtwTraceSource.ColumnProcess)
            {
                return
                    traceRecord.ProcessId == -1 ? string.Empty :
                    ProcessNames.TryGetValue(traceRecord.ProcessId, out string name) && !string.IsNullOrEmpty(name) ? $"{traceRecord.ProcessId} ({name})" : traceRecord.ProcessId.ToString();
            }
            else if (column == EtwTraceSource.ColumnThread)
            {
                return
                    traceRecord.ThreadId == -1 ? string.Empty :
                    ThreadNames.TryGetValue(traceRecord.ThreadId, out string name) ? $"{traceRecord.ThreadId} ({name})" : traceRecord.ThreadId.ToString();
            }
            else if (column == EtwTraceSource.ColumnProvider)
            {
                return traceRecord.ProviderName;
            }
            else if (column == EtwTraceSource.ColumnLevel)
            {
                // Shorten "Informational" to "Info" to save space.
                return
                    traceRecord.Level == TraceEventLevel.Informational ? "Info" :
                    traceRecord.Level.ToString();
            }
            else if (column == EtwTraceSource.ColumnTime)
            {
                return traceRecord.Timestamp.ToString("HH:mm:ss.ffffff");
            }
            else if (column == EtwTraceSource.ColumnOpCode)
            {
                return
                    traceRecord.OpCode == 0 ? string.Empty :
                    traceRecord.OpCode == 10 ? "Load" :
                    traceRecord.OpCode == 11 ? "Terminate" : ((TraceEventOpcode)traceRecord.OpCode).ToString();
            }
            else if (column == EtwTraceSource.ColumnKeywords)
            {
                StringBuilder sb = new();
                void AppendString(string value)
                {
                    if (sb.Length > 0)
                    {
                        sb.Append(", ");
                    }
                    sb.Append(value);
                }

                ulong keywords = traceRecord.Keywords;
                void AppendKnownKeyword(KnownKeywords knownKeyword)
                {
                    if ((keywords & (ulong)knownKeyword) == (ulong)knownKeyword)
                    {
                        AppendString(knownKeyword.ToString());
                        keywords &= ~(ulong)knownKeyword;
                    }
                }

                AppendKnownKeyword(KnownKeywords.TelemetryMeasures);
                AppendKnownKeyword(KnownKeywords.TelemetryCritical);
                AppendKnownKeyword(KnownKeywords.Telemetry);
                if (keywords != 0)
                {
                    AppendString(keywords.ToString("X"));
                }

                return sb.ToString();
            }
            else if (column == EtwTraceSource.ColumnName)
            {
                return traceRecord.Name;
            }
            else if (column == EtwTraceSource.ColumnMessage)
            {
                // TODO: In the future it would be nice to make these tooltips over the field which is underlined like a hyperlink.
                TryGetCustomizedValue tryGetCustomizedValue = (string name, object value, out string customValue) =>
                {
                    string friendlyName;
                    if (value is int intValue && CodeLookup.TryGetFriendlyName(name, intValue, out friendlyName))
                    {
                        customValue = intValue == 0 ?
                            $"0 [{friendlyName}]" :
                            $"0x{intValue:X8} [{friendlyName}]";
                        return true;
                    }

                    customValue = null;
                    return false;
                };

                return NamedValue.GetCollectionString(traceRecord.NamedValues, allowMultiline, tryGetCustomizedValue);
            }

            throw new NotImplementedException();
        }

        public int GetColumnInt(int rowIndex, TraceSourceSchemaColumn column)
            => column == EtwTraceSource.ColumnProcess ? RecordSnapshot[rowIndex].ProcessId :
               column == EtwTraceSource.ColumnThread ? RecordSnapshot[rowIndex].ThreadId :
               throw new NotSupportedException();

        public DateTime GetColumnDateTime(int rowIndex, TraceSourceSchemaColumn column)
            =>  column == EtwTraceSource.ColumnTime ? RecordSnapshot[rowIndex].Timestamp :
                throw new NotSupportedException();

        public UnifiedLevel GetColumnUnifiedLevel(int rowIndex, TraceSourceSchemaColumn column)
            => column == EtwTraceSource.ColumnLevel ? ConvertLevel(RecordSnapshot[rowIndex].Level) :
               throw new NotSupportedException();

        private UnifiedLevel ConvertLevel(TraceEventLevel level)
            => level == TraceEventLevel.Critical ? UnifiedLevel.Fatal :
               level == TraceEventLevel.Error ? UnifiedLevel.Error :
               level == TraceEventLevel.Warning ? UnifiedLevel.Warning :
               level == TraceEventLevel.Verbose ? UnifiedLevel.Verbose : UnifiedLevel.Info;
    }
}