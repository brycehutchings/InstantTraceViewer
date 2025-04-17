using InstantTraceViewer;
using System;

namespace InstantTraceViewerUI.Perfetto
{
    internal enum Source {
        Metadata,

        TrackEvent,
        FTrace,
        LogcatDefault,
        LogcatRadio,
        LogcatEvents,
        LogcatSystem,
        LogcatCrash,
        LogcatStats,
        LogcatSecurity,
        LogcatKernel,
    };

    internal enum Category { 
        None,Begin,
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
        public string ProcessName;
        public int Tid;
        public string ThreadName;
        public string Name;
        public Source Source;
        public Category Category;
        public Priority Priority;
        public DateTime Timestamp;
        public NamedValue[] NamedValues;
    }
}
