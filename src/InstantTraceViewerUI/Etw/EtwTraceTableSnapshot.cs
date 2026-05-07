using InstantTraceViewer;
using Microsoft.Diagnostics.Tracing;
using System;
using System.Text;

namespace InstantTraceViewerUI.Etw
{
    class EtwTraceTableSnapshot : ITraceTableSnapshot
    {
        public ListBuilderSnapshot<EtwRecord> RecordSnapshot { get; init; }

        public ProcessDatabase ProcessDatabase { get; init; }

        public TraceTableSchema Schema { get; init; }

        public int RowCount => RecordSnapshot.Count;

        public int GenerationId { get; init; }

        private string GetProcessName(int rowIndex)
        {
            EtwRecord traceRecord = RecordSnapshot[rowIndex];
            return ProcessDatabase.GetProcessName(traceRecord.ProcessId, traceRecord.Timestamp);
        }

        private string GetThreadName(int rowIndex)
        {
            EtwRecord traceRecord = RecordSnapshot[rowIndex];
            return ProcessDatabase.GetThreadName(traceRecord.ThreadId, traceRecord.Timestamp);
        }

        public string GetColumnValueString(int rowIndex, TraceSourceSchemaColumn column, bool allowMultiline = false)
        {
            EtwRecord traceRecord = RecordSnapshot[rowIndex];

            if (column == EtwTraceSource.ColumnProcess)
            {
                string processName = GetProcessName(rowIndex);
                return traceRecord.ProcessId switch
                {
                    -1 => string.Empty,
                    _ when !string.IsNullOrEmpty(processName) => $"{traceRecord.ProcessId} ({processName})",
                    _ => traceRecord.ProcessId.ToString(),
                };
            }
            else if (column == EtwTraceSource.ColumnThread)
            {
                string threadName = GetThreadName(rowIndex);
                return traceRecord.ThreadId switch
                {
                    -1 => string.Empty,
                    _ when !string.IsNullOrEmpty(threadName) => $"{traceRecord.ThreadId} ({threadName})",
                    _ => traceRecord.ThreadId.ToString(),
                };
            }
            else if (column == EtwTraceSource.ColumnProvider)
            {
                return traceRecord.ProviderName;
            }
            else if (column == EtwTraceSource.ColumnLevel)
            {
                // Shorten "Informational" to "Info" to save space.
                return traceRecord.Level switch
                {
                    TraceEventLevel.Informational => "Info",
                    _ => traceRecord.Level.ToString(),
                };
            }
            else if (column == EtwTraceSource.ColumnTime)
            {
                return FriendlyStringify.ToString(traceRecord.Timestamp);
            }
            else if (column == EtwTraceSource.ColumnOpCode)
            {
                return traceRecord.OpCode.ToString();
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
                        customValue = intValue switch
                        {
                            0 => $"0 [{friendlyName}]",
                            _ => $"0x{intValue:X8} [{friendlyName}]",
                        };
                        return true;
                    }

                    customValue = null;
                    return false;
                };

                return NamedValue.GetCollectionString(traceRecord.NamedValues, allowMultiline, tryGetCustomizedValue);
            }

            throw new NotImplementedException();
        }

        public string GetColumnValueNameForId(int rowIndex, TraceSourceSchemaColumn column)
            => column switch
            {
                _ when column == EtwTraceSource.ColumnProcess => GetProcessName(rowIndex),
                _ when column == EtwTraceSource.ColumnThread => GetThreadName(rowIndex),
                _ => throw new NotSupportedException(),
            };

        public int GetColumnValueInt(int rowIndex, TraceSourceSchemaColumn column)
            => column switch
            {
                _ when column == EtwTraceSource.ColumnProcess => RecordSnapshot[rowIndex].ProcessId,
                _ when column == EtwTraceSource.ColumnThread => RecordSnapshot[rowIndex].ThreadId,
                _ => throw new NotSupportedException(),
            };

        public DateTime GetColumnValueDateTime(int rowIndex, TraceSourceSchemaColumn column)
            => column switch
            {
                _ when column == EtwTraceSource.ColumnTime => RecordSnapshot[rowIndex].Timestamp,
                _ => throw new NotSupportedException(),
            };

        public UnifiedLevel GetColumnValueUnifiedLevel(int rowIndex, TraceSourceSchemaColumn column)
            => column switch
            {
                _ when column == EtwTraceSource.ColumnLevel => ConvertLevel(RecordSnapshot[rowIndex].Level),
                _ => throw new NotSupportedException(),
            };

        public UnifiedOpcode GetColumnValueUnifiedOpcode(int rowIndex, TraceSourceSchemaColumn column)
            => column switch
            {
                _ when column == EtwTraceSource.ColumnOpCode => ConvertOpcode(RecordSnapshot[rowIndex].OpCode),
                _ => throw new NotSupportedException(),
            };

        public UnifiedLifecycleEvent GetLifecycleEvent(int rowIndex)
            => ConvertLifecycleEvent(RecordSnapshot[rowIndex]);

        private UnifiedLevel ConvertLevel(TraceEventLevel level)
            => level switch
            {
                TraceEventLevel.Critical => UnifiedLevel.Fatal,
                TraceEventLevel.Error => UnifiedLevel.Error,
                TraceEventLevel.Warning => UnifiedLevel.Warning,
                TraceEventLevel.Verbose => UnifiedLevel.Verbose,
                _ => UnifiedLevel.Info,
            };

        public UnifiedOpcode ConvertOpcode(TraceEventOpcodeExtended opCode)
            => opCode switch
            {
                TraceEventOpcodeExtended.Start or TraceEventOpcodeExtended.DataCollectionStart => UnifiedOpcode.Start,
                TraceEventOpcodeExtended.Stop or TraceEventOpcodeExtended.DataCollectionStop => UnifiedOpcode.Stop,
                _ => UnifiedOpcode.None,
            };

        public UnifiedLifecycleEvent ConvertLifecycleEvent(EtwRecord record)
            => (record.InternalFlags, record.OpCode) switch
            {
                (InternalFlags.ThreadLifecycle, TraceEventOpcodeExtended.Start) => UnifiedLifecycleEvent.ThreadStart,
                (InternalFlags.ThreadLifecycle, TraceEventOpcodeExtended.Stop) => UnifiedLifecycleEvent.ThreadStop,
                (InternalFlags.ProcessLifecycle, TraceEventOpcodeExtended.Start) => UnifiedLifecycleEvent.ProcessStart,
                (InternalFlags.ProcessLifecycle, TraceEventOpcodeExtended.Stop) => UnifiedLifecycleEvent.ProcessStop,
                _ => UnifiedLifecycleEvent.None,
            };
    }
}