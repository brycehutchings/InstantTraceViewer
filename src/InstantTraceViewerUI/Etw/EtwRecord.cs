using InstantTraceViewer;
using System;

namespace InstantTraceViewerUI.Etw
{
    public struct EtwRecord
    {
        public int ProcessId;

        public int ThreadId;

        public TraceLevel Level;

        public string ProviderName;

        public string Name;

        public NamedValue[] NamedValues;

        public byte OpCode;

        public ulong Keywords;

        public DateTime Timestamp;
    }
}
