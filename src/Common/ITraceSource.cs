namespace InstantTraceViewer
{
    /// <summary>
    /// A unified level that all sources can use for a special level column type used for colorization.
    /// </summary>
    public enum UnifiedLevel
    {
        Verbose,
        Info,
        Warning,
        Error,
        Fatal,
    }

    public enum UnifiedOpcode
    {
        None,
        Start,
        Stop
    }

    public interface ITraceTableSnapshot
    {
        TraceTableSchema Schema { get; }

        /// <summary>
        /// The number of rows in the snapshot. Must be greater than zero.
        /// </summary>
        int RowCount { get; }

        /// <summary>
        /// This will increment if existing rows have been modified or removed or the schema has changed.
        /// This does not increase if new rows are added, so it should be a rare event.
        /// </summary>
        int GenerationId { get; }

        string GetColumnValueString(int rowIndex, TraceSourceSchemaColumn column, bool allowMultiline = false);

        // For columns that contain both integer and a friendly name (GetColumnValueString usually combines them), this returns the friendly name.
        string GetColumnValueNameForId(int rowIndex, TraceSourceSchemaColumn column);

        DateTime GetColumnValueDateTime(int rowIndex, TraceSourceSchemaColumn column);

        int GetColumnValueInt(int rowIndex, TraceSourceSchemaColumn column);

        UnifiedLevel GetColumnValueUnifiedLevel(int rowIndex, TraceSourceSchemaColumn column);

        UnifiedOpcode GetColumnValueUnifiedOpcode(int rowIndex, TraceSourceSchemaColumn column);
    }

    public interface ITraceSource : IDisposable
    {
        string DisplayName { get; }

        int LostEvents { get; }

        bool CanClear { get; }
        void Clear();

        bool CanPause { get; }
        bool IsPaused { get; }
        void TogglePause();

        // Returns true if the trace source is in the process of loading the data and not able to stream any events out yet.
        bool IsPreprocessingData { get; }

        ITraceTableSnapshot CreateSnapshot();
    }

    public class TraceSourceSchemaColumn
    {
        public required string Name { get; init; }

        /// <summary>
        /// Size is multipled by the current font height in pixels.
        /// Returns null when the column size is stretched (this is only recommended for the message).
        /// </summary>
        public float? DefaultColumnSize { get; init; }

        /// <summary>
        /// Set to true to have the log viewer colorize the column
        /// </summary>
        public bool Colorize { get; init; } = false;
    }

    public class TraceTableSchema
    {
        public required IReadOnlyList<TraceSourceSchemaColumn> Columns { get; init; }

        /// <summary>
        /// The column which represents the timestamp of a row. Must support queries using GetColumnValueDateTime.
        /// </summary>
        public TraceSourceSchemaColumn? TimestampColumn { get; init; }

        /// <summary>
        /// The column which represents the level/severity/priority of a row. Must support queries using GetColumnValueUnifiedLevel.
        /// </summary>
        public TraceSourceSchemaColumn? UnifiedLevelColumn { get; init; }

        /// <summary>
        /// The column which represents the unified opcode of a row. Must support queries using GetColumnValueUnifiedOpcode.
        /// </summary>
        public TraceSourceSchemaColumn? UnifiedOpcodeColumn { get; init; }

        /// <summary>
        /// The column which represents the process id of a row. Must support queries using GetColumnValueInt.
        /// </summary>
        public TraceSourceSchemaColumn? ProcessIdColumn { get; init; }

        /// <summary>
        /// The column which represents the thread id of a row. Must support queries using GetColumnValueInt.
        /// </summary>
        public TraceSourceSchemaColumn? ThreadIdColumn { get; init; }

        /// <summary>
        /// The column which represents the provider/data source of the row.
        /// </summary>
        public TraceSourceSchemaColumn? ProviderColumn { get; init; }

        /// <summary>
        /// The column which represents the name of the event for this row.
        /// </summary>
        public TraceSourceSchemaColumn? NameColumn { get; init; }
    }

    public static class TraceTableSnapshotExtensions
    {
        public static DateTime GetTimestamp(this ITraceTableSnapshot snapshot, int rowIndex)
        {
            if (snapshot.Schema.TimestampColumn == null)
            {
                throw new Exception("The schema does not have a timestamp column.");
            }

            return snapshot.GetColumnValueDateTime(rowIndex, snapshot.Schema.TimestampColumn);
        }
        public static string GetName(this ITraceTableSnapshot snapshot, int rowIndex)
        {
            if (snapshot.Schema.NameColumn == null)
            {
                throw new Exception("The schema does not have a name column.");
            }

            return snapshot.GetColumnValueString(rowIndex, snapshot.Schema.NameColumn);
        }

        public static UnifiedLevel GetUnifiedLevel(this ITraceTableSnapshot snapshot, int rowIndex)
        {
            if (snapshot.Schema.UnifiedLevelColumn == null)
            {
                throw new Exception("The schema does not have a unified level column.");
            }

            return snapshot.GetColumnValueUnifiedLevel(rowIndex, snapshot.Schema.UnifiedLevelColumn);
        }

        public static UnifiedOpcode GetUnifiedOpcode(this ITraceTableSnapshot snapshot, int rowIndex)
        {
            if (snapshot.Schema.UnifiedOpcodeColumn == null)
            {
                throw new Exception("The schema does not have a unified opcode column.");
            }
            return snapshot.GetColumnValueUnifiedOpcode(rowIndex, snapshot.Schema.UnifiedOpcodeColumn);
        }

        public static int GetProcessId(this ITraceTableSnapshot snapshot, int rowIndex)
        {
            if (snapshot.Schema.ProcessIdColumn == null)
            {
                throw new Exception("The schema does not have a process id column.");
            }

            return snapshot.GetColumnValueInt(rowIndex, snapshot.Schema.ProcessIdColumn);
        }

        public static int GetThreadId(this ITraceTableSnapshot snapshot, int rowIndex)
        {
            if (snapshot.Schema.ThreadIdColumn == null)
            {
                throw new Exception("The schema does not have a thread id column.");
            }

            return snapshot.GetColumnValueInt(rowIndex, snapshot.Schema.ThreadIdColumn);
        }
    }
}
