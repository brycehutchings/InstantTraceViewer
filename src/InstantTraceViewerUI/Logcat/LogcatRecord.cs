using AdvancedSharpAdbClient.Logs;
using System;

namespace InstantTraceViewerUI.Logcat
{
    public struct LogcatRecord
    {
        public int ProcessId;

        public string ProcessName;

        public int ThreadId;

        public Priority Priority;

        public LogId LogId;

        public string Tag;

        public string Message;

        public DateTime Timestamp;
    }
}
