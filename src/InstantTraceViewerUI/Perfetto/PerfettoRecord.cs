using InstantTraceViewer;
using System;

namespace InstantTraceViewerUI.Perfetto
{
    internal enum Source {
        SystemInfo,
        TraceConfig,
        TrackEvent,
        FTrace,

        // Logcat
        LogcatDefault,
        LogcatRadio,
        LogcatEvents,
        LogcatSystem,
        LogcatCrash,
        LogcatStats,
        LogcatSecurity,
        LogcatKernel,
    };

    // Union of TrackEvent Type, <TODO>
    internal enum Category { 
        None,
        Begin,
        End,
    }

    internal enum Priority
    {
        Verbose,
        Debug,
        Info,
        Warning,
        Error,
        Fatal,
    }

    internal struct PerfettoRecord
    {
        public int Pid;
        //public string ProcessName;
        public int Tid;
        //public string ThreadName;
        public string Name;
        public Source Source;
        public Category Category;
        public Priority Priority;
        //public string Category;
        //public ulong BootTimestamp;
        //public ulong? RealtimeTimestamp;
        public DateTime Timestamp;
        //public string Data;
        public NamedValue[] NamedValues;
    }
}
