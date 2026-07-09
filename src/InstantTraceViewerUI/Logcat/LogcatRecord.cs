using System;
using InstantTraceViewer.Adb;

namespace InstantTraceViewerUI.Logcat
{
    public struct LogcatRecord
    {
        public int ProcessId;

        public string ProcessName;

        public int ThreadId;

        public uint? Uid;

        public string PackageName;

        public AdbLogPriority Priority;

        public AdbLogId LogId;

        public string Tag;

        public string Message;

        public DateTime Timestamp;
    }
}
