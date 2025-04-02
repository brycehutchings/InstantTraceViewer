namespace InstantTraceViewer
{
    /// <summary>
    /// Simple snapshot of an array of strings.
    /// </summary>
    public class StringArrayTableSnapshot : ITraceTableSnapshot
    {
        private readonly ListBuilderSnapshot<string[]> _recordSnapshot;

        public StringArrayTableSnapshot(ListBuilderSnapshot<string[]> recordSnapshot, TraceTableSchema schema)
        {
            _recordSnapshot = recordSnapshot;
            Schema = schema;
        }

        public TraceTableSchema Schema { get; private set; }

        public int RowCount => _recordSnapshot.Count;

        public int GenerationId => 1;

        public DateTime GetColumnValueDateTime(int rowIndex, TraceSourceSchemaColumn column)
        {
            throw new NotImplementedException();
        }

        public string GetColumnValueNameForId(int rowIndex, TraceSourceSchemaColumn column)
        {
            throw new NotImplementedException();
        }

        public int GetColumnValueInt(int rowIndex, TraceSourceSchemaColumn column)
        {
            throw new NotImplementedException();
        }

        public string GetColumnValueString(int rowIndex, TraceSourceSchemaColumn column, bool allowMultiline = false)
        {
            for (int i = 0; i < Schema.Columns.Count; i++)
            {
                if (Schema.Columns[i] == column)
                {
                    return _recordSnapshot[rowIndex][i];
                }
            }

            throw new ArgumentException("Column not found in schema", nameof(column));
        }

        public UnifiedLevel GetColumnValueUnifiedLevel(int rowIndex, TraceSourceSchemaColumn column)
        {
            throw new NotImplementedException();
        }

        public UnifiedOpcode GetColumnValueUnifiedOpcode(int rowIndex, TraceSourceSchemaColumn column)
        {
            throw new NotImplementedException();
        }
    }
}
