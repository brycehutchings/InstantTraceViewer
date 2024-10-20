using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Session;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace InstantTraceViewerUI.Etw
{
    internal partial class EtwTraceSource : ITraceSource
    {
        private static HashSet<int> SessionNums = new();

        // Fixed name is used because ETW sessions can outlive their processes and there is a low system limit. This way we replace leaked sessions rather than creating new ones.
        private static string SessionNamePrefix = "InstantTraceViewerSession";

        private readonly TraceEventSession _etwSession;
        private readonly ETWTraceEventSource _etwSource;
        private readonly bool _kernelProcessThreadProviderEnabled;

        private readonly int _sessionNum;
        private readonly Thread _processingThread;

        private readonly ReaderWriterLockSlim _pendingTraceRecordsLock = new ReaderWriterLockSlim();
        private List<TraceRecord> _pendingTraceRecords = new();

        private readonly ReaderWriterLockSlim _traceRecordsLock = new ReaderWriterLockSlim();
        private readonly ImmutableList<TraceRecord>.Builder _traceRecords = ImmutableList.CreateBuilder<TraceRecord>();
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

        private void AddEvent(TraceRecord record)
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
                AddEvent(new TraceRecord { Message = $"Failed to process ETW session: {ex.Message}" });
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

        public string GetOpCodeName(byte opCode)
        {
            return
                opCode == 0 ? string.Empty :
                opCode == 10 ? "Load" :
                opCode == 11 ? "Terminate" :
                                ((TraceEventOpcode)opCode).ToString();
        }

        public string GetProcessName(int processId)
        {
            return
                processId == -1 ? string.Empty :
                _processNames.TryGetValue(processId, out string name) && !string.IsNullOrEmpty(name) ? $"{processId} ({name})" : processId.ToString();
        }

        public string GetThreadName(int threadId)
        {
            return
                threadId == -1 ? string.Empty :
                _threadNames.TryGetValue(threadId, out string name) ? $"{threadId} ({name})" : threadId.ToString();
        }

        public void Clear()
        {
            _traceRecordsLock.EnterWriteLock();
            try
            {
                _traceRecords.Clear();
                _generationId++;
            }
            finally
            {
                _traceRecordsLock.ExitWriteLock();
            }

            GC.Collect();
        }

        public TraceRecordSnapshot CreateSnapshot()
        {
            // By moving out the pending records, there is only brief contention on the 'pendingTraceRecords' list.
            // It is important to not block the ETW event callback or events might get dropped.
            List<TraceRecord> pendingTraceRecordsLocal;
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
                // Now we can append on the new events.
                if (pendingTraceRecordsLocal.Count > 0)
                {
                    _traceRecords.AddRange(pendingTraceRecordsLocal);
                }

                return new TraceRecordSnapshot
                {
                    GenerationId = _generationId,
                    Records = _traceRecords.ToImmutableList()
                };
            }
            finally
            {
                _traceRecordsLock.ExitWriteLock();
            }
        }

        private void UpdateProcessNameTable(IReadOnlyList<TraceRecord> traceRecords)
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