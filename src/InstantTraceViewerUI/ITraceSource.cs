using System;
using System.Collections.Generic;

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

    public interface ITraceTableSnapshot
    {
        TraceTableSchema Schema { get; }

        /// <summary>
        /// The number of trace records in the snapshot.
        /// </summary>
        int RowCount { get; }

        /// <summary>
        /// This will increment if existing records have been modified or removed or the schema has changed.
        /// This does not increase if new records are added, so it should be a rare event.
        /// </summary>
        int GenerationId { get; }

        string GetColumnString(int rowIndex, TraceSourceSchemaColumn column, bool allowMultiline = false);

        DateTime GetColumnDateTime(int rowIndex, TraceSourceSchemaColumn column);
    }

    public interface ITraceSource : IDisposable
    {
        string DisplayName { get; }

        bool CanClear { get; }

        void Clear();

        ITraceTableSnapshot CreateSnapshot();
    }

    public class TraceSourceSchemaColumn
    {
        public string Name { get; init; }

        /// <summary>
        /// Size is multipled by the current font height in pixels.
        /// Returns null when the column size is stretched (this is only recommended for the message).
        /// </summary>
        public float? DefaultColumnSize { get; init; }
    }

    public class TraceTableSchema
    {
        public IReadOnlyList<TraceSourceSchemaColumn> Columns { get; init; }

        /// <summary>
        /// The column which represents the timestamp of the trace record.
        /// </summary>
        public TraceSourceSchemaColumn? TimestampColumn { get; init; }
    }

    public static class TraceTableSnapshotExtensions
    {
        public static DateTime GetTimestamp(this ITraceTableSnapshot snapshot, int rowIndex)
        {
            if (snapshot.Schema.TimestampColumn == null)
            {
                throw new Exception("The schema does not have a timestamp column.");
            }

            return snapshot.GetColumnDateTime(rowIndex, snapshot.Schema.TimestampColumn);
        }
    }
}
