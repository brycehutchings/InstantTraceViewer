using System;

namespace InstantTraceViewerUI.Logcat
{
    public struct LogcatRecord
    {
        public int ProcessId;

        public int ThreadId;

        public TraceLevel Level;

        public string Tag;

        public string Message;

        public DateTime Timestamp;
    }
}
