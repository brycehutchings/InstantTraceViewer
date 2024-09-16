using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Session;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Threading;

namespace InstantTraceViewerUI.Etw
{

    internal class EtwTraceSource : ITraceSource
    {
        private static HashSet<int> SessionNums = new();

        // Fixed name is used because ETW sessions can outlive their processes and there is a low system limit. This way we replace leaked sessions rather than creating new ones.
        private static string SessionNamePrefix = "InstantTraceViewerSession";

        private readonly ReaderWriterLockSlim _lock = new ReaderWriterLockSlim();

        private readonly TraceEventSession _etwSession;
        private readonly int _sessionNum;
        private readonly Thread _processingThread;
        private readonly List<TraceRecord> tableRecords = new();

        private EtwTraceSource(TraceEventSession etwSession, int sessionNum)
        {
            _etwSession = etwSession;
            _sessionNum = sessionNum;
            _processingThread = new Thread(() => ProcessThread());
            _processingThread.Start();
        }

        private void ProcessThread()
        {
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
                    data.Level == TraceEventLevel.Informational ? TraceLevel.Information : TraceLevel.Verbose;
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
                        if (i > 0)
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

                _lock.EnterWriteLock();
                try
                {
                    tableRecords.Add(newRecord);
                }
                finally
                {
                    _lock.ExitWriteLock();
                }
            };

            _etwSession.Source.Process();
        }

        static public EtwTraceSource CreateRealTimeSession(Etw.WprpProfile profile)
        {
            int sessionNum = ReserveNextSessionNumber();
            TraceEventSession etwSession = new($"{SessionNamePrefix}{sessionNum}");

            try
            {
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
            return ((TraceEventOpcode)opCode).ToString();
        }

        public void ReadUnderLock(Action<IReadOnlyList<TraceRecord>> action)
        {
            _lock.EnterReadLock();
            try
            {
                action(tableRecords);
            }
            finally
            {
                _lock.ExitReadLock();
            }
        }
    }
}
