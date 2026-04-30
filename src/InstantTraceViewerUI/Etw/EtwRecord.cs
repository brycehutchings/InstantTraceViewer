using InstantTraceViewer;
using Microsoft.Diagnostics.Tracing;
using System;

namespace InstantTraceViewerUI.Etw
{
    // Additional opcodes on top of TraceEventOpcode
    public enum TraceEventOpcodeExtended : byte
    {
        //
        // Copied from TraceEventOpcode
        //
        Info = 0,
        Start = 1,
        Stop = 2,
        DataCollectionStart = 3,
        DataCollectionStop = 4,
        Extension = 5,
        Reply = 6,
        Resume = 7,
        Suspend = 8,
        Transfer = 9,
        // Receive = 240,
        // 255 is used as in 'illegal opcode' and signifies a WPP style event.  These events 
        // use the event ID and the TASK Guid as their lookup key.  

        //
        // Additional OpCodes used by the Kernel provider
        //
        Load = 10,
        Terminate = 11,
    }

    // ITV internal flags used during event processing.
    public enum InternalFlags : byte
    {
        None = 0,
        ThreadLifecycle = 1,
        ProcessLifecycle = 2,
    }

    public struct EtwRecord
    {
        public DateTime Timestamp;

        public ulong Keywords;

        public string ProcessName;

        public string ThreadName;

        public string ProviderName;

        public string Name;

        public NamedValue[] NamedValues;

        public int ProcessId;

        public int ThreadId;

        public TraceEventLevel Level;

        public TraceEventOpcodeExtended OpCode;

        public InternalFlags InternalFlags;
    }
}
