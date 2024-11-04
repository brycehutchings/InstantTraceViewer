using System;
using System.Collections.Generic;
using InstantTraceViewer;

namespace InstantTraceViewerUI
{
    public enum TraceLevel
    {
        Always,
        Critical,
        Error,
        Warning,
        Info,
        Verbose,
    }

    public struct TraceRecord
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

    public struct TraceRecordSnapshot
    {
        public TraceRecordSnapshot()
        {
            Records = Array.Empty<TraceRecord>();
            GenerationId = -1;
        }

        public IReadOnlyList<TraceRecord> Records;

        /// <summary>
        /// This will increment if existing records have been modified or removed.
        /// This does not increase if new records are added, so it should be a rare event.
        /// </summary>
        public int GenerationId;
    }

    public interface ITraceSource : IDisposable
    {
        string DisplayName { get; }

        TraceSourceSchema Schema { get; }

        string GetColumnString(TraceRecord traceRecord, TraceSourceSchemaColumn column, bool allowMultiline = false);
        //DateTime GetColumnDateTime(TraceRecord traceRecord, TraceSourceSchemaColumn column);
        //int GetColumnInt(TraceRecord traceRecord, TraceSourceSchemaColumn column); 
        //NamedValue[] GetNamedValues(TraceRecord traceRecord, TraceSourceSchemaColumn column);

        bool CanClear { get; }

        void Clear();

        TraceRecordSnapshot CreateSnapshot();
    }

    [Flags]
    public enum TraceSourceSchemaColumnType
    {
        String = 0x01,
        Int = 0x02,
        DateTime = 0x04,
        NamedValues = 0x08,
        // TODO: Trace level?
        // TODO: UInt, Long, ULong, Float, Double, Guid.

        // Special flags that can be combined with the above
        ProcessId = 0x100,
        ThreadId = 0x200,
        Timestamp = 0x400,
        EventName = 0x800,
        Message = 0x1000
    }

    public class TraceSourceSchemaColumn
    {
        public string Name { get; init;  }

        public TraceSourceSchemaColumnType Type { get; init; }

        /// <summary>
        /// Size is multipled by the current font height in pixels.
        /// Returns null when the column size is stretched (this is only recommended for the message).
        /// </summary>
        public float? DefaultColumnSize { get; init; }
    }

    public class TraceSourceSchema
    {
        public IReadOnlyList<TraceSourceSchemaColumn> Columns { get; init; }
    }
}
