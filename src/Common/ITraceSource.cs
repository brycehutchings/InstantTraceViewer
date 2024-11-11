using System;
using System.Collections.Generic;

namespace InstantTraceViewer
{
    /// <summary>
    /// A unified level that all sources can use for a special level column type used for colorization.
    /// </summary>
    public enum UnifiedLevel
    {
        Fatal,
        Error,
        Warning,
        Info,
        Verbose,
    }

    public interface ITraceTableSnapshot
    {
        TraceTableSchema Schema { get; }

        /// <summary>
        /// The number of rows in the snapshot.
        /// </summary>
        int RowCount { get; }

        /// <summary>
        /// This will increment if existing rows have been modified or removed or the schema has changed.
        /// This does not increase if new rows are added, so it should be a rare event.
        /// </summary>
        int GenerationId { get; }

        string GetColumnString(int rowIndex, TraceSourceSchemaColumn column, bool allowMultiline = false);

        DateTime GetColumnDateTime(int rowIndex, TraceSourceSchemaColumn column);

        int GetColumnInt(int rowIndex, TraceSourceSchemaColumn column);

        UnifiedLevel GetColumnUnifiedLevel(int rowIndex, TraceSourceSchemaColumn column);
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
        /// The column which represents the timestamp of a row. Must support queries using GetColumnDateTime.
        /// </summary>
        public TraceSourceSchemaColumn? TimestampColumn { get; init; }

        /// <summary>
        /// The column which represents the level/severity/priority of a row. Must support queries using GetColumnUnifiedLevel.
        /// </summary>
        public TraceSourceSchemaColumn? UnifiedLevelColumn { get; init; }

        /// <summary>
        /// The column which represents the process id of a row. Must support queries using GetColumnInt.
        /// </summary>
        public TraceSourceSchemaColumn? ProcessIdColumn { get; init; }

        /// <summary>
        /// The column which represents the thread id of a row. Must support queries using GetColumnInt.
        /// </summary>
        public TraceSourceSchemaColumn? ThreadIdColumn { get; init; }
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

        public static UnifiedLevel GetUnifiedLevel(this ITraceTableSnapshot snapshot, int rowIndex)
        {
            if (snapshot.Schema.UnifiedLevelColumn == null)
            {
                throw new Exception("The schema does not have a unified level column.");
            }

            return snapshot.GetColumnUnifiedLevel(rowIndex, snapshot.Schema.UnifiedLevelColumn);
        }

        public static int GetProcessId(this ITraceTableSnapshot snapshot, int rowIndex)
        {
            if (snapshot.Schema.ProcessIdColumn == null)
            {
                throw new Exception("The schema does not have a process id column.");
            }

            return snapshot.GetColumnInt(rowIndex, snapshot.Schema.ProcessIdColumn);
        }

        public static int GetThreadId(this ITraceTableSnapshot snapshot, int rowIndex)
        {
            if (snapshot.Schema.ThreadIdColumn == null)
            {
                throw new Exception("The schema does not have a thread id column.");
            }

            return snapshot.GetColumnInt(rowIndex, snapshot.Schema.ThreadIdColumn);
        }
    }
}
