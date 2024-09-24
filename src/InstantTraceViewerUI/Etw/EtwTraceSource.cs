using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Session;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
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

        private readonly int _sessionNum;
        private readonly Thread _processingThread;

        private readonly ReaderWriterLockSlim _pendingTableRecordsLock = new ReaderWriterLockSlim();
        private List<TraceRecord> _pendingTableRecords = new();

        private readonly ReaderWriterLockSlim _tableRecordsLock = new ReaderWriterLockSlim();
        private readonly List<TraceRecord> _tableRecords = new();
        private int _generationId = 1;

        private bool isDisposed;

        private EtwTraceSource(TraceEventSession etwSession, string displayName, int sessionNum)
        {
            DisplayName = $"{displayName} (ETW)";
            _etwSession = etwSession;
            _etwSource = etwSession.Source;
            _sessionNum = sessionNum;
            _processingThread = new Thread(() => ProcessThread());
            _processingThread.Start();
        }

        private EtwTraceSource(ETWTraceEventSource etwSource, string displayName)
        {
            DisplayName = displayName;
            _etwSession = null;
            _etwSource = etwSource;
            _sessionNum = -1;
            _processingThread = new Thread(() => ProcessThread());
            _processingThread.Start();
        }

        private void AddEvent(TraceRecord record)
        {
            _tableRecordsLock.EnterWriteLock();
            try
            {
                _tableRecords.Add(record);
            }
            finally
            {
                _tableRecordsLock.ExitWriteLock();
            }
        }

        private void ProcessThread()
        {
            SubscribeToKernelEvents();
            SubscribeToDynamicEvents();

            // This will block until the ETW session has been disposed.
            _etwSource.Process();
        }

        static public EtwTraceSource CreateEtlSession(string etlFile)
        {
            var eventSource = new ETWTraceEventSource(etlFile);
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
                if (profile.KernelKeywords != KernelTraceEventParser.Keywords.None)
                {
                    etwSession.EnableKernelProvider(profile.KernelKeywords);
                }

                foreach (var provider in profile.Providers)
                {
                    etwSession.EnableProvider(provider.Name, provider.Level, provider.MatchAnyKeyword);
                }

                return new EtwTraceSource(etwSession, profile.DisplayName, sessionNum);
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

        public void Clear()
        {
            _tableRecordsLock.EnterWriteLock();
            try
            {
                _tableRecords.Clear();
                _generationId++;
            }
            finally
            {
                _tableRecordsLock.ExitWriteLock();
            }
        }

        public void ReadUnderLock(ReadTraceRecords callback)
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
                callback(_generationId, _tableRecords);
            }
            finally
            {
                _pendingTableRecordsLock.ExitReadLock();
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