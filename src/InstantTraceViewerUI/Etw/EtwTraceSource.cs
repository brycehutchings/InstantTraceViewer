using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Session;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace InstantTraceViewerUI.Etw
{
    internal interface ITraceSource : IDisposable
    {
    };

    internal class EtwTraceSource : ITraceSource
    {
        private static HashSet<int> SessionNums = new();

        // Fixed name is used because ETW sessions can outlive their processes and there is a low system limit. This way we replace leaked sessions rather than creating new ones.
        private static string SessionNamePrefix = "InstantTraceViewerSession";

        private TraceEventSession _etwSession;
        private int _sessionNum;
        private Thread _processingThread;

        private EtwTraceSource(TraceEventSession etwSession, int sessionNum)
        {
            _etwSession = etwSession;
            _sessionNum = sessionNum;
            _processingThread = new Thread(() => ProcessThread());
            _processingThread.Start();
        }

        private void ProcessThread()
        {
            //new DynamicTraceEventParser(
            _etwSession.Source.Dynamic.All += delegate (TraceEvent data)
            {
                Debug.Print($"{data}\n");
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
    }
}
