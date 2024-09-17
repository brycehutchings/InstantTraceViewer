using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Parsers.Kernel;
using Microsoft.Diagnostics.Tracing.Session;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;

namespace InstantTraceViewerUI.Etw
{

    internal class EtwTraceSource : ITraceSource
    {
        // Some of these combinations seem nonsensical but I believe certain combinations are used for new meaning to avoid exceeding 64bit limit.
        // Example: SpinLock = Keywords.NetworkTCPIP | Keywords.ThreadPriority
        private readonly static Dictionary<string, KernelTraceEventParser.Keywords> KernelKeywordMap = new Dictionary<string, KernelTraceEventParser.Keywords> {
            { "AllFaults", KernelTraceEventParser.Keywords.Memory },
            { "Alpc", KernelTraceEventParser.Keywords.AdvancedLocalProcedureCalls },
            { "AntiStarvation", KernelTraceEventParser.Keywords.Process | KernelTraceEventParser.Keywords.ReferenceSet },
            { "CompactCSwitch", KernelTraceEventParser.Keywords.DiskIO | KernelTraceEventParser.Keywords.ThreadPriority },
            { "ContiguousMemorygeneration", KernelTraceEventParser.Keywords.AdvancedLocalProcedureCalls | KernelTraceEventParser.Keywords.ThreadPriority },
            { "CpuConfig", KernelTraceEventParser.Keywords.NetworkTCPIP | KernelTraceEventParser.Keywords.ReferenceSet },
            { "CSwitch", KernelTraceEventParser.Keywords.ContextSwitch },
            { "CSwitch_Internal", KernelTraceEventParser.Keywords.ImageLoad | KernelTraceEventParser.Keywords.ThreadPriority },
            { "DiskIO", KernelTraceEventParser.Keywords.DiskIO | KernelTraceEventParser.Keywords.DiskFileIO },
            { "DiskIOInit", KernelTraceEventParser.Keywords.DiskIOInit },
            { "Dpc", KernelTraceEventParser.Keywords.DeferedProcedureCalls },
            { "Dpc_Internal", KernelTraceEventParser.Keywords.SystemCall | KernelTraceEventParser.Keywords.ThreadPriority },
            { "Drivers", KernelTraceEventParser.Keywords.Driver },
            { "Drivers_Internal", KernelTraceEventParser.Keywords.ContextSwitch | KernelTraceEventParser.Keywords.ThreadPriority },
            { "FileIO", KernelTraceEventParser.Keywords.FileIO },
            { "FileIOInit", KernelTraceEventParser.Keywords.FileIOInit },
            { "Filename", KernelTraceEventParser.Keywords.DiskFileIO },
            { "FootPrint", KernelTraceEventParser.Keywords.ProcessCounters | KernelTraceEventParser.Keywords.ThreadPriority },
            { "KeClock", KernelTraceEventParser.Keywords.AdvancedLocalProcedureCalls | KernelTraceEventParser.Keywords.ReferenceSet },
            { "HardFaults", KernelTraceEventParser.Keywords.MemoryHardFaults },
            { "IdleStates", KernelTraceEventParser.Keywords.VAMap | KernelTraceEventParser.Keywords.ReferenceSet },
            { "InterProcessorInterrupt", KernelTraceEventParser.Keywords.Handle | KernelTraceEventParser.Keywords.ReferenceSet },
            { "Interrupt", KernelTraceEventParser.Keywords.Interrupt },
            { "Interrupt_Internal", KernelTraceEventParser.Keywords.VirtualAlloc | KernelTraceEventParser.Keywords.ThreadPriority },
            { "KernelQueue", KernelTraceEventParser.Keywords.Profile | KernelTraceEventParser.Keywords.ThreadPriority },
            { "Loader", KernelTraceEventParser.Keywords.ImageLoad },
            { "Memory", KernelTraceEventParser.Keywords.Process | KernelTraceEventParser.Keywords.ThreadPriority },
            { "MemoryInfoWS", KernelTraceEventParser.Keywords.Driver | KernelTraceEventParser.Keywords.ThreadPriority },
            { "NetworkTrace", KernelTraceEventParser.Keywords.NetworkTCPIP },
            { "PmcProfile", KernelTraceEventParser.Keywords.DiskIOInit | KernelTraceEventParser.Keywords.ThreadPriority },
            { "Pool", KernelTraceEventParser.Keywords.Interrupt | KernelTraceEventParser.Keywords.ThreadPriority },
            { "ProcessCounter", KernelTraceEventParser.Keywords.ProcessCounters },
            { "ProcessThread", KernelTraceEventParser.Keywords.Process | KernelTraceEventParser.Keywords.Thread },
            { "ProcessFreeze", KernelTraceEventParser.Keywords.Thread | KernelTraceEventParser.Keywords.ReferenceSet },
            { "ReadyThread", KernelTraceEventParser.Keywords.Dispatcher },
            { "ReadyThread_Internal", KernelTraceEventParser.Keywords.DiskFileIO | KernelTraceEventParser.Keywords.ThreadPriority },
            { "ReferenceSet", KernelTraceEventParser.Keywords.DeferedProcedureCalls | KernelTraceEventParser.Keywords.ThreadPriority },
            { "Registry", KernelTraceEventParser.Keywords.Registry },
            { "RegistryHive", KernelTraceEventParser.Keywords.Profile | KernelTraceEventParser.Keywords.ReferenceSet },
            { "RegistryNotify", KernelTraceEventParser.Keywords.FileIO | KernelTraceEventParser.Keywords.ReferenceSet },
            { "SampledProfile", KernelTraceEventParser.Keywords.Profile },
            { "SampledProfile_Internal", KernelTraceEventParser.Keywords.Thread | KernelTraceEventParser.Keywords.ThreadPriority },
            { "Session", KernelTraceEventParser.Keywords.Handle | KernelTraceEventParser.Keywords.ThreadPriority },
            { "SpinLock", KernelTraceEventParser.Keywords.NetworkTCPIP | KernelTraceEventParser.Keywords.ThreadPriority },
            { "SplitIO", KernelTraceEventParser.Keywords.SplitIO },
            { "SynchronizationObjects", KernelTraceEventParser.Keywords.Registry | KernelTraceEventParser.Keywords.ThreadPriority },
            { "SystemCall", KernelTraceEventParser.Keywords.SystemCall },
            { "SystemCall_Internal", KernelTraceEventParser.Keywords.Interrupt | KernelTraceEventParser.Keywords.ReferenceSet },
            { "ThreadPriority", KernelTraceEventParser.Keywords.MemoryHardFaults | KernelTraceEventParser.Keywords.ThreadPriority },
            { "Timer", KernelTraceEventParser.Keywords.Registry | KernelTraceEventParser.Keywords.ReferenceSet },
            { "VirtualAllocation", KernelTraceEventParser.Keywords.VirtualAlloc },
            { "VirtualAllocation_Internal", KernelTraceEventParser.Keywords.VAMap | KernelTraceEventParser.Keywords.ThreadPriority },
            { "VAMap", KernelTraceEventParser.Keywords.VAMap }
        };

        private static HashSet<int> SessionNums = new();

        // Fixed name is used because ETW sessions can outlive their processes and there is a low system limit. This way we replace leaked sessions rather than creating new ones.
        private static string SessionNamePrefix = "InstantTraceViewerSession";

        private readonly TraceEventSession _etwSession;
        private readonly int _sessionNum;
        private readonly Thread _processingThread;

        private readonly ReaderWriterLockSlim _pendingTableRecordsLock = new ReaderWriterLockSlim();
        private List<TraceRecord> _pendingTableRecords = new();

        private readonly ReaderWriterLockSlim _tableRecordsLock = new ReaderWriterLockSlim();
        private readonly List<TraceRecord> _tableRecords = new();

        private EtwTraceSource(TraceEventSession etwSession, int sessionNum)
        {
            _etwSession = etwSession;
            _sessionNum = sessionNum;
            _processingThread = new Thread(() => ProcessThread());
            _processingThread.Start();
        }

        private void ProcessThread()
        {
            _etwSession.Source.Kernel.ThreadStart += delegate (ThreadTraceData data)
            {
                var newRecord = new TraceRecord();
                newRecord.ProcessId = data.ProcessID;
                newRecord.ThreadId = data.ThreadID;
                newRecord.Timestamp = data.TimeStamp;
                newRecord.Name = "ThreadStart";
                newRecord.Level = TraceLevel.Info;
                newRecord.ProviderName = "Kernel";
                newRecord.OpCode = 0;
                newRecord.Keywords = 0;
                newRecord.ActivityId = Guid.Empty;
                newRecord.RelatedActivityId = Guid.Empty;

                newRecord.Message = $"ParentPid:{data.ParentProcessID} ParentTid:{data.ParentThreadID}";
                if (!string.IsNullOrEmpty(data.ThreadName))
                {
                    newRecord.Message += $" ThreadName:{data.ThreadName}";
                }

                _pendingTableRecordsLock.EnterWriteLock();
                try
                {
                    _pendingTableRecords.Add(newRecord);
                }
                finally
                {
                    _pendingTableRecordsLock.ExitWriteLock();
                }
            };

            _etwSession.Source.Kernel.ProcessStart += delegate (ProcessTraceData data)
            {
                var newRecord = new TraceRecord();
                newRecord.ProcessId = data.ProcessID;
                newRecord.ThreadId = data.ThreadID;
                newRecord.Timestamp = data.TimeStamp;
                newRecord.Name = "ProcessStart";
                newRecord.Level = TraceLevel.Info;
                newRecord.ProviderName = "Kernel";

                newRecord.Message = $"ParentPid:{data.ParentID} CommandLine:{data.CommandLine}";
                _pendingTableRecordsLock.EnterWriteLock();
                try
                {
                    _pendingTableRecords.Add(newRecord);
                }
                finally
                {
                    _pendingTableRecordsLock.ExitWriteLock();
                }
            };

            _etwSession.Source.Dynamic.All += delegate (TraceEvent data)
            {
                var newRecord = new TraceRecord();
                newRecord.ProcessId = data.ProcessID;
                newRecord.ThreadId = data.ThreadID;
                newRecord.Timestamp = data.TimeStamp;
                newRecord.Name = data.EventName;
                newRecord.Level =
                    data.Level == TraceEventLevel.Always ? TraceLevel.Always :
                    data.Level == TraceEventLevel.Critical ? TraceLevel.Critical :
                    data.Level == TraceEventLevel.Error ? TraceLevel.Error :
                    data.Level == TraceEventLevel.Warning ? TraceLevel.Warning :
                    data.Level == TraceEventLevel.Informational ? TraceLevel.Info : TraceLevel.Verbose;
                newRecord.ProviderName = data.ProviderName;
                newRecord.OpCode = (byte)data.Opcode;
                newRecord.Keywords = (long)data.Keywords;
                newRecord.ActivityId = data.ActivityID;
                newRecord.RelatedActivityId = data.RelatedActivityID;

                newRecord.Message = data.FormattedMessage;
                if (newRecord.Message == null)
                {
                    StringBuilder sb = new();
                    for (int i = 0; i < data.PayloadNames.Length; i++)
                    {
                        // Extract process and thread IDs from events without them (e.g.
                        if (newRecord.ProcessId == -1 && string.Equals(data.PayloadNames[i], "ProcessID", StringComparison.OrdinalIgnoreCase) && data.PayloadValue(i) is int pid)
                        {
                            newRecord.ProcessId = pid;
                            continue;
                        }
                        else if (newRecord.ThreadId == -1 && string.Equals(data.PayloadNames[i], "ThreadID", StringComparison.OrdinalIgnoreCase) && data.PayloadValue(i) is int tid)
                        {
                            newRecord.ThreadId = tid;
                            continue;
                        }

                        if (sb.Length > 0)
                        {
                            sb.Append(' ');
                        }

                        sb.Append(data.PayloadNames[i]);
                        sb.Append(":");

                        // This format provider has no digit separators.
                        sb.Append(data.PayloadString(i, CultureInfo.InvariantCulture));
                    }

                    newRecord.Message = sb.ToString();
                }

                _pendingTableRecordsLock.EnterWriteLock();
                try
                {
                    _pendingTableRecords.Add(newRecord);
                }
                finally
                {
                    _pendingTableRecordsLock.ExitWriteLock();
                }
            };

            // This will block until the ETW session has been disposed.
            _etwSession.Source.Process();
        }

        static public EtwTraceSource CreateRealTimeSession(Etw.WprpProfile profile)
        {
            int sessionNum = ReserveNextSessionNumber();
            TraceEventSession etwSession = new($"{SessionNamePrefix}{sessionNum}");

            try
            {
                if (profile.SystemCollector != null)
                {
                    KernelTraceEventParser.Keywords flags = KernelTraceEventParser.Keywords.None;
                    foreach (var keyword in profile.SystemProvider.Keywords)
                    {
                        if (KernelKeywordMap.TryGetValue(keyword, out KernelTraceEventParser.Keywords matchingFlags))
                        {
                            flags |= matchingFlags;
                        }
                        else
                        {
                            Debug.WriteLine("Unknown system/kernel keyword: " + keyword);
                        }
                    }

                    if (flags != KernelTraceEventParser.Keywords.None)
                    {
                        etwSession.EnableKernelProvider(flags);
                    }
                }


                foreach (var collectorEventProviders in profile.EventProviders)
                {
                    foreach (var eventProvider in collectorEventProviders.Value)
                    {
                        // TODO: Needed when more advanced features are supported.
                        // TraceEventProviderOptions options = new();

                        TraceEventLevel level = TraceEventLevel.Verbose;
                        if (eventProvider.Level.HasValue)
                        {
                            level = (TraceEventLevel)eventProvider.Level.Value;
                        }

                        ulong matchAnyKeywords = ulong.MaxValue;
                        if (eventProvider.Keywords.HasValue)
                        {
                            matchAnyKeywords = eventProvider.Keywords.Value;
                        }

                        etwSession.EnableProvider(eventProvider.Name, level, matchAnyKeywords);
                    }
                }

                return new EtwTraceSource(etwSession, sessionNum);
            }
            catch
            {
                etwSession.Dispose();
                SessionNums.Remove(sessionNum);
                throw;
            }
        }

        public void Dispose()
        {
            _etwSession.Dispose();
            SessionNums.Remove(_sessionNum);
        }

        private static int ReserveNextSessionNumber()
        {
            for (int i = 1; i < 64; i++)
            {
                if (SessionNums.Add(i))
                {
                    return i;
                }
            }

            throw new InvalidOperationException("Too many active ETW sessions have been created.");
        }

        public string GetOpCodeName(byte opCode)
        {
            return opCode == 0 ? string.Empty : ((TraceEventOpcode)opCode).ToString();
        }

        public string GetProcessName(int processId)
        {
            // TODO: Get from etw or local processes if no event and this is real-time.
            return processId == -1 ? string.Empty : processId.ToString();
        }

        public string GetThreadName(int threadId)
        {
            // TODO: Get from etw.
            return threadId == -1 ? string.Empty : threadId.ToString();
        }

        public void ReadUnderLock(Action<IReadOnlyList<TraceRecord>> action)
        {
            // By moving out the pending records, there is only brief contention on the 'pendingTableRecords' list.
            // It is important to not block the ETW event callback or events might get dropped.
            List<TraceRecord> pendingTableRecordsLocal;
            _pendingTableRecordsLock.EnterWriteLock();
            try
            {
                pendingTableRecordsLocal = _pendingTableRecords;
                _pendingTableRecords = new();
            }
            finally
            {
                _pendingTableRecordsLock.ExitWriteLock();
            }

            // Now we can append on the new events.
            if (pendingTableRecordsLocal.Count > 0)
            {
                _tableRecordsLock.EnterWriteLock();
                try
                {
                    _tableRecords.AddRange(pendingTableRecordsLocal);
                }
                finally
                {
                    _tableRecordsLock.ExitWriteLock();
                }
            }

            _pendingTableRecordsLock.EnterReadLock();
            try
            {
                action(_tableRecords);
            }
            finally
            {
                _pendingTableRecordsLock.ExitReadLock();
            }
        }
    }
}