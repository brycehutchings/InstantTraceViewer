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
        Info,
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

    struct TraceRecordSnapshot
    {
        public TraceRecordSnapshot()
        {
            Records = Array.Empty<TraceRecord>();
            GenerationId = -1;
        }

        public IReadOnlyList<TraceRecord> Records;
        public int GenerationId;
    }

    internal interface ITraceSource : IDisposable
    {
        string DisplayName { get; }

        string GetOpCodeName(byte opCode);

        string GetProcessName(int processId);

        string GetThreadName(int threadId);

        bool CanClear { get; }

        void Clear();

        TraceRecordSnapshot CreateSnapshot();
    }
}
