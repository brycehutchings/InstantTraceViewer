using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Session;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using InstantTraceViewer;

namespace InstantTraceViewerUI.Etw
{
    // See https://github.com/microsoft/cpp_client_telemetry_modules/blob/master/utc/MicrosoftTelemetry.h
    [Flags]
    internal enum KnownKeywords : ulong
    {
        Telemetry = 0x0000200000000000,
        TelemetryCritical = 0x0000800000000000,
        TelemetryMeasures = 0x0000400000000000,
    }

    internal partial class EtwTraceSource : ITraceSource
    {
        public static readonly TraceSourceSchemaColumn ColumnProcess = new TraceSourceSchemaColumn { Name = "Process", DefaultColumnSize = 3.75f };
        public static readonly TraceSourceSchemaColumn ColumnThread = new TraceSourceSchemaColumn { Name = "Thread", DefaultColumnSize = 3.75f };
        public static readonly TraceSourceSchemaColumn ColumnProvider = new TraceSourceSchemaColumn { Name = "Provider", DefaultColumnSize = 11.00f };
        public static readonly TraceSourceSchemaColumn ColumnOpCode = new TraceSourceSchemaColumn { Name = "OpCode", DefaultColumnSize = 3.75f };
        public static readonly TraceSourceSchemaColumn ColumnKeywords = new TraceSourceSchemaColumn { Name = "Keywords", DefaultColumnSize = 3.75f };
        public static readonly TraceSourceSchemaColumn ColumnName = new TraceSourceSchemaColumn { Name = "Name", DefaultColumnSize = 8.75f };
        public static readonly TraceSourceSchemaColumn ColumnLevel = new TraceSourceSchemaColumn { Name = "Level", DefaultColumnSize = 3.75f };
        public static readonly TraceSourceSchemaColumn ColumnTime = new TraceSourceSchemaColumn { Name = "Time", DefaultColumnSize = 9.00f };
        public static readonly TraceSourceSchemaColumn ColumnMessage = new TraceSourceSchemaColumn { Name = "Message", DefaultColumnSize = null };

        private static readonly TraceTableSchema _schema = new TraceTableSchema
        {
            Columns = [ColumnProcess, ColumnThread, ColumnProvider, ColumnOpCode, ColumnKeywords, ColumnName, ColumnLevel, ColumnTime, ColumnMessage],
            TimestampColumn = ColumnTime,
            UnifiedLevelColumn = ColumnLevel,
            UnifiedOpcodeColumn = ColumnOpCode,
            ProcessIdColumn = ColumnProcess,
            ThreadIdColumn = ColumnThread,
            ProviderColumn = ColumnProvider,
            NameColumn = ColumnName,
        };

        private static HashSet<int> SessionNums = new();

        // Fixed name is used because ETW sessions can outlive their processes and there is a low system limit. This way we replace leaked sessions rather than creating new ones.
        private static string SessionNamePrefix = "InstantTraceViewerSession";

        private readonly TraceEventSession _etwSession;
        private readonly ETWTraceEventSource? _etwSource;
        private readonly bool _kernelProcessThreadProviderEnabled;

        private readonly int _sessionNum;
        private readonly Thread _processingThread;

        private readonly ReaderWriterLockSlim _pendingTraceRecordsLock = new ReaderWriterLockSlim();
        private List<EtwRecord> _pendingTraceRecords = new();

        private readonly ReaderWriterLockSlim _traceRecordsLock = new ReaderWriterLockSlim();
        private ListBuilder<EtwRecord> _traceRecords = new ListBuilder<EtwRecord>();
        private int _generationId = 1;

        private ConcurrentDictionary<int, string> _threadNames = new();
        private ConcurrentDictionary<int, string> _processNames = new();

        private bool isDisposed;

        public EtwTraceSource(TraceEventSession etwSession, bool kernelProcessThreadProviderEnabled, string displayName, int sessionNum = -1)
        {
            DisplayName = $"{displayName} (ETW)";
            _etwSession = etwSession;
            _etwSource = etwSession.Source;
            _kernelProcessThreadProviderEnabled = kernelProcessThreadProviderEnabled;
            _sessionNum = sessionNum;
            _processingThread = new Thread(() => ProcessThread());
            _processingThread.Start();
        }

        public EtwTraceSource(ETWTraceEventSource etwSource, string displayName)
        {
            DisplayName = displayName;
            _etwSession = null;
            _etwSource = etwSource;
            _kernelProcessThreadProviderEnabled = false;
            _sessionNum = -1;
            _processingThread = new Thread(() => ProcessThread());
            _processingThread.Start();
        }

        // Autologgers save the etl extensions with a number suffix. Associate a handful of them too.
        public static IEnumerable<string> EtlFileExtensions => new[] { ".etl" }.Concat(Enumerable.Range(1, 15).Select(i => $".{i:D3}"));

        private void AddEvent(EtwRecord record)
        {
            _pendingTraceRecordsLock.EnterWriteLock();
            try
            {
                _pendingTraceRecords.Add(record);
            }
            finally
            {
                _pendingTraceRecordsLock.ExitWriteLock();
            }
        }

        private void ProcessThread()
        {
            SubscribeToKernelEvents();
            SubscribeToDynamicEvents();

            try
            {
                // This will block until the ETW session has been disposed.
                _etwSource.Process();
            }
            catch (Exception ex)
            {
                AddEvent(new EtwRecord { NamedValues = new[] { new NamedValue { Value = $"Failed to process ETW session: {ex.Message}" } } });
            }
        }

        static public EtwTraceSource CreateEtlSession(string etlFile)
        {
            var eventSource = new ETWTraceEventSource(etlFile, TraceEventSourceType.FileOnly);
            try
            {
                return new EtwTraceSource(eventSource, Path.GetFileName(etlFile));
            }
            catch
            {
                eventSource.Dispose();
                throw;
            }
        }

        static public EtwTraceSource CreateRealTimeSession(EtwSessionProfile profile)
        {
            int sessionNum = ReserveNextSessionNumber();
            TraceEventSession etwSession = new($"{SessionNamePrefix}{sessionNum}");

            try
            {
                bool kernelProcessThreadProviderEnabled = false;

#if true
                if (profile.KernelKeywords != KernelTraceEventParser.Keywords.None)
                {
                    // EnableKernelProvider will always enable Process and Thread events.
                    kernelProcessThreadProviderEnabled = true;
                    etwSession.EnableKernelProvider(profile.KernelKeywords);
                }

                foreach (var provider in profile.Providers)
                {
                    etwSession.EnableProvider(provider.Name, provider.Level, provider.MatchAnyKeyword);
                }
#else
                TraceEventSession etwSession = new(KernelTraceEventParser.KernelSessionName); //new($"{SessionNamePrefix}{sessionNum}");

                kernelProcessThreadProviderEnabled = true;
                etwSession.EnableKernelProvider(KernelTraceEventParser.Keywords.PMCProfile | KernelTraceEventParser.Keywords.Process);
                var profileSources = TraceEventProfileSources.GetInfo();

                TraceEventProfileSources.Set(profileSources["Timer"].ID, profileSources["Timer"].Interval);
#endif
                return new EtwTraceSource(etwSession, kernelProcessThreadProviderEnabled, profile.DisplayName, sessionNum);
            }
            catch
            {
                etwSession.Dispose();
                SessionNums.Remove(sessionNum);
                throw;
            }
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

        public string DisplayName { get; private set; }

        public bool CanClear => _etwSource.IsRealTime;

        public void Clear()
        {
            _traceRecordsLock.EnterWriteLock();
            try
            {
                _traceRecords = new();
                _generationId++;
            }
            finally
            {
                _traceRecordsLock.ExitWriteLock();
            }

            GC.Collect();
        }

        public ITraceTableSnapshot CreateSnapshot()
        {
            // By moving out the pending records, there is only brief contention on the 'pendingTraceRecords' list.
            // It is important to not block the ETW event callback or events might get dropped.
            List<EtwRecord> pendingTraceRecordsLocal;
            _pendingTraceRecordsLock.EnterWriteLock();
            try
            {
                pendingTraceRecordsLocal = _pendingTraceRecords;
                _pendingTraceRecords = new();
            }
            finally
            {
                _pendingTraceRecordsLock.ExitWriteLock();
            }

            UpdateProcessNameTable(pendingTraceRecordsLocal);

            _traceRecordsLock.EnterWriteLock();
            try
            {
                foreach (var record in pendingTraceRecordsLocal)
                {
                    _traceRecords.Add(record);
                }

                return new EtwTraceTableSnapshot
                {
                    RecordSnapshot = _traceRecords.CreateSnapshot(),
                    GenerationId = _generationId,
                    Schema = _schema,
                };
            }
            finally
            {
                _traceRecordsLock.ExitWriteLock();
            }
        }

        private void UpdateProcessNameTable(IReadOnlyList<EtwRecord> traceRecords)
        {
            // Microsoft.Diagnostics.Tracing will track process names when the Kernel provider is enabled, otherwise we need to do it.
            // If this is not a realtime session, then no point in trying to look up process names--they could be from a different machine or be reused at this point.
            if (!_etwSource.IsRealTime || _kernelProcessThreadProviderEnabled)
            {
                return;
            }

            foreach (var record in traceRecords)
            {
                _processNames.GetOrAdd(record.ProcessId, _ =>
                {
                    try
                    {
                        // We could go lower-level if it is useful and PInvoke QueryFullProcessImageName and open the process handle with PROCESS_QUERY_LIMITED_INFORMATION,
                        // but since this is a realtime session, we're probably elevated already and shouldn't have problems. This would also avoid the need for a try-catch.
                        // However we now have no way to know if the process terminated and the process id could be re-used in the future.
                        using (var process = Process.GetProcessById(record.ProcessId))
                        {
                            return process.ProcessName;
                        }
                    }
                    catch
                    {
                        Debug.WriteLine($"Failed to get process name for pid {record.ProcessId})");
                        return null; // Avoid querying again.
                    }
                });
            }
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!isDisposed)
            {
                if (disposing)
                {
                    _etwSource.Dispose();
                    _etwSession?.Dispose();
                    SessionNums.Remove(_sessionNum);
                }

                isDisposed = true;
            }
        }

        public void Dispose()
        {
            // Do not change this code. Put cleanup code in 'Dispose(bool disposing)' method
            Dispose(disposing: true);
            GC.SuppressFinalize(this);
        }
    }
}