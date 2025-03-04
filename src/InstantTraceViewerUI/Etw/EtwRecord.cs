using InstantTraceViewer;
using Microsoft.Diagnostics.Tracing;
using System;

namespace InstantTraceViewerUI.Etw
{
    public struct EtwRecord
    {
        public int ProcessId;

        public int ThreadId;

        public string ProcessName;

        public string ThreadName;

        public TraceEventLevel Level;

        public string ProviderName;

        public string Name;

        public NamedValue[] NamedValues;

        public byte OpCode;

        public ulong Keywords;

        public DateTime Timestamp;
    }
}
