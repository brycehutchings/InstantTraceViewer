/*
 * TODO:
 * * Figure out how to use TrackDescriptorManager with FTrace to get process and thread names.
 *   We are currently processing FTrace events in timestamp-sorted order which differs from how TrackDescriptorManager works which needs to run incrementally in packet order.
 */
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using System.Linq;
using System.Collections.Generic;
using InstantTraceViewer;
using Google.Protobuf;
using Perfetto.Protos;

namespace InstantTraceViewerUI.Perfetto
{
    internal class PerfettoTraceSource : ITraceSource
    {
        public static readonly TraceSourceSchemaColumn ColumnProcess = new TraceSourceSchemaColumn { Name = "Process", DefaultColumnSize = 3.75f };
        public static readonly TraceSourceSchemaColumn ColumnThread = new TraceSourceSchemaColumn { Name = "Thread", DefaultColumnSize = 3.75f };
        public static readonly TraceSourceSchemaColumn ColumnSource = new TraceSourceSchemaColumn { Name = "Source", DefaultColumnSize = 3.75f };
        public static readonly TraceSourceSchemaColumn ColumnName = new TraceSourceSchemaColumn { Name = "Name", DefaultColumnSize = 8.75f };
        public static readonly TraceSourceSchemaColumn ColumnCategory = new TraceSourceSchemaColumn { Name = "Category", DefaultColumnSize = 3.75f };
        public static readonly TraceSourceSchemaColumn ColumnPriority = new TraceSourceSchemaColumn { Name = "Priority", DefaultColumnSize = 3.75f };
        public static readonly TraceSourceSchemaColumn ColumnTime = new TraceSourceSchemaColumn { Name = "Time", DefaultColumnSize = 9.00f };
        public static readonly TraceSourceSchemaColumn ColumnData = new TraceSourceSchemaColumn { Name = "Data", DefaultColumnSize = null };

        private static readonly TraceTableSchema _schema = new TraceTableSchema
        {
            Columns = [ColumnProcess, ColumnThread, ColumnSource, ColumnName, ColumnCategory, ColumnPriority, ColumnTime, ColumnData],
            TimestampColumn = ColumnTime,
            ProviderColumn = ColumnSource,
            UnifiedLevelColumn = ColumnPriority,
            UnifiedOpcodeColumn = ColumnCategory,
            ProcessIdColumn = ColumnProcess,
            ThreadIdColumn = ColumnThread,
            NameColumn = ColumnName,
        };

        private readonly FileStream _fileStream;
        private readonly string _displayName;

        private readonly ReaderWriterLockSlim _traceRecordsLock = new ReaderWriterLockSlim();
        private ListBuilder<PerfettoRecord> _traceRecords = new ListBuilder<PerfettoRecord>();
        private int _generationId = 0;
        private readonly CancellationTokenSource _tokenSource = new CancellationTokenSource();
        private readonly Thread _readThread;

        private readonly ConcurrentDictionary<int, string> _processNames = new ConcurrentDictionary<int, string>();
        private Dictionary<uint, Stack<string>> _sliceBeginNames = new Dictionary<uint, Stack<string>>();

        private static readonly JsonFormatter JsonFormatter = new JsonFormatter(JsonFormatter.Settings.Default.WithIndentation());

        public PerfettoTraceSource(string perfettoPath)
        {
            _fileStream = new FileStream(perfettoPath, FileMode.Open, FileAccess.Read);
            _displayName = Path.GetFileName(perfettoPath);

            _readThread = new Thread(ReadThread);
            _readThread.Start();
        }

        public string DisplayName => $"{_displayName} (Perfetto)";

        public bool CanClear => false;

        public void Clear() => throw new NotSupportedException();

        public int LostEvents => 0;

        public ITraceTableSnapshot CreateSnapshot()
        {
            _traceRecordsLock.EnterReadLock();
            try
            {
                return new PerfettoTraceTableSnapshot
                {
                    RecordSnapshot = _traceRecords.CreateSnapshot(),
                    GenerationId = _generationId,
                    Schema = _schema,
                };
            }
            finally
            {
                _traceRecordsLock.ExitReadLock();
            }
        }

        public void Dispose()
        {
            _tokenSource.Cancel();
        }

        ulong _prevTimestamp;

        private async void ReadThread()
        {
            try
            {
                List<PerfettoRecord> records = new();

                Trace trace = Trace.Parser.ParseFrom(_fileStream);

                var trackDescriptorManager = new TrackDescriptorManager();
                var clockSync = new PerfettoClockConverter(trace);

                // First pass: Preprocess packets for track descriptors.
                foreach (var packet in trace.Packet)
                {
                    _tokenSource.Token.ThrowIfCancellationRequested();
                    trackDescriptorManager.ProcessPacket(packet);
                }

                Dictionary<uint, ulong> ftraceTimestamps = new();
                var internedStringManager = new InternedStringManager();
                foreach (var packet in trace.Packet)
                {
                    // Interned strings may be reset or overridden and so the interned string manager can't be precomputed.
                    // It must be used as it is consuming packets.
                    internedStringManager.ProcessPacket(packet);

                    if (packet.SystemInfo != null)
                    {
                        PerfettoRecord record = new();
                        record.Source = Source.SystemInfo;
                        record.Name = "SystemInfo";
                        record.Priority = Priority.Info;
                        record.NamedValues = [new NamedValue { Name = null, Value = JsonFormatter.Format(packet.SystemInfo) }];
                        record.Timestamp = clockSync.GetPacketRealtimeTimestamp(packet);
                        records.Add(record);
                    }
                    else if (packet.TraceConfig != null)
                    {
                        PerfettoRecord record = new();
                        record.Source = Source.TraceConfig;
                        record.Name = "TraceConfig";
                        record.Priority = Priority.Info;
                        record.NamedValues = [new NamedValue { Name = null, Value = JsonFormatter.Format(packet.TraceConfig) }];
                        record.Timestamp = clockSync.GetPacketRealtimeTimestamp(packet);
                        records.Add(record);
                    }
                    else if (packet.TrackEvent != null)
                    {
                        ProcessTrackEvent(records, packet, internedStringManager, trackDescriptorManager, clockSync);
                    }
                    else if (packet.AndroidLog != null)
                    {
                        ParseAndroidLogEvent(records, packet, trackDescriptorManager, clockSync);
                    }
                    else if (packet.FtraceEvents != null)
                    {
                        // Must be processed later to reorder things...
                    }
                    else if (packet.PerfSample != null)
                    {
                        // Stack sampling ignored. Too noisy and not intended for a log viewer.
                    }

                    if (packet.SynchronizationMarker != null)
                    {
                        // TODO: Can this be used to incrementally load in data? Docs say:
                        // > This is used to be able to efficiently partition long traces without having to fully parse them.
                        // But I am seeing FTrace events that come out of sequence across synchronization markers.
                    }
                }

                ProcessFTrace(records, trace, clockSync);

                // All events have been added and now they can be sorted.
                records.Sort((left, right) =>
                    (left.Timestamp < right.Timestamp) ? -1 :
                    (left.Timestamp > right.Timestamp) ? 1 : 0);

                _traceRecordsLock.EnterWriteLock();
                try
                {
                    foreach (var record in records)
                    {
                        _traceRecords.Add(record);
                    }
                }
                finally
                {
                    _traceRecordsLock.ExitWriteLock();
                }
            }
            catch (OperationCanceledException)
            {
                // Trace source is being disposed.
            }
        }

        private static void ProcessFTrace(List<PerfettoRecord> records, Trace trace, PerfettoClockConverter clockSync)
        {
            // See ParseSystraceTracePoint in Perfetto's trace_processor for the known structure of the 'Buf' field.
            Dictionary<(uint pid, int tid), Stack<string>> ftracePrintStacks = new();

            // In rare cases an FTrace end packet won't specify the pid so we have to look it up by last observed tid from a begin packet...
            Dictionary<int /* tid */, int /* pid */> pidTracker = new();

            // FTrace events may be out of order so they must be sorted first in order to process the Begin/End print messages correctly...
            foreach (var e in trace.Packet
                .Where(p => p.FtraceEvents != null)
                .SelectMany(p => p.FtraceEvents.Event)
                .Where(e => e.EventCase == FtraceEvent.EventOneofCase.Print && e.Print.HasBuf && e.HasPid && e.HasTimestamp)
                .OrderBy(e => e.Timestamp))
            {
                string buf = e.Print.Buf.TrimEnd('\n');
                if (buf.StartsWith("B|")) // Begin
                {
                    int nameIndex = buf.IndexOf('|', 2);
                    if (nameIndex != -1 && nameIndex < buf.Length - 1 && int.TryParse(buf[2..nameIndex], out int pid))
                    {
                        string name = buf[(nameIndex + 1)..];

                        Stack<string> printStack;
                        if (!ftracePrintStacks.TryGetValue((e.Pid, pid), out printStack))
                        {
                            printStack = new Stack<string>();
                            ftracePrintStacks.Add((e.Pid, pid), printStack);
                        }

                        printStack.Push(name);

                        PerfettoRecord record = new();
                        record.Source = Source.FTrace;
                        record.Name = name;
                        record.Pid = pid;
                        record.Tid = (int)e.Pid /* kernel pid appears to be tid... */;
                        record.Category = Category.Begin;
                        record.Priority = Priority.Info;
                        record.Timestamp = PerfettoClockConverter.RealTimeClockToDateTime(clockSync.ConvertTimestamp(BuiltinClock.Boottime, BuiltinClock.Realtime, e.Timestamp));
                        records.Add(record);

                        pidTracker[record.Tid] = record.Pid;
                    }
                }
                else if (buf.StartsWith("E|")) // End
                {
                    // if tgid == 0 on BEG: context_->process_tracker->GetOrCreateThread(pid)
                    // if tgid != 0 on BEG: context_->process_tracker->UpdateThread(pid, point.tgid)
                    // if tgid == 0 on END: auto opt_utid = context_->process_tracker->GetThreadOrNull(pid);
                    int pid;
                    if (int.TryParse(buf[2..], out pid) || pidTracker.TryGetValue((int)e.Pid, out pid))
                    {
                        // Must protect against getting an End without a matching Begin.
                        if (ftracePrintStacks.TryGetValue((e.Pid, pid), out Stack<string> printStack) && printStack.TryPop(out string name))
                        {
                            PerfettoRecord record = new();
                            record.Source = Source.FTrace;
                            record.Name = name;
                            record.Pid = pid;
                            record.Tid = (int)e.Pid /* kernel pid appears to be tid... */;
                            record.Category = Category.End;
                            record.Priority = Priority.Info;
                            record.Timestamp = PerfettoClockConverter.RealTimeClockToDateTime(clockSync.ConvertTimestamp(BuiltinClock.Boottime, BuiltinClock.Realtime, e.Timestamp));
                            records.Add(record);
                        }
                    }
                }
                else if (buf.StartsWith("I")) // Instant
                {
                    int nameIndex = buf.IndexOf('|', 2);
                    if (nameIndex != -1 && nameIndex < buf.Length - 1 && int.TryParse(buf[2..nameIndex], out int pid))
                    {
                        string name = buf[(nameIndex + 1)..];

                        PerfettoRecord record = new();
                        record.Source = Source.FTrace;
                        record.Name = name;
                        record.Pid = pid;
                        record.Tid = (int)e.Pid /* kernel pid appears to be tid... */;
                        record.Priority = Priority.Info;
                        record.Timestamp = PerfettoClockConverter.RealTimeClockToDateTime(clockSync.ConvertTimestamp(BuiltinClock.Boottime, BuiltinClock.Realtime, e.Timestamp));
                        records.Add(record);

                        pidTracker[record.Tid] = record.Pid;
                    }
                }
            }
        }

        private void ProcessTrackEvent(List<PerfettoRecord> records, TracePacket packet, InternedStringManager internedStringManager, TrackDescriptorManager trackDescriptorManager, PerfettoClockConverter clockConverter)
        {
            // Use the provided non-interned name if available.
            string name = string.Empty;
            if (packet.TrackEvent.NameFieldCase == TrackEvent.NameFieldOneofCase.NameIid)
            {
                name = internedStringManager.GetInternedEventName(packet, packet.TrackEvent.NameIid);
            }
            else if (packet.TrackEvent.NameFieldCase == TrackEvent.NameFieldOneofCase.Name)
            {
                name = packet.TrackEvent.Name;
            }

            // Later versions of Perfetto do not store the event name with the SliceEnd. Instead if must be inferred using a stack.
            // TODO: Handle packet.SequenceFlags & SEQ_INCREMENTAL_STATE_CLEARED?

            if (!string.IsNullOrEmpty(name) && packet.TrackEvent.Type == TrackEvent.Types.Type.SliceBegin)
            {
                Stack<string> nameStack = null;
                if (_sliceBeginNames.TryGetValue(packet.TrustedPacketSequenceId, out nameStack))
                {
                    nameStack.Push(name);
                }
                else
                {
                    nameStack = new Stack<string>(new[] { name });
                    _sliceBeginNames.Add(packet.TrustedPacketSequenceId, nameStack);
                }
            }
            else if (string.IsNullOrEmpty(name) && packet.TrackEvent.Type == TrackEvent.Types.Type.SliceEnd)
            {
                Stack<string> nameStack = null;
                if (_sliceBeginNames.TryGetValue(packet.TrustedPacketSequenceId, out nameStack) && nameStack.Count > 0)
                {
                    name = nameStack.Pop();
                }
                else
                {
                    name = "!!!MatchingSliceBeginMissing!!!";
                }
            }

            // I am unsure if these are exclusive but I just combine them.
            var categories = new List<string>();
            categories.AddRange(packet.TrackEvent.Categories);
            categories.AddRange(packet.TrackEvent.CategoryIids.Select(ciid => internedStringManager.GetInternedCategoryName(packet, ciid)));

            // See https://github.com/google/perfetto/blob/21753a5bd0877d6b7aac4bea0b593d3f8e55cfef/src/trace_processor/util/debug_annotation_parser.cc
            List<NamedValue> namedValues = new();
            foreach (var debugAnnotation in packet.TrackEvent.DebugAnnotations)
            {
                string debugAnnotationName = string.Empty;
                if (debugAnnotation.HasNameIid && debugAnnotation.NameFieldCase == DebugAnnotation.NameFieldOneofCase.NameIid)
                {
                    debugAnnotationName = internedStringManager.GetInternedDebugAnnotationName(packet, debugAnnotation.NameIid);
                }
                else if (debugAnnotation.HasName)
                {
                    debugAnnotationName = debugAnnotation.Name;
                }
                else
                {
                    debugAnnotationName = "???";
                }

                var debugAnnotationValue = GetDebugAnnotationStringValue(debugAnnotation);
                namedValues.Add(new NamedValue(debugAnnotationName, debugAnnotationValue));
            }

            ThreadDescriptor threadDescriptor = trackDescriptorManager.GetThreadDescriptor(packet);
            ProcessDescriptor processDescriptor = trackDescriptorManager.GetProcessDescriptor(packet, threadDescriptor);

            PerfettoRecord record = new();
            record.Name = name;
            record.Source = Source.TrackEvent;
            record.Category = packet.TrackEvent.Type switch
            {
                TrackEvent.Types.Type.SliceBegin => Category.Begin,
                TrackEvent.Types.Type.SliceEnd => Category.End,
                _ => Category.None,
            };
            record.Priority = record.Category == Category.None ? Priority.Info : Priority.Verbose;
            record.ProcessName = processDescriptor?.ProcessName;
            record.ThreadName = threadDescriptor?.ThreadName;
            record.Pid = processDescriptor?.Pid ?? threadDescriptor?.Pid ?? 0;
            record.Tid = threadDescriptor?.Tid ?? 0;
            record.Timestamp = clockConverter.GetPacketRealtimeTimestamp(packet);
            record.NamedValues = namedValues.ToArray();
            records.Add(record);
        }

        // See DebugAnnotationParser::ParseDebugAnnotationValue
        private static string GetDebugAnnotationStringValue(DebugAnnotation debugAnnotation)
        {
            switch (debugAnnotation.ValueCase)
            {
                case DebugAnnotation.ValueOneofCase.None:
                    return string.Empty;
                case DebugAnnotation.ValueOneofCase.BoolValue:
                    return debugAnnotation.BoolValue.ToString();
                case DebugAnnotation.ValueOneofCase.UintValue:
                    return debugAnnotation.UintValue.ToString();
                case DebugAnnotation.ValueOneofCase.IntValue:
                    return debugAnnotation.IntValue.ToString();
                case DebugAnnotation.ValueOneofCase.DoubleValue:
                    return debugAnnotation.DoubleValue.ToString();
                case DebugAnnotation.ValueOneofCase.StringValue:
                    return debugAnnotation.StringValue;
                case DebugAnnotation.ValueOneofCase.PointerValue:
                    return debugAnnotation.PointerValue.ToString("X");
                case DebugAnnotation.ValueOneofCase.NestedValue:
                    // This annotation type is marked as deprecated.
                    return GetDebugAnnotationNestedValueStringValue(debugAnnotation.NestedValue);
                case DebugAnnotation.ValueOneofCase.LegacyJsonValue:
                    return debugAnnotation.LegacyJsonValue;
            }

            if ((debugAnnotation.ArrayValues?.Count ?? 0) > 0)
            {
                // TODO: Is this right? Do I just ignore the Values on this annotation? Why isn't this one of the "ValueCase"?
                return $"[{string.Join(", ", debugAnnotation.ArrayValues.Select(a => GetDebugAnnotationStringValue(a)))}]";
            }

            if (debugAnnotation.DictEntries != null)
            {
                return "<UNSUPPORTED DICTIONARY>";
                // TODO: Do I just ignore the Values on this annotation? Why isn't this one of the "ValueCase"?
                // TODO: Implement me.
            }

            return string.Empty;
        }

        private static string GetDebugAnnotationNestedValueStringValue(DebugAnnotation.Types.NestedValue nestedValue)
        {
            if (nestedValue.HasIntValue)
            {
                return nestedValue.IntValue.ToString();
            }
            else if (nestedValue.HasBoolValue)
            {
                return nestedValue.BoolValue.ToString();
            }
            else if (nestedValue.HasDoubleValue)
            {
                return nestedValue.DoubleValue.ToString();
            }
            else if (nestedValue.HasStringValue)
            {
                return nestedValue.StringValue;
            }
            // nestedValue.NestedType is unspecified when the NestedValue contains array data so it is ignored.
            else if (nestedValue.ArrayValues?.Count > 0)
            {
                return $"[{string.Join(", ", nestedValue.ArrayValues.Select(av => GetDebugAnnotationNestedValueStringValue(av)))}]";
            }
            else if (nestedValue.DictKeys?.Count > 0)
            {
                System.Diagnostics.Debug.Assert(nestedValue.DictKeys.Count == nestedValue.DictValues.Count);
                return $"[{string.Join(", ", nestedValue.DictKeys.Select((dk, i) => $"{dk}={GetDebugAnnotationNestedValueStringValue(nestedValue.DictValues[i])}"))}]";
            }

            return "UNSPECIFIED NESTED VALUE TYPE";
        }


        private void ParseAndroidLogEvent(List<PerfettoRecord> records, TracePacket packet, TrackDescriptorManager trackDescriptorManager, PerfettoClockConverter clockSync)
        {
            foreach (var evt in packet.AndroidLog.Events)
            {
                var pid = evt.HasPid ? evt.Pid : 0;
                var tid = evt.HasTid ? evt.Tid : 0;

                ProcessDescriptor processDescriptor = trackDescriptorManager.GetProcessDescriptorByPid(pid);
                ThreadDescriptor threadDescriptor = trackDescriptorManager.GetThreadDescriptorByTid(tid);

                PerfettoRecord record = new();
                record.Name = evt.HasTag ? evt.Tag : string.Empty;
                record.Source = evt.LogId switch
                {
                    AndroidLogId.LidDefault => Source.LogcatDefault,
                    AndroidLogId.LidRadio => Source.LogcatRadio,
                    AndroidLogId.LidEvents => Source.LogcatEvents,
                    AndroidLogId.LidSystem => Source.LogcatSystem,
                    AndroidLogId.LidCrash => Source.LogcatCrash,
                    AndroidLogId.LidStats => Source.LogcatStats,
                    AndroidLogId.LidSecurity => Source.LogcatSecurity,
                    AndroidLogId.LidKernel => Source.LogcatKernel,
                };
                record.Priority = evt.Prio switch
                {
                    AndroidLogPriority.PrioVerbose => Priority.Verbose,
                    AndroidLogPriority.PrioDebug => Priority.Debug,
                    AndroidLogPriority.PrioInfo => Priority.Info,
                    AndroidLogPriority.PrioWarn => Priority.Warning,
                    AndroidLogPriority.PrioError => Priority.Error,
                    AndroidLogPriority.PrioFatal => Priority.Fatal,
                };
                record.Pid = processDescriptor?.Pid ?? threadDescriptor?.Pid ?? 0;
                record.ProcessName = processDescriptor?.ProcessName;
                record.Tid = threadDescriptor?.Tid ?? 0;
                record.ThreadName = threadDescriptor?.ThreadName;

                // evt.Timestamp is more accurate than packet.Timestamp. It's already in the Realtime clock domain.
                record.Timestamp = PerfettoClockConverter.RealTimeClockToDateTime(evt.Timestamp);
                record.NamedValues = [new NamedValue(null, evt.Message ?? string.Empty)];

                records.Add(record);
            }
        }
    }
}
