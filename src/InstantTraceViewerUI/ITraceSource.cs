using System;
using System.Collections.Generic;

namespace InstantTraceViewerUI
{
    internal enum TraceLevel
    {
        Always,
        Critical,
        Error,
        Warning,
        Information,
        Verbose,
    }

    internal struct TraceRecord
    {
        public int ProcessId;

        public int ThreadId;

        public TraceLevel Level;

        public string ProviderName;

        public string Name;

        public string Message;

        public byte OpCode;

        // 0 = None, ...., -1L = All
        public long Keywords;

        public DateTime Timestamp;

        public Guid ActivityId;

        public Guid RelatedActivityId;
    }

    internal interface ITraceSource : IDisposable
    {
        string GetOpCodeName(byte opCode);

        void ReadUnderLock(Action<IReadOnlyList<TraceRecord>> action);
    }
}
